using HotspotMeter.Core.TrafficGuard;

namespace HotspotMeter.Tests.Unit.TrafficGuard;

public sealed class GuardAlertServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private const long Mb = 1024 * 1024;

    [Fact]
    public async Task Threshold_InsertsPendingThenMarksSent()
    {
        var repository = new FakeRepository();
        var sender = new FakeSender();
        var service = Create(repository, sender);

        await service.EvaluateAsync(Target());

        var record = Assert.Single(repository.Inserted);
        Assert.Equal(GuardAlertKind.Threshold, record.Kind);
        Assert.Equal(GuardNotificationStatus.Sent, Assert.Single(repository.Updates).Status);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task NotificationFailure_IsRecordedButSampleEvaluationContinues()
    {
        var repository = new FakeRepository();
        var sender = new FakeSender { Throw = true };
        var service = Create(repository, sender);

        await service.EvaluateAsync(Target());

        Assert.Single(repository.Inserted);
        var update = Assert.Single(repository.Updates);
        Assert.Equal(GuardNotificationStatus.Failed, update.Status);
        Assert.False(string.IsNullOrWhiteSpace(update.Error));
    }

    [Fact]
    public async Task InsertFailure_DoesNotSendNotification()
    {
        var repository = new FakeRepository { FailInsert = true };
        var sender = new FakeSender();

        await Create(repository, sender).EvaluateAsync(Target());

        Assert.Empty(sender.Sent);
        Assert.Empty(repository.Updates);
    }

    [Fact]
    public async Task Cooldown_PreventsDuplicateAlert()
    {
        var repository = new FakeRepository();
        repository.Latest["exe:c:\\demo.exe"] = new GuardAlertRecord(
            7,
            "exe:c:\\demo.exe",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            GuardPeriod.Today,
            new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero),
            GuardAlertKind.Threshold,
            100 * Mb,
            150 * Mb,
            150 * Mb,
            0,
            Now.AddMinutes(-10),
            GuardNotificationStatus.Sent);

        await Create(repository, new FakeSender()).EvaluateAsync(Target());

        Assert.Empty(repository.Inserted);
    }

    [Fact]
    public async Task TodayPeriod_StartsAtLocalMidnight()
    {
        var repository = new FakeRepository();
        await Create(repository, new FakeSender()).EvaluateAsync(Target());

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), repository.LastFromUtc);
    }

    [Fact]
    public async Task RollingPeriod_StartsTwentyFourHoursBack()
    {
        var repository = new FakeRepository();
        repository.Settings = GuardAlertSettings.Default with { Period = GuardPeriod.Rolling24Hours };

        await Create(repository, new FakeSender()).EvaluateAsync(Target());

        Assert.Equal(Now.AddHours(-24), repository.LastFromUtc);
    }

    [Fact]
    public async Task SessionPeriod_WithoutActiveSession_DoesNothing()
    {
        var repository = new FakeRepository();
        repository.Settings = GuardAlertSettings.Default with { Period = GuardPeriod.Session };
        repository.ActiveSessionStart = null;

        await Create(repository, new FakeSender()).EvaluateAsync(Target());

        Assert.Empty(repository.Inserted);
        Assert.Empty(repository.ReadAggregateCalls);
    }

    [Fact]
    public async Task IgnoredRule_PreventsAlert()
    {
        var repository = new FakeRepository();
        repository.Rules.Add(new GuardAppRule("exe:c:\\demo.exe", GuardRuleMode.Ignored, null, Now.AddHours(1)));

        await Create(repository, new FakeSender()).EvaluateAsync(Target());

        Assert.Empty(repository.Inserted);
    }

    private static GuardAlertService Create(FakeRepository repository, FakeSender sender) =>
        new(
            repository,
            sender,
            key => key,
            new FixedTimeProvider(Now),
            TimeZoneInfo.Utc);

    private static GuardTarget Target() =>
        new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Guid.Parse("11111111-1111-1111-1111-111111111111"), "TestHotspot");

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeSender : IGuardNotificationSender
    {
        public bool Throw { get; init; }
        public List<GuardNotification> Sent { get; } = [];

        public Task<GuardNotificationResult> SendAsync(GuardNotification notification, CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("toast failed");
            }

            Sent.Add(notification);
            return Task.FromResult(new GuardNotificationResult(GuardNotificationStatus.Sent));
        }
    }

    private sealed class FakeRepository : IGuardAlertRepository
    {
        public GuardAlertSettings Settings { get; set; } = GuardAlertSettings.Default;
        public List<GuardAppRule> Rules { get; } = [];
        public Dictionary<string, GuardAlertRecord> Latest { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset? ActiveSessionStart { get; set; } = Now.AddHours(-2);
        public bool FailInsert { get; init; }
        public List<GuardAlertRecord> Inserted { get; } = [];
        public List<(long Id, GuardNotificationStatus Status, string? Error)> Updates { get; } = [];
        public List<DateTimeOffset> ReadAggregateCalls { get; } = [];
        public DateTimeOffset? LastFromUtc { get; private set; }

        public Task<GuardAlertSettings> LoadAlertSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings);

        public Task SaveAlertSettingsAsync(GuardAlertSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GuardAppRule>> LoadAppRulesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GuardAppRule>>(Rules);

        public Task SaveAppRuleAsync(GuardAppRule rule, CancellationToken cancellationToken = default)
        {
            Rules.RemoveAll(existing => existing.AppKey == rule.AppKey);
            Rules.Add(rule);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GuardAppAggregate>> ReadAggregatesAsync(
            string profileKey,
            DateTimeOffset fromUtc,
            CancellationToken cancellationToken = default)
        {
            ReadAggregateCalls.Add(fromUtc);
            LastFromUtc = fromUtc;
            return Task.FromResult<IReadOnlyList<GuardAppAggregate>>(
            [
                new GuardAppAggregate(
                    "exe:c:\\demo.exe",
                    "demo.exe",
                    180 * Mb,
                    20 * Mb,
                    GuardActivityState.Background,
                    Now,
                    4),
            ]);
        }

        public Task<IReadOnlyDictionary<string, GuardAlertRecord>> LoadLatestAlertsAsync(
            GuardPeriod period,
            DateTimeOffset fromUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, GuardAlertRecord>>(Latest);

        public Task<DateTimeOffset?> GetActiveSessionStartAsync(string profileKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(ActiveSessionStart);

        public Task<long> InsertAlertAsync(GuardAlertRecord record, CancellationToken cancellationToken = default)
        {
            if (FailInsert)
            {
                throw new InvalidOperationException("insert failed");
            }

            Inserted.Add(record);
            return Task.FromResult((long)Inserted.Count);
        }

        public Task UpdateNotificationAsync(
            long alertId,
            GuardNotificationStatus status,
            string? error,
            CancellationToken cancellationToken = default)
        {
            Updates.Add((alertId, status, error));
            return Task.CompletedTask;
        }
    }
}