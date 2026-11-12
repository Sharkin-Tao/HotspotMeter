using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Unit.Persistence;

public class SchemaMigratorTests
{
    [Fact]
    public void Migrate_InitializesEmptyDatabaseToSupportedVersion()
    {
        using var temp = new TempDirectory();
        var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));
        using var connection = factory.Create();

        var version = new SchemaMigrator().Migrate(connection);

        Assert.Equal(SchemaMigrator.SupportedVersion, version);
        Assert.Equal(1, SchemaMigrator.ReadUserVersion(connection));
        Assert.Equal(1, ReadInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SchemaInfo';"));

        var values = ReadSchemaInfo(connection);
        Assert.True(values.ContainsKey("created_at_utc"));
        Assert.True(values.ContainsKey("initialized_at_utc"));
        Assert.True(values.ContainsKey("app_version"));
        Assert.False(values.ContainsKey("schema_version"), "schema_version 不应重复保存，版本权威是 PRAGMA user_version。");
    }

    [Fact]
    public void Migrate_IsIdempotent_AndKeepsCreationTimestamp()
    {
        using var temp = new TempDirectory();
        var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));
        using var connection = factory.Create();
        var migrator = new SchemaMigrator();

        migrator.Migrate(connection);
        var createdAt = ReadSchemaInfo(connection)["created_at_utc"];

        var second = migrator.Migrate(connection);

        Assert.Equal(1, second);
        Assert.Equal(createdAt, ReadSchemaInfo(connection)["created_at_utc"]);
        Assert.Equal(3, ReadInt(connection, "SELECT COUNT(*) FROM SchemaInfo;"));
    }

    [Fact]
    public void Migrate_RollsBackWholeVersion_WhenMigrationFails()
    {
        using var temp = new TempDirectory();
        var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));
        using var connection = factory.Create();

        var migrations = new[]
        {
            new DatabaseMigration(1, "ok", new[] { "CREATE TABLE first_table (id INTEGER PRIMARY KEY);" }),
            new DatabaseMigration(
                2,
                "broken",
                new[]
                {
                    "CREATE TABLE partial_table (id INTEGER PRIMARY KEY);",
                    "THIS IS NOT VALID SQL;",
                }),
        };

        Assert.Throws<SqliteException>(() => new SchemaMigrator(migrations).Migrate(connection));

        // v1 已提交，v2 里的 partial_table 及其版本号必须一起回滚。
        Assert.Equal(1, SchemaMigrator.ReadUserVersion(connection));
        Assert.Equal(1, ReadInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='first_table';"));
        Assert.Equal(0, ReadInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='partial_table';"));
    }

    [Fact]
    public void Migrate_RejectsDatabaseFromNewerProgram()
    {
        using var temp = new TempDirectory();
        var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));
        using var connection = factory.Create();
        Execute(connection, "PRAGMA user_version = 99;");

        var exception = Assert.Throws<DatabaseVersionTooNewException>(() => new SchemaMigrator().Migrate(connection));

        Assert.Equal(99, exception.CurrentVersion);
        Assert.Equal(SchemaMigrator.SupportedVersion, exception.SupportedVersion);
        Assert.Equal(99, SchemaMigrator.ReadUserVersion(connection));
    }

    [Fact]
    public void Constructor_RejectsNonContiguousMigrationVersions()
    {
        var migrations = new[]
        {
            new DatabaseMigration(1, "one", Array.Empty<string>()),
            new DatabaseMigration(3, "three", Array.Empty<string>()),
        };

        Assert.Throws<ArgumentException>(() => new SchemaMigrator(migrations));
    }

    [Fact]
    public void Migrate_AppliesOnlyMissingVersions()
    {
        using var temp = new TempDirectory();
        var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));
        using var connection = factory.Create();
        Execute(connection, "PRAGMA user_version = 1;");
        Execute(connection, "CREATE TABLE first_table (id INTEGER PRIMARY KEY);");

        var migrations = new[]
        {
            new DatabaseMigration(1, "one", new[] { "CREATE TABLE first_table (id INTEGER PRIMARY KEY);" }),
            new DatabaseMigration(2, "two", new[] { "CREATE TABLE second_table (id INTEGER PRIMARY KEY);" }),
        };

        var version = new SchemaMigrator(migrations).Migrate(connection);

        Assert.Equal(2, version);
        Assert.Equal(1, ReadInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='second_table';"));
    }

    private static int ReadInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static Dictionary<string, string> ReadSchemaInfo(SqliteConnection connection)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM SchemaInfo;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}