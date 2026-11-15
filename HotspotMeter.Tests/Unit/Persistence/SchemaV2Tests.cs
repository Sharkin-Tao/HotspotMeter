using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Unit.Persistence;

/// <summary>
/// 模式 v2 的约束回归：验证 CHECK、复合外键、部分唯一索引在真实 SQLite 上确实生效。
/// 这些测试只依赖真实数据库行为，不使用内存假数据。
/// </summary>
public class SchemaV2Tests
{
    private const string InterfaceGuid = "11111111-1111-1111-1111-111111111111";
    private const string StartedAt = "2026-01-01T00:00:00.0000000Z";
    private const string EndedAt = "2026-01-01T01:00:00.0000000Z";
    private const string TimeZoneId = "Asia/Shanghai";

    [Fact]
    public void Migrate_CreatesAllV2Tables_AndReachesLatestVersion()
    {
        using var temp = new TempDirectory();
        using var connection = CreateMigratedConnection(temp);

        Assert.Equal(3, SchemaMigrator.ReadUserVersion(connection));

        var tables = ReadNames(connection, "table");
        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "SchemaInfo",
                "HotspotProfiles",
                "HotspotProfileBssids",
                "ConnectionSessions",
                "SessionRunMappings",
                "TrafficSnapshots",
                "DailyTraffic",
                "RecoveryGaps",
                "AppSettings",
                "TrafficOperations",
            },
            tables);
    }

    [Fact]
    public void Migrate_FromV1_UpgradesSchema_AndKeepsMetadata()
    {
        using var temp = new TempDirectory();
        var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));
        using var connection = factory.Create();

        var v1 = new SchemaMigrator(new[] { SchemaMigrator.DefaultMigrations[0] });
        v1.Migrate(connection);
        Assert.Equal(1, SchemaMigrator.ReadUserVersion(connection));

        var version = new SchemaMigrator().Migrate(connection);

        Assert.Equal(3, version);
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM SchemaInfo WHERE Key = 'created_at_utc';"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'TrafficSnapshots';"));
    }

    [Fact]
    public void HotspotProfiles_RejectsNonHexProfileKey_AndAcceptsNFormat()
    {
        using var temp = new TempDirectory();
        using var connection = CreateMigratedConnection(temp);

        var id = InsertProfile(connection, "0123456789abcdef0123456789abcdef");
        Assert.True(id > 0);

        Assert.Throws<SqliteException>(() =>
            InsertProfile(connection, "0123456789abcdef0123456789abcdeg"));
        Assert.Throws<SqliteException>(() =>
            InsertProfile(connection, "0123-4567-89ab-cdef-0123-456789abcdef"));
    }

    [Fact]
    public void Bssids_OnlyConfirmedAddressIsGloballyUnique()
    {
        using var temp = new TempDirectory();
        using var connection = CreateMigratedConnection(temp);
        var profileA = InsertProfile(connection, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var profileB = InsertProfile(connection, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        // 未确认：两个 Profile 可以同时观察同一个 BSSID（换 Profile / 同名设备过渡期）。
        InsertBssid(connection, profileA, "02:00:00:00:00:01", confirmed: false);
        InsertBssid(connection, profileB, "02:00:00:00:00:01", confirmed: false);

        // 确认后：全局唯一，第二个 Profile 不能再确认同一个 BSSID。
        InsertBssid(connection, profileA, "1A:44:DD:0E:E5:B1", confirmed: true);
        Assert.Throws<SqliteException>(() =>
            InsertBssid(connection, profileB, "1A:44:DD:0E:E5:B1", confirmed: true));
    }

    [Fact]
    public void Sessions_EnforceThreeStateCheck()
    {
        using var temp = new TempDirectory();
        using var connection = CreateMigratedConnection(temp);
        var profileId = InsertProfile(connection, "cccccccccccccccccccccccccccccccc");

        var active = InsertSession(connection, profileId);
        Assert.True(active > 0);

        // Active 携带结束字段：非法。
        Assert.Throws<SqliteException>(() =>
            InsertSession(connection, profileId, status: 0, endedAt: EndedAt, endReason: 0));

        // Closed 缺少结束时间/原因：非法。
        Assert.Throws<SqliteException>(() =>
            InsertSession(connection, profileId, status: 1));

        // 恢复结束：RecoveryRejected(5) + 具体结局(1) 合法。
        InsertSession(connection, profileId, status: 2, endedAt: EndedAt, endReason: 5, resumeOutcome: 1);

        // 恢复结束：RecoveryRejected(5) 不允许搭配 Continued(0)。
        Assert.Throws<SqliteException>(() =>
            InsertSession(connection, profileId, status: 2, endedAt: EndedAt, endReason: 5, resumeOutcome: 0));

        // Unverified(4) 必须搭配 Continued(0)。
        InsertSession(connection, profileId, status: 2, endedAt: EndedAt, endReason: 4, resumeOutcome: 0);
        Assert.Throws<SqliteException>(() =>
            InsertSession(connection, profileId, status: 2, endedAt: EndedAt, endReason: 4, resumeOutcome: 2));
    }

    [Fact]
    public void Sessions_RejectEndBeforeStart_AndHalfBaseline()
    {
        using var temp = new TempDirectory();
        using var connection = CreateMigratedConnection(temp);
        var profileId = InsertProfile(connection, "dddddddddddddddddddddddddddddddd");

        Assert.Throws<SqliteException>(() =>
            InsertSession(connection, profileId, status: 1, endedAt: "2025-12-31T23:59:59.0000000Z", endReason: 0));

        Assert.Throws<SqliteException>(() => Execute(
            connection,
            """
            INSERT INTO ConnectionSessions
                (ProfileId, SessionStatus, InterfaceGuid, Ssid, StartedAtUtc, CreatedAtUtc, LastBaselineCapturedAtUtc)
            VALUES ($pid, 0, $guid, 'TestHotspot', $started, $started, $started);
            """,
            ("$pid", profileId),
            ("$guid", InterfaceGuid),
            ("$started", StartedAt)));
    }

    [Fact]
    public void Snapshots_RejectProfileMismatchThroughCompositeForeignKey()
    {
        using var temp = new TempDirectory();
        using var connection = CreateMigratedConnection(temp);
        var profileA = InsertProfile(connection, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var profileB = InsertProfile(connection, "ffffffffffffffffffffffffffffffff");
        var session = InsertSession(connection, profileA);

        InsertSnapshot(connection, profileA, session, operationId: "op-alpha-01");

        Assert.Throws<SqliteException>(() =>
            InsertSnapshot(connection, profileB, session, operationId: "op-beta-001"));
    }

    [Fact]
    public void Snapshots_RejectInvalidLocalDate_AndDuplicateOperationSegment()
    {
        using var temp = new TempDirectory();
        using var connection = CreateMigratedConnection(temp);
        var profileId = InsertProfile(connection, "11111111111111111111111111111111");
        var session = InsertSession(connection, profileId);

        Assert.Throws<SqliteException>(() =>
            InsertSnapshot(connection, profileId, session, operationId: "op-bad-date", localDate: "2026-02-30"));

        InsertSnapshot(connection, profileId, session, operationId: "op-dup-0001", segmentIndex: 0);
        Assert.Throws<SqliteException>(() =>
            InsertSnapshot(connection, profileId, session, operationId: "op-dup-0001", segmentIndex: 0));
        InsertSnapshot(connection, profileId, session, operationId: "op-dup-0001", segmentIndex: 1);
    }

    [Fact]
    public void Snapshots_FinalKindMustMatchIsFinalFlag()
    {
        using var temp = new TempDirectory();
        using var connection = CreateMigratedConnection(temp);
        var profileId = InsertProfile(connection, "22222222222222222222222222222222");
        var session = InsertSession(connection, profileId);

        Assert.Throws<SqliteException>(() =>
            InsertSnapshot(connection, profileId, session, operationId: "op-final-01", kind: 2, isFinal: 0));
        Assert.Throws<SqliteException>(() =>
            InsertSnapshot(connection, profileId, session, operationId: "op-notfin-01", kind: 1, isFinal: 1));

        InsertSnapshot(connection, profileId, session, operationId: "op-ok-00001", kind: 2, isFinal: 1);
    }

    [Fact]
    public void DailyTraffic_RequiresFirstAndLastSessionTogether()
    {
        using var temp = new TempDirectory();
        using var connection = CreateMigratedConnection(temp);
        var profileId = InsertProfile(connection, "33333333333333333333333333333333");

        Assert.Throws<SqliteException>(() => Execute(
            connection,
            """
            INSERT INTO DailyTraffic (LocalDate, ProfileId, UpdatedAtUtc, FirstSessionAtUtc)
            VALUES ('2026-01-01', $pid, $now, $now);
            """,
            ("$pid", profileId),
            ("$now", StartedAt)));

        Execute(
            connection,
            """
            INSERT INTO DailyTraffic (LocalDate, ProfileId, UpdatedAtUtc, FirstSessionAtUtc, LastSessionAtUtc)
            VALUES ('2026-01-01', $pid, $now, $now, $now);
            """,
            ("$pid", profileId),
            ("$now", StartedAt));
    }

    [Fact]
    public void RecoveryGaps_RejectInvalidShapes()
    {
        using var temp = new TempDirectory();
        using var connection = CreateMigratedConnection(temp);
        var profileId = InsertProfile(connection, "44444444444444444444444444444444");
        var oldSession = InsertSession(connection, profileId);
        var newSession = InsertSession(connection, profileId);

        Assert.True(newSession > oldSession);

        InsertRecoveryGap(connection, profileId, oldSession, newSession, continuity: 1);

        // 候选只可能来自 Unverified/Interrupted；Continuous 直接并入，不会成为候选。
        Assert.Throws<SqliteException>(() =>
            InsertRecoveryGap(connection, profileId, oldSession, newSession, continuity: 0, gapId: "gap-2"));

        // 不允许把候选挂回旧会话本身。
        Assert.Throws<SqliteException>(() =>
            InsertRecoveryGap(connection, profileId, oldSession, oldSession, continuity: 1, gapId: "gap-3"));

        // Pending 不能带合并来源。
        Assert.Throws<SqliteException>(() =>
            InsertRecoveryGap(connection, profileId, oldSession, newSession, continuity: 1, gapId: "gap-4", mergeSource: 1));
    }

    [Fact]
    public void SessionRunMappings_EnforcePairUniqueness_AndSingleActive()
    {
        using var temp = new TempDirectory();
        using var connection = CreateMigratedConnection(temp);
        var profileId = InsertProfile(connection, "55555555555555555555555555555555");
        var session = InsertSession(connection, profileId);

        InsertMapping(connection, session, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1, status: 1);

        // 同一 RunId + CoreSessionId 只能有一行。
        Assert.Throws<SqliteException>(() =>
            InsertMapping(connection, session, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1, status: 1));

        // 同一 DB 会话只能有一个 Active 映射。
        Assert.Throws<SqliteException>(() =>
            InsertMapping(connection, session, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 2, status: 1));

        // 历史映射（Closed）可以同时存在。
        InsertMapping(connection, session, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 2, status: 2);
    }

    [Fact]
    public void ChildRows_RejectUnknownProfile()
    {
        using var temp = new TempDirectory();
        using var connection = CreateMigratedConnection(temp);

        Assert.Throws<SqliteException>(() =>
            InsertBssid(connection, 9999, "02:00:00:00:00:01", confirmed: false));
    }

    private static SqliteConnection CreateMigratedConnection(TempDirectory temp)
    {
        var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));
        var connection = factory.Create();
        new SchemaMigrator().Migrate(connection);
        return connection;
    }

    private static long InsertProfile(SqliteConnection connection, string profileKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO HotspotProfiles
                (ProfileKey, DisplayName, InterfaceGuid, Ssid, CreatedAtUtc, LastSeenUtc)
            VALUES ($key, '手机热点', $guid, 'TestHotspot', $now, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$key", profileKey);
        command.Parameters.AddWithValue("$guid", InterfaceGuid);
        command.Parameters.AddWithValue("$now", StartedAt);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void InsertBssid(SqliteConnection connection, long profileId, string bssid, bool confirmed)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO HotspotProfileBssids
                (ProfileId, Bssid, IsConfirmed, Source, FirstSeenUtc, LastSeenUtc)
            VALUES ($pid, $bssid, $confirmed, $source, $now, $now);
            """;
        command.Parameters.AddWithValue("$pid", profileId);
        command.Parameters.AddWithValue("$bssid", bssid);
        command.Parameters.AddWithValue("$confirmed", confirmed ? 1 : 0);
        command.Parameters.AddWithValue("$source", confirmed ? 1 : 0);
        command.Parameters.AddWithValue("$now", StartedAt);
        command.ExecuteNonQuery();
    }

    private static long InsertSession(
        SqliteConnection connection,
        long profileId,
        int status = 0,
        string? endedAt = null,
        int? endReason = null,
        int? resumeOutcome = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ConnectionSessions
                (ProfileId, SessionStatus, InterfaceGuid, Ssid, StartedAtUtc, CreatedAtUtc,
                 EndedAtUtc, EndReason, ResumeOutcome)
            VALUES ($pid, $status, $guid, 'TestHotspot', $started, $started, $ended, $reason, $outcome);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$pid", profileId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$guid", InterfaceGuid);
        command.Parameters.AddWithValue("$started", StartedAt);
        command.Parameters.AddWithValue("$ended", (object?)endedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", (object?)endReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", (object?)resumeOutcome ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void InsertSnapshot(
        SqliteConnection connection,
        long profileId,
        long sessionId,
        string operationId,
        int segmentIndex = 0,
        int kind = 1,
        int isFinal = 0,
        string localDate = "2026-01-01")
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TrafficSnapshots
                (OperationId, OperationSequence, SegmentIndex, ProfileId, SessionId, Kind, IsFinal,
                 FromUtc, ToUtc, CapturedAtUtc, LocalDate, TimeZoneId,
                 DownloadBytes, UploadBytes, CreatedAtUtc)
            VALUES ($op, 1, $segment, $pid, $sid, $kind, $final,
                    $started, $ended, $ended, $date, $tz, 1024, 512, $ended);
            """;
        command.Parameters.AddWithValue("$op", operationId);
        command.Parameters.AddWithValue("$segment", segmentIndex);
        command.Parameters.AddWithValue("$pid", profileId);
        command.Parameters.AddWithValue("$sid", sessionId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$final", isFinal);
        command.Parameters.AddWithValue("$started", StartedAt);
        command.Parameters.AddWithValue("$ended", EndedAt);
        command.Parameters.AddWithValue("$date", localDate);
        command.Parameters.AddWithValue("$tz", TimeZoneId);
        command.ExecuteNonQuery();
    }

    private static void InsertRecoveryGap(
        SqliteConnection connection,
        long profileId,
        long oldSessionId,
        long newSessionId,
        int continuity,
        string gapId = "gap-1",
        int mergeSource = 0)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO RecoveryGaps
                (GapId, ProfileId, OldSessionId, NewSessionId, FromUtc, ToUtc, CapturedAtUtc,
                 DownloadBytes, UploadBytes, Continuity, CandidateStatus, MergeSource,
                 FirstSeenUtc, UpdatedAtUtc)
            VALUES ($gap, $pid, $old, $new, $started, $ended, $ended, 100, 50, $continuity, 0, $merge, $ended, $ended);
            """;
        command.Parameters.AddWithValue("$gap", gapId);
        command.Parameters.AddWithValue("$pid", profileId);
        command.Parameters.AddWithValue("$old", oldSessionId);
        command.Parameters.AddWithValue("$new", newSessionId);
        command.Parameters.AddWithValue("$started", StartedAt);
        command.Parameters.AddWithValue("$ended", EndedAt);
        command.Parameters.AddWithValue("$continuity", continuity);
        command.Parameters.AddWithValue("$merge", mergeSource);
        command.ExecuteNonQuery();
    }

    private static void InsertMapping(
        SqliteConnection connection,
        long sessionId,
        string runId,
        long coreSessionId,
        int status)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO SessionRunMappings
                (SessionId, RunId, CoreSessionId, AttachedAtUtc, DetachedAtUtc, MappingStatus)
            VALUES ($sid, $run, $core, $started, $detached, $status);
            """;
        command.Parameters.AddWithValue("$sid", sessionId);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$core", coreSessionId);
        command.Parameters.AddWithValue("$started", StartedAt);
        command.Parameters.AddWithValue("$detached", status == 1 ? (object)DBNull.Value : EndedAt);
        command.Parameters.AddWithValue("$status", status);
        command.ExecuteNonQuery();
    }

    private static HashSet<string> ReadNames(SqliteConnection connection, string type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type;";
        command.Parameters.AddWithValue("$type", type);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static int Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}