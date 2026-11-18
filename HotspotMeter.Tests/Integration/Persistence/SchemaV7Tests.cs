using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Integration.Persistence;

/// <summary>
/// v7 的目标不是“索引名存在”，而是实际索引定义正确。
/// 因此这里统一用 <c>PRAGMA index_xinfo</c> 检查列顺序与排序方向，
/// 并同时验证提醒数据在 DROP/CREATE INDEX 前后逐列不变。
/// </summary>
public sealed class SchemaV7Tests : IDisposable
{
    private const string IndexName = "IX_GuardAlerts_CreatedAt";
    private const string NotificationIndexName = "IX_GuardAlerts_Notification";

    private readonly TempDirectory _directory = new("schema-v7");

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void FreshDatabase_HasCompositeAlertIndex()
    {
        using var connection = CreateConnection();

        Assert.Equal(7, new SchemaMigrator().Migrate(connection));

        AssertCompositeIndex(connection);
        Assert.True(IndexExists(connection, NotificationIndexName));
        Assert.Equal("ok", ScalarText(connection, "PRAGMA integrity_check;"));
    }

    [Fact]
    public void Migrate_FromV5_ThroughV6ToV7_PreservesAlert_AndCorrectsIndex()
    {
        using var connection = CreateConnection();
        Assert.Equal(5, MigrateToVersion(connection, 5));
        SeedGuardData(connection, includeV6Columns: false);

        Assert.Equal(6, new SchemaMigrator(SchemaMigrator.DefaultMigrations.Take(6).ToList()).Migrate(connection));
        // v6 自身的 DDL 也必须已经消除 v4 遗留的单列索引。
        AssertCompositeIndex(connection);
        var before = ReadSingleAlert(connection);

        Assert.Equal(7, new SchemaMigrator().Migrate(connection));

        AssertCompositeIndex(connection);
        AssertAlertDataPreserved(before, ReadSingleAlert(connection));
        Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM GuardAlertHistory;"));
    }

    [Fact]
    public void Migrate_FromLegacyV6SingleColumnIndex_ReplacesIndex()
    {
        using var connection = CreateConnection();
        MigrateToLegacyV6(connection);
        SeedGuardData(connection, includeV6Columns: true);
        AssertLegacySingleColumnIndex(connection);
        var before = ReadSingleAlert(connection);

        Assert.Equal(7, new SchemaMigrator().Migrate(connection));

        AssertCompositeIndex(connection);
        AssertAlertDataPreserved(before, ReadSingleAlert(connection));
        Assert.True(IndexExists(connection, NotificationIndexName));
    }

    [Fact]
    public void Migrate_FromV6CompositeIndex_KeepsIndexAndData()
    {
        using var connection = CreateConnection();
        Assert.Equal(5, MigrateToVersion(connection, 5));
        Assert.Equal(6, new SchemaMigrator(SchemaMigrator.DefaultMigrations.Take(6).ToList()).Migrate(connection));
        SeedGuardData(connection, includeV6Columns: true);
        AssertCompositeIndex(connection);
        var before = ReadSingleAlert(connection);

        Assert.Equal(7, new SchemaMigrator().Migrate(connection));

        AssertCompositeIndex(connection);
        AssertAlertDataPreserved(before, ReadSingleAlert(connection));
    }

    [Fact]
    public void Migrate_IsIdempotent_AtV7()
    {
        using var connection = CreateConnection();
        var migrator = new SchemaMigrator();
        Assert.Equal(7, migrator.Migrate(connection));
        var indexSql = ReadIndexSql(connection);

        Assert.Equal(7, migrator.Migrate(connection));

        Assert.Equal(7, SchemaMigrator.ReadUserVersion(connection));
        Assert.Equal(indexSql, ReadIndexSql(connection));
        AssertCompositeIndex(connection);
    }

    [Fact]
    public void SchemaV7Statements_CanBeAppliedTwice()
    {
        using var connection = CreateConnection();
        MigrateToLegacyV6(connection);
        SeedGuardData(connection, includeV6Columns: true);

        using (var transaction = connection.BeginTransaction())
        {
            foreach (var statement in SchemaV7.Statements.Concat(SchemaV7.Statements))
            {
                Execute(connection, statement, transaction);
            }

            transaction.Commit();
        }

        AssertCompositeIndex(connection);
        Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM GuardAlertHistory;"));
    }

    [Fact]
    public void FailedV7Migration_RollsBackIndexAndData()
    {
        using var connection = CreateConnection();
        MigrateToLegacyV6(connection);
        SeedGuardData(connection, includeV6Columns: true);
        var before = ReadSingleAlert(connection);

        var broken = SchemaMigrator.DefaultMigrations.Take(5)
            .Append(BuildLegacyV6Migration())
            .Append(new DatabaseMigration(
                7,
                "broken-v7",
                SchemaV7.Statements.Append("THIS IS NOT VALID SQL;").ToList()))
            .ToList();

        Assert.Throws<SqliteException>(() => new SchemaMigrator(broken).Migrate(connection));

        // DROP INDEX 和 CREATE INDEX 同处 v7 事务：失败后旧索引、旧数据和版本号都必须回来。
        Assert.Equal(6, SchemaMigrator.ReadUserVersion(connection));
        AssertLegacySingleColumnIndex(connection);
        AssertAlertDataPreserved(before, ReadSingleAlert(connection));
    }

    private SqliteConnection CreateConnection() =>
        new SqliteConnectionFactory(_directory.File("hotspotmeter.db")).Create();

    private static int MigrateToVersion(SqliteConnection connection, int version) =>
        new SchemaMigrator(SchemaMigrator.DefaultMigrations.Take(version).ToList()).Migrate(connection);

    private void MigrateToLegacyV6(SqliteConnection connection)
    {
        var migrations = SchemaMigrator.DefaultMigrations.Take(5)
            .Append(BuildLegacyV6Migration())
            .ToList();
        Assert.Equal(6, new SchemaMigrator(migrations).Migrate(connection));
    }

    /// <summary>
    /// 旧版 v6 的真实形态：五个 v6 列 + 仍然保留 v4 的同名单列索引。
    /// 用 <c>SchemaV6.Statements.Take(5)</c> 复用列定义，避免测试和产品 DDL 漂移。
    /// </summary>
    private static DatabaseMigration BuildLegacyV6Migration() => new(
        6,
        "legacy-traffic-guard-management-v6",
        new List<string>(SchemaV6.Statements.Take(5))
        {
            """
            CREATE INDEX IF NOT EXISTS IX_GuardAlerts_CreatedAt
                ON GuardAlertHistory (CreatedAtUtc);
            """,
            """
            CREATE INDEX IF NOT EXISTS IX_GuardAlerts_Notification
                ON GuardAlertHistory (NotificationStatus, CreatedAtUtc DESC);
            """,
        });

    private static void SeedGuardData(SqliteConnection connection, bool includeV6Columns)
    {
        Execute(connection,
            """
            INSERT INTO GuardAppRules
                (AppKey, DisplayName, IdentityKind, IdentityCertain, TrustState, FirstSeenAtUtc, LastSeenAtUtc)
            VALUES
                ('exe:c:\apps\demo.exe', 'demo.exe', 1, 1, 0,
                 '2026-11-17T00:00:00.0000000+00:00', '2026-11-17T00:00:00.0000000+00:00');
            """);

        if (includeV6Columns)
        {
            Execute(connection,
                """
                INSERT INTO GuardAlertHistory
                    (AppKey, ProfileKey, AlertType, ThresholdBytes, ActualBytes, CreatedAtUtc,
                     IsDismissed, Details, MetricKind, ThresholdSource, ActivityState, HandlingAction)
                VALUES
                    ('exe:c:\apps\demo.exe', NULL, 1, 100, 200, '2026-11-17T10:00:00.0000000+00:00',
                     1, 'preserve-me', 1, 1, 2, 3);
                """);
            return;
        }

        Execute(connection,
            """
            INSERT INTO GuardAlertHistory
                (AppKey, ProfileKey, AlertType, ThresholdBytes, ActualBytes, CreatedAtUtc, IsDismissed, Details)
            VALUES
                ('exe:c:\apps\demo.exe', NULL, 1, 100, 200, '2026-11-17T10:00:00.0000000+00:00',
                 1, 'preserve-me');
            """);
    }

    private static Dictionary<string, object?> ReadSingleAlert(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM GuardAlertHistory WHERE AlertId = 1;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            values[reader.GetName(ordinal)] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }

        Assert.False(reader.Read());
        return values;
    }

    private static void AssertAlertDataPreserved(
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> after)
    {
        Assert.Equal(before.Count, after.Count);
        foreach (var pair in before)
        {
            Assert.True(after.ContainsKey(pair.Key), $"迁移后缺少字段 {pair.Key}。");
            Assert.Equal(pair.Value, after[pair.Key]);
        }
    }

    private static void AssertCompositeIndex(SqliteConnection connection)
    {
        var keys = ReadIndexColumns(connection, IndexName)
            .Where(column => column.Key == 1)
            .OrderBy(column => column.Seqno)
            .ToList();

        Assert.Equal(2, keys.Count);
        Assert.Equal(0, keys[0].Seqno);
        Assert.Equal("CreatedAtUtc", keys[0].Name);
        Assert.Equal(1, keys[0].Desc);
        Assert.Equal(1, keys[1].Seqno);
        Assert.Equal("AlertId", keys[1].Name);
        Assert.Equal(1, keys[1].Desc);

        // 同名的旧索引不可能与复合索引并存，但历史上可能留下别的错误索引；
        // 这里把提醒表的索引集合固定下来，防止 v7 之后出现重复或遗留索引。
        Assert.Equal(
            new[] { "IX_GuardAlerts_App_Period", IndexName, NotificationIndexName }
                .OrderBy(name => name, StringComparer.Ordinal),
            ReadIndexNames(connection, "GuardAlertHistory")
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    private static IReadOnlyList<string> ReadIndexNames(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list('{table}');";
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static void AssertLegacySingleColumnIndex(SqliteConnection connection)
    {
        var keys = ReadIndexColumns(connection, IndexName)
            .Where(column => column.Key == 1)
            .OrderBy(column => column.Seqno)
            .ToList();

        Assert.Single(keys);
        Assert.Equal(0, keys[0].Seqno);
        Assert.Equal("CreatedAtUtc", keys[0].Name);
        Assert.Equal(0, keys[0].Desc);
    }

    private static IReadOnlyList<IndexColumn> ReadIndexColumns(SqliteConnection connection, string index)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_xinfo('{index}');";
        using var reader = command.ExecuteReader();

        var columns = new List<IndexColumn>();
        while (reader.Read())
        {
            columns.Add(new IndexColumn(
                Seqno: reader.GetInt32(0),
                Name: reader.IsDBNull(2) ? null : reader.GetString(2),
                Desc: reader.GetInt32(3),
                Key: reader.GetInt32(5)));
        }

        return columns;
    }

    private static string ReadIndexSql(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name;";
        command.Parameters.AddWithValue("$name", IndexName);
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
    }

    private static bool IndexExists(SqliteConnection connection, string index)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";
        command.Parameters.AddWithValue("$name", index);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
    }

    private static void Execute(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed record IndexColumn(int Seqno, string? Name, int Desc, int Key);
}