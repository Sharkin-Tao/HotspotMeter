using HotspotMeter.Core.TrafficGuard;

namespace HotspotMeter.Tests.Unit.TrafficGuard;

public sealed class ProcessStateClassifierTests
{
    [Fact]
    public void ForegroundWindow_WinsEvenWithoutTraffic()
    {
        Assert.Equal(
            GuardActivityState.Foreground,
            ProcessStateClassifier.Classify(isForeground: true, hasVisibleWindow: true, hasNetworkActivity: false));
    }

    [Fact]
    public void VisibleWindowWithoutTraffic_IsBackground_NotIdle()
    {
        Assert.Equal(
            GuardActivityState.Background,
            ProcessStateClassifier.Classify(isForeground: false, hasVisibleWindow: true, hasNetworkActivity: false));
    }

    [Fact]
    public void NoWindowButNetworkActivity_IsBackground()
    {
        // 托盘程序/更新服务没有可见窗口，但确实在传流量，不能判成 Idle。
        Assert.Equal(
            GuardActivityState.Background,
            ProcessStateClassifier.Classify(isForeground: false, hasVisibleWindow: false, hasNetworkActivity: true));
    }

    [Fact]
    public void NoForegroundNoWindowNoTraffic_IsIdle()
    {
        Assert.Equal(
            GuardActivityState.Idle,
            ProcessStateClassifier.Classify(isForeground: false, hasVisibleWindow: false, hasNetworkActivity: false));
    }
}