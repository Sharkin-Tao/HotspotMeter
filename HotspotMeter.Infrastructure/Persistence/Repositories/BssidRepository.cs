using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>确认 BSSID 的结果。</summary>
public enum BssidConfirmationOutcome
{
    /// <summary>本次确认成功。</summary>
    Confirmed = 0,

    /// <summary>该 Profile 之前已经确认过同一个 BSSID，本次为空操作。</summary>
    AlreadyConfirmed = 1,

    /// <summary>该 BSSID 已被另一个 Profile 确认，不允许自动转移所有权。</summary>
    ConflictWithOtherProfile = 2,
}

/// <summary>确认结果，含当前确认者，便于上层展示冲突对象。</summary>
public sealed record BssidConfirmationResult(BssidConfirmationOutcome Outcome, long? OwnerProfileId);

/// <summary>
/// BSSID 观察与确认。
///
/// 设计要点：
/// 1. “看到过”与“确认属于”是两个概念：未确认的 BSSID 允许多个 Profile 同时观察，
///    只有人工/上层确认后才通过部分唯一索引保证全局唯一。
/// 2. 确认操作先在同一事务内检查现有确认者，再写入；并发时依赖
///    <c>UX_Bssid_Confirmed</c> 唯一索引兜底，失败者回读后返回冲突，绝不自动抢占。
/// 3. 所有查询都带 ProfileId 过滤，跨 Profile 数据互不可见。
/// </summary>
public sealed class BssidRepository
{
    private readonly SqliteConnectionFactory _factory;

    public BssidRepository(SqliteConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>记录一次 BSSID 观察；已存在则累加 SeenCount 并刷新最后观察时间。</summary>
    public Task RecordObservationAsync(
        long profileId,
        string bssid,
        DateTimeOffset seenAtUtc,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(bssid);
        if (profileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profileId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO HotspotProfileBssids
                (ProfileId, Bssid, IsConfirmed, Source, FirstSeenUtc, LastSeenUtc, SeenCount)
            VALUES ($pid, $bssid, 0, 0, $seen, $seen, 1)
            ON CONFLICT(ProfileId, Bssid) DO UPDATE SET
                LastSeenUtc = excluded.LastSeenUtc,
                SeenCount = SeenCount + 1;
            """;
        command.Parameters.AddWithValue("$pid", profileId);
        command.Parameters.AddWithValue("$bssid", normalized);
        command.Parameters.AddWithValue("$seen", ToIso(seenAtUtc));
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>该 Profile 已确认的 BSSID 列表（大写、冒号分隔）。</summary>
    public Task<IReadOnlyList<string>> GetConfirmedAsync(
        long profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Bssid FROM HotspotProfileBssids
            WHERE ProfileId = $pid AND IsConfirmed = 1
            ORDER BY Bssid;
            """;
        command.Parameters.AddWithValue("$pid", profileId);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    /// <summary>该 BSSID 是否已被该 Profile 确认。</summary>
    public Task<bool> IsConfirmedAsync(
        long profileId,
        string bssid,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(bssid);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM HotspotProfileBssids WHERE ProfileId = $pid AND Bssid = $bssid AND IsConfirmed = 1;";
        command.Parameters.AddWithValue("$pid", profileId);
        command.Parameters.AddWithValue("$bssid", normalized);
        return Task.FromResult(Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0);
    }

    /// <summary>
    /// 确认 BSSID 属于该 Profile。若已被别的 Profile 确认，返回冲突且不修改任何数据。
    /// </summary>
    public Task<BssidConfirmationResult> ConfirmAsync(
        long profileId,
        string bssid,
        DateTimeOffset confirmedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(bssid);
        if (profileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profileId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction(deferred: false);

        var existing = ReadConfirmation(connection, transaction, normalized);
        if (existing is { } owner)
        {
            transaction.Commit();
            return Task.FromResult(owner == profileId
                ? new BssidConfirmationResult(BssidConfirmationOutcome.AlreadyConfirmed, owner)
                : new BssidConfirmationResult(BssidConfirmationOutcome.ConflictWithOtherProfile, owner));
        }

        try
        {
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE HotspotProfileBssids
                    SET IsConfirmed = 1, Source = 1, LastSeenUtc = $now
                    WHERE ProfileId = $pid AND Bssid = $bssid;
                    """;
                update.Parameters.AddWithValue("$now", ToIso(confirmedAtUtc));
                update.Parameters.AddWithValue("$pid", profileId);
                update.Parameters.AddWithValue("$bssid", normalized);

                if (update.ExecuteNonQuery() == 0)
                {
                    InsertConfirmed(connection, transaction, profileId, normalized, confirmedAtUtc);
                }
            }

            transaction.Commit();
            return Task.FromResult(new BssidConfirmationResult(BssidConfirmationOutcome.Confirmed, profileId));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            // 并发确认：另一个 Profile 已先提交，部分唯一索引拒绝了本次写入。
            transaction.Rollback();
            var currentOwner = ReadConfirmationAfterRollback(normalized);
            return Task.FromResult(currentOwner == profileId
                ? new BssidConfirmationResult(BssidConfirmationOutcome.AlreadyConfirmed, currentOwner)
                : new BssidConfirmationResult(BssidConfirmationOutcome.ConflictWithOtherProfile, currentOwner));
        }
    }

    private static long? ReadConfirmation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string bssid)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ProfileId FROM HotspotProfileBssids WHERE Bssid = $bssid AND IsConfirmed = 1;";
        command.Parameters.AddWithValue("$bssid", bssid);
        var result = command.ExecuteScalar();
        return result is null ? null : Convert.ToInt64(result);
    }

    private long? ReadConfirmationAfterRollback(string bssid)
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProfileId FROM HotspotProfileBssids WHERE Bssid = $bssid AND IsConfirmed = 1;";
        command.Parameters.AddWithValue("$bssid", bssid);
        var result = command.ExecuteScalar();
        return result is null ? null : Convert.ToInt64(result);
    }

    private static void InsertConfirmed(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long profileId,
        string bssid,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO HotspotProfileBssids
                (ProfileId, Bssid, IsConfirmed, Source, FirstSeenUtc, LastSeenUtc, SeenCount)
            VALUES ($pid, $bssid, 1, 1, $now, $now, 1);
            """;
        command.Parameters.AddWithValue("$pid", profileId);
        command.Parameters.AddWithValue("$bssid", bssid);
        command.Parameters.AddWithValue("$now", ToIso(now));
        command.ExecuteNonQuery();
    }

    private static string Normalize(string bssid)
    {
        if (string.IsNullOrWhiteSpace(bssid))
        {
            throw new ArgumentException("BSSID 不能为空。", nameof(bssid));
        }

        var normalized = bssid.Trim().ToUpperInvariant();
        var parts = normalized.Split(':');
        if (parts.Length != 6 || parts.Any(part => part.Length != 2 || !part.All(Uri.IsHexDigit)))
        {
            throw new ArgumentException($"BSSID 格式非法：'{bssid}'，应为 6 组两位十六进制。", nameof(bssid));
        }

        return normalized;
    }

    private static string ToIso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}