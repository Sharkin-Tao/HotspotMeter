using HotspotMeter.Core.TrafficGuard;

namespace HotspotMeter.Tests.Unit.TrafficGuard;

public sealed class GuardProcessPolicyTests
{
    private const string Windows = @"C:\Windows";

    [Theory]
    [InlineData("smss.exe")]
    [InlineData("csrss.exe")]
    [InlineData("winlogon.exe")]
    [InlineData("services.exe")]
    [InlineData("lsass.exe")]
    [InlineData("svchost.exe")]
    [InlineData("dwm.exe")]
    [InlineData("explorer.exe")]
    [InlineData("System")]
    [InlineData("Registry")]
    public void ProtectedProcesses_AreBlocked(string name)
    {
        var safety = GuardProcessPolicy.Evaluate(
            $@"exe:c:\apps\{name}",
            $@"c:\apps\{name}",
            1,
            Windows);

        Assert.False(safety.CanTerminate);
        Assert.True(
            safety.Reason is GuardProcessBlockReason.ProtectedProcess
                or GuardProcessBlockReason.SharedService);
    }

    [Fact]
    public void ExecutableUnderWindowsDirectory_IsBlocked()
    {
        var safety = GuardProcessPolicy.Evaluate(
            @"exe:c:\windows\system32\notepad.exe",
            @"C:\Windows\System32\notepad.exe",
            1,
            Windows);

        Assert.False(safety.CanTerminate);
        Assert.Equal(GuardProcessBlockReason.SystemPath, safety.Reason);
    }

    [Fact]
    public void SessionZero_IsBlocked()
    {
        var safety = GuardProcessPolicy.Evaluate(
            @"exe:c:\apps\demo.exe",
            @"c:\apps\demo.exe",
            0,
            Windows);

        Assert.False(safety.CanTerminate);
        Assert.Equal(GuardProcessBlockReason.SessionZero, safety.Reason);
    }

    [Theory]
    [InlineData("uncertain:123:456:demo")]
    [InlineData("opaque:abcdef")]
    public void UncertainOrOpaqueIdentity_IsBlocked(string appKey)
    {
        var safety = GuardProcessPolicy.Evaluate(appKey, @"c:\apps\demo.exe", 1, Windows);

        Assert.False(safety.CanTerminate);
        Assert.Equal(GuardProcessBlockReason.UncertainIdentity, safety.Reason);
    }

    [Fact]
    public void MissingPath_IsBlocked()
    {
        var safety = GuardProcessPolicy.Evaluate(@"exe:c:\apps\demo.exe", null, 1, Windows);

        Assert.False(safety.CanTerminate);
        Assert.Equal(GuardProcessBlockReason.PathUnavailable, safety.Reason);
    }

    [Fact]
    public void NormalUserProgram_IsAllowed()
    {
        var safety = GuardProcessPolicy.Evaluate(
            @"exe:c:\apps\demo.exe",
            @"c:\apps\demo.exe",
            1,
            Windows);

        Assert.True(safety.CanTerminate);
        Assert.Equal(GuardProcessBlockReason.None, safety.Reason);
    }
}