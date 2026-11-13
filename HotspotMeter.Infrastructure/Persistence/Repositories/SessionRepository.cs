using System.Globalization;
using HotspotMeter.Core.Models;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>创建会话并建立 Run 内映射所需的信息。</summary>
public sealed record AttachSessionRequest(
    string RunId,
    long CoreSessionId,
    long ProfileId,
    string InterfaceGuid,
    string Ssid,
    string? Bssid,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// 负责 Core 运行时会话 Id 与数据库持久化会话 Id 的桥接。
///
/// 设计要点：
/// 1. <c>CoreSessionId</c> 只在一次进程运行内有效；数据库用 <c>SessionRunMappings</c> 把它与
///    <c>RunId</c> 绑定，绝不能仅凭数字相同就跨进程复用。
/// 2. “新建会话”和“建立映射”必须在同一事务内，否则崩溃会留下没有映射的孤儿会话。
/// 3. 重复关联同一 (RunId, CoreSessionId) 返回已有 SessionId，不重复插入。
/// 4. 旧 Run 遗留的 Active 映射在启动时统一置为 Abandoned，但不猜测会话结局；
///    孤儿会话由 3D 的恢复流程判定后关闭，避免伪造 EndReason/ResumeOutcome。
/// </summary>
public sealed class SessionRepository
{
    private const int MappingActive = 1;
    private const int MappingClosed = 2;
    private const int MappingAbandoned = 3;

    private const int SessionActive = 0;

    private readonly SqliteConnectionFactory _factory;

    public SessionRepository(SqliteConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>查询已有映射，不创建。</summary>
    public Task<long?> FindDbSessionAsync(
        string runId,
        long coreSessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateRunAndCore(runId, coreSessionId);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SessionId FROM SessionRunMappings WHERE RunId = $run AND CoreSessionId = $core;";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$core", coreSessionId);
        var result = command.ExecuteScalar();
        return Task.FromResult<long?>(result is null ? null : Convert.ToInt64(result));
    }

    /// <summary>
    /// 把一次运行内的会话绑定到数据库会话；已绑定则直接返回原 Id（幂等）。
    /// </summary>
    public Task<long> AttachSessionAsync(AttachSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRunAndCore(request.RunId, request.CoreSessionId);
        if (request.ProfileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "ProfileId 必须为正数。");
        }

        if (string.IsNullOrWhiteSpace(request.InterfaceGuid) || string.IsNullOrWhiteSpace(request.Ssid))
        {
            throw new ArgumentException("接口 GUID 与 SSID 不能为空。", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _factory.Create();
            using var transaction = connection.BeginTransaction(deferred: false);

            var existing = FindMapping(connection, transaction, request.RunId, request.CoreSessionId);
            if (existing is not null)
            {
                transaction.Commit();
                return Task.FromResult(existing.Value);
            }

            var sessionId = InsertSession(connection, transaction, request);

            using (var mapping = connection.CreateCommand())
            {
                mapping.Transaction = transaction;
                mapping.CommandText =
                    """
                    INSERT INTO SessionRunMappings
                        (SessionId, RunId, CoreSessionId, AttachedAtUtc, MappingStatus)
                    VALUES ($sid, $run, $core, $attached, 1);
                    """;
                mapping.Parameters.AddWithValue("$sid", sessionId);
                mapping.Parameters.AddWithValue("$run", request.RunId);
                mapping.Parameters.AddWithValue("$core", request.CoreSessionId);
                mapping.Parameters.AddWithValue("$attached", ToIso(request.StartedAtUtc));
                mapping.ExecuteNonQuery();
            }

            transaction.Commit();
            return Task.FromResult(sessionId);
        }
        catch (SqliteException exception) when (IsConstraintViolation(exception))
        {
            // 并发首次关联：另一个调用已建立映射，回读它而不是制造第二个会话。
            using var connection = _factory.Create();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT SessionId FROM SessionRunMappings WHERE RunId = $run AND CoreSessionId = $core;";
            command.Parameters.AddWithValue("$run", request.RunId);
            command.Parameters.AddWithValue("$core", request.CoreSessionId);
            var result = command.ExecuteScalar();
            if (result is not null)
            {
                return Task.FromResult(Convert.ToInt64(result));
            }

            throw;
        }
    }

    /// <summary>
    /// 关闭会话并结束其 Active 映射。重复关闭返回 false，不修改已写入的结束信息。
    /// </summary>
    public Task<bool> CloseSessionAsync(
        long dbSessionId,
        SessionEndReason endReason,
        ResumeOutcome? resumeOutcome,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (dbSessionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dbSessionId));
        }

        var isRecovery = endReason is SessionEndReason.RecoveryUnconfirmed or SessionEndReason.RecoveryRejected;
        if (isRecovery && resumeOutcome is null)
        {
            throw new ArgumentException("恢复类结束必须提供 ResumeOutcome。", nameof(resumeOutcome));
        }

        if (!isRecovery && resumeOutcome is not null)
        {
            throw new ArgumentException("普通结束不得携带 ResumeOutcome。", nameof(resumeOutcome));
        }

        var status = isRecovery ? 2 : 1;
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction(deferred: false);

        var current = ReadSessionStatus(connection, transaction, dbSessionId)
            ?? throw new KeyNotFoundException($"会话 {dbSessionId} 不存在。");

        if (current != SessionActive)
        {
            transaction.Commit();
            return Task.FromResult(false);
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE ConnectionSessions
                SET SessionStatus = $status,
                    EndedAtUtc = $ended,
                    EndReason = $reason,
                    ResumeOutcome = $outcome
                WHERE SessionId = $id AND SessionStatus = 0;
                """;
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$ended", ToIso(endedAtUtc));
            update.Parameters.AddWithValue("$reason", (int)endReason);
            update.Parameters.AddWithValue("$outcome", resumeOutcome is null ? DBNull.Value : (int)resumeOutcome.Value);
            update.Parameters.AddWithValue("$id", dbSessionId);
            update.ExecuteNonQuery();
        }

        using (var mapping = connection.CreateCommand())
        {
            mapping.Transaction = transaction;
            mapping.CommandText =
                """
                UPDATE SessionRunMappings
                SET MappingStatus = 2, DetachedAtUtc = $ended
                WHERE SessionId = $id AND MappingStatus = 1;
                """;
            mapping.Parameters.AddWithValue("$ended", ToIso(endedAtUtc));
            mapping.Parameters.AddWithValue("$id", dbSessionId);
            mapping.ExecuteNonQuery();
        }

        transaction.Commit();
        return Task.FromResult(true);
    }

    /// <summary>
    /// 把非当前 Run 的 Active 映射标记为 Abandoned（异常退出后的启动清理）。
    /// 不猜测会话结局；返回被清理的映射数。
    /// </summary>
    public Task<int> AbandonStaleMappingsAsync(
        string currentRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateRunAndCore(currentRunId, coreSessionId: 1);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE SessionRunMappings
            SET MappingStatus = 3, DetachedAtUtc = $now
            WHERE MappingStatus = 1 AND RunId <> $run;
            """;
        command.Parameters.AddWithValue("$now", ToIso(now));
        command.Parameters.AddWithValue("$run", currentRunId);
        return Task.FromResult(command.ExecuteNonQuery());
    }

    /// <summary>
    /// 查询“仍为 Active 但已无 Active 映射”的孤儿会话，交给 3D 的恢复流程处理。
    /// </summary>
    public Task<IReadOnlyList<long>> GetOrphanedActiveSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.SessionId
            FROM ConnectionSessions s
            WHERE s.SessionStatus = 0
              AND NOT EXISTS (
                  SELECT 1 FROM SessionRunMappings m
                  WHERE m.SessionId = s.SessionId AND m.MappingStatus = 1)
            ORDER BY s.SessionId;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<long>();
        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }

        return Task.FromResult<IReadOnlyList<long>>(result);
    }

    private static long? FindMapping(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        long coreSessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT SessionId FROM SessionRunMappings WHERE RunId = $run AND CoreSessionId = $core;";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$core", coreSessionId);
        var result = command.ExecuteScalar();
        return result is null ? null : Convert.ToInt64(result);
    }

    private static long InsertSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AttachSessionRequest request)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO ConnectionSessions
                (ProfileId, SessionStatus, InterfaceGuid, Ssid, Bssid, StartedAtUtc, CreatedAtUtc)
            VALUES ($pid, 0, $guid, $ssid, $bssid, $started, $created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$pid", request.ProfileId);
        command.Parameters.AddWithValue("$guid", request.InterfaceGuid);
        command.Parameters.AddWithValue("$ssid", request.Ssid);
        command.Parameters.AddWithValue("$bssid", (object?)request.Bssid ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", ToIso(request.StartedAtUtc));
        command.Parameters.AddWithValue("$created", ToIso(DateTimeOffset.UtcNow));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static int? ReadSessionStatus(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long dbSessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT SessionStatus FROM ConnectionSessions WHERE SessionId = $id;";
        command.Parameters.AddWithValue("$id", dbSessionId);
        var result = command.ExecuteScalar();
        return result is null ? null : Convert.ToInt32(result);
    }

    private static bool IsConstraintViolation(SqliteException exception) =>
        exception.SqliteErrorCode == 19;

    private static void ValidateRunAndCore(string runId, long coreSessionId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Length != 32
            || runId.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("RunId 必须是 32 位十六进制字符串。", nameof(runId));
        }

        if (coreSessionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coreSessionId), "CoreSessionId 必须为正数。");
        }
    }

    private static string ToIso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}