using HotspotMeter.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.TestSupport;

/// <summary>
/// 基于真实 SQLite 文件的持久化测试上下文：临时目录 + 已迁移数据库 + 三个仓库。
/// 仓库测试不使用内存假数据，避免“Mock 通过、SQL 不通过”。
/// </summary>
public sealed class PersistenceContext : IDisposable
{
    public PersistenceContext()
    {
        Temp = new TempDirectory("hotspotmeter-repo");
        Factory = new SqliteConnectionFactory(Temp.File("hotspotmeter.db"));
        using (var connection = Factory.Create())
        {
            new SchemaMigrator().Migrate(connection);
        }

        Profiles = new ProfileResolver(Factory);
        Sessions = new SessionRepository(Factory);
        Bssids = new BssidRepository(Factory);
    }

    public TempDirectory Temp { get; }

    public SqliteConnectionFactory Factory { get; }

    public ProfileResolver Profiles { get; }

    public SessionRepository Sessions { get; }

    public BssidRepository Bssids { get; }

    public long Count(string table)
    {
        using var connection = Factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public T Scalar<T>(string sql)
    {
        using var connection = Factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    public void Dispose() => Temp.Dispose();
}