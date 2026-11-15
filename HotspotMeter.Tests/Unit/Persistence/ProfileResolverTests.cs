using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;

namespace HotspotMeter.Tests.Unit.Persistence;

public class ProfileResolverTests
{
    private static readonly Guid InterfaceGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task GetOrCreate_IsStable_ForSameGuid()
    {
        using var context = new PersistenceContext();
        var coreId = Guid.NewGuid();

        var first = await context.Profiles.GetOrCreateAsync(coreId, InterfaceGuid, "TestHotspot");
        var second = await context.Profiles.GetOrCreateAsync(coreId, InterfaceGuid, "TestHotspot");

        Assert.Equal(first, second);
        Assert.Equal(1, context.Count("HotspotProfiles"));
        var key = context.Scalar<string>("SELECT ProfileKey FROM HotspotProfiles;");
        Assert.Equal(32, key.Length);
        Assert.Equal(coreId.ToString("N"), key);
    }

    [Fact]
    public async Task DifferentGuids_AreNotMerged()
    {
        using var context = new PersistenceContext();

        var first = await context.Profiles.GetOrCreateAsync(Guid.NewGuid(), InterfaceGuid, "热点 A");
        var second = await context.Profiles.GetOrCreateAsync(Guid.NewGuid(), InterfaceGuid, "热点 A");

        Assert.NotEqual(first, second);
        Assert.Equal(2, context.Count("HotspotProfiles"));
    }

    [Fact]
    public async Task NewResolverInstance_AfterRestart_KeepsMapping()
    {
        using var context = new PersistenceContext();
        var coreId = Guid.NewGuid();

        var first = await context.Profiles.GetOrCreateAsync(coreId, InterfaceGuid, "TestHotspot");

        // 模拟进程重启：新的解析器没有任何缓存，只能依赖数据库。
        var restarted = new ProfileResolver(context.Factory);
        var second = await restarted.GetOrCreateAsync(coreId, InterfaceGuid, "TestHotspot");

        Assert.Equal(first, second);
        Assert.Equal(1, context.Count("HotspotProfiles"));
    }

    [Fact]
    public async Task ConcurrentFirstCreate_ProducesSingleRow()
    {
        using var context = new PersistenceContext();
        var coreId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            context.Profiles.GetOrCreateAsync(coreId, InterfaceGuid, "TestHotspot"))));

        Assert.Single(results.Distinct());
        Assert.Equal(1, context.Count("HotspotProfiles"));
    }

    [Fact]
    public async Task GetOrCreate_RejectsEmptySsid()
    {
        using var context = new PersistenceContext();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            context.Profiles.GetOrCreateAsync(Guid.NewGuid(), InterfaceGuid, "  "));
    }

    [Fact]
    public async Task FindAsync_ReturnsNull_WhenProfileNeverSeen()
    {
        using var context = new PersistenceContext();

        var result = await context.Profiles.FindAsync(Guid.NewGuid());

        Assert.Null(result);
    }
}