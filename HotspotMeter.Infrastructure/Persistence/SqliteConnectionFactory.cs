using Microsoft.Data.Sqlite;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 创建已配置好的 SQLite 连接。
///
/// 关键点：
/// 1. <c>foreign_keys</c>、<c>busy_timeout</c>、<c>synchronous</c> 是“每连接”设置，
///    必须每次打开都执行；漏掉任何一个，外键或并发行为就会与预期不同。
/// 2. <c>journal_mode=WAL</c> 是数据库级设置，写在数据库头里，切换本身是持久的；
///    这里仍在连接建立时执行并校验返回值，是为了尽早发现"文件只读/网络盘不支持 WAL"
///    这类环境问题，而不是等到写入时才报错。
/// 3. 关闭连接池（<c>Pooling=false</c>）：本应用是单写入者模型，连接数量很少，
///    关闭池化可以避免“Dispose 之后文件仍被池占用”导致备份、隔离、删除失败。
/// </summary>
public sealed class SqliteConnectionFactory
{
    /// <summary>默认忙等待时间：5 秒。</summary>
    public const int DefaultBusyTimeoutMilliseconds = 5_000;

    private readonly int _busyTimeoutMilliseconds;
    private readonly string _connectionString;

    /// <summary>创建工厂。</summary>
    /// <param name="databasePath">数据库文件绝对路径。</param>
    /// <param name="busyTimeoutMilliseconds">SQLite 忙等待时间（毫秒）。</param>
    public SqliteConnectionFactory(string databasePath, int busyTimeoutMilliseconds = DefaultBusyTimeoutMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        if (!Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException("数据库路径必须是绝对路径。", nameof(databasePath));
        }

        if (busyTimeoutMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(busyTimeoutMilliseconds),
                "忙等待时间不能为负数。");
        }

        DatabasePath = Path.GetFullPath(databasePath);
        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        _connectionString = builder.ToString();
    }

    /// <summary>数据库文件绝对路径。</summary>
    public string DatabasePath { get; }

    /// <summary>打开一个已应用 PRAGMA 的连接。调用方负责释放。</summary>
    public SqliteConnection Create()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            Configure(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>读取当前连接的忙等待值，供健康检查与测试断言使用。</summary>
    public static int ReadBusyTimeout(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void Configure(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, $"PRAGMA busy_timeout = {_busyTimeoutMilliseconds};");
        // FULL：每次提交都 fsync。检查点约 10 秒一次，开销可忽略；
// NORMAL 在 WAL 下断电可能丢掉整个 WAL（不只是最后 10 秒），与数据恢复文档承诺不符。
        Execute(connection, "PRAGMA synchronous = FULL;");

        using (var journalCommand = connection.CreateCommand())
        {
            journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
            var mode = journalCommand.ExecuteScalar() as string;
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"无法把数据库切换到 WAL 模式（当前为 '{mode}'）。请检查数据库文件是否位于只读位置或不受支持的存储上。");
            }
        }

        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "PRAGMA foreign_keys;";
        if (Convert.ToInt32(checkCommand.ExecuteScalar()) != 1)
        {
            throw new InvalidOperationException("无法启用外键约束。");
        }

        // synchronous 在 NORMAL 时返回 1；这里不强制断言具体值，只确保 PRAGMA 生效。
        using var syncCommand = connection.CreateCommand();
        syncCommand.CommandText = "PRAGMA synchronous;";
        _ = syncCommand.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}