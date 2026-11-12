using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;

namespace HotspotMeter.Tests.Unit.Persistence;

public class DatabaseQuarantineTests
{
    [Fact]
    public void Quarantine_MovesDatabaseAndAllSidecarFiles()
    {
        using var temp = new TempDirectory();
        var root = temp.File("data");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "hotspotmeter.db");
        File.WriteAllText(databasePath, "main");
        File.WriteAllText(databasePath + "-wal", "wal");
        File.WriteAllText(databasePath + "-shm", "shm");
        File.WriteAllText(databasePath + "-journal", "journal");

        var result = new DatabaseQuarantine().Quarantine(databasePath, Path.Combine(root, "corrupt"), DateTimeOffset.UtcNow);

        Assert.NotNull(result.Directory);
        Assert.Equal(4, result.MovedFiles.Count);
        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
        Assert.False(File.Exists(databasePath + "-journal"));
        Assert.Equal("main", File.ReadAllText(Path.Combine(result.Directory!, "hotspotmeter.db")));
        Assert.Equal("wal", File.ReadAllText(Path.Combine(result.Directory!, "hotspotmeter.db-wal")));
    }

    [Fact]
    public void Quarantine_HandlesOnlyExistingFiles()
    {
        using var temp = new TempDirectory();
        var databasePath = temp.File("hotspotmeter.db");
        File.WriteAllText(databasePath, "main");

        var result = new DatabaseQuarantine().Quarantine(databasePath, temp.File("corrupt"), DateTimeOffset.UtcNow);

        Assert.Single(result.MovedFiles);
    }

    [Fact]
    public void Quarantine_ReturnsNullDirectory_WhenNothingToMove()
    {
        using var temp = new TempDirectory();

        var result = new DatabaseQuarantine().Quarantine(temp.File("hotspotmeter.db"), temp.File("corrupt"), DateTimeOffset.UtcNow);

        Assert.Null(result.Directory);
        Assert.Empty(result.MovedFiles);
    }

    [Fact]
    public void Quarantine_WhenFileIsLocked_ThrowsAndKeepsOriginalIntact()
    {
        using var temp = new TempDirectory();
        var root = temp.File("data");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "hotspotmeter.db");
        File.WriteAllText(databasePath, "evidence");
        var corruptionRoot = Path.Combine(root, "corrupt");

        using (new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var exception = Assert.Throws<DatabaseQuarantineException>(
                () => new DatabaseQuarantine().Quarantine(databasePath, corruptionRoot, DateTimeOffset.UtcNow));

            Assert.Contains("已停止启动", exception.Message);
        }

        Assert.Equal("evidence", File.ReadAllText(databasePath));
        Assert.False(File.Exists(Path.Combine(corruptionRoot, "hotspotmeter.db")));

        // 失败时不应留下空隔离目录。
        if (Directory.Exists(corruptionRoot))
        {
            Assert.Empty(Directory.EnumerateDirectories(corruptionRoot));
        }
    }

    [Fact]
    public void Quarantine_UsesUniqueDirectoryPerIncident()
    {
        using var temp = new TempDirectory();
        var root = temp.File("data");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "hotspotmeter.db");
        var corruptionRoot = Path.Combine(root, "corrupt");
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var quarantine = new DatabaseQuarantine();

        File.WriteAllText(databasePath, "first");
        var first = quarantine.Quarantine(databasePath, corruptionRoot, now);

        File.WriteAllText(databasePath, "second");
        var second = quarantine.Quarantine(databasePath, corruptionRoot, now);

        Assert.NotEqual(first.Directory, second.Directory);
        Assert.True(Directory.Exists(first.Directory!));
        Assert.True(Directory.Exists(second.Directory!));
    }
}