namespace HotspotMeter.Core.TrafficGuard;

/// <summary>
/// 纯规则判定：给定聚合、全局配置、单程序规则和最近一次提醒，决定是否应该提醒。
/// 不读数据库、不发通知，因此所有时间/阈值边界都可以用可控数据测试。
/// </summary>
public static class GuardRuleEngine
{
    /// <summary>
    /// 判定一个程序在当前周期是否应触发提醒。
    /// </summary>
    /// <param name="aggregate">该程序在当前周期内的已确认增量聚合。</param>
    /// <param name="settings">全局提醒配置。</param>
    /// <param name="rule">单程序覆盖规则；null 表示完全继承。</param>
    /// <param name="lastAlert">同一周期内最近一次提醒；null 表示本周期尚未提醒。</param>
    /// <param name="nowUtc">本次评估时刻（UTC）。</param>
    /// <param name="periodStartUtc">当前周期的起点（UTC）。</param>
    public static GuardAlertDecision Evaluate(
        GuardAppAggregate aggregate,
        GuardAlertSettings settings,
        GuardAppRule? rule,
        GuardAlertRecord? lastAlert,
        DateTimeOffset nowUtc,
        DateTimeOffset periodStartUtc)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.AlertsEnabled)
        {
            return GuardAlertDecision.None("AlertsDisabled");
        }

        if (rule is not null)
        {
            switch (rule.Mode)
            {
                case GuardRuleMode.Trusted:
                case GuardRuleMode.Disabled:
                    return GuardAlertDecision.None("RuleDisabled");
                case GuardRuleMode.Ignored:
                    // IgnoreUntilUtc 为空表示永久忽略；过期后自动回到继承规则。
                    if (rule.IgnoreUntilUtc is null || rule.IgnoreUntilUtc > nowUtc)
                    {
                        return GuardAlertDecision.None("Ignored");
                    }

                    break;
            }
        }

        if (settings.BackgroundOnly && aggregate.LastState != GuardActivityState.Background)
        {
            return GuardAlertDecision.None("NotBackground");
        }

        var threshold = rule?.ThresholdOverrideBytes ?? settings.DefaultThresholdBytes;
        if (threshold <= 0)
        {
            return GuardAlertDecision.None("InvalidThreshold");
        }

        var actual = aggregate.ValueFor(settings.Metric);
        if (actual < threshold)
        {
            return GuardAlertDecision.None("BelowThreshold");
        }

        if (lastAlert is null)
        {
            return new GuardAlertDecision(GuardAlertDecisionKind.Threshold, threshold, actual, "ThresholdReached");
        }

        var samePeriod = settings.Period == GuardPeriod.Rolling24Hours
            ? lastAlert.CreatedAtUtc >= periodStartUtc
            : lastAlert.PeriodStartUtc == periodStartUtc;
        if (!samePeriod)
        {
            return new GuardAlertDecision(GuardAlertDecisionKind.Threshold, threshold, actual, "PeriodReset");
        }

        if (nowUtc - lastAlert.CreatedAtUtc < settings.Cooldown)
        {
            return GuardAlertDecision.None("Cooldown");
        }

        if (!settings.SecondaryEnabled)
        {
            return GuardAlertDecision.None("SecondaryDisabled");
        }

        var growth = actual - lastAlert.ActualBytes;
        if (growth >= settings.SecondaryGrowthBytes)
        {
            return new GuardAlertDecision(GuardAlertDecisionKind.Secondary, threshold, actual, "SecondaryGrowth");
        }

        return GuardAlertDecision.None("NoGrowth");
    }
}