namespace HotspotMeter.Core.Models;

/// <summary>
/// 采样健康度。与"是否连接目标热点"是两个独立维度：未连接时状态仍是 <see cref="Running"/>，只是没有会话。
/// </summary>
public enum MonitorState
{
    /// <summary>引擎尚未启动或已停止。仅表示生命周期，不表示故障。</summary>
    Stopped,

    /// <summary>采样正常（或已正常结束会话）。</summary>
    Running,

    /// <summary>目标接口读不到计数行，或检测器暂时失败。会话暂停但不结束，累计保留。</summary>
    InterfaceMissing,

    /// <summary>计数器比分基线小。已重建基线，本帧不计入。</summary>
    CounterReset,

    /// <summary>两次采样间隔超过阈值（睡眠/长暂停）。已重建基线，本帧不计入。</summary>
    Suspended,

    /// <summary>恢复时判定为重启过。禁止任何跨重启增量恢复。</summary>
    Rebooted,
}