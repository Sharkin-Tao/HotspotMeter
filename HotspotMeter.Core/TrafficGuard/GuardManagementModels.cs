namespace HotspotMeter.Core.TrafficGuard;

/// <summary>用户在异常记录上采取过的处理动作。数值与数据库 v6 的 CHECK 一一对应。</summary>
public enum GuardHandlingAction
{
    None = 0,
    Trusted = 1,
    Untrusted = 2,
    Ignored = 3,
    OpenedFolder = 4,
    Terminated = 5,
}

/// <summary>手动结束进程的结果。每种失败都必须能区分原因，不能统一报“失败”。</summary>
public enum GuardProcessActionStatus
{
    Success = 0,
    AlreadyExited = 1,
    AccessDenied = 2,
    IdentityChanged = 3,
    Blocked = 4,
    PathUnavailable = 5,
    Failed = 6,
}

/// <summary>进程被禁止结束的原因；UI 负责翻译成用户可读文案。</summary>
public enum GuardProcessBlockReason
{
    None = 0,
    UncertainIdentity = 1,
    SessionZero = 2,
    SystemPath = 3,
    ProtectedProcess = 4,
    SharedService = 5,
    PathUnavailable = 6,
}

public sealed record GuardProcessSafety(bool CanTerminate, GuardProcessBlockReason Reason)
{
    public static GuardProcessSafety Allowed { get; } = new(true, GuardProcessBlockReason.None);
}

/// <summary>当前仍在运行、且可能属于某条异常记录的一个进程。</summary>
public sealed record GuardProcessCandidate(
    int ProcessId,
    string AppKey,
    string DisplayName,
    string? ExecutablePath,
    string? PackageFamilyName,
    DateTimeOffset StartedAtUtc,
    int SessionId,
    GuardProcessSafety Safety);

/// <summary>结束前必须由 UI 填入用户确认过的进程身份，防止 PID 复用误杀。</summary>
public sealed record GuardTerminationRequest(
    string AppKey,
    int ProcessId,
    DateTimeOffset ExpectedStartedAtUtc,
    string? ExpectedExecutablePath,
    string? ExpectedPackageFamilyName);

public sealed record GuardTerminationResult(
    GuardProcessActionStatus Status,
    string? Detail = null,
    GuardProcessBlockReason BlockReason = GuardProcessBlockReason.None);

/// <summary>
/// 异常详情。字段覆盖：程序身份、触发上下文、流量口径、通知结果和用户处理记录。
/// 这是只读视图，不承担规则判定。
/// </summary>
public sealed record GuardAlertDetail(
    long AlertId,
    string AppKey,
    string DisplayName,
    AppIdentityKind IdentityKind,
    bool IdentityCertain,
    string? ExecutablePath,
    string ProfileKey,
    DateTimeOffset CreatedAtUtc,
    GuardPeriod Period,
    DateTimeOffset PeriodStartUtc,
    GuardAlertKind Kind,
    long ThresholdBytes,
    long ActualBytes,
    long DownloadBytes,
    long UploadBytes,
    GuardMetric Metric,
    bool ThresholdFromAppRule,
    GuardActivityState ActivityState,
    GuardNotificationStatus NotificationStatus,
    string? NotificationError,
    bool IsDismissed,
    DateTimeOffset? HandledAtUtc,
    GuardHandlingAction HandlingAction,
    GuardRuleMode RuleMode,
    DateTimeOffset? IgnoreUntilUtc)
{
    public long TotalBytes => DownloadBytes + UploadBytes;
}

public sealed record GuardHistoryQuery(
    string? AppKey = null,
    GuardNotificationStatus? NotificationStatus = null,
    int Limit = 200)
{
    public int SafeLimit => Math.Clamp(Limit, 1, 500);
}

/// <summary>异常历史读取 + 处理记录写入。所有方法都异步，调用方负责异常隔离。</summary>
public interface IGuardManagementRepository
{
    Task<IReadOnlyList<GuardAlertDetail>> GetAlertDetailsAsync(
        GuardHistoryQuery query,
        CancellationToken cancellationToken = default);

    Task<GuardAlertDetail?> GetAlertDetailAsync(long alertId, CancellationToken cancellationToken = default);

    Task RecordHandlingAsync(
        long alertId,
        GuardHandlingAction action,
        DateTimeOffset handledAtUtc,
        CancellationToken cancellationToken = default);

    Task<GuardAppRule?> GetAppRuleAsync(string appKey, CancellationToken cancellationToken = default);

    /// <summary>保存程序规则（信任/忽略/阈值覆盖）。写入后调用方应重新从库读取，避免 UI 与数据库不一致。</summary>
    Task SaveAppRuleAsync(GuardAppRule rule, CancellationToken cancellationToken = default);
}

/// <summary>手动结束进程。实现位于 Infrastructure；所有安全校验必须在结束前重新执行一次。</summary>
public interface IGuardProcessService
{
    Task<IReadOnlyList<GuardProcessCandidate>> GetProcessesAsync(
        string appKey,
        CancellationToken cancellationToken = default);

    Task<GuardTerminationResult> TerminateAsync(
        GuardTerminationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 结束进程前的纯安全策略。放在 Core 便于用模拟路径/会话/身份覆盖边界，
/// 不依赖真实进程，也不会在测试中结束任何系统进程。
/// </summary>
public static class GuardProcessPolicy
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "lsaiso", "svchost", "fontdrvhost", "dwm",
        "explorer", "sihost", "taskhostw", "ctfmon", "audiodg", "spoolsv",
        "dllhost", "runtimebroker", "searchhost", "startmenuexperiencehost",
        "shellexperiencehost", "textinputhost", "applicationframehost",
    };

    public static GuardProcessSafety Evaluate(
        string appKey,
        string? executablePath,
        int sessionId,
        string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(appKey)
            || appKey.StartsWith("uncertain:", StringComparison.OrdinalIgnoreCase)
            || appKey.StartsWith("opaque:", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, GuardProcessBlockReason.UncertainIdentity);
        }

        if (sessionId == 0)
        {
            return new(false, GuardProcessBlockReason.SessionZero);
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new(false, GuardProcessBlockReason.PathUnavailable);
        }

        var fullPath = executablePath.Trim();
        var name = Path.GetFileNameWithoutExtension(fullPath);
        if (ProtectedNames.Contains(name))
        {
            var reason = name is "svchost" or "services" or "dllhost" or "runtimebroker" or "spoolsv"
                ? GuardProcessBlockReason.SharedService
                : GuardProcessBlockReason.ProtectedProcess;
            return new(false, reason);
        }

        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            var windows = windowsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, windows, StringComparison.OrdinalIgnoreCase))
            {
                return new(false, GuardProcessBlockReason.SystemPath);
            }
        }

        return GuardProcessSafety.Allowed;
    }
}