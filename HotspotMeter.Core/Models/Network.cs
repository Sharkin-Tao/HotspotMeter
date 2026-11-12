namespace HotspotMeter.Core.Models;

/// <summary>
/// 网卡身份。以 Windows 接口 GUID 作为主键；LUID 用来识别"GUID 没变但底层设备换了"。
/// 阶段 1 实测：同一块无线网卡在 IP Helper 表里会出现多行同值记录，只有 GUID 能唯一定位。
/// </summary>
public readonly record struct NetworkInterfaceKey(Guid InterfaceGuid, ulong InterfaceLuid);

/// <summary>网卡累计收发字节。注意这是<strong>累计值</strong>，驱动重置后会归零，不能直接当作会话流量。</summary>
public sealed record InterfaceCounters(ulong InOctets, ulong OutOctets);

/// <summary>一次成功的网卡计数读取结果。</summary>
public sealed record InterfaceReading(NetworkInterfaceKey Key, InterfaceCounters Counters);

/// <summary>无线接口枚举结果，供将来的设置页选择目标热点。</summary>
public sealed record HotspotCandidate(
    Guid InterfaceGuid,
    string InterfaceDescription,
    string Ssid,
    string? Bssid,
    bool IsConnected,
    int SignalQuality,
    ulong RxLinkSpeedBps,
    ulong TxLinkSpeedBps)
{
    /// <summary>两个方向都拿不到速率时为 false，此时不参与任何"合理性上限"计算。</summary>
    public bool HasLinkSpeed => RxLinkSpeedBps > 0 || TxLinkSpeedBps > 0;
}

/// <summary>
/// 当前 WLAN 连接信息。<see cref="Bssid"/> 仅用于展示与诊断，<strong>不参与身份匹配</strong>，
/// 漫游导致 BSSID 变化时不得结束会话。
/// </summary>
public sealed record HotspotConnection(
    Guid InterfaceGuid,
    string Ssid,
    string? Bssid,
    ulong RxLinkSpeedBps,
    ulong TxLinkSpeedBps)
{
    /// <summary>两个方向都拿不到速率时为 false，此时不参与任何"合理性上限"计算。</summary>
    public bool HasLinkSpeed => RxLinkSpeedBps > 0 || TxLinkSpeedBps > 0;

    /// <summary>取两个方向中较大的链路速率，用于估算上限；都未知时返回 0。</summary>
    public ulong MaxLinkSpeedBps => Math.Max(RxLinkSpeedBps, TxLinkSpeedBps);
}

/// <summary>
/// 用户选中的目标热点。身份由 <see cref="InterfaceGuid"/> 决定，<see cref="Ssid"/> 只作辅助确认，
/// <see cref="BssidForDisplay"/> 只用于界面展示和日志，不参与匹配。
/// </summary>
public sealed record HotspotTarget(
    Guid ProfileId,
    Guid InterfaceGuid,
    string Ssid,
    string? BssidForDisplay = null);

/// <summary>目标匹配结果。用四种状态而不是布尔值，便于区分"换了网卡"和"连了别的 Wi-Fi"。</summary>
public enum HotspotMatchResult
{
    /// <summary>检测器返回 null：查询成功且确实没有连接。</summary>
    NoConnection,

    /// <summary>GUID 与 SSID 都符合目标。</summary>
    Matched,

    /// <summary>GUID 不同：换了网卡，或连接的是另一块无线适配器。</summary>
    InterfaceMismatch,

    /// <summary>GUID 相同但 SSID 不同：连了学校 Wi-Fi 等其它网络。</summary>
    SsidMismatch,
}