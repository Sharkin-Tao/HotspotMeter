using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Unit.Persistence;

public class SqliteConnectionFactoryTests
{
    [Fact]
    public void Create_OpensDatabaseWithExpectedPragmas()
    {
        using var temp = new TempDirectory();
        var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));

        using var connection = factory.Create();

        Assert.Equal(1, ReadInt(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(5_000, ReadInt(connection, "PRAGMA busy_timeout;"));
        Assert.Equal(1, ReadInt(connection, "PRAGMA synchronous;"));
        Assert.Equal("wal", ReadString(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void Create_EnforcesForeignKeys()
    {
        using var temp = new TempDirectory();
        var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));
        using var connection = factory.Create();

        Execute(connection, "CREATE TABLE parent (id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parent(id));");

        var exception = Assert.Throws<SqliteException>(() =>
            Execute(connection, "INSERT INTO child (id, parent_id) VALUES (1, 999);"));
        Assert.Contains("FOREIGN KEY", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_CanBeRepeatedWithoutLeakingFileHandles()
    {
        using var temp = new TempDirectory();
        var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));

        for (var i = 0; i < 20; i++)
        {
            using var connection = factory.Create();
            Execute(connection, "CREATE TABLE IF NOT EXISTS probe (id INTEGER PRIMARY KEY);");
        }

        // 所有连接都释放后，数据库与其日志文件应可被独占打开。
        using var exclusive = new FileStream(factory.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Fact]
    public void Constructor_RejectsRelativePath()
    {
        Assert.Throws<ArgumentException>(() => new SqliteConnectionFactory("hotspotmeter.db"));
    }

    [Fact]
    public void Create_CreatesMissingDirectory()
    {
        using var temp = new TempDirectory();
        var nested = Path.Combine(temp.Path, "nested", "db");
        var factory = new SqliteConnectionFactory(Path.Combine(nested, "hotspotmeter.db"));

        using var connection = factory.Create();

        Assert.True(File.Exists(factory.DatabasePath));
    }

    private static int ReadInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string ReadString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}