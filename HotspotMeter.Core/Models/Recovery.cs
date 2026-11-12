using System.Globalization;

namespace HotspotMeter.Core.Models;

/// <summary>
/// 持久化恢复所需的最小基线。阶段 2 只负责产生与消费，阶段 3 负责存库。
/// <see cref="TickCount64"/> 与 <see cref="BootEpochUtc"/> 用于区分"同一开机"与"重启过"：
/// 前者是单调证据，后者只在单调证据无法定论时作保守辅助。
/// </summary>
public sealed record BaselineState(
    Guid InterfaceGuid,
    ulong InterfaceLuid,
    ulong InOctets,
    ulong OutOctets,
    DateTimeOffset CapturedAtUtc,
    long TickCount64,
    DateTimeOffset BootEpochUtc,
    string Ssid,
    string? Bssid);

/// <summary>恢复调用所需的上下文：基线 + 原会话身份与累计。阶段 3 从数据库读出来后原样传入。</summary>
public sealed record ResumeContext(
    BaselineState Baseline,
    long SessionId,
    long SessionDownloadBytes,
    long SessionUploadBytes,
    DateTimeOffset SessionStartedAtUtc);

/// <summary>恢复判定结果。除 <see cref="Continued"/> 外，一律禁止产生任何可恢复增量。</summary>
public enum ResumeOutcome
{
    /// <summary>同一开机、同一接口、计数递增，且当前仍连接目标热点。仅表示"可以计算 gap"。</summary>
    Continued,

    /// <summary>计数器回退，基线已失效。</summary>
    CounterReset,

    /// <summary>接口 LUID 变化，视为换了设备。</summary>
    InterfaceChanged,

    /// <summary>判定为重启过。</summary>
    Rebooted,

    /// <summary>当前读不到该接口的计数行。</summary>
    InterfaceMissing,

    /// <summary>当前没有连接目标 SSID（R-05 安全闸：不能把别的网络流量算进来）。</summary>
    NotOnTarget,
}

/// <summary>判定重启时所依据的证据。单调证据优先，开机近似时刻只作辅助。</summary>
public enum RebootEvidence
{
    None,

    /// <summary>TickCount64 变小：同一开机内不可能发生，属于确定性证据。</summary>
    TickRegression,

    /// <summary>TickCount64 未变小但开机近似时刻偏移超过容差：无法区分重启与改时间，保守判为重启。</summary>
    BootEpochShift,
}

/// <summary>
/// 恢复窗口内连接连续性的可信状态。计数器增长只能证明"网卡上发生过流量"，
/// 不能证明"这段时间一直连着目标热点"（用户可能断开、连别的 Wi-Fi、再连回来）。
/// </summary>
public enum RecoveryContinuity
{
    /// <summary>有可信证据证明窗口内连接未中断，允许并入原会话正式统计。</summary>
    Continuous,

    /// <summary>无法证明连续性。默认值，只作为恢复候选上报，不并入会话。</summary>
    Unverified,

    /// <summary>有证据证明窗口内发生过中断。</summary>
    Interrupted,
}

/// <summary>评估连续性时提供给证据源的时间窗口。</summary>
public sealed record RecoveryWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc, Guid InterfaceGuid, string Ssid);

/// <summary>连续性证据源的评估结果，必须同时给出可读依据，便于日志与阶段 3 展示。</summary>
public sealed record ContinuityAssessment(RecoveryContinuity Continuity, string EvidenceSummary)
{
    /// <summary>没有证据时的标准结果：不确定，不允许合并。</summary>
    public static ContinuityAssessment Unverified(string reason) => new(RecoveryContinuity.Unverified, reason);
}

/// <summary>
/// 一段"程序未运行期间"的计数器差值。<see cref="RecoveryId"/> 是幂等键：
/// 同一 SessionId + 同一基线必然得到同一个 Id，重复恢复不会产生重复记录。
/// </summary>
public sealed record RecoveryGap(
    string RecoveryId,
    long DownloadBytes,
    long UploadBytes,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    RecoveryContinuity Continuity,
    string EvidenceSummary)
{
    /// <summary>下载 + 上传字节数。</summary>
    public long TotalBytes => DownloadBytes + UploadBytes;

    /// <summary>只有可信连续性证据存在时才允许并入原会话。</summary>
    public bool MayMergeIntoSession => Continuity == RecoveryContinuity.Continuous;
}

/// <summary>
/// 恢复判定输出。纯计算结果，不接触会话或事件。
/// </summary>
public sealed record ResumeEvaluation(
    ResumeOutcome Outcome,
    long DownloadBytes,
    long UploadBytes,
    string Reason,
    RebootEvidence Evidence);

/// <summary>
/// 恢复调用结果。<see cref="AlreadyApplied"/> 表示该恢复窗口此前已处理过（幂等键命中），
/// 本次没有合并、也不会再次发出持久化事件。
/// </summary>
public sealed record ResumeResult(
    ResumeOutcome Outcome,
    RecoveryGap? Gap,
    string Reason,
    RebootEvidence Evidence,
    bool AlreadyApplied = false);

/// <summary>开机近似时刻计算。墙钟被调整时该值会跟着漂移，因此只能作为辅助证据。</summary>
public static class BootSignatures
{
    /// <summary>用"当前 UTC − 开机以来的毫秒数"估算本次开机时刻。</summary>
    public static DateTimeOffset ComputeBootEpoch(DateTimeOffset utcNow, long tickCount64)
        => utcNow - TimeSpan.FromMilliseconds(tickCount64);
}

/// <summary>恢复幂等键的生成规则。只依赖会话与基线，不依赖当前时间，因此可重复计算。</summary>
public static class RecoveryId
{
    /// <summary>生成稳定的恢复 Id；同一会话、同一基线永远得到同一个字符串。</summary>
    public static string Create(long sessionId, BaselineState baseline)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1:N}:{2}:{3}:{4}",
            sessionId,
            baseline.InterfaceGuid,
            baseline.CapturedAtUtc.UtcTicks,
            baseline.InOctets,
            baseline.OutOctets);
}