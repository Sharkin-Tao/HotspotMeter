using System.Globalization;
using HotspotMeter.Core.Formatting;

namespace HotspotMeter.Core.TrafficGuard;

/// <summary>
/// 把规则判定结果转成已本地化的通知文本。文本只描述客观事实（用量、阈值、前后台），
/// 不推断程序是否在“自动更新”或存在恶意行为。
/// </summary>
public static class GuardAlertText
{
    public static GuardNotification Create(
        GuardAppAggregate aggregate,
        GuardAlertDecision decision,
        GuardAlertSettings settings,
        Func<string, string> translate,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(translate);

        culture ??= CultureInfo.CurrentCulture;
        var secondary = decision.Kind == GuardAlertDecisionKind.Secondary;
        var title = translate(secondary ? "Guard_AlertSecondaryTitle" : "Guard_AlertTitle");
        var bodyTemplate = translate(secondary ? "Guard_AlertSecondaryBody" : "Guard_AlertBody");
        var period = translate(settings.Period switch
        {
            GuardPeriod.Session => "Guard_PeriodSession",
            GuardPeriod.Today => "Guard_PeriodToday",
            _ => "Guard_PeriodRolling24h",
        });
        var state = translate(aggregate.LastState switch
        {
            GuardActivityState.Foreground => "Guard_StateForeground",
            GuardActivityState.Background => "Guard_StateBackground",
            _ => "Guard_StateIdle",
        });

        var body = string.Format(
            culture,
            bodyTemplate,
            aggregate.DisplayName,
            ByteSizeFormatter.Format(decision.ActualBytes, culture),
            ByteSizeFormatter.Format(aggregate.DownloadBytes, culture),
            ByteSizeFormatter.Format(aggregate.UploadBytes, culture),
            ByteSizeFormatter.Format(decision.ThresholdBytes, culture),
            state,
            period);

        return new GuardNotification(
            aggregate.DisplayName,
            title,
            body,
            translate("Guard_AlertAction"),
            "stats");
    }
}