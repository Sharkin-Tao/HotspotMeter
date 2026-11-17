using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Integration.Persistence;

/// <summary>
/// Schema v3（TrafficOperations）的迁移与约束测试。
///
/// 时间格式约束不是"看起来对"就行：SQLite 3.53.3 会接受 24:00:00，也会让 29:00:00 的
/// strftime 返回 NULL 从而使 CHECK 放行。这些用例把每一个已知漏洞都钉在真实数据库上，
/// 未来升级 SQLite provider 时若语义变化，测试会先失败。
/// </summary>
public sealed class SchemaV3Tests : IDisposable
{
    private const string ValidFrom = "2026-11-13T18:43:53.1234567+00:00";
    private const string ValidTo = "9999-12-31T23:59:59.9999999+00:00";
    private const string ValidApplied = "2026-11-13T18:43:53.1234567+00:00";

    private readonly TempDirectory _directory = new("schema-v3");

    public void Dispose() => _directory.Dispose();

    private SqliteConnectionFactory Factory() => new(_directory.File("hotspotmeter.db"));

    [Fact]
    public void Migrate_FreshDatabase_ReachesSupportedVersion()
    {
        using var connection = Factory().Create();

        var version = new SchemaMigrator().Migrate(connection);

        Assert.Equal(SchemaMigrator.SupportedVersion, version);
        Assert.Equal(SchemaMigrator.SupportedVersion, SchemaMigrator.ReadUserVersion(connection));
        Assert.True(version >= 3);
        Assert.True(TableExists(connection, "TrafficOperations"));
        Assert.True(IndexExists(connection, "IX_Operations_Session_FromUtc"));
        Assert.True(TableExists(connection, "TrafficSnapshots"));
    }

    [Fact]
    public void Migrate_FromV2_PreservesExistingRows()
    {
        var factory = Factory();
        using var connection = factory.Create();
        var v2Only = new SchemaMigrator(SchemaMigrator.DefaultMigrations.Take(2).ToList());
        Assert.Equal(2, v2Only.Migrate(connection));

        SeedProfileAndSession(connection);

        var version = new SchemaMigrator().Migrate(connection);

        Assert.Equal(SchemaMigrator.SupportedVersion, version);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM HotspotProfiles;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM ConnectionSessions;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM TrafficOperations;"));
    }

    [Fact]
    public void Migrate_RejectsDatabaseNewerThanSupported()
    {
        using var connection = Factory().Create();
        Execute(connection, "PRAGMA user_version = 99;");

        var exception = Assert.Throws<DatabaseVersionTooNewException>(() => new SchemaMigrator().Migrate(connection));
        Assert.Equal(99, exception.CurrentVersion);
        Assert.Equal(SchemaMigrator.SupportedVersion, exception.SupportedVersion);
    }

    [Fact]
    public void Insert_ValidOperation_RoundTripsValues()
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);
        SeedProfileAndSession(connection);

        InsertOperation(connection, ValidFrom);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT OperationType, FromUtc, SegmentCount, RequestDigest FROM TrafficOperations;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(ValidFrom, reader.GetString(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal(new string('a', 64), reader.GetString(3));
    }

    [Theory]
    [InlineData("2026-11-13T18:43:53.1234567+00:00")]
    [InlineData("2026-11-13T23:59:59.9999999+00:00")]
    [InlineData("2024-02-29T00:00:00.0000000+00:00")]
    [InlineData("9999-12-31T23:59:59.9999999+00:00")]
    public void Insert_AcceptsCanonicalTimestamps(string fromUtc)
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);
        SeedProfileAndSession(connection);

        InsertOperation(connection, fromUtc);

        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM TrafficOperations;"));
    }

    [Theory]
    [InlineData("2026-11-13T24:00:00.0000000+00:00")] // 24:00 被 SQLite 视为合法，必须靠小时位拒绝
    [InlineData("2026-11-13T29:00:00.0000000+00:00")] // strftime 返回 NULL，CHECK 会放行，必须靠 IS NOT NULL
    [InlineData("2026-11-13T25:00:00.0000000+00:00")]
    [InlineData("2026-11-13T18:60:00.0000000+00:00")]
    [InlineData("2026-02-30T00:00:00.0000000+00:00")]
    [InlineData("2025-02-29T00:00:00.0000000+00:00")]
    [InlineData("2026-13-01T00:00:00.0000000+00:00")]
    [InlineData("2026-11-32T00:00:00.0000000+00:00")]
    [InlineData("2026-11-13T18:43:53.1234567+08:00")]
    [InlineData("2026-11-13T18:43:53.123456+00:00")]
    [InlineData("2026-11-13T18:43:53.12345678+00:00")]
    [InlineData("2026-11-13 18:43:53.1234567+00:00")]
    [InlineData("0000-01-01T00:00:00.0000000+00:00")]
    [InlineData("2026-11-13T18:43:53.1234567+00:00 ")]
    public void Insert_RejectsNonCanonicalTimestamps(string fromUtc)
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);
        SeedProfileAndSession(connection);

        var exception = Assert.Throws<SqliteException>(() => InsertOperation(connection, fromUtc));
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM TrafficOperations;"));
    }

    [Fact]
    public void Insert_RejectsDuplicateOperationId()
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);
        SeedProfileAndSession(connection);

        InsertOperation(connection, ValidFrom);

        var exception = Assert.Throws<SqliteException>(() => InsertOperation(connection, ValidFrom, sequence: 2));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void Insert_RejectsDuplicateSessionSequence()
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);
        SeedProfileAndSession(connection);

        InsertOperation(connection, ValidFrom, operationId: "op:1:1:0:1");

        var exception = Assert.Throws<SqliteException>(() => InsertOperation(connection, ValidFrom, operationId: "op:1:1:0:2"));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(401)]
    public void Insert_RejectsInvalidSegmentCount(int segmentCount)
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);
        SeedProfileAndSession(connection);

        var exception = Assert.Throws<SqliteException>(() => InsertOperation(connection, ValidFrom, segmentCount: segmentCount));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void Insert_RejectsUppercaseOrShortDigest()
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);
        SeedProfileAndSession(connection);

        Assert.Throws<SqliteException>(() => InsertOperation(connection, ValidFrom, requestDigest: new string('A', 64)));
        Assert.Throws<SqliteException>(() => InsertOperation(connection, ValidFrom, requestDigest: new string('a', 63)));
    }

    [Fact]
    public void Insert_RejectsUnknownSession()
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);
        SeedProfileAndSession(connection);

        var exception = Assert.Throws<SqliteException>(() => InsertOperation(connection, ValidFrom, sessionId: 999));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void Insert_RejectsFromAfterTo()
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);
        SeedProfileAndSession(connection);

        var exception = Assert.Throws<SqliteException>(
            () => InsertOperation(connection, ValidFrom, toUtc: "2026-11-13T00:00:00.0000000+00:00", capturedAtUtc: "2026-11-13T00:00:00.0000000+00:00"));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void Insert_RejectsCapturedBeforeTo()
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);
        SeedProfileAndSession(connection);

        var exception = Assert.Throws<SqliteException>(
            () => InsertOperation(connection, ValidFrom, toUtc: "2026-11-14T00:00:00.0000000+00:00", capturedAtUtc: "2026-11-13T23:00:00.0000000+00:00"));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    private static void SeedProfileAndSession(SqliteConnection connection)
    {
        Execute(
            connection,
            """
            INSERT INTO HotspotProfiles (ProfileKey, DisplayName, InterfaceGuid, Ssid, BssidEnforcement, CreatedAtUtc, LastSeenUtc)
            VALUES ('11111111111111111111111111111111', 'TestHotspot', '11111111-1111-1111-1111-111111111111', 'TestHotspot', 0,
                    '2026-11-13T00:00:00.0000000+00:00', '2026-11-13T00:00:00.0000000+00:00');
            """);

        Execute(
            connection,
            """
            INSERT INTO ConnectionSessions (ProfileId, InterfaceGuid, Ssid, StartedAtUtc, CreatedAtUtc)
            VALUES (1, '11111111-1111-1111-1111-111111111111', 'TestHotspot',
                    '2026-11-13T00:00:00.0000000+00:00', '2026-11-13T00:00:00.0000000+00:00');
            """);
    }

    private static void InsertOperation(
        SqliteConnection connection,
        string fromUtc,
        string operationId = "op:1:1:0:0",
        long sequence = 1,
        int operationType = 1,
        long sessionId = 1,
        long profileId = 1,
        string? toUtc = null,
        string? capturedAtUtc = null,
        int segmentCount = 1,
        string? requestDigest = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TrafficOperations (
                OperationId, OperationSequence, OperationType, DbSessionId, ProfileId,
                FromUtc, ToUtc, CapturedAtUtc, AppliedAtUtc,
                DownloadBytesAfter, UploadBytesAfter, ConnectedMilliseconds,
                SegmentCount, TimeZoneId, QualityFlags, RequestDigest, PayloadDigest)
            VALUES (
                $opid, $seq, $type, $session, $profile,
                $from, $to, $captured, $applied,
                $down, $up, $ms,
                $segments, 'Asia/Shanghai', 0, $req, $payload);
            """;
        command.Parameters.AddWithValue("$opid", operationId);
        command.Parameters.AddWithValue("$seq", sequence);
        command.Parameters.AddWithValue("$type", operationType);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$from", fromUtc);
        command.Parameters.AddWithValue("$to", toUtc ?? ValidTo);
        command.Parameters.AddWithValue("$captured", capturedAtUtc ?? ValidTo);
        command.Parameters.AddWithValue("$applied", ValidApplied);
        command.Parameters.AddWithValue("$down", 1_000);
        command.Parameters.AddWithValue("$up", 100);
        command.Parameters.AddWithValue("$ms", 600_000);
        command.Parameters.AddWithValue("$segments", segmentCount);
        command.Parameters.AddWithValue("$req", requestDigest ?? new string('a', 64));
        command.Parameters.AddWithValue("$payload", new string('b', 64));
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string name) =>
        Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;", ("$name", name)) == 1;

    private static bool IndexExists(SqliteConnection connection, string name) =>
        Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;", ("$name", name)) == 1;

    private static long Scalar(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}