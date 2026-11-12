using HotspotMeter.Core.Abstractions;
using HotspotMeter.Core.Models;
using HotspotMeter.Core.Monitoring;
using HotspotMeter.Infrastructure;
using HotspotMeter.Infrastructure.Monitoring;
using HotspotMeter.Tests.TestDoubles;

namespace HotspotMeter.Tests.Unit.Monitoring;

public sealed class MonitorEngineTests
{
    private const long BaselineTick = 5_000_000;
    private static readonly DateTimeOffset CapturedAt = DateTimeOffset.UnixEpoch;

    private static MonitorEngine CreateEngine(
        FakeHotspotDetector detector,
        FakeTrafficCounter counter,
        IMonotonicClock monotonic,
        FakeWallClock wall,
        FakeUptimeProvider uptime,
        FakeContinuityProvider continuity,
        MonitorOptions? options = null)
        => new(options ?? new MonitorOptions(), TestData.Target, detector, counter, monotonic, wall, uptime, continuity);

    private static BaselineState CreateBaseline()
        => new(
            TestData.InterfaceGuid,
            TestData.Luid,
            1000,
            500,
            CapturedAt,
            BaselineTick,
            BootSignatures.ComputeBootEpoch(CapturedAt, BaselineTick),
            "OPPO Find X9",
            "1A:44:DD:0E:E5:B0");

    private static ResumeContext CreateContext(BaselineState baseline)
        => new(baseline, SessionId: 42, SessionDownloadBytes: 100, SessionUploadBytes: 50, SessionStartedAtUtc: CapturedAt.AddMinutes(-5));

    [Fact]
    public async Task Loop_AccumulatesTrafficAndReportsSessionAtStop()
    {
        var detector = new FakeHotspotDetector { Connection = TestData.Connection() };
        ulong inOctets = 0;
        ulong outOctets = 0;
        var counter = new FakeTrafficCounter
        {
            Provider = () => TestData.Reading(inOctets += 1000, outOctets += 200),
        };

        var engine = CreateEngine(
            detector,
            counter,
            new SystemMonotonicClock(),
            new FakeWallClock(),
            new FakeUptimeProvider(),
            new FakeContinuityProvider(),
            new MonitorOptions { SamplingInterval = TimeSpan.FromMilliseconds(500) });

        var ended = new List<MonitorSessionEndedInfo>();
        engine.SessionEnded += (_, info) => ended.Add(info);

        await engine.StartAsync(CancellationToken.None);
        await Task.Delay(1300);
        await engine.StopAsync();

        Assert.Single(ended);
        Assert.Equal(SessionEndReason.EngineStopped, ended[0].Reason);
        Assert.True(ended[0].DownloadBytes > 0, "采样循环应累加出正的真实流量。");
        Assert.Equal(MonitorState.Stopped, engine.State);
    }

    [Fact]
    public async Task Loop_DetectorFailure_ReportsAnomalyAndPauses()
    {
        var detector = new FakeHotspotDetector { Failure = new InvalidOperationException("WLAN 服务不可用") };
        var counter = new FakeTrafficCounter();

        var engine = CreateEngine(
            detector,
            counter,
            new SystemMonotonicClock(),
            new FakeWallClock(),
            new FakeUptimeProvider(),
            new FakeContinuityProvider(),
            new MonitorOptions { SamplingInterval = TimeSpan.FromMilliseconds(500) });

        var anomalies = new List<MonitorAnomaly>();
        engine.AnomalyDetected += (_, anomaly) => anomalies.Add(anomaly);

        await engine.StartAsync(CancellationToken.None);
        await Task.Delay(1200);
        await engine.StopAsync();

        Assert.Contains(anomalies, a => a.Kind == AnomalyKind.DetectorFailure);
        Assert.Equal(MonitorState.Stopped, engine.State);
    }

    [Fact]
    public async Task Resume_ContinuousEvidence_MergesGapAndResumesSession()
    {
        var baseline = CreateBaseline();
        var detector = new FakeHotspotDetector { Connection = TestData.Connection() };
        var counter = new FakeTrafficCounter { Reading = TestData.Reading(6000, 1000) };
        var wall = new FakeWallClock { UtcNow = CapturedAt.AddSeconds(60) };
        var uptime = new FakeUptimeProvider { TickCount64 = BaselineTick + 60_000 };
        var continuity = new FakeContinuityProvider
        {
            Assessment = new ContinuityAssessment(RecoveryContinuity.Continuous, "测试桩证明连续"),
        };

        var engine = CreateEngine(detector, counter, new FakeMonotonicClock(), wall, uptime, continuity);
        var resumed = new List<MonitorSessionInfo>();
        var ended = new List<MonitorSessionEndedInfo>();
        engine.SessionResumed += (_, info) => resumed.Add(info);
        engine.SessionEnded += (_, info) => ended.Add(info);

        var result = await engine.ResumeAsync(CreateContext(baseline), CancellationToken.None);

        Assert.Equal(ResumeOutcome.Continued, result.Outcome);
        Assert.Empty(ended); // Continuous 不改会话边界。
        Assert.NotNull(result.Gap);
        Assert.Equal(5000, result.Gap!.DownloadBytes);
        Assert.Equal(500, result.Gap.UploadBytes);
        Assert.True(result.Gap.MayMergeIntoSession);
        Assert.Single(resumed);
        Assert.Equal(5100, engine.Current!.SessionDownloadBytes);
        Assert.Equal(550, engine.Current.SessionUploadBytes);
        Assert.False(engine.Current.HasUnmergedRecovery);
    }

    [Fact]
    public async Task Resume_IsIdempotentForSameSessionAndBaseline()
    {
        var baseline = CreateBaseline();
        var detector = new FakeHotspotDetector { Connection = TestData.Connection() };
        var counter = new FakeTrafficCounter { Reading = TestData.Reading(6000, 1000) };
        var wall = new FakeWallClock { UtcNow = CapturedAt.AddSeconds(60) };
        var uptime = new FakeUptimeProvider { TickCount64 = BaselineTick + 60_000 };
        var continuity = new FakeContinuityProvider
        {
            Assessment = new ContinuityAssessment(RecoveryContinuity.Continuous, "测试桩证明连续"),
        };

        var engine = CreateEngine(detector, counter, new FakeMonotonicClock(), wall, uptime, continuity);
        var resumed = new List<MonitorSessionInfo>();
        engine.SessionResumed += (_, info) => resumed.Add(info);

        var context = CreateContext(baseline);
        var first = await engine.ResumeAsync(context, CancellationToken.None);
        var second = await engine.ResumeAsync(context, CancellationToken.None);

        Assert.Equal(ResumeOutcome.Continued, first.Outcome);
        Assert.False(first.AlreadyApplied);
        Assert.True(second.AlreadyApplied);
        Assert.Single(resumed);
        Assert.Equal(5100, engine.Current!.SessionDownloadBytes);
    }

    [Fact]
    public async Task Resume_ConcurrentCalls_ProcessOnlyOnce()
    {
        var baseline = CreateBaseline();
        var detector = new FakeHotspotDetector { Connection = TestData.Connection() };
        var counter = new FakeTrafficCounter { Reading = TestData.Reading(6000, 1000) };
        var wall = new FakeWallClock { UtcNow = CapturedAt.AddSeconds(60) };
        var uptime = new FakeUptimeProvider { TickCount64 = BaselineTick + 60_000 };
        var continuity = new FakeContinuityProvider
        {
            Assessment = new ContinuityAssessment(RecoveryContinuity.Continuous, "测试桩证明连续"),
        };

        var engine = CreateEngine(detector, counter, new FakeMonotonicClock(), wall, uptime, continuity);
        var resumed = 0;
        engine.SessionResumed += (_, _) => Interlocked.Increment(ref resumed);

        var context = CreateContext(baseline);
        var results = await Task.WhenAll(
            engine.ResumeAsync(context, CancellationToken.None),
            engine.ResumeAsync(context, CancellationToken.None));

        Assert.Equal(1, resumed);
        Assert.Single(results, r => !r.AlreadyApplied);
        Assert.Single(results, r => r.AlreadyApplied);
        Assert.Equal(5100, engine.Current!.SessionDownloadBytes);
    }

    [Fact]
    public async Task Resume_UnverifiedEvidence_DoesNotMergeAndRaisesCandidate()
    {
        var baseline = CreateBaseline();
        var detector = new FakeHotspotDetector { Connection = TestData.Connection() };
        var counter = new FakeTrafficCounter { Reading = TestData.Reading(6000, 1000) };
        var wall = new FakeWallClock { UtcNow = CapturedAt.AddSeconds(60) };
        var uptime = new FakeUptimeProvider { TickCount64 = BaselineTick + 60_000 };
        var continuity = new FakeContinuityProvider { Assessment = ContinuityAssessment.Unverified("没有证据") };

        var engine = CreateEngine(detector, counter, new FakeMonotonicClock(), wall, uptime, continuity);
        var resumed = 0;
        var candidates = new List<RecoveryCandidate>();
        var ended = new List<MonitorSessionEndedInfo>();
        var started = new List<MonitorSessionInfo>();
        engine.SessionResumed += (_, _) => resumed++;
        engine.RecoveryCandidateDetected += (_, candidate) => candidates.Add(candidate);
        engine.SessionEnded += (_, info) => ended.Add(info);
        engine.SessionStarted += (_, info) => started.Add(info);

        var result = await engine.ResumeAsync(CreateContext(baseline), CancellationToken.None);

        Assert.Equal(ResumeOutcome.Continued, result.Outcome);
        Assert.False(result.Gap!.MayMergeIntoSession);
        Assert.Equal(0, resumed);
        Assert.Single(candidates);
        Assert.Equal(5000, candidates[0].Gap.DownloadBytes);

        // 旧会话结束，新会话从 0 开始；后续流量只会进入新 Id。
        Assert.Single(ended);
        Assert.Equal(42, ended[0].SessionId);
        Assert.Equal(100, ended[0].DownloadBytes);
        Assert.Equal(50, ended[0].UploadBytes);
        Assert.Equal(SessionEndReason.RecoveryUnconfirmed, ended[0].Reason);
        Assert.Equal(ResumeOutcome.Continued, ended[0].ResumeOutcome);
        Assert.Single(started);
        Assert.Equal(43, started[0].SessionId);
        Assert.Equal(0, started[0].DownloadBytes);
        Assert.Equal(43, engine.Current!.SessionId);
        Assert.Equal(0, engine.Current.SessionDownloadBytes);
        Assert.True(engine.Current.HasUnmergedRecovery);
        Assert.Equal(5000 + 500, engine.Current.UnmergedRecoveryBytes);
    }

    [Fact]
    public async Task Resume_TickRegression_ResetsWithoutSessionOrGap()
    {
        var baseline = CreateBaseline();
        var detector = new FakeHotspotDetector { Connection = TestData.Connection() };
        var counter = new FakeTrafficCounter { Reading = TestData.Reading(6000, 1000) };
        var wall = new FakeWallClock { UtcNow = CapturedAt.AddSeconds(60) };
        var uptime = new FakeUptimeProvider { TickCount64 = BaselineTick - 1_000 };

        var engine = CreateEngine(detector, counter, new FakeMonotonicClock(), wall, uptime, new FakeContinuityProvider());
        var resumed = 0;
        var ended = new List<MonitorSessionEndedInfo>();
        engine.SessionResumed += (_, _) => resumed++;
        engine.SessionEnded += (_, info) => ended.Add(info);

        var context = CreateContext(baseline);
        var result = await engine.ResumeAsync(context, CancellationToken.None);
        var repeated = await engine.ResumeAsync(context, CancellationToken.None);

        Assert.Equal(ResumeOutcome.Rebooted, result.Outcome);
        Assert.Null(result.Gap);
        Assert.Equal(0, resumed);
        Assert.Equal(MonitorState.Rebooted, engine.State);

        Assert.Single(ended);
        Assert.Equal(42, ended[0].SessionId);
        Assert.Equal(100, ended[0].DownloadBytes);
        Assert.Equal(50, ended[0].UploadBytes);
        Assert.Equal(SessionEndReason.RecoveryRejected, ended[0].Reason);
        Assert.Equal(ResumeOutcome.Rebooted, ended[0].ResumeOutcome);
        Assert.Equal(baseline.CapturedAtUtc, ended[0].EndedAtUtc);

        // 重复调用恢复流程不得重复结束同一会话。
        Assert.Equal(ResumeOutcome.Rebooted, repeated.Outcome);
        Assert.Single(ended);
    }

    [Fact]
    public async Task Resume_ConnectedToOtherHotspot_DoesNotContinue()
    {
        // 对应"断开目标热点 → 连别的 Wi-Fi → 再连回"场景中"还连着别家"的时刻：
        // 期间计数器涨了，但增量绝不能算进原会话。
        var baseline = CreateBaseline();
        var detector = new FakeHotspotDetector { Connection = TestData.Connection(ssid: "SchoolWiFi") };
        var counter = new FakeTrafficCounter { Reading = TestData.Reading(6000, 1000) };
        var wall = new FakeWallClock { UtcNow = CapturedAt.AddSeconds(60) };
        var uptime = new FakeUptimeProvider { TickCount64 = BaselineTick + 60_000 };
        var continuity = new FakeContinuityProvider
        {
            Assessment = new ContinuityAssessment(RecoveryContinuity.Continuous, "证据不应被采信"),
        };

        var engine = CreateEngine(detector, counter, new FakeMonotonicClock(), wall, uptime, continuity);
        var resumed = 0;
        engine.SessionResumed += (_, _) => resumed++;

        var result = await engine.ResumeAsync(CreateContext(baseline), CancellationToken.None);

        Assert.Equal(ResumeOutcome.NotOnTarget, result.Outcome);
        Assert.Null(result.Gap);
        Assert.Equal(0, resumed);
    }

    [Fact]
    public async Task Resume_NotOnTarget_DoesNotContinue()
    {
        var baseline = CreateBaseline();
        var detector = new FakeHotspotDetector { Connection = null };
        var counter = new FakeTrafficCounter();
        var wall = new FakeWallClock { UtcNow = CapturedAt.AddSeconds(60) };
        var uptime = new FakeUptimeProvider { TickCount64 = BaselineTick + 60_000 };

        var engine = CreateEngine(detector, counter, new FakeMonotonicClock(), wall, uptime, new FakeContinuityProvider());
        var resumed = 0;
        var ended = new List<MonitorSessionEndedInfo>();
        engine.SessionResumed += (_, _) => resumed++;
        engine.SessionEnded += (_, info) => ended.Add(info);

        var result = await engine.ResumeAsync(CreateContext(baseline), CancellationToken.None);

        Assert.Equal(ResumeOutcome.NotOnTarget, result.Outcome);
        Assert.Equal(0, resumed);
        Assert.Single(ended);
        Assert.Equal(SessionEndReason.RecoveryRejected, ended[0].Reason);
        Assert.Equal(ResumeOutcome.NotOnTarget, ended[0].ResumeOutcome);
    }
}