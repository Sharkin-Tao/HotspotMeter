using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;

namespace HotspotMeter.Tests.Unit.Persistence;

public class DatabaseHealthTests
{
    [Fact]
    public void Check_ReturnsHealthy_ForValidDatabase()
    {
        using var temp = new TempDirectory();
        var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));
        using (var connection = factory.Create())
        {
            new SchemaMigrator().Migrate(connection);
        }

        var result = new DatabaseHealth().Check(factory.DatabasePath);

        Assert.True(result.IsHealthy, result.Detail);
        Assert.Contains("ok", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_TreatsMissingFileAsHealthyButUninitialized()
    {
        using var temp = new TempDirectory();

        var result = new DatabaseHealth().Check(temp.File("hotspotmeter.db"));

        Assert.True(result.IsHealthy);
        Assert.Contains("尚不存在", result.Detail);
    }

    [Fact]
    public void Check_DetectsFileThatIsNotADatabase()
    {
        using var temp = new TempDirectory();
        var path = temp.File("hotspotmeter.db");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, 8192).ToArray());

        var result = new DatabaseHealth().Check(path);

        Assert.False(result.IsHealthy);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        Assert.NotNull(result.SqliteErrorCode);
    }

    [Fact]
    public void Check_DetectsCorruptedHeader()
    {
        using var temp = new TempDirectory();
        var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));
        using (var connection = factory.Create())
        {
            new SchemaMigrator().Migrate(connection);
        }

        // 破坏文件头：SQLite 会把它当作非数据库文件或损坏页。
        var bytes = File.ReadAllBytes(factory.DatabasePath);
        bytes[0] = 0x00;
        bytes[1] = 0x00;
        File.WriteAllBytes(factory.DatabasePath, bytes);

        var result = new DatabaseHealth().Check(factory.DatabasePath);

        Assert.False(result.IsHealthy);
    }
}