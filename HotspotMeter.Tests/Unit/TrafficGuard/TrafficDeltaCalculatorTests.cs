using HotspotMeter.Core.TrafficGuard;

namespace HotspotMeter.Tests.Unit.TrafficGuard;

public sealed class TrafficDeltaCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(90);
    private const string Profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string App = "exe:c:\\apps\\demo.exe";

    [Fact]
    public void FirstObservation_OnlyBuildsBaseline()
    {
        var calculator = new TrafficDeltaCalculator();

        var result = calculator.Compute(Profile, App, 1_000, 200, T0, Gap);

        Assert.False(result.HasSample);
        Assert.True(result.QualityFlags.HasFlag(GuardQualityFlags.Baseline));
        Assert.True(result.ProfileChanged);
        Assert.Equal(0, result.DownloadBytes);
    }

    [Fact]
    public void SecondObservation_ReturnsOnlyTheDelta()
    {
        var calculator = new TrafficDeltaCalculator();
        calculator.Compute(Profile, App, 1_000, 200, T0, Gap);

        var result = calculator.Compute(Profile, App, 1_600, 250, T0.AddSeconds(30), Gap);

        Assert.True(result.HasSample);
        Assert.Equal(600, result.DownloadBytes);
        Assert.Equal(50, result.UploadBytes);
        Assert.Equal(T0, result.FromUtc);
        Assert.Equal(T0.AddSeconds(30), result.ToUtc);
        Assert.False(result.QualityFlags.HasFlag(GuardQualityFlags.IntervalGap));
    }

    [Fact]
    public void SameTimestamp_DoesNotProduceASecondIncrement()
    {
        var calculator = new TrafficDeltaCalculator();
        calculator.Compute(Profile, App, 1_000, 200, T0, Gap);

        var result = calculator.Compute(Profile, App, 2_000, 300, T0, Gap);

        Assert.False(result.HasSample);
    }

    [Fact]
    public void AnyCounterGoingBackwards_RebuildsBothDirections()
    {
        var calculator = new TrafficDeltaCalculator();
        calculator.Compute(Profile, App, 1_000, 500, T0, Gap);

        var reset = calculator.Compute(Profile, App, 900, 700, T0.AddSeconds(30), Gap);

        Assert.False(reset.HasSample);
        Assert.True(reset.QualityFlags.HasFlag(GuardQualityFlags.CounterReset));

        // 回退后新基线是 (900,700)，下一轮不能把回退差额当流量。
        var next = calculator.Compute(Profile, App, 950, 800, T0.AddSeconds(60), Gap);
        Assert.True(next.HasSample);
        Assert.Equal(50, next.DownloadBytes);
        Assert.Equal(100, next.UploadBytes);
    }

    [Fact]
    public void ProfileChange_ClearsOldNetworkBaselines()
    {
        var calculator = new TrafficDeltaCalculator();
        calculator.Compute(Profile, App, 1_000, 200, T0, Gap);

        var changed = calculator.Compute("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", App, 10_000, 2_000, T0.AddSeconds(30), Gap);

        Assert.False(changed.HasSample);
        Assert.True(changed.ProfileChanged);
        Assert.True(changed.QualityFlags.HasFlag(GuardQualityFlags.ProfileChanged));
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", calculator.ProfileKey);
    }

    [Fact]
    public void CompleteCycle_RemovesProgramsMissingFromTheSnapshot()
    {
        var calculator = new TrafficDeltaCalculator();
        calculator.Compute(Profile, App, 1_000, 200, T0, Gap);
        calculator.Compute(Profile, "exe:c:\\apps\\other.exe", 10, 20, T0, Gap);

        var removed = calculator.CompleteCycle(new[] { App });

        Assert.Equal(1, removed);

        // 重新出现时只能重建基线，不能把缺席期间算成一个长区间。
        var reappeared = calculator.Compute(Profile, "exe:c:\\apps\\other.exe", 500, 50, T0.AddMinutes(1), Gap);
        Assert.False(reappeared.HasSample);
        Assert.True(reappeared.QualityFlags.HasFlag(GuardQualityFlags.Baseline));
    }

    [Fact]
    public void LongGap_IsMarkedButDeltaIsStillReported()
    {
        var calculator = new TrafficDeltaCalculator();
        calculator.Compute(Profile, App, 1_000, 200, T0, Gap);

        var result = calculator.Compute(Profile, App, 2_000, 300, T0.AddSeconds(120), Gap);

        Assert.True(result.HasSample);
        Assert.True(result.QualityFlags.HasFlag(GuardQualityFlags.IntervalGap));
    }

    [Fact]
    public void RestoreState_RollsBackWhenTheWriteFails()
    {
        var calculator = new TrafficDeltaCalculator();
        calculator.Compute(Profile, App, 1_000, 200, T0, Gap);
        var state = calculator.CaptureState();

        calculator.Compute(Profile, App, 2_000, 300, T0.AddSeconds(30), Gap);
        calculator.RestoreState(state);

        var retried = calculator.Compute(Profile, App, 2_000, 300, T0.AddSeconds(60), Gap);
        Assert.True(retried.HasSample);
        Assert.Equal(1_000, retried.DownloadBytes);
        Assert.Equal(100, retried.UploadBytes);
    }

    [Fact]
    public void NegativeCumulativeValue_IsRejectedRatherThanClampedToZero()
    {
        var calculator = new TrafficDeltaCalculator();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            calculator.Compute(Profile, App, -1, 0, T0, Gap));
    }

    [Fact]
    public void ZeroDelta_IsNotASample()
    {
        var calculator = new TrafficDeltaCalculator();
        calculator.Compute(Profile, App, 1_000, 200, T0, Gap);

        var result = calculator.Compute(Profile, App, 1_000, 200, T0.AddSeconds(30), Gap);

        Assert.False(result.HasSample);
        Assert.Equal(GuardQualityFlags.None, result.QualityFlags);
    }
}