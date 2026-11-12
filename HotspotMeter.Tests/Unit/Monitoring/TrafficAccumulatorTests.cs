using HotspotMeter.Core.Models;
using HotspotMeter.Core.Monitoring;
using HotspotMeter.Tests.TestDoubles;

namespace HotspotMeter.Tests.Unit.Monitoring;

public sealed class TrafficAccumulatorTests
{
    private static TrafficAccumulator NewAccumulator(MonitorOptions? options = null)
        => new(TestData.Target, options ?? new MonitorOptions());

    [Fact]
    public void FirstFrame_CreatesSessionWithZeroDelta()
    {
        var accumulator = NewAccumulator();

        var step = accumulator.Process(TestData.Frame(0, 1000, 500));

        Assert.NotNull(step.SessionStarted);
        Assert.Equal(1, step.SessionStarted!.SessionId);
        Assert.Equal(TestData.InterfaceGuid, step.SessionStarted.InterfaceGuid);
        Assert.Equal(0, step.Snapshot.SessionDownloadBytes);
        Assert.Equal(0, step.Snapshot.SessionUploadBytes);
        Assert.Equal(MonitorState.Running, step.Snapshot.State);
        Assert.True(step.Snapshot.IsTargetConnected);
    }

    [Fact]
    public void SecondFrame_AccumulatesDeltaAndComputesRate()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));

        var step = accumulator.Process(TestData.Frame(1000, 3000, 700));

        Assert.Equal(2000, step.Snapshot.SessionDownloadBytes);
        Assert.Equal(200, step.Snapshot.SessionUploadBytes);
        Assert.Equal(2000.0, step.Snapshot.DownloadBytesPerSecond, 3);
        Assert.Equal(200.0, step.Snapshot.UploadBytesPerSecond, 3);
        Assert.Equal(1000, step.Snapshot.GapMilliseconds);
    }

    [Fact]
    public void CounterReset_RebuildsBaselineAndSkipsTwoConfirmFrames()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));
        accumulator.Process(TestData.Frame(1000, 5000, 1500)); // +4000 / +1000

        var reset = accumulator.Process(TestData.Frame(2000, 100, 50));
        Assert.Equal(MonitorState.CounterReset, reset.Snapshot.State);
        Assert.Equal(4000, reset.Snapshot.SessionDownloadBytes);
        Assert.Equal(2, reset.Snapshot.PendingConfirmFrames);

        var firstConfirm = accumulator.Process(TestData.Frame(3000, 200, 80));
        Assert.Equal(1, firstConfirm.Snapshot.PendingConfirmFrames);
        Assert.Equal(4000, firstConfirm.Snapshot.SessionDownloadBytes);

        var secondConfirm = accumulator.Process(TestData.Frame(4000, 400, 120));
        Assert.Equal(0, secondConfirm.Snapshot.PendingConfirmFrames);
        Assert.Equal(4000, secondConfirm.Snapshot.SessionDownloadBytes);

        var normal = accumulator.Process(TestData.Frame(5000, 700, 200));
        Assert.Equal(4300, normal.Snapshot.SessionDownloadBytes);
        Assert.Equal(1080, normal.Snapshot.SessionUploadBytes);
    }

    [Fact]
    public void LongGap_IsTreatedAsSleepAndRebuildsBaseline()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));

        var step = accumulator.Process(TestData.Frame(5000, 9000, 4000));

        Assert.Equal(MonitorState.Suspended, step.Snapshot.State);
        Assert.Equal(1, step.Snapshot.WakeRebuilds);
        Assert.Equal(0, step.Snapshot.SessionDownloadBytes);
        Assert.Equal(0.0, step.Snapshot.DownloadBytesPerSecond, 3);
        Assert.Equal(2, step.Snapshot.PendingConfirmFrames);
    }

    [Fact]
    public void AfterSleep_TwoConfirmFramesThenAccumulate()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));
        accumulator.Process(TestData.Frame(5000, 9000, 4000)); // 睡眠被识别，基线已重建

        accumulator.Process(TestData.Frame(6000, 10000, 4200));
        accumulator.Process(TestData.Frame(7000, 11000, 4400));
        Assert.Equal(0, accumulator.Current!.SessionDownloadBytes);

        var step = accumulator.Process(TestData.Frame(8000, 12000, 4600));
        Assert.Equal(1000, step.Snapshot.SessionDownloadBytes);
        Assert.Equal(200, step.Snapshot.SessionUploadBytes);
    }

    [Fact]
    public void MissingReading_PausesButKeepsSession()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));

        var paused = accumulator.Process(TestData.FrameWithoutReading(1000));

        Assert.Equal(MonitorState.InterfaceMissing, paused.Snapshot.State);
        Assert.Null(paused.SessionEnded);
        Assert.True(paused.Snapshot.HasSession);
        Assert.Equal(1, paused.Snapshot.SessionId);

        var resumed = accumulator.Process(TestData.Frame(2000, 5000, 1500));
        Assert.Equal(MonitorState.Running, resumed.Snapshot.State);
        Assert.Equal(1, resumed.Snapshot.SessionId);
        Assert.Equal(0, resumed.Snapshot.SessionDownloadBytes);
    }

    [Fact]
    public void RealDisconnect_EndsSessionWithTotals()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));
        accumulator.Process(TestData.Frame(1000, 3000, 900));

        var frame = new MonitorFrame(DateTimeOffset.UnixEpoch.AddSeconds(2), 2000, null, null, 5_000_000);
        var step = accumulator.Process(frame);

        Assert.NotNull(step.SessionEnded);
        Assert.Equal(SessionEndReason.Disconnected, step.SessionEnded!.Reason);
        Assert.Equal(2000, step.SessionEnded.DownloadBytes);
        Assert.Equal(400, step.SessionEnded.UploadBytes);
        Assert.False(step.Snapshot.HasSession);
    }

    [Fact]
    public void DifferentSsid_EndsSessionWithSsidChanged()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));

        var step = accumulator.Process(TestData.Frame(1000, 2000, 600, TestData.Connection(ssid: "SchoolWiFi")));

        Assert.NotNull(step.SessionEnded);
        Assert.Equal(SessionEndReason.SsidChanged, step.SessionEnded!.Reason);
        Assert.False(step.Snapshot.IsTargetConnected);
    }

    [Fact]
    public void DifferentInterface_EndsSessionWithInterfaceChanged()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));

        var step = accumulator.Process(TestData.Frame(1000, 2000, 600, TestData.Connection(guid: TestData.OtherInterfaceGuid)));

        Assert.NotNull(step.SessionEnded);
        Assert.Equal(SessionEndReason.InterfaceChanged, step.SessionEnded!.Reason);
    }

    [Fact]
    public void DetectorFailure_PausesWithoutEndingSession()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));

        var step = accumulator.Process(TestData.FrameWithoutReading(1000, detectorFailed: true));

        Assert.Null(step.SessionEnded);
        Assert.Equal(MonitorState.InterfaceMissing, step.Snapshot.State);
        Assert.Equal(1, step.Snapshot.SessionId);
    }

    [Fact]
    public void BssidChange_IsDiagnosticOnly()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));

        var step = accumulator.Process(TestData.Frame(1000, 3000, 700, TestData.Connection(bssid: "AA:BB:CC:DD:EE:FF")));

        Assert.NotNull(step.BssidChanged);
        Assert.Null(step.SessionEnded);
        Assert.Equal(2000, step.Snapshot.SessionDownloadBytes);
        Assert.Equal(1, step.Snapshot.SessionId);
    }

    [Fact]
    public void ImplausibleDelta_WarnKeepsBytesAndReportsAnomaly()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 0, 0));

        var step = accumulator.Process(TestData.Frame(1000, 500_000_000, 0));

        Assert.NotNull(step.Anomaly);
        Assert.Equal(AnomalyKind.ImplausibleDelta, step.Anomaly!.Kind);
        Assert.Equal(500_000_000, step.Snapshot.SessionDownloadBytes);
    }

    [Fact]
    public void ImplausibleDelta_DropDiscardsFrame()
    {
        var accumulator = NewAccumulator(new MonitorOptions { Plausibility = PlausibilityMode.Drop });
        accumulator.Process(TestData.Frame(0, 0, 0));

        var step = accumulator.Process(TestData.Frame(1000, 500_000_000, 0));

        Assert.NotNull(step.Anomaly);
        Assert.Equal(0, step.Snapshot.SessionDownloadBytes);
        Assert.Equal(0.0, step.Snapshot.DownloadBytesPerSecond, 3);
    }

    [Fact]
    public void ImplausibleDelta_OffNeverReportsAnomaly()
    {
        var accumulator = NewAccumulator(new MonitorOptions { Plausibility = PlausibilityMode.Off });
        accumulator.Process(TestData.Frame(0, 0, 0));

        var step = accumulator.Process(TestData.Frame(1000, 500_000_000, 0));

        Assert.Null(step.Anomaly);
        Assert.Equal(500_000_000, step.Snapshot.SessionDownloadBytes);
    }

    [Fact]
    public void UnknownLinkSpeed_NeverReportsAnomaly()
    {
        var accumulator = NewAccumulator();
        var slowLink = TestData.Connection(speedBps: 0);
        accumulator.Process(TestData.Frame(0, 0, 0, slowLink));

        var step = accumulator.Process(TestData.Frame(1000, 500_000_000, 0, slowLink));

        Assert.Null(step.Anomaly);
        Assert.Equal(500_000_000, step.Snapshot.SessionDownloadBytes);
    }

    [Fact]
    public void Stop_EndsSessionAsEngineStopped()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));
        accumulator.Process(TestData.Frame(1000, 2000, 600));

        var step = accumulator.Stop(DateTimeOffset.UnixEpoch.AddSeconds(2));

        Assert.NotNull(step.SessionEnded);
        Assert.Equal(SessionEndReason.EngineStopped, step.SessionEnded!.Reason);
        Assert.Equal(1000, step.SessionEnded.DownloadBytes);
        Assert.Equal(MonitorState.Stopped, step.Snapshot.State);
        Assert.False(step.Snapshot.HasSession);
    }

    [Fact]
    public void AdoptResumedSession_MergesGapOnlyWhenAsked()
    {
        var accumulator = NewAccumulator();
        var context = new ResumeContext(
            new BaselineState(TestData.InterfaceGuid, TestData.Luid, 1000, 500, DateTimeOffset.UnixEpoch, 5_000_000, DateTimeOffset.UnixEpoch.AddMilliseconds(-5_000_000), "OPPO Find X9", null),
            SessionId: 7,
            SessionDownloadBytes: 100,
            SessionUploadBytes: 50,
            SessionStartedAtUtc: DateTimeOffset.UnixEpoch.AddMinutes(-5));
        var gap = new RecoveryGap("7:key", 5000, 1000, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), RecoveryContinuity.Continuous, "有证据");

        var step = accumulator.AdoptResumedSession(context, gap, mergeGap: true, TestData.Frame(1000, 7000, 2000));

        Assert.NotNull(step.SessionResumed);
        Assert.Null(step.SessionEnded);
        Assert.Null(step.SessionStarted);
        Assert.Equal(7, step.Snapshot.SessionId);
        Assert.Equal(5100, step.Snapshot.SessionDownloadBytes);
        Assert.Equal(1050, step.Snapshot.SessionUploadBytes);
        Assert.False(step.Snapshot.HasUnmergedRecovery);
    }

    [Fact]
    public void AdoptResumedSession_Unconfirmed_EndsOldSessionAndStartsNewOne()
    {
        var accumulator = NewAccumulator();
        var context = CreateResumeContext(sessionId: 7);
        var gap = new RecoveryGap("7:key", 5000, 1000, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), RecoveryContinuity.Unverified, "无证据");

        var step = accumulator.AdoptResumedSession(context, gap, mergeGap: false, TestData.Frame(1000, 7000, 2000));

        Assert.NotNull(step.SessionEnded);
        Assert.Equal(7, step.SessionEnded!.SessionId);
        Assert.Equal(100, step.SessionEnded.DownloadBytes);
        Assert.Equal(50, step.SessionEnded.UploadBytes);
        Assert.Equal(SessionEndReason.RecoveryUnconfirmed, step.SessionEnded.Reason);
        Assert.Equal(ResumeOutcome.Continued, step.SessionEnded.ResumeOutcome);
        Assert.Equal(DateTimeOffset.UnixEpoch, step.SessionEnded.EndedAtUtc);

        Assert.NotNull(step.SessionStarted);
        Assert.Equal(8, step.SessionStarted!.SessionId);
        Assert.Equal(0, step.SessionStarted.DownloadBytes);
        Assert.Equal(0, step.SessionStarted.UploadBytes);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), step.SessionStarted.StartedAtUtc);
        Assert.Equal(8, step.Snapshot.SessionId);
        Assert.Null(step.SessionResumed);

        Assert.True(step.Snapshot.HasUnmergedRecovery);
        Assert.Equal(6000, step.Snapshot.UnmergedRecoveryBytes);
    }

    [Fact]
    public void AdoptResumedSession_Unconfirmed_NextFrameGoesToNewSession()
    {
        var accumulator = NewAccumulator();
        var context = CreateResumeContext(sessionId: 7);
        var gap = new RecoveryGap("7:key", 5000, 1000, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), RecoveryContinuity.Unverified, "无证据");
        accumulator.AdoptResumedSession(context, gap, mergeGap: false, TestData.Frame(1000, 7000, 2000));

        var next = accumulator.Process(TestData.Frame(2000, 8000, 2200));

        Assert.Equal(8, next.Snapshot.SessionId);
        Assert.Equal(1000, next.Snapshot.SessionDownloadBytes);
        Assert.Equal(200, next.Snapshot.SessionUploadBytes);
        Assert.Null(next.SessionEnded);
    }

    [Fact]
    public void AdoptResumedSession_Interrupted_BehavesLikeUnverified()
    {
        var accumulator = NewAccumulator();
        var context = CreateResumeContext(sessionId: 7);
        var gap = new RecoveryGap("7:key", 5000, 1000, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), RecoveryContinuity.Interrupted, "有中断证据");

        var step = accumulator.AdoptResumedSession(context, gap, mergeGap: false, TestData.Frame(1000, 7000, 2000));

        Assert.Equal(SessionEndReason.RecoveryUnconfirmed, step.SessionEnded!.Reason);
        Assert.Equal(8, step.Snapshot.SessionId);
        Assert.Equal(0, step.Snapshot.SessionDownloadBytes);
        Assert.Equal(6000, step.Snapshot.UnmergedRecoveryBytes);
    }

    [Fact]
    public void ResetForResume_EndsOldSessionOnceWithConcreteOutcome()
    {
        var accumulator = NewAccumulator();
        var context = CreateResumeContext(sessionId: 7);
        var now = DateTimeOffset.UnixEpoch.AddMinutes(10);

        var first = accumulator.ResetForResume(context, ResumeOutcome.Rebooted, now);
        var second = accumulator.ResetForResume(context, ResumeOutcome.Rebooted, now);

        Assert.NotNull(first.SessionEnded);
        Assert.Equal(7, first.SessionEnded!.SessionId);
        Assert.Equal(100, first.SessionEnded.DownloadBytes);
        Assert.Equal(50, first.SessionEnded.UploadBytes);
        Assert.Equal(SessionEndReason.RecoveryRejected, first.SessionEnded.Reason);
        Assert.Equal(ResumeOutcome.Rebooted, first.SessionEnded.ResumeOutcome);
        Assert.Equal(context.Baseline.CapturedAtUtc, first.SessionEnded.EndedAtUtc);
        Assert.Null(second.SessionEnded);
        Assert.Equal(MonitorState.Rebooted, first.Snapshot.State);
    }

    [Fact]
    public void ResetForResume_NextSessionIdDoesNotReuseOldId()
    {
        var accumulator = NewAccumulator();
        var context = CreateResumeContext(sessionId: 7);

        accumulator.ResetForResume(context, ResumeOutcome.NotOnTarget, DateTimeOffset.UnixEpoch.AddMinutes(10));
        var step = accumulator.Process(TestData.Frame(2000, 100, 50));

        Assert.NotNull(step.SessionStarted);
        Assert.Equal(8, step.SessionStarted!.SessionId);
        Assert.Equal(0, step.Snapshot.SessionDownloadBytes);
    }

    private static ResumeContext CreateResumeContext(long sessionId)
        => new(
            new BaselineState(TestData.InterfaceGuid, TestData.Luid, 1000, 500, DateTimeOffset.UnixEpoch, 5_000_000, DateTimeOffset.UnixEpoch.AddMilliseconds(-5_000_000), "OPPO Find X9", null),
            sessionId,
            SessionDownloadBytes: 100,
            SessionUploadBytes: 50,
            SessionStartedAtUtc: DateTimeOffset.UnixEpoch.AddMinutes(-5));

    [Fact]
    public void CaptureBaseline_ReturnsSessionIdentityAndCounters()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));

        var baseline = accumulator.CaptureBaseline();

        Assert.NotNull(baseline);
        Assert.Equal(1000UL, baseline!.InOctets);
        Assert.Equal(TestData.Luid, baseline.InterfaceLuid);
        Assert.Equal("OPPO Find X9", baseline.Ssid);
        Assert.Equal(TestData.InterfaceGuid, baseline.InterfaceGuid);
    }

    [Fact]
    public void WallClockJump_DoesNotAffectSpeedOrDelta()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 0, 0));

        // 墙钟被向前调了 2 小时，但单调时钟只前进 1 秒：速度必须仍按 1 秒计算。
        var jumped = new MonitorFrame(
            DateTimeOffset.UnixEpoch.AddHours(2).AddSeconds(1),
            1000,
            TestData.Connection(),
            TestData.Reading(1000, 200),
            5_000_000);

        var step = accumulator.Process(jumped);

        Assert.Equal(1000, step.Snapshot.SessionDownloadBytes);
        Assert.Equal(1000.0, step.Snapshot.DownloadBytesPerSecond, 3);
        Assert.Null(step.Anomaly);
    }

    [Fact]
    public void Reconnect_CreatesNewSessionId()
    {
        var accumulator = NewAccumulator();
        accumulator.Process(TestData.Frame(0, 1000, 500));
        accumulator.Process(new MonitorFrame(DateTimeOffset.UnixEpoch.AddSeconds(1), 1000, null, null, 5_000_000));

        var step = accumulator.Process(TestData.Frame(2000, 100, 50));

        Assert.NotNull(step.SessionStarted);
        Assert.Equal(2, step.SessionStarted!.SessionId);
    }
}