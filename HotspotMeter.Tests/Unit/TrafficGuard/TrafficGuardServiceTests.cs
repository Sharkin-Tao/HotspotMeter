using HotspotMeter.Core.TrafficGuard;

namespace HotspotMeter.Tests.Unit.TrafficGuard;

public sealed class TrafficGuardServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private static readonly GuardTarget Target = new(
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "TestHotspot");

    private static readonly GuardUsageEntry DemoUsage =
        new(@"C:\Apps\demo.exe", "demo.exe", 1_000, 200);

    [Fact]
    public async Task FirstCapture_EstablishesBaselineWithoutWritingFakeZeroTraffic()
    {
        var env = new Environment(snapshot: Available(DemoUsage), processes: DemoProcesses());

        var result = await env.Service.CaptureOnceAsync();

        Assert.True(result.TargetAvailable);
        Assert.Equal(0, result.SampleCount);
        Assert.Empty(env.Sink.Batches);
    }

    [Fact]
    public async Task SecondCapture_WritesOneSampleAggregatedAcrossPids()
    {
        var env = new Environment(snapshot: Available(DemoUsage), processes: DemoProcesses(foreground: true));
        await env.Service.CaptureOnceAsync();

        env.Time.Now = T0.AddSeconds(30);
        env.Source.Snapshot = Available(new GuardUsageEntry(@"C:\Apps\demo.exe", "demo.exe", 1_600, 250));
        var result = await env.Service.CaptureOnceAsync();

        Assert.Equal(1, result.SampleCount);
        var sample = Assert.Single(env.Sink.Batches.SelectMany(batch => batch));
        Assert.Equal(600, sample.DownloadBytes);
        Assert.Equal(50, sample.UploadBytes);
        Assert.Equal(2, sample.ProcessCount);
        Assert.Equal(GuardActivityState.Foreground, sample.ActivityState);
        Assert.True(sample.IdentityCertain);
    }

    [Fact]
    public async Task UnavailableApi_DoesNotWriteZeroSamples_AndNextCaptureRebuildsBaseline()
    {
        var env = new Environment(snapshot: Available(DemoUsage), processes: DemoProcesses());
        await env.Service.CaptureOnceAsync();

        env.Time.Now = T0.AddSeconds(30);
        env.Source.Snapshot = GuardUsageSnapshot.Unavailable("ProbeFailure", env.Time.GetUtcNow());
        var unavailable = await env.Service.CaptureOnceAsync();

        Assert.False(unavailable.DataAvailable);
        Assert.Empty(env.Sink.Batches);

        // API 恢复后累计值已增长，但基准已被清空：必须先建基线，不能把整段当本轮增量。
        env.Time.Now = T0.AddSeconds(60);
        env.Source.Snapshot = Available(new GuardUsageEntry(@"C:\Apps\demo.exe", "demo.exe", 5_000, 500));
        var recovered = await env.Service.CaptureOnceAsync();

        Assert.Equal(0, recovered.SampleCount);
        Assert.Empty(env.Sink.Batches);
        Assert.True(env.Diagnostics.Messages.Count > 0);
    }

    [Fact]
    public async Task MissingTarget_ResetsBaselines()
    {
        var env = new Environment(snapshot: Available(DemoUsage), processes: DemoProcesses());
        await env.Service.CaptureOnceAsync();

        env.Targets.Current = null;
        var result = await env.Service.CaptureOnceAsync();

        Assert.False(result.TargetAvailable);
        Assert.Equal("TargetUnavailable", result.Status);

        env.Targets.Current = Target;
        env.Time.Now = T0.AddSeconds(30);
        env.Source.Snapshot = Available(new GuardUsageEntry(@"C:\Apps\demo.exe", "demo.exe", 9_000, 900));
        var resumed = await env.Service.CaptureOnceAsync();

        Assert.Equal(0, resumed.SampleCount);
    }

    [Fact]
    public async Task EmptyAttributionId_IsSkipped_NotAssignedToAnyProgram()
    {
        var env = new Environment(
            snapshot: Available(
                new GuardUsageEntry(string.Empty, string.Empty, 4_000, 500),
                DemoUsage),
            processes: DemoProcesses());

        var result = await env.Service.CaptureOnceAsync();

        Assert.Equal(1, result.SkippedMissingEntries);
        Assert.Equal(0, result.SampleCount);
    }

    [Fact]
    public async Task ProfileChange_StartsANewBaselineForTheNewNetwork()
    {
        var env = new Environment(snapshot: Available(DemoUsage), processes: DemoProcesses());
        await env.Service.CaptureOnceAsync();

        env.Time.Now = T0.AddSeconds(30);
        var changed = new GuardTarget(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "OtherHotspot");
        env.Targets.Current = changed;
        env.Source.Snapshot = Available(new GuardUsageEntry(@"C:\Apps\demo.exe", "demo.exe", 50_000, 5_000));
        var result = await env.Service.CaptureOnceAsync();

        Assert.True(result.ProfileChanged);
        Assert.Equal(0, result.SampleCount);
    }

    [Fact]
    public async Task SinkFailure_RollsBackSoTheNextCycleDoesNotLoseBytes()
    {
        var env = new Environment(snapshot: Available(DemoUsage), processes: DemoProcesses());
        await env.Service.CaptureOnceAsync();

        env.Time.Now = T0.AddSeconds(30);
        env.Source.Snapshot = Available(new GuardUsageEntry(@"C:\Apps\demo.exe", "demo.exe", 1_600, 250));
        env.Sink.FailNext = true;
        var failed = await env.Service.CaptureOnceAsync();
        Assert.Equal(0, failed.SampleCount);
        Assert.Equal("WriteFailed", failed.Status);

        env.Time.Now = T0.AddSeconds(60);
        env.Source.Snapshot = Available(new GuardUsageEntry(@"C:\Apps\demo.exe", "demo.exe", 1_600, 250));
        var retried = await env.Service.CaptureOnceAsync();

        Assert.Equal(1, retried.SampleCount);
        var sample = Assert.Single(env.Sink.Batches.SelectMany(batch => batch));
        Assert.Equal(600, sample.DownloadBytes);
        Assert.Equal(50, sample.UploadBytes);
    }

    [Fact]
    public async Task Lifecycle_PauseResumeStop_AndRepeatedStartDoNotThrow()
    {
        var env = new Environment(snapshot: Available(DemoUsage), processes: DemoProcesses());

        await env.Service.StartAsync();
        await env.Service.StartAsync();
        Assert.Equal(TrafficGuardState.Running, env.Service.State);

        await env.Service.PauseAsync();
        Assert.Equal(TrafficGuardState.Paused, env.Service.State);
        await env.Service.PauseAsync();

        await env.Service.ResumeAsync();
        Assert.Equal(TrafficGuardState.Running, env.Service.State);

        await env.Service.StopAsync();
        Assert.Equal(TrafficGuardState.Stopped, env.Service.State);

        await env.Service.StartAsync();
        Assert.Equal(TrafficGuardState.Running, env.Service.State);
        await env.Service.DisposeAsync();
    }

    [Fact]
    public async Task ReadException_IsReportedAndDoesNotEscape()
    {
        var env = new Environment(snapshot: Available(DemoUsage), processes: DemoProcesses());
        env.Source.Throw = true;

        var result = await env.Service.CaptureOnceAsync();

        Assert.Equal("ReadFailed", result.Status);
        Assert.Single(env.Diagnostics.Messages);
    }

    private static GuardUsageSnapshot Available(params GuardUsageEntry[] entries) =>
        new(true, $"Entries={entries.Length}", T0, entries);

    private static IReadOnlyList<ProcessObservation> DemoProcesses(bool foreground = false) =>
    [
        new(100, "demo.exe", @"C:\Apps\demo.exe", null, true, foreground, 1),
        new(101, "demo.exe", @"C:\Apps\demo.exe", null, false, false, 2),
    ];

    private sealed class Environment
    {
        public Environment(GuardUsageSnapshot snapshot, IReadOnlyList<ProcessObservation> processes)
        {
            Source = new FakeUsageSource { Snapshot = snapshot };
            Inspector = new FakeInspector { Processes = processes };
            Sink = new FakeSink();
            Targets = new FakeTargets { Current = Target };
            Diagnostics = new FakeDiagnostics();
            Time = new FakeTimeProvider(T0);
            Service = new TrafficGuardService(
                Source,
                Inspector,
                Sink,
                Targets,
                Diagnostics,
                new GuardOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(90)),
                Time);
        }

        public FakeUsageSource Source { get; }
        public FakeInspector Inspector { get; }
        public FakeSink Sink { get; }
        public FakeTargets Targets { get; }
        public FakeDiagnostics Diagnostics { get; }
        public FakeTimeProvider Time { get; }
        public TrafficGuardService Service { get; }
    }

    private sealed class FakeUsageSource : IGuardUsageSource
    {
        public GuardUsageSnapshot Snapshot { get; set; } = GuardUsageSnapshot.Unavailable("none", T0);
        public bool Throw { get; set; }

        public Task<GuardUsageSnapshot> ReadAsync(GuardTarget target, CancellationToken cancellationToken = default) =>
            Throw
                ? throw new InvalidOperationException("simulated WinRT failure")
                : Task.FromResult(Snapshot with { CapturedAtUtc = Snapshot.CapturedAtUtc });
    }

    private sealed class FakeInspector : IProcessInspector
    {
        public IReadOnlyList<ProcessObservation> Processes { get; set; } = [];
        public IReadOnlyList<ProcessObservation> Capture() => Processes;
    }

    private sealed class FakeSink : IGuardSampleSink
    {
        public List<IReadOnlyList<GuardTrafficSample>> Batches { get; } = [];
        public bool FailNext { get; set; }

        public Task WriteAsync(IReadOnlyList<GuardTrafficSample> samples, CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("simulated database failure");
            }

            Batches.Add(samples);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTargets : IGuardTargetProvider
    {
        public GuardTarget? Current { get; set; }
        public GuardTarget? GetTarget() => Current;
    }

    private sealed class FakeDiagnostics : IGuardDiagnostics
    {
        public List<string> Messages { get; } = [];
        public void Report(string message, Exception? exception = null) => Messages.Add(message);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public FakeTimeProvider(DateTimeOffset now) => Now = now;
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}