using HotspotMeter.Core.Models;

namespace HotspotMeter.Core.Monitoring;

/// <summary>
/// 目标热点匹配。身份以接口 GUID 为准，SSID 只作辅助确认；BSSID 不参与匹配，
/// 因此漫游（BSSID 改变）不会被误判为断开。
/// </summary>
public static class HotspotMatcher
{
    /// <summary>比较用户配置的目标与当前连接。</summary>
    public static HotspotMatchResult Match(HotspotTarget target, HotspotConnection? connection)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (connection is null)
        {
            return HotspotMatchResult.NoConnection;
        }

        if (connection.InterfaceGuid != target.InterfaceGuid)
        {
            return HotspotMatchResult.InterfaceMismatch;
        }

        // SSID 区分大小写（Windows 语义），不做模糊匹配。
        return string.Equals(connection.Ssid, target.Ssid, StringComparison.Ordinal)
            ? HotspotMatchResult.Matched
            : HotspotMatchResult.SsidMismatch;
    }
}