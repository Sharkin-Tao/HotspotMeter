namespace HotspotMeter.Core.TrafficGuard;

/// <summary>当前目标热点。ProfileKey 复用现有持久化身份，Guard 不另建一套网络标识。</summary>
public sealed record GuardTarget(string ProfileKey, Guid InterfaceGuid, string Ssid);

/// <summary>Windows 逐应用归属 API 的一条累计读数（当天、当前连接配置）。</summary>
public sealed record GuardUsageEntry(
    string AttributionId,
    string DisplayName,
    long DownloadBytes,
    long UploadBytes);

/// <summary>
/// 一次逐应用读数。API 不可用时必须通过 <see cref="IsAvailable"/> 明确表达，
/// 不能返回空列表让调用方误以为“所有程序流量都是 0”。
/// </summary>
public sealed record GuardUsageSnapshot(
    bool IsAvailable,
    string Status,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<GuardUsageEntry> Entries)
{
    public static GuardUsageSnapshot Unavailable(string status, DateTimeOffset capturedAtUtc) =>
        new(false, status, capturedAtUtc, []);
}

/// <summary>进程快照。所有字段都来自一次系统查询，不在 Core 里再次调用 Win32。</summary>
public sealed record ProcessObservation(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? PackageFamilyName,
    bool HasVisibleWindow,
    bool IsForeground,
    long StartTimeUtcTicks);

/// <summary>Guard 的三种活动状态。0/1/2 与数据库 CHECK 一一对应。</summary>
public enum GuardActivityState
{
    Foreground = 0,
    Background = 1,
    Idle = 2,
}

/// <summary>
/// 样本质量标记。缺失/重置/网络切换绝不写成“正常 0 字节”，而是通过标记表达。
/// </summary>
[Flags]
public enum GuardQualityFlags
{
    None = 0,

    /// <summary>基线样本，仅用于建立基准，不应作为流量增量持久化。</summary>
    Baseline = 1,

    /// <summary>计数器回退，已重建基线；本周期没有可信增量。</summary>
    CounterReset = 2,

    /// <summary>网络身份变化，全部基线已重置。</summary>
    ProfileChanged = 4,

    /// <summary>读取失败或数据缺失，本周期没有样本。</summary>
    DataMissing = 8,

    /// <summary>身份不确定，AppKey 不可跨重启复用。</summary>
    IdentityUncertain = 16,

    /// <summary>相邻采样间隔明显大于配置间隔，增量的时间归属不精确。</summary>
    IntervalGap = 32,
}

/// <summary>一条已确认的增量样本。字节数只表示本段增量，不表示程序累计。</summary>
public sealed record GuardTrafficSample(
    string AppKey,
    string DisplayName,
    AppIdentityKind IdentityKind,
    bool IdentityCertain,
    string ProfileKey,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long DownloadBytes,
    long UploadBytes,
    GuardActivityState ActivityState,
    int ProcessCount,
    GuardQualityFlags QualityFlags);

/// <summary>一次采样的结果，用于日志/测试；不含任何 UI 或提醒业务。</summary>
public sealed record GuardCaptureResult(
    bool TargetAvailable,
    bool DataAvailable,
    int SampleCount,
    int SkippedMissingEntries,
    bool ProfileChanged,
    string Status);

/// <summary>采样参数。默认 30 秒来自实测：Windows 逐应用累计值约 30 秒刷新一次。</summary>
public sealed record GuardOptions(
    TimeSpan SamplingInterval,
    TimeSpan GapThreshold)
{
    public static GuardOptions Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90));
}

/// <summary>读取逐应用累计流量。实现位于 App 层（WinRT），Core 不直接依赖 Windows。</summary>
public interface IGuardUsageSource
{
    Task<GuardUsageSnapshot> ReadAsync(GuardTarget target, CancellationToken cancellationToken = default);
}

/// <summary>进程与窗口状态读取。实现位于 Infrastructure，Core 不直接 P/Invoke。</summary>
public interface IProcessInspector
{
    IReadOnlyList<ProcessObservation> Capture();
}

/// <summary>持久化入口。实现应在一个事务内幂等写入；失败时抛出，由服务记录并下轮重试。</summary>
public interface IGuardSampleSink
{
    Task WriteAsync(IReadOnlyList<GuardTrafficSample> samples, CancellationToken cancellationToken = default);
}

/// <summary>目标热点来源。未连接目标时返回 null，Guard 不猜测其他网络。</summary>
public interface IGuardTargetProvider
{
    GuardTarget? GetTarget();
}

/// <summary>错误回调。Core 不引用 AppLog，由宿主决定写日志还是计数。</summary>
public interface IGuardDiagnostics
{
    void Report(string message, Exception? exception = null);
}