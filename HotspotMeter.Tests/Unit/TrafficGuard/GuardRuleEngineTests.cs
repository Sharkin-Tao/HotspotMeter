using System.Globalization;
using HotspotMeter.Core.Localization;
using HotspotMeter.Core.TrafficGuard;

namespace HotspotMeter.Tests.Unit.TrafficGuard;

public sealed class GuardRuleEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodStart = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private const long Mb = 1024 * 1024;

    [Fact]
    public void DefaultThreshold_TriggersFirstAlert()
    {
        var decision = GuardRuleEngine.Evaluate(
            Aggregate(download: 101 * Mb),
            Settings(),
            rule: null,
            lastAlert: null,
            Now,
            PeriodStart);

        Assert.Equal(GuardAlertDecisionKind.Threshold, decision.Kind);
        Assert.Equal("ThresholdReached", decision.Reason);
    }

    [Fact]
    public void ApplicationOverride_ReplacesGlobalThreshold()
    {
        var rule = new GuardAppRule("exe:c:\\demo.exe", GuardRuleMode.Inherit, 200 * Mb, null);
        Assert.Equal(
            GuardAlertDecisionKind.None,
            GuardRuleEngine.Evaluate(Aggregate(150 * Mb), Settings(), rule, null, Now, PeriodStart).Kind);

        Assert.Equal(
            GuardAlertDecisionKind.Threshold,
            GuardRuleEngine.Evaluate(Aggregate(201 * Mb), Settings(), rule, null, Now, PeriodStart).Kind);
    }

    [Theory]
    [InlineData(GuardMetric.Download)]
    [InlineData(GuardMetric.Upload)]
    [InlineData(GuardMetric.Total)]
    public void Metric_SelectsMatchingDirection(GuardMetric metric)
    {
        var settings = Settings() with { Metric = metric, DefaultThresholdBytes = 100 * Mb };
        var (download, upload, expected) = metric switch
        {
            GuardMetric.Download => (100 * Mb, 0L, GuardAlertDecisionKind.Threshold),
            GuardMetric.Upload => (0L, 100 * Mb, GuardAlertDecisionKind.Threshold),
            _ => (40 * Mb, 40 * Mb, GuardAlertDecisionKind.None),
        };

        Assert.Equal(
            expected,
            GuardRuleEngine.Evaluate(Aggregate(download, upload: upload), settings, null, null, Now, PeriodStart).Kind);
    }

    [Theory]
    [InlineData(GuardRuleMode.Trusted)]
    [InlineData(GuardRuleMode.Disabled)]
    public void TrustedAndDisabledRules_NeverAlert(GuardRuleMode mode)
    {
        var rule = new GuardAppRule("exe:c:\\demo.exe", mode, null, null);
        Assert.Equal(
            GuardAlertDecisionKind.None,
            GuardRuleEngine.Evaluate(Aggregate(500 * Mb), Settings(), rule, null, Now, PeriodStart).Kind);
    }

    [Fact]
    public void IgnoredRule_SuppressesUntilExpiry()
    {
        var rule = new GuardAppRule("exe:c:\\demo.exe", GuardRuleMode.Ignored, null, Now.AddMinutes(5));
        Assert.Equal(
            GuardAlertDecisionKind.None,
            GuardRuleEngine.Evaluate(Aggregate(500 * Mb), Settings(), rule, null, Now, PeriodStart).Kind);

        Assert.Equal(
            GuardAlertDecisionKind.Threshold,
            GuardRuleEngine.Evaluate(Aggregate(500 * Mb), Settings(), rule, null, Now.AddMinutes(6), PeriodStart).Kind);
    }

    [Fact]
    public void BackgroundOnly_SkipsForegroundAndIdle()
    {
        var settings = Settings() with { BackgroundOnly = true };
        Assert.Equal(
            GuardAlertDecisionKind.None,
            GuardRuleEngine.Evaluate(Aggregate(500 * Mb, GuardActivityState.Foreground), settings, null, null, Now, PeriodStart).Kind);
        Assert.Equal(
            GuardAlertDecisionKind.None,
            GuardRuleEngine.Evaluate(Aggregate(500 * Mb, GuardActivityState.Idle), settings, null, null, Now, PeriodStart).Kind);
        Assert.Equal(
            GuardAlertDecisionKind.Threshold,
            GuardRuleEngine.Evaluate(Aggregate(500 * Mb, GuardActivityState.Background), settings, null, null, Now, PeriodStart).Kind);
    }

    [Fact]
    public void Cooldown_SuppressesRepeatedAlert()
    {
        var settings = Settings() with { Cooldown = TimeSpan.FromHours(1) };
        var last = LastAlert(Now.AddMinutes(-30), actual: 110 * Mb);

        Assert.Equal(
            GuardAlertDecisionKind.None,
            GuardRuleEngine.Evaluate(Aggregate(150 * Mb), settings, null, last, Now, PeriodStart).Kind);
    }

    [Fact]
    public void SecondaryGrowth_TriggersAfterCooldown()
    {
        var settings = Settings() with
        {
            Cooldown = TimeSpan.FromMinutes(10),
            SecondaryEnabled = true,
            SecondaryGrowthBytes = 50 * Mb,
        };
        var last = LastAlert(Now.AddMinutes(-30), actual: 110 * Mb);

        Assert.Equal(
            GuardAlertDecisionKind.Secondary,
            GuardRuleEngine.Evaluate(Aggregate(161 * Mb), settings, null, last, Now, PeriodStart).Kind);
    }

    [Fact]
    public void SecondaryDisabled_DoesNotRepeatThresholdAlert()
    {
        var settings = Settings() with { Cooldown = TimeSpan.FromMinutes(1), SecondaryEnabled = false };
        var last = LastAlert(Now.AddMinutes(-30), actual: 110 * Mb);

        Assert.Equal(
            GuardAlertDecisionKind.None,
            GuardRuleEngine.Evaluate(Aggregate(500 * Mb), settings, null, last, Now, PeriodStart).Kind);
    }

    [Fact]
    public void PeriodReset_TriggersAgain()
    {
        var last = LastAlert(Now.AddMinutes(-10), actual: 110 * Mb) with
        {
            PeriodStartUtc = PeriodStart.AddDays(-1),
        };

        Assert.Equal(
            GuardAlertDecisionKind.Threshold,
            GuardRuleEngine.Evaluate(Aggregate(300 * Mb), Settings(), null, last, Now, PeriodStart).Kind);
    }

    [Fact]
    public void DisabledAlerts_NeverTriggers()
    {
        var settings = Settings() with { AlertsEnabled = false };
        Assert.Equal(
            GuardAlertDecisionKind.None,
            GuardRuleEngine.Evaluate(Aggregate(500 * Mb), settings, null, null, Now, PeriodStart).Kind);
    }

    [Fact]
    public void NotificationText_IsLocalizedAndFactual()
    {
        var aggregate = Aggregate(180 * Mb, GuardActivityState.Background);
        var decision = new GuardAlertDecision(GuardAlertDecisionKind.Threshold, 100 * Mb, 180 * Mb, "ThresholdReached");
        var zh = GuardAlertText.Create(
            aggregate,
            decision,
            Settings(),
            key => LanguageCatalog.Get(LanguageCatalog.ChineseSimplified, key),
            CultureInfo.GetCultureInfo("zh-CN"));
        var en = GuardAlertText.Create(
            aggregate,
            decision,
            Settings(),
            key => LanguageCatalog.Get(LanguageCatalog.English, key),
            CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("流量提醒", zh.Title);
        Assert.Contains("demo.exe", zh.Body);
        Assert.Contains("后台", zh.Body);
        Assert.DoesNotContain("自动更新", zh.Body);
        Assert.Equal("Traffic alert", en.Title);
        Assert.Contains("Background", en.Body);
        Assert.DoesNotContain("automatic update", en.Body, StringComparison.OrdinalIgnoreCase);
    }

    private static GuardAlertSettings Settings() => GuardAlertSettings.Default;

    private static GuardAppAggregate Aggregate(
        long download,
        GuardActivityState state = GuardActivityState.Background,
        long upload = 0) =>
        new("exe:c:\\demo.exe", "demo.exe", download, upload, state, Now, 4);

    private static GuardAlertRecord LastAlert(DateTimeOffset created, long actual) =>
        new(
            1,
            "exe:c:\\demo.exe",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            GuardPeriod.Today,
            PeriodStart,
            GuardAlertKind.Threshold,
            100 * Mb,
            actual,
            0,
            0,
            created,
            GuardNotificationStatus.Sent);
}