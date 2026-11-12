namespace HotspotMeter.Core.Models;

/// <summary>
/// 一次采样的原始输入。<see cref="DetectorFailed"/> 用于区分"WLAN 明确未连接"与"WLAN 查询失败"：
/// 前者要结束会话，后者只能暂停。
/// </summary>
public readonly record struct MonitorFrame(
    DateTimeOffset TimestampUtc,
    long MonotonicMilliseconds,
    HotspotConnection? Connection,
    InterfaceReading? Reading,
    long TickCount64,
    bool DetectorFailed = false);

/// <summary>
/// 一次采样的处理结果：快照 + 本帧产生的领域事件。引擎只负责把事件转成 .NET 事件，不含业务判断。
/// </summary>
public sealed record MonitorStep(
    MonitorSnapshot Snapshot,
    MonitorSessionInfo? SessionStarted = null,
    MonitorSessionInfo? SessionResumed = null,
    MonitorSessionEndedInfo? SessionEnded = null,
    BssidChangedInfo? BssidChanged = null,
    MonitorAnomaly? Anomaly = null,
    RecoveryCandidate? Recovery = null);