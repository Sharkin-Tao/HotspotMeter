using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;

namespace HotspotMeter.Tests.Unit.Persistence;

public class DatabaseBootstrapperTests
{
    [Fact]
    public void Initialize_CreatesAndMigratesFreshDatabase()
    {
        using var temp = new TempDirectory();
        var paths = new DatabasePaths(temp.File("data"));

        var result = new DatabaseBootstrapper(paths).Initialize();

        Assert.Equal(SchemaMigrator.SupportedVersion, result.SchemaVersion);
        Assert.False(result.WasQuarantined);
        Assert.Null(result.QuarantineDirectory);
        Assert.True(File.Exists(paths.DatabaseFile));

        var health = new DatabaseHealth().Check(paths.DatabaseFile);
        Assert.True(health.IsHealthy, health.Detail);
    }

    [Fact]
    public void Initialize_ReusesHealthyDatabase_WithoutQuarantine()
    {
        using var temp = new TempDirectory();
        var paths = new DatabasePaths(temp.File("data"));
        var first = new DatabaseBootstrapper(paths).Initialize();

        var second = new DatabaseBootstrapper(paths).Initialize();

        Assert.Equal(first.SchemaVersion, second.SchemaVersion);
        Assert.False(second.WasQuarantined);
        Assert.Null(second.QuarantineDirectory);
        Assert.NotNull(second.HealthBefore);
        Assert.True(second.HealthBefore!.IsHealthy);
    }

    [Fact]
    public void Initialize_QuarantinesCorruptDatabase_ThenCreatesFreshOne()
    {
        using var temp = new TempDirectory();
        var paths = new DatabasePaths(temp.File("data"));
        paths.EnsureDirectories();
        File.WriteAllBytes(paths.DatabaseFile, Enumerable.Repeat((byte)0x7F, 4096).ToArray());
        File.WriteAllText(paths.DatabaseFile + "-wal", "stale wal");

        var result = new DatabaseBootstrapper(paths).Initialize();

        Assert.True(result.WasQuarantined);
        Assert.NotNull(result.QuarantineDirectory);
        Assert.True(Directory.Exists(result.QuarantineDirectory));
        Assert.True(File.Exists(Path.Combine(result.QuarantineDirectory!, "hotspotmeter.db")));

        // 新库必须可用且版本正确。
        Assert.Equal(SchemaMigrator.SupportedVersion, result.SchemaVersion);
        var health = new DatabaseHealth().Check(paths.DatabaseFile);
        Assert.True(health.IsHealthy, health.Detail);
    }

    [Fact]
    public void Initialize_DoesNotCreateNewDatabase_WhenQuarantineFails()
    {
        using var temp = new TempDirectory();
        var paths = new DatabasePaths(temp.File("data"));
        paths.EnsureDirectories();
        File.WriteAllBytes(paths.DatabaseFile, Enumerable.Repeat((byte)0x55, 4096).ToArray());

        using (new FileStream(paths.DatabaseFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<DatabaseQuarantineException>(() => new DatabaseBootstrapper(paths).Initialize());
        }

        // 原始损坏文件仍在；不能因为隔离失败而生成一个同名新库把它盖住。
        Assert.Equal(4096, new FileInfo(paths.DatabaseFile).Length);
        Assert.Equal(0x55, File.ReadAllBytes(paths.DatabaseFile)[0]);
    }
}