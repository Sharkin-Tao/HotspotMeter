using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Integration.Persistence;

public sealed class SchemaV5Tests : IDisposable
{
    private const string Created = "2026-11-17T10:00:00.0000000+00:00";
    private readonly TempDirectory _directory = new("schema-v5");

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Migrate_FromV4_PreservesRulesAndAlerts_AndAddsAlertColumns()
    {
        using var connection = new SqliteConnectionFactory(_directory.File("hotspotmeter.db")).Create();
        Assert.Equal(4, new SchemaMigrator(SchemaMigrator.DefaultMigrations.Take(4).ToList()).Migrate(connection));
        Execute(connection,
            """
            UPDATE GuardSettings
            SET Enabled = 0, SamplingIntervalSeconds = 45, DataRetentionDays = 14,
                UpdatedAtUtc = '2026-11-17T00:00:00.0000000+00:00'
            WHERE Id = 1;
            INSERT INTO GuardAppRules (AppKey, DisplayName, IdentityKind, IdentityCertain, TrustState, FirstSeenAtUtc, LastSeenAtUtc)
            VALUES ('exe:c:\apps\demo.exe', 'demo.exe', 1, 1, 0,
                    '2026-11-17T00:00:00.0000000+00:00', '2026-11-17T00:00:00.0000000+00:00');
            INSERT INTO GuardAlertHistory (AppKey, ProfileKey, AlertType, ThresholdBytes, ActualBytes, CreatedAtUtc)
            VALUES ('exe:c:\apps\demo.exe', NULL, 1, 100, 200, '2026-11-17T10:00:00.0000000+00:00');
            """);

        Assert.Equal(5, new SchemaMigrator().Migrate(connection));

        Assert.Equal(0L, Scalar(connection, "SELECT Enabled FROM GuardSettings WHERE Id = 1;"));
        Assert.Equal(45L, Scalar(connection, "SELECT SamplingIntervalSeconds FROM GuardSettings WHERE Id = 1;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM GuardAppRules;"));
        Assert.Equal(200L, Scalar(connection, "SELECT ActualBytes FROM GuardAlertHistory;"));
        Assert.Equal(0L, Scalar(connection, "SELECT NotificationStatus FROM GuardAlertHistory;"));
        Assert.Equal(1L, Scalar(connection, "SELECT AlertsEnabled FROM GuardSettings WHERE Id = 1;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM GuardSettings WHERE DefaultThresholdBytes = 104857600;"));
        Assert.True(IndexExists(connection, "IX_GuardAlerts_App_Period"));
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        using var connection = new SqliteConnectionFactory(_directory.File("hotspotmeter.db")).Create();
        Assert.Equal(5, new SchemaMigrator().Migrate(connection));
        Assert.Equal(5, new SchemaMigrator().Migrate(connection));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM GuardSettings;"));
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

    private static bool IndexExists(SqliteConnection connection, string index)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";
        command.Parameters.AddWithValue("$name", index);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }
}