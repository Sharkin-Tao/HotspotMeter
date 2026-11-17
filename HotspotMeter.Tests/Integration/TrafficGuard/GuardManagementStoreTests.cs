using HotspotMeter.Core.TrafficGuard;
using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Infrastructure.TrafficGuard;
using HotspotMeter.Tests.TestSupport;

namespace HotspotMeter.Tests.Integration.TrafficGuard;

public sealed class GuardManagementStoreTests : IAsyncLifetime
{
    private const string ProfileKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AppKey = @"exe:c:\apps\demo.exe";
    private const string OtherKey = @"exe:c:\apps\other.exe";
    private static readonly DateTimeOffset Base = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _directory = new("guard-management-store");
    private SqliteConnectionFactory? _factory;
    private GuardStore? _store;

    public Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(_directory.File("hotspotmeter.db"));
        using (var connection = _factory.Create())
        {
            new SchemaMigrator().Migrate(connection);
        }

        _store = new GuardStore(_factory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        _directory.Dispose();
    }

    [Fact]
    public async Task AlertDetails_AreOrderedFilteredAndJoinedWithRules()
    {
        await _store!.WriteAsync(new[] { Sample(AppKey, "demo.exe") });
        var first = await InsertAsync(Alert(OtherKey, Base, GuardNotificationStatus.Sent));
        var second = await InsertAsync(Alert(AppKey, Base.AddMinutes(1), GuardNotificationStatus.Failed));

        var all = await _store.GetAlertDetailsAsync(new GuardHistoryQuery());
        Assert.Equal(new[] { second, first }, all.Select(item => item.AlertId));

        Assert.Equal("demo.exe", all[0].DisplayName);
        Assert.Equal(AppIdentityKind.DesktopExecutable, all[0].IdentityKind);
        Assert.Equal(@"c:\apps\demo.exe", all[0].ExecutablePath);

        var failed = Assert.Single(await _store.GetAlertDetailsAsync(
            new GuardHistoryQuery(NotificationStatus: GuardNotificationStatus.Failed)));
        Assert.Equal(second, failed.AlertId);

        var byApp = Assert.Single(await _store.GetAlertDetailsAsync(new GuardHistoryQuery(AppKey: OtherKey)));
        Assert.Equal(first, byApp.AlertId);
        // 没有规则的程序仍可读：名称回退 AppKey，身份为不确定，规则为继承。
        Assert.Equal(OtherKey, byApp.DisplayName);
        Assert.Equal(AppIdentityKind.Uncertain, byApp.IdentityKind);
        Assert.Equal(GuardRuleMode.Inherit, byApp.RuleMode);
    }

    [Fact]
    public async Task AlertDetail_IncludesMetricSourceActivityAndNotification()
    {
        var id = await InsertAsync(Alert(AppKey, Base, GuardNotificationStatus.Failed) with
        {
            Metric = GuardMetric.Upload,
            ThresholdFromAppRule = true,
            ActivityState = GuardActivityState.Foreground,
        });

        var detail = await _store!.GetAlertDetailAsync(id);

        Assert.NotNull(detail);
        Assert.Equal(GuardMetric.Upload, detail.Metric);
        Assert.True(detail.ThresholdFromAppRule);
        Assert.Equal(GuardActivityState.Foreground, detail.ActivityState);
        Assert.Equal(GuardNotificationStatus.Failed, detail.NotificationStatus);
        Assert.Equal("denied", detail.NotificationError);
        Assert.Equal(150, detail.TotalBytes);
        Assert.Null(await _store.GetAlertDetailAsync(0));
        Assert.Null(await _store.GetAlertDetailAsync(999_999));
    }

    [Fact]
    public async Task RecordHandling_MarksHandled_AndDismissesExceptOpenedFolder()
    {
        var id = await InsertAsync(Alert(AppKey, Base, GuardNotificationStatus.Sent));

        await _store!.RecordHandlingAsync(id, GuardHandlingAction.OpenedFolder, Base.AddSeconds(5));
        var opened = await _store.GetAlertDetailAsync(id);
        Assert.Equal(GuardHandlingAction.OpenedFolder, opened!.HandlingAction);
        Assert.Equal(Base.AddSeconds(5), opened.HandledAtUtc);
        Assert.False(opened.IsDismissed);

        await _store.RecordHandlingAsync(id, GuardHandlingAction.Trusted, Base.AddSeconds(10));
        var trusted = await _store.GetAlertDetailAsync(id);
        Assert.Equal(GuardHandlingAction.Trusted, trusted!.HandlingAction);
        Assert.True(trusted.IsDismissed);
    }

    [Fact]
    public async Task AppRule_WithExecutablePath_RoundTrips()
    {
        await _store!.SaveAppRuleAsync(new GuardAppRule(
            AppKey,
            GuardRuleMode.Ignored,
            1024,
            Base.AddHours(1),
            @"c:\apps\demo.exe"));

        var rule = await _store.GetAppRuleAsync(AppKey);

        Assert.NotNull(rule);
        Assert.Equal(GuardRuleMode.Ignored, rule.Mode);
        Assert.Equal(1024, rule.ThresholdOverrideBytes);
        Assert.Equal(Base.AddHours(1), rule.IgnoreUntilUtc);
        Assert.Equal(@"c:\apps\demo.exe", rule.ExecutablePath);
        Assert.Null(await _store.GetAppRuleAsync(OtherKey));
    }

    [Fact]
    public async Task Write_KeepsExecutablePathForRepeatedSamples()
    {
        await _store!.WriteAsync(new[] { Sample(AppKey, "demo.exe") });
        await _store.SaveAppRuleAsync(new GuardAppRule(AppKey, GuardRuleMode.Trusted, null, null, @"c:\apps\demo.exe"));

        // 同一 exe AppKey 的后续样本仍应保留已写入的路径。
        await _store.WriteAsync(new[] { Sample(AppKey, "demo.exe") });

        var rule = await _store.GetAppRuleAsync(AppKey);
        Assert.Equal(@"c:\apps\demo.exe", rule!.ExecutablePath);
    }

    private async Task<long> InsertAsync(GuardAlertRecord record)
    {
        if (record.AppKey is { Length: > 0 } key && await _store!.GetAppRuleAsync(key) is null)
        {
            await _store.SaveAppRuleAsync(new GuardAppRule(key, GuardRuleMode.Inherit, null, null));
        }

        return await _store!.InsertAlertAsync(record);
    }

    private static GuardAlertRecord Alert(
        string appKey,
        DateTimeOffset createdAt,
        GuardNotificationStatus status) => new(
            0,
            appKey,
            ProfileKey,
            GuardPeriod.Today,
            new DateTimeOffset(2026, 11, 16, 16, 0, 0, TimeSpan.Zero),
            GuardAlertKind.Threshold,
            100,
            150,
            100,
            50,
            createdAt,
            status,
            Metric: GuardMetric.Total,
            ThresholdFromAppRule: false,
            ActivityState: GuardActivityState.Background)
        {
            NotificationError = status == GuardNotificationStatus.Failed ? "denied" : null,
        };

    private static GuardTrafficSample Sample(string appKey, string name) => new(
        appKey,
        name,
        AppIdentityKind.DesktopExecutable,
        true,
        ProfileKey,
        Base,
        Base.AddSeconds(30),
        100,
        50,
        GuardActivityState.Background,
        1,
        GuardQualityFlags.None);
}

