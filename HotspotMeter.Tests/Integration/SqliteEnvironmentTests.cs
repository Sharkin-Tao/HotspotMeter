using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Integration;

/// <summary>
/// 环境冒烟测试：确认 Microsoft.Data.Sqlite 在 .NET 10 上能加载原生 SQLite 并执行 SQL。
/// 它不验证任何业务逻辑，只证明数据层依赖在本机可用（阶段 3 会在这里写真正的仓储测试）。
/// </summary>
public class SqliteEnvironmentTests
{
    [Fact]
    public void InMemoryDatabase_CanExecuteScalar()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";

        var version = command.ExecuteScalar() as string;

        Assert.False(string.IsNullOrWhiteSpace(version));
    }
}