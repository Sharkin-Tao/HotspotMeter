namespace HotspotMeter.Core.TrafficGuard;

/// <summary>提醒使用的统计周期。0/1/2 与 GuardSettings.PeriodKind 一一对应。</summary>
public enum GuardPeriod
{
    /// <summary>当前热点连接会话，从活动会话的 StartedAtUtc 起算。</summary>
    Session = 0,

    /// <summary>本地时区的自然日，从当天 00:00 起算。</summary>
    Today = 1,

    /// <summary>以评估时刻为终点向前滚动 24 小时。</summary>
    Rolling24Hours = 2,
}

/// <summary>阈值比较的流量方向。0/1/2 与 GuardSettings.MetricKind 一一对应。</summary>
public enum GuardMetric
{
    Download = 0,
    Upload = 1,
    Total = 2,
}

/// <summary>
/// 单程序规则。Inherit 使用全局阈值；Trusted/Disabled 永久不提醒；
/// Ignored 在 IgnoreUntilUtc 之前不提醒。
/// </summary>
public enum GuardRuleMode
{
    Inherit = 0,
    Trusted = 1,
    Disabled = 2,
    Ignored = 3,
}

/// <summary>一次提醒是首次达到阈值，还是在冷却后继续增长触发的二次提醒。</summary>
public enum GuardAlertKind
{
    Threshold = 1,
    Secondary = 2,
}

/// <summary>提醒记录的通知发送结果。0/1/2/3 与 GuardAlertHistory.NotificationStatus 对应。</summary>
public enum GuardNotificationStatus
{
    Pending = 0,
    Sent = 1,
    Suppressed = 2,
    Failed = 3,
}

/// <summary>
/// 全局提醒配置。默认值刻意保守：100 MB、自然日、上下行合计、仅后台、1 小时冷却。
/// 这些默认值只影响 Guard 提醒，不改变现有流量统计或胶囊设置。
/// </summary>
public sealed record GuardAlertSettings(
    bool AlertsEnabled,
    long DefaultThresholdBytes,
    GuardPeriod Period,
    GuardMetric Metric,
    bool BackgroundOnly,
    TimeSpan Cooldown,
    bool SecondaryEnabled,
    long SecondaryGrowthBytes)
{
    public static GuardAlertSettings Default { get; } = new(
        true,
        100L * 1024 * 1024,
        GuardPeriod.Today,
        GuardMetric.Total,
        true,
        TimeSpan.FromHours(1),
        true,
        50L * 1024 * 1024);

    public void Validate()
    {
        if (DefaultThresholdBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultThresholdBytes), "默认阈值必须大于 0。");
        }

        if (!Enum.IsDefined(Period))
        {
            throw new ArgumentOutOfRangeException(nameof(Period));
        }

        if (!Enum.IsDefined(Metric))
        {
            throw new ArgumentOutOfRangeException(nameof(Metric));
        }

        if (Cooldown < TimeSpan.FromSeconds(60) || Cooldown > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(Cooldown), "提醒冷却必须在 60 秒到 24 小时之间。");
        }

        if (SecondaryGrowthBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SecondaryGrowthBytes), "二次提醒增长量必须大于 0。");
        }
    }
}

/// <summary>单程序覆盖规则；ThresholdOverrideBytes 为 null 时继承全局阈值。</summary>
public sealed record GuardAppRule(
    string AppKey,
    GuardRuleMode Mode,
    long? ThresholdOverrideBytes,
    DateTimeOffset? IgnoreUntilUtc);

/// <summary>
/// 某个程序在统计周期内的聚合。字节数来自 GuardTrafficSamples 的已确认增量之和；
/// 缺失/重置的周期不会产生样本，因此这里天然不会把它们当成 0。
/// </summary>
public sealed record GuardAppAggregate(
    string AppKey,
    string DisplayName,
    long DownloadBytes,
    long UploadBytes,
    GuardActivityState LastState,
    DateTimeOffset LastSampleUtc,
    int SampleCount)
{
    public long ValueFor(GuardMetric metric) => metric switch
    {
        GuardMetric.Download => DownloadBytes,
        GuardMetric.Upload => UploadBytes,
        GuardMetric.Total => checked(DownloadBytes + UploadBytes),
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };
}

/// <summary>历史上一条提醒记录；引擎只用它做去重/冷却判断。</summary>
public sealed record GuardAlertRecord(
    long AlertId,
    string AppKey,
    string ProfileKey,
    GuardPeriod Period,
    DateTimeOffset PeriodStartUtc,
    GuardAlertKind Kind,
    long ThresholdBytes,
    long ActualBytes,
    long DownloadBytes,
    long UploadBytes,
    DateTimeOffset CreatedAtUtc,
    GuardNotificationStatus NotificationStatus);

/// <summary>规则判定的纯结果，不包含任何 I/O，便于覆盖边界测试。</summary>
public enum GuardAlertDecisionKind
{
    None = 0,
    Threshold = 1,
    Secondary = 2,
}

public sealed record GuardAlertDecision(
    GuardAlertDecisionKind Kind,
    long ThresholdBytes,
    long ActualBytes,
    string Reason)
{
    public static GuardAlertDecision None(string reason) =>
        new(GuardAlertDecisionKind.None, 0, 0, reason);
}

/// <summary>发送给通知适配器的、已经本地化好的文本。</summary>
public sealed record GuardNotification(
    string AppName,
    string Title,
    string Body,
    string ActionText,
    string LaunchArgument);

/// <summary>通知适配器的发送结果。失败只记录，不向采样循环抛出。</summary>
public sealed record GuardNotificationResult(
    GuardNotificationStatus Status,
    string? Error = null);

/// <summary>提醒持久化接口。实现位于 Infrastructure，Core 只依赖抽象。</summary>
public interface IGuardAlertRepository
{
    Task<GuardAlertSettings> LoadAlertSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveAlertSettingsAsync(GuardAlertSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GuardAppRule>> LoadAppRulesAsync(CancellationToken cancellationToken = default);

    Task SaveAppRuleAsync(GuardAppRule rule, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GuardAppAggregate>> ReadAggregatesAsync(
        string profileKey,
        DateTimeOffset fromUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, GuardAlertRecord>> LoadLatestAlertsAsync(
        GuardPeriod period,
        DateTimeOffset fromUtc,
        CancellationToken cancellationToken = default);

    Task<DateTimeOffset?> GetActiveSessionStartAsync(
        string profileKey,
        CancellationToken cancellationToken = default);

    Task<long> InsertAlertAsync(GuardAlertRecord record, CancellationToken cancellationToken = default);

    Task UpdateNotificationAsync(
        long alertId,
        GuardNotificationStatus status,
        string? error,
        CancellationToken cancellationToken = default);
}

/// <summary>Windows 通知适配器；实现失败时必须返回 Failed，不能抛异常打断提醒循环。</summary>
public interface IGuardNotificationSender
{
    Task<GuardNotificationResult> SendAsync(
        GuardNotification notification,
        CancellationToken cancellationToken = default);
}

/// <summary>Guard 采样循环在写库成功后调用；实现抛异常由循环隔离。</summary>
public interface IGuardAlertEngine
{
    Task EvaluateAsync(GuardTarget target, CancellationToken cancellationToken = default);
}