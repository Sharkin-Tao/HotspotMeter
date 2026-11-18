using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Integration.Persistence;

public sealed class SchemaV6Tests : IDisposable
{
    private const string Created = "2026-11-17T10:00:00.0000000+00:00";
    private readonly TempDirectory _directory = new("schema-v6");

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Migrate_FromV5_PreservesHistory_AndAddsManagementColumns()
    {
        using var connection = new SqliteConnectionFactory(_directory.File("hotspotmeter.db")).Create();
        var v5 = new SchemaMigrator(SchemaMigrator.DefaultMigrations.Take(5).ToList());
        Assert.Equal(5, v5.Migrate(connection));

        Execute(connection,
            """
            INSERT INTO GuardAppRules (AppKey, DisplayName, IdentityKind, IdentityCertain, TrustState, FirstSeenAtUtc, LastSeenAtUtc)
            VALUES ('exe:c:\apps\demo.exe', 'demo.exe', 1, 1, 0, '2026-11-17T00:00:00.0000000+00:00', '2026-11-17T00:00:00.0000000+00:00');
            INSERT INTO GuardAlertHistory (AppKey, ProfileKey, AlertType, ThresholdBytes, ActualBytes, CreatedAtUtc, PeriodKind, NotificationStatus)
            VALUES ('exe:c:\apps\demo.exe', NULL, 1, 100, 200, '2026-11-17T10:00:00.0000000+00:00', 1, 1);
            """);

        Assert.Equal(6, new SchemaMigrator(SchemaMigrator.DefaultMigrations.Take(6).ToList()).Migrate(connection));

        // 旧记录保留，新列取默认值。
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM GuardAppRules;"));
        Assert.Equal(200L, Scalar(connection, "SELECT ActualBytes FROM GuardAlertHistory;"));
        Assert.Equal(2L, Scalar(connection, "SELECT MetricKind FROM GuardAlertHistory;"));
        Assert.Equal(0L, Scalar(connection, "SELECT ThresholdSource FROM GuardAlertHistory;"));
        Assert.Equal(0L, Scalar(connection, "SELECT ActivityState FROM GuardAlertHistory;"));
        Assert.Equal(0L, Scalar(connection, "SELECT HandlingAction FROM GuardAlertHistory;"));
        Assert.True(ColumnExists(connection, "GuardAppRules", "ExecutablePath"));
        Assert.True(IndexExists(connection, "IX_GuardAlerts_CreatedAt"));
        Assert.True(IndexExists(connection, "IX_GuardAlerts_Notification"));
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        using var connection = new SqliteConnectionFactory(_directory.File("hotspotmeter.db")).Create();
        Assert.Equal(SchemaMigrator.SupportedVersion, new SchemaMigrator().Migrate(connection));
        Assert.Equal(SchemaMigrator.SupportedVersion, new SchemaMigrator().Migrate(connection));
        Assert.Equal(SchemaMigrator.SupportedVersion, SchemaMigrator.ReadUserVersion(connection));
    }

    [Fact]
    public void FailedV6Migration_RollsBack_AndKeepsV5Data()
    {
        using var connection = new SqliteConnectionFactory(_directory.File("hotspotmeter.db")).Create();
        Assert.Equal(5, new SchemaMigrator(SchemaMigrator.DefaultMigrations.Take(5).ToList()).Migrate(connection));
        Execute(connection,
            """
            INSERT INTO GuardAppRules (AppKey, DisplayName, IdentityKind, IdentityCertain, TrustState, FirstSeenAtUtc, LastSeenAtUtc)
            VALUES ('exe:c:\apps\demo.exe', 'demo.exe', 1, 1, 0, '2026-11-17T00:00:00.0000000+00:00', '2026-11-17T00:00:00.0000000+00:00');
            """);

        var broken = SchemaMigrator.DefaultMigrations.Take(5)
            .Append(new DatabaseMigration(
                6,
                "broken-v6",
                new[]
                {
                    "ALTER TABLE GuardAppRules ADD COLUMN ExecutablePath TEXT NULL;",
                    "THIS IS NOT VALID SQL;",
                }))
            .ToList();

        Assert.Throws<SqliteException>(() => new SchemaMigrator(broken).Migrate(connection));

        Assert.Equal(5, SchemaMigrator.ReadUserVersion(connection));
        Assert.False(ColumnExists(connection, "GuardAppRules", "ExecutablePath"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM GuardAppRules;"));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static bool TableExists(SqliteConnection connection, string table) =>
        Scalar(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';") == 1;

    private static bool ColumnExists(SqliteConnection connection, string table, string column) =>
        Scalar(connection,
            $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';") == 1;

    private static bool IndexExists(SqliteConnection connection, string index) =>
        Scalar(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = '{index}';") == 1;
}