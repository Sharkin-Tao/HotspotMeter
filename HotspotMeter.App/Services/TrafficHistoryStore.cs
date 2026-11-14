using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HotspotMeter.Core.Models;
using HotspotMeter.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.App.Services;

/// <summary>
/// 把监控引擎的会话快照写入 SQLite，并维护 DailyTraffic 增量。
///
/// 边界：
/// 1. 只负责“已经确认属于目标热点的会话字节”，不读取网卡累计计数器；
/// 2. 检查点默认 10 秒一次，会话结束强制写最终结算；
/// 3. 跨午夜由 <see cref="NightlySplitter"/> 按本地自然日切分，字节守恒；
/// 4. 同一 OperationId 已存在时整笔跳过，避免重试造成重复累计；
/// 5. 写入通过 <see cref="SqliteWriter"/> 单消费者串行执行，不阻塞 UI 线程。
/// </summary>
public sealed class TrafficHistoryStore : IAsyncDisposable
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(10);

    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.Local;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, SessionBuffer> _sessions = new();

    private SqliteConnectionFactory? _factory;
    private SqliteWriter? _writer;
    private long _profileId;
    private Guid _profileKey;

    public bool IsAvailable => _writer is { IsRunning: true };

    public string RunId => _runId;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var paths = DatabasePaths.CreateDefault();
        var bootstrap = new DatabaseBootstrapper(paths);
        bootstrap.Initialize();

        _factory = new SqliteConnectionFactory(paths.DatabaseFile);
        _writer = new SqliteWriter(_factory);
        await CloseAbandonedSessionsAsync(cancellationToken);
    }

    /// <summary>
    /// 上次进程异常退出时，ConnectionSessions 会留下 SessionStatus=0 的行。
    /// 启动后无法证明空档期间仍连着同一热点，因此按“恢复被拒绝/接口缺失”收尾：
    /// 保留最后一次检查点之前的字节，绝不把空档流量补进会话；每日连接次数与时长已按检查点累计，不重复写。
    /// </summary>
    private async Task CloseAbandonedSessionsAsync(CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new InvalidOperationException("TrafficHistoryStore 尚未初始化。");
        var closed = await writer.WriteAsync(async (connection, transaction, token) =>
        {
            // 先关映射再关会话：映射必须带 DetachedAtUtc，否则 CHECK 约束会拒绝。
            await using var mappings = connection.CreateCommand();
            mappings.Transaction = transaction;
            mappings.CommandText =
                """
                UPDATE SessionRunMappings
                SET MappingStatus = 2,
                    DetachedAtUtc = (
                        SELECT COALESCE(s.LastPersistedToUtc, s.StartedAtUtc)
                        FROM ConnectionSessions s
                        WHERE s.SessionId = SessionRunMappings.SessionId)
                WHERE MappingStatus = 1
                  AND SessionId IN (SELECT SessionId FROM ConnectionSessions WHERE SessionStatus = 0);
                """;
            await mappings.ExecuteNonQueryAsync(token);

            await using var sessions = connection.CreateCommand();
            sessions.Transaction = transaction;
            sessions.CommandText =
                """
                UPDATE ConnectionSessions
                SET SessionStatus = 2,
                    EndedAtUtc = COALESCE(LastPersistedToUtc, StartedAtUtc),
                    EndReason = 5,
                    ResumeOutcome = 4
                WHERE SessionStatus = 0;
                """;
            return await sessions.ExecuteNonQueryAsync(token);
        }, cancellationToken).ConfigureAwait(false);

        if (closed > 0)
        {
            AppLog.Info($"关闭上次异常退出遗留的 {closed} 个活动会话（按最后检查点收尾，不补空档流量）。");
        }
    }

    /// <summary>
    /// 解析目标热点的稳定 ProfileKey。接口 GUID + SSID 的 SHA-256 前 16 字节：
    /// 同一热点跨程序重启得到同一 ProfileId，历史才能连续；不使用随机 Guid。
    /// </summary>
    public static Guid StableProfileKey(Guid interfaceGuid, string ssid)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{interfaceGuid:N}\n{ssid}"));
        var guidBytes = bytes[..16];
        // 标记为 RFC 4122 版本 5 形态，便于在数据库里识别“派生身份”而非随机值。
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    public async Task<long> BeginSessionAsync(
        Guid profileKey,
        Guid interfaceGuid,
        string ssid,
        string? bssid,
        long coreSessionId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var writer = _writer ?? throw new InvalidOperationException("TrafficHistoryStore 尚未初始化。");
        _profileKey = profileKey;

        var dbSessionId = await writer.WriteAsync(async (connection, transaction, token) =>
        {
            var profileId = await EnsureProfileAsync(connection, transaction, profileKey, interfaceGuid, ssid, token);
            _profileId = profileId;

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO ConnectionSessions
                    (ProfileId, SessionStatus, InterfaceGuid, Ssid, Bssid, StartedAtUtc, CreatedAtUtc)
                VALUES ($profile, 0, $guid, $ssid, $bssid, $started, $created);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$profile", profileId);
            insert.Parameters.AddWithValue("$guid", interfaceGuid.ToString("D"));
            insert.Parameters.AddWithValue("$ssid", ssid);
            insert.Parameters.AddWithValue("$bssid", (object?)bssid ?? DBNull.Value);
            insert.Parameters.AddWithValue("$started", Utc(startedAtUtc));
            insert.Parameters.AddWithValue("$created", Utc(startedAtUtc));
            var sessionId = Convert.ToInt64(await insert.ExecuteScalarAsync(token));

            await using var mapping = connection.CreateCommand();
            mapping.Transaction = transaction;
            mapping.CommandText =
                """
                INSERT INTO SessionRunMappings (SessionId, RunId, CoreSessionId, AttachedAtUtc, MappingStatus)
                VALUES ($sid, $run, $core, $attached, 1);
                """;
            mapping.Parameters.AddWithValue("$sid", sessionId);
            mapping.Parameters.AddWithValue("$run", _runId);
            mapping.Parameters.AddWithValue("$core", coreSessionId);
            mapping.Parameters.AddWithValue("$attached", Utc(startedAtUtc));
            await mapping.ExecuteNonQueryAsync(token);

            // 每日连接次数在“会话开始”时记一次，跨午夜会话也只算开始那天，
            // 避免结算时按分段重复计数。即便该会话最终没有流量，这仍是一次真实连接。
            var startLocalDate = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(startedAtUtc, _timeZone).DateTime);
            var sessionDelta = DailyTrafficDelta.Create(
                profileId,
                startLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                downloadBytes: 0,
                uploadBytes: 0,
                sessionCount: 1,
                connectedMilliseconds: 0,
                firstSessionAtUtc: startedAtUtc,
                lastSessionAtUtc: startedAtUtc,
                updatedAtUtc: DateTimeOffset.UtcNow);
            await new DailyTrafficRepository().ApplyAsync(connection, transaction, sessionDelta, token);

            return sessionId;
        }, cancellationToken);

        _sessions[coreSessionId] = new SessionBuffer(
            dbSessionId, profileKey, _profileId, startedAtUtc, 0, 0, 0, 0);
        return dbSessionId;
    }

    /// <summary>
    /// 记录一个检查点。未到 10 秒且非强制时直接返回，避免每秒写库。
    /// </summary>
    public async Task RecordAsync(
        long coreSessionId,
        MonitorSnapshot snapshot,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (_writer is null || !snapshot.HasSession)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_sessions.TryGetValue(coreSessionId, out var session))
            {
                return;
            }

            var to = snapshot.TimestampUtc.ToUniversalTime();
            if (to <= session.LastToUtc)
            {
                return;
            }

            if (!force && to - session.LastToUtc < CheckpointInterval)
            {
                return;
            }

            var downloadDelta = snapshot.SessionDownloadBytes - session.PersistedDownload;
            var uploadDelta = snapshot.SessionUploadBytes - session.PersistedUpload;
            if (downloadDelta < 0 || uploadDelta < 0)
            {
                // 计数器回退/会话重建：不向历史写入负数，等待调用方开新会话。
                AppLog.Info($"历史记录跳过负增量：会话 {coreSessionId} download={downloadDelta} upload={uploadDelta}");
                return;
            }

            var connectedMs = (long)Math.Max(0, (to - session.LastToUtc).TotalMilliseconds);
            var segments = NightlySplitter.Split(
                session.LastToUtc,
                to,
                _timeZone,
                downloadDelta,
                uploadDelta,
                connectedMs);

            if (segments.Count == 0)
            {
                return;
            }

            var operationSequence = session.OperationSequence + 1;
            var operationId = OperationIdFactory.Create(
                session.DbSessionId, SnapshotKind.Checkpoint, session.LastToUtc, to);

            // 基线必须等事务提交后再推进：写库失败时保留旧基线，
            // 下一检查点会把这段字节重新算进去；OperationId 唯一约束防止重复入库。
            await _writer.WriteAsync(async (connection, transaction, token) =>
            {
                await using (var exists = connection.CreateCommand())
                {
                    exists.Transaction = transaction;
                    exists.CommandText = "SELECT 1 FROM TrafficSnapshots WHERE OperationId = $op LIMIT 1;";
                    exists.Parameters.AddWithValue("$op", operationId);
                    if (await exists.ExecuteScalarAsync(token) is not null)
                    {
                        return;
                    }
                }

                for (var index = 0; index < segments.Count; index++)
                {
                    var segment = segments[index];
                    await using var snapshotCommand = connection.CreateCommand();
                    snapshotCommand.Transaction = transaction;
                    snapshotCommand.CommandText =
                        """
                        INSERT INTO TrafficSnapshots
                            (OperationId, OperationSequence, SegmentIndex, ProfileId, SessionId, Kind, IsFinal,
                             FromUtc, ToUtc, CapturedAtUtc, LocalDate, TimeZoneId, DownloadBytes, UploadBytes,
                             QualityFlags, CreatedAtUtc)
                        VALUES ($op, $seq, $segment, $profile, $session, 1, 0,
                                $from, $to, $captured, $date, $tz, $down, $up, 0, $created);
                        """;
                    snapshotCommand.Parameters.AddWithValue("$op", operationId);
                    snapshotCommand.Parameters.AddWithValue("$seq", operationSequence);
                    snapshotCommand.Parameters.AddWithValue("$segment", index);
                    snapshotCommand.Parameters.AddWithValue("$profile", session.ProfileId);
                    snapshotCommand.Parameters.AddWithValue("$session", session.DbSessionId);
                    snapshotCommand.Parameters.AddWithValue("$from", Utc(segment.FromUtc));
                    snapshotCommand.Parameters.AddWithValue("$to", Utc(segment.ToUtc));
                    snapshotCommand.Parameters.AddWithValue("$captured", Utc(to));
                    snapshotCommand.Parameters.AddWithValue("$date", segment.LocalDate);
                    snapshotCommand.Parameters.AddWithValue("$tz", _timeZone.Id);
                    snapshotCommand.Parameters.AddWithValue("$down", segment.DownloadBytes);
                    snapshotCommand.Parameters.AddWithValue("$up", segment.UploadBytes);
                    snapshotCommand.Parameters.AddWithValue("$created", Utc(DateTimeOffset.UtcNow));
                    await snapshotCommand.ExecuteNonQueryAsync(token);

                    var delta = DailyTrafficDelta.Create(
                        session.ProfileId,
                        segment.LocalDate,
                        segment.DownloadBytes,
                        segment.UploadBytes,
                        sessionCount: 0,
                        segment.ConnectedMilliseconds,
                        firstSessionAtUtc: null,
                        lastSessionAtUtc: null,
                        updatedAtUtc: DateTimeOffset.UtcNow);
                    await new DailyTrafficRepository().ApplyAsync(connection, transaction, delta, token);
                }

                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE ConnectionSessions
                    SET DownloadBytes = $down,
                        UploadBytes = $up,
                        PersistedDownloadBytes = $down,
                        PersistedUploadBytes = $up,
                        LastOperationSequence = $seq,
                        LastPersistedToUtc = $to
                    WHERE SessionId = $sid;
                    """;
                update.Parameters.AddWithValue("$down", snapshot.SessionDownloadBytes);
                update.Parameters.AddWithValue("$up", snapshot.SessionUploadBytes);
                update.Parameters.AddWithValue("$seq", operationSequence);
                update.Parameters.AddWithValue("$to", Utc(to));
                update.Parameters.AddWithValue("$sid", session.DbSessionId);
                await update.ExecuteNonQueryAsync(token);
            }, cancellationToken);

            session.PersistedDownload = snapshot.SessionDownloadBytes;
            session.PersistedUpload = snapshot.SessionUploadBytes;
            session.LastToUtc = to;
            session.OperationSequence = operationSequence;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 会话结束：写最终结算（IsFinal=1）并关闭映射与会话。
    /// 结束信息来自引擎确认的会话总量，不重新读取网卡。
    /// </summary>
    public async Task EndSessionAsync(
        MonitorSessionEndedInfo info,
        CancellationToken cancellationToken = default)
    {
        if (_writer is null || !_sessions.TryGetValue(info.SessionId, out var session))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var to = info.EndedAtUtc.ToUniversalTime();
            var downloadDelta = Math.Max(0, info.DownloadBytes - session.PersistedDownload);
            var uploadDelta = Math.Max(0, info.UploadBytes - session.PersistedUpload);
            var connectedMs = (long)Math.Max(0, (to - session.LastToUtc).TotalMilliseconds);
            var segments = to > session.LastToUtc
                ? NightlySplitter.Split(session.LastToUtc, to, _timeZone, downloadDelta, uploadDelta, connectedMs)
                : [];

            var sequence = session.OperationSequence + 1;
            var operationId = OperationIdFactory.Create(
                session.DbSessionId, SnapshotKind.Settlement, session.LastToUtc, to);

            await _writer.WriteAsync(async (connection, transaction, token) =>
            {
                if (to > session.LastToUtc)
                {
                    for (var index = 0; index < segments.Count; index++)
                    {
                        var segment = segments[index];
                        await using var insert = connection.CreateCommand();
                        insert.Transaction = transaction;
                        insert.CommandText =
                            """
                            INSERT INTO TrafficSnapshots
                                (OperationId, OperationSequence, SegmentIndex, ProfileId, SessionId, Kind, IsFinal,
                                 FromUtc, ToUtc, CapturedAtUtc, LocalDate, TimeZoneId, DownloadBytes, UploadBytes,
                                 QualityFlags, CreatedAtUtc)
                            VALUES ($op, $seq, $segment, $profile, $session, 2, 1,
                                    $from, $to, $captured, $date, $tz, $down, $up, 0, $created);
                            """;
                        insert.Parameters.AddWithValue("$op", operationId);
                        insert.Parameters.AddWithValue("$seq", sequence);
                        insert.Parameters.AddWithValue("$segment", index);
                        insert.Parameters.AddWithValue("$profile", session.ProfileId);
                        insert.Parameters.AddWithValue("$session", session.DbSessionId);
                        insert.Parameters.AddWithValue("$from", Utc(segment.FromUtc));
                        insert.Parameters.AddWithValue("$to", Utc(segment.ToUtc));
                        insert.Parameters.AddWithValue("$captured", Utc(to));
                        insert.Parameters.AddWithValue("$date", segment.LocalDate);
                        insert.Parameters.AddWithValue("$tz", _timeZone.Id);
                        insert.Parameters.AddWithValue("$down", segment.DownloadBytes);
                        insert.Parameters.AddWithValue("$up", segment.UploadBytes);
                        insert.Parameters.AddWithValue("$created", Utc(DateTimeOffset.UtcNow));
                        await insert.ExecuteNonQueryAsync(token);

                        var delta = DailyTrafficDelta.Create(
                            session.ProfileId,
                            segment.LocalDate,
                            segment.DownloadBytes,
                            segment.UploadBytes,
                            sessionCount: 0,
                            segment.ConnectedMilliseconds,
                            firstSessionAtUtc: null,
                            lastSessionAtUtc: null,
                            updatedAtUtc: DateTimeOffset.UtcNow);
                        await new DailyTrafficRepository().ApplyAsync(connection, transaction, delta, token);
                    }
                }

                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE ConnectionSessions
                    SET DownloadBytes = $down, UploadBytes = $up,
                        PersistedDownloadBytes = $down, PersistedUploadBytes = $up,
                        SessionStatus = 1, EndedAtUtc = $ended, EndReason = $reason,
                        LastOperationSequence = $seq, LastPersistedToUtc = $to
                    WHERE SessionId = $sid AND SessionStatus = 0;
                    """;
                update.Parameters.AddWithValue("$down", info.DownloadBytes);
                update.Parameters.AddWithValue("$up", info.UploadBytes);
                update.Parameters.AddWithValue("$ended", Utc(to));
                update.Parameters.AddWithValue("$reason", (int)info.Reason);
                update.Parameters.AddWithValue("$seq", sequence);
                update.Parameters.AddWithValue("$to", Utc(to));
                update.Parameters.AddWithValue("$sid", session.DbSessionId);
                await update.ExecuteNonQueryAsync(token);

                await using var mapping = connection.CreateCommand();
                mapping.Transaction = transaction;
                mapping.CommandText =
                    """
                    UPDATE SessionRunMappings
                    SET MappingStatus = 2, DetachedAtUtc = $ended
                    WHERE SessionId = $sid AND MappingStatus = 1;
                    """;
                mapping.Parameters.AddWithValue("$ended", Utc(to));
                mapping.Parameters.AddWithValue("$sid", session.DbSessionId);
                await mapping.ExecuteNonQueryAsync(token);
            }, cancellationToken);

            _sessions.Remove(info.SessionId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<long> EnsureProfileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileKey,
        Guid interfaceGuid,
        string ssid,
        CancellationToken cancellationToken)
    {
        var key = profileKey.ToString("N");
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT ProfileId FROM HotspotProfiles WHERE ProfileKey = $key;";
            select.Parameters.AddWithValue("$key", key);
            if (await select.ExecuteScalarAsync(cancellationToken) is long existing)
            {
                return existing;
            }
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO HotspotProfiles (ProfileKey, DisplayName, InterfaceGuid, Ssid, CreatedAtUtc, LastSeenUtc)
            VALUES ($key, $name, $guid, $ssid, $now, $now)
            ON CONFLICT(ProfileKey) DO NOTHING;
            SELECT ProfileId FROM HotspotProfiles WHERE ProfileKey = $key;
            """;
        insert.Parameters.AddWithValue("$key", key);
        insert.Parameters.AddWithValue("$name", ssid);
        insert.Parameters.AddWithValue("$guid", interfaceGuid.ToString("D"));
        insert.Parameters.AddWithValue("$ssid", ssid);
        insert.Parameters.AddWithValue("$now", Utc(DateTimeOffset.UtcNow));
        return Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
    }

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        if (_writer is not null)
        {
            try
            {
                await _writer.StopAsync();
            }
            catch
            {
                // 退出路径不抛异常：最后一次检查点可能因此丢失，这是已声明的完整性边界。
            }

            await _writer.DisposeAsync();
            _writer = null;
        }

        _factory = null;
    }

    private sealed class SessionBuffer(
        long dbSessionId,
        Guid profileKey,
        long profileId,
        DateTimeOffset lastToUtc,
        long persistedDownload,
        long persistedUpload,
        long operationSequence,
        long lastConnectedMs)
    {
        public long DbSessionId { get; } = dbSessionId;

        public Guid ProfileKey { get; } = profileKey;

        public long ProfileId { get; } = profileId;

        public DateTimeOffset LastToUtc { get; set; } = lastToUtc;

        public long PersistedDownload { get; set; } = persistedDownload;

        public long PersistedUpload { get; set; } = persistedUpload;

        public long OperationSequence { get; set; } = operationSequence;

        public long LastConnectedMs { get; set; } = lastConnectedMs;
    }
}