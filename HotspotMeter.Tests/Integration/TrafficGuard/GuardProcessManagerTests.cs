using System.Diagnostics;
using HotspotMeter.Core.TrafficGuard;
using HotspotMeter.Infrastructure.TrafficGuard;
using HotspotMeter.Tests.TestSupport;

namespace HotspotMeter.Tests.Integration.TrafficGuard;

public sealed class GuardProcessManagerTests
{
    private static readonly string WindowsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Fact]
    public async Task GetProcesses_MatchesExecutablePath_AndReportsSafety()
    {
        var observation = Observation(999_001, @"c:\apps\demo.exe", null, 1234);
        var manager = new GuardProcessManager(
            new FakeInspector(observation),
            @"C:\Windows");

        var items = await manager.GetProcessesAsync(@"exe:c:\apps\demo.exe");

        var item = Assert.Single(items);
        Assert.Equal(999_001, item.ProcessId);
        Assert.Equal(@"c:\apps\demo.exe", item.ExecutablePath);
        Assert.Equal(GuardProcessBlockReason.None, item.Safety.Reason);
        Assert.Empty(await manager.GetProcessesAsync(@"exe:c:\apps\other.exe"));
    }

    [Fact]
    public async Task GetProcesses_MatchesPackageFamilyName()
    {
        var observation = Observation(999_002, null, "Vendor.App_123", 1234);
        var manager = new GuardProcessManager(new FakeInspector(observation), WindowsDirectory);

        var item = Assert.Single(await manager.GetProcessesAsync("pkg:vendor.app_123"));

        Assert.Equal("Vendor.App_123", item.PackageFamilyName);
        // 没有路径的打包程序无法通过路径校验，默认禁止结束。
        Assert.False(item.Safety.CanTerminate);
        Assert.Equal(GuardProcessBlockReason.PathUnavailable, item.Safety.Reason);
    }

    [Fact]
    public async Task Terminate_AlreadyExitedProcess_ReturnsAlreadyExited()
    {
        var manager = new GuardProcessManager(new FakeInspector(), WindowsDirectory);

        var result = await manager.TerminateAsync(
            new GuardTerminationRequest(@"exe:c:\apps\demo.exe", int.MaxValue, DateTimeOffset.UtcNow, null, null));

        Assert.Equal(GuardProcessActionStatus.AlreadyExited, result.Status);
    }

    [Fact]
    public async Task Terminate_WhenStartTimeDiffers_ReturnsIdentityChanged()
    {
        using var current = Process.GetCurrentProcess();
        var observation = Observation(
            current.Id,
            current.MainModule?.FileName ?? @"c:\apps\testhost.exe",
            null,
            current.StartTime.ToUniversalTime().AddMinutes(-5).Ticks);
        var manager = new GuardProcessManager(new FakeInspector(observation), WindowsDirectory);

        var result = await manager.TerminateAsync(new GuardTerminationRequest(
            observation.ExecutablePath is null ? "exe:x" : "exe:" + observation.ExecutablePath,
            current.Id,
            DateTimeOffset.UtcNow,
            null,
            null));

        Assert.Equal(GuardProcessActionStatus.IdentityChanged, result.Status);
    }

    [Fact]
    public async Task Terminate_SystemProcess_ReturnsBlocked_WithoutKilling()
    {
        using var current = Process.GetCurrentProcess();
        var observation = Observation(
            current.Id,
            @"C:\Windows\explorer.exe",
            null,
            current.StartTime.ToUniversalTime().Ticks);
        var manager = new GuardProcessManager(new FakeInspector(observation), WindowsDirectory);

        var result = await manager.TerminateAsync(new GuardTerminationRequest(
            @"exe:c:\windows\explorer.exe",
            current.Id,
            new DateTimeOffset(observation.StartTimeUtcTicks, TimeSpan.Zero),
            null,
            null));

        Assert.Equal(GuardProcessActionStatus.Blocked, result.Status);
        Assert.Equal(GuardProcessBlockReason.ProtectedProcess, result.BlockReason);
        Assert.False(current.HasExited);
    }

    [Fact]
    public async Task Terminate_NormalSpawnedProcess_Succeeds()
    {
        using var directory = new TempDirectory("guard-process");
        var source = Path.Combine(WindowsDirectory, "System32", "ping.exe");
        Assert.True(File.Exists(source), source);
        var target = directory.File("ping.exe");
        File.Copy(source, target);

        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = target,
            Arguments = "-n 30 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        });
        Assert.NotNull(child);
        await Task.Delay(300);

        var observation = Observation(
            child!.Id,
            target,
            null,
            child.StartTime.ToUniversalTime().Ticks);
        var manager = new GuardProcessManager(new FakeInspector(observation), WindowsDirectory);

        var result = await manager.TerminateAsync(new GuardTerminationRequest(
            "exe:" + target,
            child.Id,
            new DateTimeOffset(observation.StartTimeUtcTicks, TimeSpan.Zero),
            null,
            null));

        Assert.Equal(GuardProcessActionStatus.Success, result.Status);
        Assert.True(child.WaitForExit(3000));
    }

    private static ProcessObservation Observation(
        int pid,
        string? path,
        string? pfn,
        long startTicks) =>
        new(pid, Path.GetFileName(path) is { Length: > 0 } name ? name : "app", path, pfn, false, false, startTicks);

    private sealed class FakeInspector(params ProcessObservation[] items) : IProcessInspector
    {
        public IReadOnlyList<ProcessObservation> Capture() => items;
    }
}

