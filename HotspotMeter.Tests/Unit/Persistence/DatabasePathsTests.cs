using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;

namespace HotspotMeter.Tests.Unit.Persistence;

public class DatabasePathsTests
{
    [Fact]
    public void CreateDefault_UsesLocalApplicationData_NotProgramDirectory()
    {
        var paths = DatabasePaths.CreateDefault();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localAppData, paths.RootDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), paths.RootDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("hotspotmeter.db", Path.GetFileName(paths.DatabaseFile));
        Assert.EndsWith(@"corrupt", paths.CorruptionDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureDirectories_CreatesRootAndCorruptionDirectory_Idempotently()
    {
        using var temp = new TempDirectory();
        var root = temp.File("data");
        var paths = new DatabasePaths(root);

        paths.EnsureDirectories();
        paths.EnsureDirectories();

        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(paths.CorruptionDirectory));
    }

    [Fact]
    public void Constructor_RejectsRelativePath()
    {
        var exception = Assert.Throws<ArgumentException>(() => new DatabasePaths("relative\\data"));
        Assert.Contains("绝对路径", exception.Message);
    }

    [Fact]
    public void Constructor_RejectsPathInsideApplicationDirectory()
    {
        var inside = Path.Combine(AppContext.BaseDirectory, "data");
        var exception = Assert.Throws<ArgumentException>(() => new DatabasePaths(inside));
        Assert.Contains("程序目录", exception.Message);
    }

    [Fact]
    public void DatabaseFile_IsStableAcrossInstances()
    {
        using var temp = new TempDirectory();

        var first = new DatabasePaths(temp.Path).DatabaseFile;
        var second = new DatabasePaths(temp.Path).DatabaseFile;

        Assert.Equal(first, second);
    }
}