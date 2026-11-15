using HotspotMeter.Core.Models;
using HotspotMeter.Core.Monitoring;
using HotspotMeter.Tests.TestDoubles;

namespace HotspotMeter.Tests.Unit.Monitoring;

public sealed class BaselineResumeEvaluatorTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(10);

    private static BaselineState Baseline(
        long tickCount64 = 5_000_000,
        ulong inOctets = 1000,
        ulong outOctets = 500,
        DateTimeOffset? capturedAt = null,
        ulong luid = TestData.Luid,
        string ssid = "TestHotspot")
    {
        var at = capturedAt ?? DateTimeOffset.UnixEpoch;
        return new BaselineState(
            TestData.InterfaceGuid,
            luid,
            inOctets,
            outOctets,
            at,
            tickCount64,
            BootSignatures.ComputeBootEpoch(at, tickCount64),
            ssid,
            "02:00:00:00:00:01");
    }

    [Fact]
    public void Continued_ComputesGapWhenEverythingMatches()
    {
        var baseline = Baseline();
        var now = DateTimeOffset.UnixEpoch.AddSeconds(60);
        var reading = TestData.Reading(6000, 1000);

        var evaluation = BaselineResumeEvaluator.Evaluate(
            baseline, reading, TestData.Connection(), 5_060_000, now, Tolerance);

        Assert.Equal(ResumeOutcome.Continued, evaluation.Outcome);
        Assert.Equal(5000, evaluation.DownloadBytes);
        Assert.Equal(500, evaluation.UploadBytes);
        Assert.Equal(RebootEvidence.None, evaluation.Evidence);
    }

    [Fact]
    public void TickRegression_IsDefinitiveReboot()
    {
        var evaluation = BaselineResumeEvaluator.Evaluate(
            Baseline(), TestData.Reading(6000, 1000), TestData.Connection(), 4_000_000, DateTimeOffset.UnixEpoch.AddSeconds(60), Tolerance);

        Assert.Equal(ResumeOutcome.Rebooted, evaluation.Outcome);
        Assert.Equal(RebootEvidence.TickRegression, evaluation.Evidence);
        Assert.Equal(0, evaluation.DownloadBytes);
        Assert.Equal(0, evaluation.UploadBytes);
    }

    [Fact]
    public void MissingRow_IsInterfaceMissing()
    {
        var evaluation = BaselineResumeEvaluator.Evaluate(
            Baseline(), null, TestData.Connection(), 5_060_000, DateTimeOffset.UnixEpoch.AddSeconds(60), Tolerance);

        Assert.Equal(ResumeOutcome.InterfaceMissing, evaluation.Outcome);
        Assert.Equal(0, evaluation.DownloadBytes);
    }

    [Fact]
    public void LuidChange_IsInterfaceChanged()
    {
        var reading = new InterfaceReading(
            new NetworkInterfaceKey(TestData.InterfaceGuid, 0x9999),
            new InterfaceCounters(6000, 1000));

        var evaluation = BaselineResumeEvaluator.Evaluate(
            Baseline(), reading, TestData.Connection(), 5_060_000, DateTimeOffset.UnixEpoch.AddSeconds(60), Tolerance);

        Assert.Equal(ResumeOutcome.InterfaceChanged, evaluation.Outcome);
    }

    [Fact]
    public void CounterDecrease_IsCounterReset()
    {
        var evaluation = BaselineResumeEvaluator.Evaluate(
            Baseline(), TestData.Reading(100, 50), TestData.Connection(), 5_060_000, DateTimeOffset.UnixEpoch.AddSeconds(60), Tolerance);

        Assert.Equal(ResumeOutcome.CounterReset, evaluation.Outcome);
    }

    [Fact]
    public void NoConnection_IsNotOnTarget()
    {
        var evaluation = BaselineResumeEvaluator.Evaluate(
            Baseline(), TestData.Reading(6000, 1000), null, 5_060_000, DateTimeOffset.UnixEpoch.AddSeconds(60), Tolerance);

        Assert.Equal(ResumeOutcome.NotOnTarget, evaluation.Outcome);
        Assert.Equal(0, evaluation.DownloadBytes);
    }

    [Fact]
    public void OtherSsid_IsNotOnTarget()
    {
        var evaluation = BaselineResumeEvaluator.Evaluate(
            Baseline(), TestData.Reading(6000, 1000), TestData.Connection(ssid: "SchoolWiFi"), 5_060_000, DateTimeOffset.UnixEpoch.AddSeconds(60), Tolerance);

        Assert.Equal(ResumeOutcome.NotOnTarget, evaluation.Outcome);
    }

    [Fact]
    public void BootEpochShift_BeyondTolerance_IsConservativeReboot()
    {
        // Tick 未前进但墙钟快进 11 分钟：无法区分"重启"与"改时间"，保守判重启。
        var now = DateTimeOffset.UnixEpoch.AddMinutes(11);
        var evaluation = BaselineResumeEvaluator.Evaluate(
            Baseline(), TestData.Reading(6000, 1000), TestData.Connection(), 5_000_000, now, Tolerance);

        Assert.Equal(ResumeOutcome.Rebooted, evaluation.Outcome);
        Assert.Equal(RebootEvidence.BootEpochShift, evaluation.Evidence);
    }

    [Fact]
    public void BootEpochShift_ExactlyAtTolerance_IsStillContinued()
    {
        // 边界：正好 10 分钟不算越界（判定条件为严格大于）。
        var now = DateTimeOffset.UnixEpoch.AddMinutes(10);
        var evaluation = BaselineResumeEvaluator.Evaluate(
            Baseline(), TestData.Reading(6000, 1000), TestData.Connection(), 5_000_000, now, Tolerance);

        Assert.Equal(ResumeOutcome.Continued, evaluation.Outcome);
    }

    [Fact]
    public void NegativeTolerance_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BaselineResumeEvaluator.Evaluate(
            Baseline(), TestData.Reading(6000, 1000), TestData.Connection(), 5_060_000, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void RecoveryId_IsStableAndDependsOnSessionAndBaseline()
    {
        var baseline = Baseline();

        var first = RecoveryId.Create(3, baseline);
        var second = RecoveryId.Create(3, baseline);
        var otherSession = RecoveryId.Create(4, baseline);

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherSession);
        Assert.NotEqual(first, RecoveryId.Create(3, baseline with { InOctets = 1001 }));
    }
}