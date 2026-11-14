using HotspotMeter.Core.Usage;

namespace HotspotMeter.Tests.Usage;

public sealed class AttributionDisplayNameTests
{
    [Fact]
    public void PrefersNonEmptyAttributionName()
    {
        Assert.Equal(
            "claude",
            AttributionDisplayName.Describe(@"\device\harddiskvolume3\apps\claude.exe", "  claude  "));
    }

    [Fact]
    public void DerivesDesktopExecutableNameFromDevicePath()
    {
        Assert.Equal(
            "com.vortex.helper",
            AttributionDisplayName.Describe(@"\device\harddiskvolume3\program files\vortex\com.vortex.helper.exe", null));
    }

    [Fact]
    public void StripsPackagePublisherSuffix()
    {
        Assert.Equal(
            "MicrosoftWindows.CrossDevice",
            AttributionDisplayName.Describe("MicrosoftWindows.CrossDevice_cw5n1h2txyewy", null));
    }

    [Fact]
    public void EmptyIdentityIsReportedAsUnattributedInsteadOfFabricated()
    {
        Assert.Equal(AttributionDisplayName.Unattributed, AttributionDisplayName.Describe(null, null));
        Assert.Equal(AttributionDisplayName.Unattributed, AttributionDisplayName.Describe("  ", ""));
    }

    [Fact]
    public void DevicePathWithoutFileNameIsUnknown()
    {
        Assert.Equal(AttributionDisplayName.Unknown, AttributionDisplayName.Describe(@"\device\", null));
    }
}