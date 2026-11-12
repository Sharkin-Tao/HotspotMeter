using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>数据库版本高于当前程序支持的版本时抛出。绝不自动降级或覆盖。</summary>
public sealed class DatabaseVersionTooNewException : Exception
{
    public DatabaseVersionTooNewException(int currentVersion, int supportedVersion)
        : base($"数据库版本 {currentVersion} 高于当前程序支持的版本 {supportedVersion}，已拒绝写入。请升级程序，或先备份数据库。")
    {
        CurrentVersion = currentVersion;
        SupportedVersion = supportedVersion;
    }

    public int CurrentVersion { get; }

    public int SupportedVersion { get; }
}

/// <summary>
/// 一次模式迁移。版本号必须从 1 开始连续递增，且与 DDL 一一对应。
/// </summary>
/// <param name="Version">迁移完成后写入 <c>PRAGMA user_version</c> 的值。</param>
/// <param name="Name">迁移名称，仅用于日志与诊断。</param>
/// <param name="Statements">按顺序执行的 DDL/DML。</param>
/// <param name="Seed">可选的参数化写入，在同一个事务内执行。</param>
public sealed record DatabaseMigration(
    int Version,
    string Name,
    IReadOnlyList<string> Statements,
    Action<SqliteConnection, SqliteTransaction>? Seed = null);

/// <summary>
/// SQLite 模式迁移器。
///
/// 设计要点：
/// 1. 版本唯一权威是 <c>PRAGMA user_version</c>。SchemaInfo 只保存创建时间、程序版本等元信息，
///    冗余保存 schema_version 容易在"写了表但没改版本"时产生两套真相。
/// 2. 每个版本的 DDL 与版本号写入在同一个事务内，失败整体回滚；
///    不会出现"表已建、版本没升"或"版本升了、表没建"的半迁移状态。
/// 3. 遇到更高版本直接抛异常并拒绝写入，避免旧程序把新库当成旧库继续迁移。
/// </summary>
public sealed class SchemaMigrator
{
    /// <summary>当前程序支持的最高模式版本。</summary>
    public const int SupportedVersion = 1;

    /// <summary>默认迁移集合。阶段 3A 只建立元数据表，业务表在 3B 前审核后加入。</summary>
    public static readonly IReadOnlyList<DatabaseMigration> DefaultMigrations = new[]
    {
        new DatabaseMigration(
            Version: 1,
            Name: "metadata-v1",
            Statements: new[]
            {
                """
                CREATE TABLE IF NOT EXISTS SchemaInfo (
                    Key   TEXT PRIMARY KEY NOT NULL,
                    Value TEXT NOT NULL
                );
                """,
            },
            Seed: SeedMetadata),
    };

    private readonly IReadOnlyList<DatabaseMigration> _migrations;

    /// <summary>使用默认迁移集合。</summary>
    public SchemaMigrator()
        : this(DefaultMigrations)
    {
    }

    /// <summary>使用自定义迁移集合（测试注入失败迁移时使用）。</summary>
    public SchemaMigrator(IReadOnlyList<DatabaseMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        if (migrations.Count == 0)
        {
            throw new ArgumentException("迁移集合不能为空。", nameof(migrations));
        }

        var expected = 1;
        foreach (var migration in migrations)
        {
            if (migration.Version != expected)
            {
                throw new ArgumentException(
                    $"迁移版本必须从 1 开始连续递增：期望 {expected}，实际 {migration.Version}。",
                    nameof(migrations));
            }

            expected++;
        }

        _migrations = migrations;
    }

    /// <summary>迁移集合中的最高版本。</summary>
    public int TargetVersion => _migrations[^1].Version;

    /// <summary>
    /// 把数据库迁移到目标版本。已是最新时为空操作。
    /// </summary>
    /// <returns>迁移后的 <c>user_version</c>。</returns>
    public int Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var current = ReadUserVersion(connection);
        if (current > TargetVersion)
        {
            throw new DatabaseVersionTooNewException(current, TargetVersion);
        }

        foreach (var migration in _migrations)
        {
            if (migration.Version <= current)
            {
                continue;
            }

            Apply(connection, migration);
            current = migration.Version;
        }

        return ReadUserVersion(connection);
    }

    /// <summary>读取 <c>PRAGMA user_version</c>。</summary>
    public static int ReadUserVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Apply(SqliteConnection connection, DatabaseMigration migration)
    {
        using var transaction = connection.BeginTransaction();

        foreach (var statement in migration.Statements)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        migration.Seed?.Invoke(connection, transaction);

        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.Transaction = transaction;
            // PRAGMA 不支持参数化；Version 来自代码常量/校验过的整型，不涉及外部输入。
            versionCommand.CommandText = "PRAGMA user_version = " + migration.Version.ToString(CultureInfo.InvariantCulture) + ";";
            versionCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void SeedMetadata(SqliteConnection connection, SqliteTransaction transaction)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var appVersion = typeof(SchemaMigrator).Assembly.GetName().Version?.ToString() ?? "unknown";

        InsertIfMissing(connection, transaction, "created_at_utc", now);
        InsertIfMissing(connection, transaction, "initialized_at_utc", now);
        InsertIfMissing(connection, transaction, "app_version", appVersion);
    }

    private static void InsertIfMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO SchemaInfo (Key, Value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}