namespace HotspotMeter.Core.TrafficGuard;

/// <summary>
/// 提醒编排：把“聚合 → 规则 → 去重 → 落库 → 通知 → 更新结果”串起来。
///
/// 关键顺序：先插入 Pending 记录，再发送通知。这样即使通知崩溃/进程退出，
/// 数据库仍保留了本次提醒，不会因为“没发出去”而下一轮无限重复。
/// 单程序的任何异常只影响该程序，不阻断其余程序与采样循环。
/// </summary>
public sealed class GuardAlertService : IGuardAlertEngine
{
    private readonly IGuardAlertRepository _repository;
    private readonly IGuardNotificationSender _sender;
    private readonly Func<string, string> _translate;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _timeZone;
    private readonly IGuardDiagnostics? _diagnostics;

    public GuardAlertService(
        IGuardAlertRepository repository,
        IGuardNotificationSender sender,
        Func<string, string> translate,
        TimeProvider? clock = null,
        TimeZoneInfo? timeZone = null,
        IGuardDiagnostics? diagnostics = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _translate = translate ?? throw new ArgumentNullException(nameof(translate));
        _clock = clock ?? TimeProvider.System;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public async Task EvaluateAsync(GuardTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var settings = await _repository.LoadAlertSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.AlertsEnabled)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var periodStart = await ResolvePeriodStartAsync(target.ProfileKey, settings.Period, now, cancellationToken)
            .ConfigureAwait(false);
        if (periodStart is null)
        {
            return;
        }

        var aggregates = await _repository.ReadAggregatesAsync(target.ProfileKey, periodStart.Value, cancellationToken)
            .ConfigureAwait(false);
        if (aggregates.Count == 0)
        {
            return;
        }

        var rules = (await _repository.LoadAppRulesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(rule => rule.AppKey, StringComparer.Ordinal);
        var latest = await _repository.LoadLatestAlertsAsync(settings.Period, periodStart.Value, cancellationToken)
            .ConfigureAwait(false);

        foreach (var aggregate in aggregates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rules.TryGetValue(aggregate.AppKey, out var rule);
            latest.TryGetValue(aggregate.AppKey, out var lastAlert);

            var decision = GuardRuleEngine.Evaluate(aggregate, settings, rule, lastAlert, now, periodStart.Value);
            if (decision.Kind == GuardAlertDecisionKind.None)
            {
                continue;
            }

            await RaiseAsync(aggregate, settings, rule, decision, target.ProfileKey, periodStart.Value, now, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RaiseAsync(
        GuardAppAggregate aggregate,
        GuardAlertSettings settings,
        GuardAppRule? rule,
        GuardAlertDecision decision,
        string profileKey,
        DateTimeOffset periodStart,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var kind = decision.Kind == GuardAlertDecisionKind.Secondary
            ? GuardAlertKind.Secondary
            : GuardAlertKind.Threshold;
        var record = new GuardAlertRecord(
            0,
            aggregate.AppKey,
            profileKey,
            settings.Period,
            periodStart,
            kind,
            decision.ThresholdBytes,
            decision.ActualBytes,
            aggregate.DownloadBytes,
            aggregate.UploadBytes,
            now,
            GuardNotificationStatus.Pending,
            settings.Metric,
            rule?.ThresholdOverrideBytes is not null,
            aggregate.LastState);

        long alertId;
        try
        {
            alertId = await _repository.InsertAlertAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _diagnostics?.Report($"Traffic Guard 写入提醒失败（{aggregate.AppKey}）。", exception);
            return;
        }

        var notification = GuardAlertText.Create(aggregate, decision, settings, _translate);
        GuardNotificationResult result;
        try
        {
            result = await _sender.SendAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = new GuardNotificationResult(GuardNotificationStatus.Failed, exception.Message);
        }

        try
        {
            await _repository.UpdateNotificationAsync(alertId, result.Status, result.Error, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 记录已存在（Pending），下次评估会按冷却时间避免重复；这里只报告，不回滚提醒。
            _diagnostics?.Report($"Traffic Guard 更新通知结果失败（{aggregate.AppKey}）。", exception);
        }
    }

    private async Task<DateTimeOffset?> ResolvePeriodStartAsync(
        string profileKey,
        GuardPeriod period,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        switch (period)
        {
            case GuardPeriod.Session:
                return await _repository.GetActiveSessionStartAsync(profileKey, cancellationToken)
                    .ConfigureAwait(false);
            case GuardPeriod.Today:
                var localNow = TimeZoneInfo.ConvertTime(now, _timeZone);
                var localMidnight = new DateTime(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, DateTimeKind.Unspecified);
                return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localMidnight, _timeZone), TimeSpan.Zero);
            case GuardPeriod.Rolling24Hours:
                return now - TimeSpan.FromHours(24);
            default:
                return null;
        }
    }
}