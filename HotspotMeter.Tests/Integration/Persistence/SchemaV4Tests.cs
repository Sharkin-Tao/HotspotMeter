using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Integration.Persistence;

public sealed class SchemaV4Tests : IDisposable
{
    private const string From = "2026-11-17T10:00:00.0000000+00:00";
    private const string To = "2026-11-17T10:00:30.0000000+00:00";
    private const string ProfileKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AppKey = @"exe:c:\apps\demo.exe";

    private readonly TempDirectory _directory = new("schema-v4");

    public void Dispose() => _directory.Dispose();

    private SqliteConnectionFactory Factory() => new(_directory.File("hotspotmeter.db"));

    [Fact]
    public void Migrate_FromV3_PreservesExistingHistory_AndAddsGuardTables()
    {
        using var connection = Factory().Create();
        var v3 = new SchemaMigrator(SchemaMigrator.DefaultMigrations.Take(3).ToList());
        Assert.Equal(3, v3.Migrate(connection));

        Execute(connection,
            """
            INSERT INTO HotspotProfiles (ProfileKey, DisplayName, InterfaceGuid, Ssid, BssidEnforcement, CreatedAtUtc, LastSeenUtc)
            VALUES ('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'TestHotspot',
                    '11111111-1111-1111-1111-111111111111', 'TestHotspot', 0,
                    '2026-11-17T00:00:00.0000000+00:00', '2026-11-17T00:00:00.0000000+00:00');
            """);

        var version = new SchemaMigrator().Migrate(connection);

        Assert.Equal(SchemaMigrator.SupportedVersion, version);
        Assert.Equal(SchemaMigrator.SupportedVersion, SchemaMigrator.ReadUserVersion(connection));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM HotspotProfiles;"));
        foreach (var table in new[] { "GuardSettings", "GuardAppRules", "GuardTrafficSamples", "GuardAlertHistory" })
        {
            Assert.True(TableExists(connection, table), table);
        }

        Assert.True(IndexExists(connection, "IX_GuardSamples_Profile_ToUtc"));
        Assert.True(IndexExists(connection, "IX_GuardSamples_AppKey_ToUtc"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM GuardSettings WHERE Id = 1 AND Enabled = 1;"));
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        using var connection = Factory().Create();
        Assert.Equal(SchemaMigrator.SupportedVersion, new SchemaMigrator().Migrate(connection));
        Assert.Equal(SchemaMigrator.SupportedVersion, new SchemaMigrator().Migrate(connection));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM GuardSettings;"));
    }

    [Fact]
    public void FailedV4Migration_RollsBackCompletely_AndKeepsV3History()
    {
        using var connection = Factory().Create();
        var v3 = new SchemaMigrator(SchemaMigrator.DefaultMigrations.Take(3).ToList());
        Assert.Equal(3, v3.Migrate(connection));
        Execute(connection,
            """
            INSERT INTO HotspotProfiles (ProfileKey, DisplayName, InterfaceGuid, Ssid, BssidEnforcement, CreatedAtUtc, LastSeenUtc)
            VALUES ('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'TestHotspot',
                    '11111111-1111-1111-1111-111111111111', 'TestHotspot', 0,
                    '2026-11-17T00:00:00.0000000+00:00', '2026-11-17T00:00:00.0000000+00:00');
            """);

        var broken = SchemaMigrator.DefaultMigrations.Take(3)
            .Append(new DatabaseMigration(
                4,
                "broken-v4",
                new[]
                {
                    "CREATE TABLE GuardBroken (Id INTEGER PRIMARY KEY);",
                    "THIS IS NOT VALID SQL;",
                }))
            .ToList();

        Assert.Throws<SqliteException>(() => new SchemaMigrator(broken).Migrate(connection));

        Assert.Equal(3, SchemaMigrator.ReadUserVersion(connection));
        Assert.False(TableExists(connection, "GuardBroken"));
        Assert.False(TableExists(connection, "GuardSettings"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM HotspotProfiles;"));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(301)]
    public void GuardSettings_RejectsInvalidSamplingInterval(int interval)
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);

        var exception = Assert.Throws<SqliteException>(() =>
            Execute(connection, $"UPDATE GuardSettings SET SamplingIntervalSeconds = {interval} WHERE Id = 1;"));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void Samples_RejectZeroByteAndDuplicateAndMissingRule()
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);
        InsertRule(connection);

        Assert.Throws<SqliteException>(() => InsertSample(connection, download: 0, upload: 0));
        InsertSample(connection, download: 10, upload: 2);
        Assert.Throws<SqliteException>(() => InsertSample(connection, download: 10, upload: 2));
        Assert.Throws<SqliteException>(() => InsertSample(connection, appKey: @"exe:c:\apps\other.exe"));
    }

    [Fact]
    public void Samples_RejectProfileKeyThatIsNot32LowercaseHex()
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);
        InsertRule(connection);

        Assert.Throws<SqliteException>(() => InsertSample(connection, profileKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        Assert.Throws<SqliteException>(() => InsertSample(connection, profileKey: "abc"));
    }

    [Fact]
    public void UncertainRule_MustBeMarkedUncertain()
    {
        using var connection = Factory().Create();
        new SchemaMigrator().Migrate(connection);

        var exception = Assert.Throws<SqliteException>(() => Execute(connection,
            """
            INSERT INTO GuardAppRules (AppKey, DisplayName, IdentityKind, IdentityCertain, FirstSeenAtUtc, LastSeenAtUtc)
            VALUES ('uncertain:1:1:x', 'x', 3, 1, '2026-11-17T10:00:00.0000000+00:00', '2026-11-17T10:00:00.0000000+00:00');
            """));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    private static void InsertRule(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO GuardAppRules (AppKey, DisplayName, IdentityKind, IdentityCertain, FirstSeenAtUtc, LastSeenAtUtc)
            VALUES ($key, 'demo.exe', 1, 1, $from, $to);
            """;
        command.Parameters.AddWithValue("$key", AppKey);
        command.Parameters.AddWithValue("$from", From);
        command.Parameters.AddWithValue("$to", To);
        command.ExecuteNonQuery();
    }

    private static void InsertSample(
        SqliteConnection connection,
        long download = 100,
        long upload = 20,
        string appKey = AppKey,
        string profileKey = ProfileKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO GuardTrafficSamples
                (AppKey, ProfileKey, FromUtc, ToUtc, CapturedAtUtc,
                 DownloadBytes, UploadBytes, ActivityState, ProcessCount, QualityFlags)
            VALUES ($key, $profile, $from, $to, $to, $download, $upload, 0, 1, 0);
            """;
        command.Parameters.AddWithValue("$key", appKey);
        command.Parameters.AddWithValue("$profile", profileKey);
        command.Parameters.AddWithValue("$from", From);
        command.Parameters.AddWithValue("$to", To);
        command.Parameters.AddWithValue("$download", download);
        command.Parameters.AddWithValue("$upload", upload);
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string name) =>
        Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;", ("$name", name)) == 1;

    private static bool IndexExists(SqliteConnection connection, string name) =>
        Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$name;", ("$name", name)) == 1;

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
