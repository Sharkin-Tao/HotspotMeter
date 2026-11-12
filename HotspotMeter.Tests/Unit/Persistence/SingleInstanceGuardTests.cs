using HotspotMeter.Infrastructure.SingleInstance;

namespace HotspotMeter.Tests.Unit.Persistence;

public class SingleInstanceGuardTests
{
    [Fact]
    public void DefaultName_IsStableAndSessionLocal()
    {
        Assert.StartsWith(@"Local\", SingleInstanceGuard.DefaultName);
        Assert.Contains("HotspotMeter", SingleInstanceGuard.DefaultName);
    }

    [Fact]
    public async Task SecondAcquisition_FailsWhileFirstGuardIsAlive()
    {
        var name = NewName();
        using var first = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(first);

        var second = await Task.Run(() => SingleInstanceGuard.TryAcquire(name));

        Assert.Null(second);
    }

    [Fact]
    public async Task AfterFirstGuardIsDisposed_SecondAcquisitionSucceeds()
    {
        var name = NewName();
        var first = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(first);

        first!.Dispose();

        var second = await Task.Run(() => SingleInstanceGuard.TryAcquire(name));
        Assert.NotNull(second);
        second!.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var name = NewName();
        var guard = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(guard);

        guard!.Dispose();
        guard.Dispose();

        using var again = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void TryAcquire_RejectsEmptyName()
    {
        Assert.Throws<ArgumentException>(() => SingleInstanceGuard.TryAcquire(" "));
    }

    private static string NewName() => $@"Local\HotspotMeter.Tests.{Guid.NewGuid():N}";
}