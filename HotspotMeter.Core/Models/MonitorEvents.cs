namespace HotspotMeter.Core.Models;

/// <summary>会话的公开摘要。阶段 3 用它写 ConnectionSessions，不接触引擎内部状态。</summary>
public sealed record MonitorSessionInfo(
    long SessionId,
    Guid InterfaceGuid,
    string Ssid,
    string? Bssid,
    DateTimeOffset StartedAtUtc,
    long DownloadBytes,
    long UploadBytes);

/// <summary>会话结束原因。区分"真断开"与"用户换了目标"，便于阶段 3 归类。</summary>
public enum SessionEndReason
{
    /// <summary>WLAN 明确报告未连接。</summary>
    Disconnected,

    /// <summary>同一网卡改连了别的 SSID。</summary>
    SsidChanged,

    /// <summary>GUID 变了（换了网卡）。</summary>
    InterfaceChanged,

    /// <summary>引擎停止（用户退出程序）。</summary>
    EngineStopped,
}

/// <summary>会话结束事件参数，携带封存后的累计值。</summary>
public sealed record MonitorSessionEndedInfo(
    long SessionId,
    DateTimeOffset EndedAtUtc,
    long DownloadBytes,
    long UploadBytes,
    SessionEndReason Reason,
    Guid InterfaceGuid,
    string Ssid);

/// <summary>
/// BSSID 变化只作为诊断信号：漫游时连接没有断，累计必须连续，不能因此结束会话。
/// </summary>
public sealed record BssidChangedInfo(
    long SessionId,
    Guid InterfaceGuid,
    string? OldBssid,
    string? NewBssid,
    DateTimeOffset TimestampUtc);

/// <summary>异常类别。所有异常都只记录与上报，绝不终止采样循环。</summary>
public enum AnomalyKind
{
    /// <summary>WLAN 查询失败（服务忙、句柄无效等）。本帧按"接口暂时缺失"处理。</summary>
    DetectorFailure,

    /// <summary>IP Helper 读取失败。</summary>
    CounterFailure,

    /// <summary>单帧增量超过链路速率推算的上限。默认只告警，不丢数据。</summary>
    ImplausibleDelta,
}

/// <summary>异常事件参数。</summary>
public sealed record MonitorAnomaly(
    AnomalyKind Kind,
    string Message,
    DateTimeOffset TimestampUtc,
    long? ObservedBytes = null,
    long? LimitBytes = null,
    double? LinkSpeedBps = null);

/// <summary>
/// 未确认的恢复候选。只有 <see cref="RecoveryGap.MayMergeIntoSession"/> 为 false 时才会发出，
/// 阶段 3 应把它写成独立的恢复记录，而不是并入正式会话统计。
/// </summary>
public sealed record RecoveryCandidate(
    string RecoveryId,
    long SessionId,
    RecoveryGap Gap,
    string Reason,
    DateTimeOffset DetectedAtUtc);