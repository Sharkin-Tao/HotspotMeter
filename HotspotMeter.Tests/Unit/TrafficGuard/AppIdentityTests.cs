using HotspotMeter.Core.TrafficGuard;

namespace HotspotMeter.Tests.Unit.TrafficGuard;

public sealed class AppIdentityTests
{
    [Fact]
    public void Desktop_SameFileNameInDifferentDirectories_ProducesDifferentKeys()
    {
        var first = AppIdentity.Desktop(@"C:\Apps\One\player.exe");
        var second = AppIdentity.Desktop(@"C:\Apps\Two\player.exe");

        Assert.NotEqual(first.Key, second.Key);
        Assert.True(first.IsCertain);
        Assert.Equal(AppIdentityKind.DesktopExecutable, first.Kind);
    }

    [Fact]
    public void Desktop_NormalizesCaseSeparatorsAndLongPathPrefix()
    {
        var first = AppIdentity.Desktop(@"\\?\C:\Apps\Player.EXE");
        var second = AppIdentity.Desktop("c:/apps/player.exe");

        Assert.Equal(first.Key, second.Key);
    }

    [Fact]
    public void Packaged_IsCaseInsensitive_AndStableAcrossProcesses()
    {
        var first = AppIdentity.Packaged("Microsoft.WindowsCalculator_8wekyb3d8bbwe");
        var second = AppIdentity.Packaged("microsoft.windowscalculator_8wekyb3d8bbwe");

        Assert.Equal(first.Key, second.Key);
        Assert.Equal(AppIdentityKind.Packaged, first.Kind);
    }

    [Fact]
    public void Uncertain_UsesPidAndStartTime_SoTwoSameNamedProcessesDoNotMerge()
    {
        var first = AppIdentity.Uncertain("svchost.exe", 100, 111);
        var second = AppIdentity.Uncertain("svchost.exe", 101, 222);

        Assert.NotEqual(first.Key, second.Key);
        Assert.False(first.IsCertain);
        Assert.Equal(AppIdentityKind.Uncertain, first.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FromAttributionId_EmptyId_ReturnsNull_InsteadOfInventingAnOwner(string? id)
    {
        Assert.Null(AppIdentity.FromAttributionId(id));
    }

    [Fact]
    public void FromAttributionId_DevicePath_DerivesDesktopIdentity()
    {
        var identity = AppIdentity.FromAttributionId(@"\device\harddiskvolume3\apps\demo.exe", null);

        Assert.NotNull(identity);
        Assert.Equal(AppIdentityKind.DesktopExecutable, identity.Kind);
        Assert.Equal("demo.exe", identity.DisplayName);
    }

    [Fact]
    public void FromAttributionId_SystemPseudoPath_IsUncertainNotAFakeExe()
    {
        var identity = AppIdentity.FromAttributionId(@"\device\harddiskvolume3\system\ipv6 control message", "IPv6 Control Message");

        Assert.NotNull(identity);
        Assert.Equal(AppIdentityKind.Uncertain, identity.Kind);
        Assert.False(identity.IsCertain);
        Assert.DoesNotContain(@"\device\", identity.Key);
    }

    [Fact]
    public void FromAttributionId_PackageFamilyName_DerivesPackagedIdentity()
    {
        var identity = AppIdentity.FromAttributionId("MicrosoftWindows.CrossDevice_cw5n1h2txyewy", null);

        Assert.NotNull(identity);
        Assert.Equal(AppIdentityKind.Packaged, identity.Kind);
    }
}