using HotspotMeter.ApiProbe.Model;
using HotspotMeter.ApiProbe.Native;

namespace HotspotMeter.ApiProbe.Support;

/// <summary>
/// 选定"要监控的那一个无线接口"，并把它与 IP Helper 的行对应起来。
/// 规则：只考虑 WLAN 侧处于 Connected 的接口；可用 --guid 精确指定，或用 --ssid 过滤；
/// 都不给时取第一个已连接的无线接口（本机通常只有一个）。
/// </summary>
internal static class TargetResolver
{
    public sealed record Resolved(WlanSnapshot Wlan, IfRow? Row);

    public static Resolved Resolve(Guid? guidFilter, string? ssidFilter)
    {
        using var client = WlanClient.Open();
        var interfaces = client.Enumerate();
        var rows = IpHelper.ReadRows();
        return Resolve(interfaces, rows, guidFilter, ssidFilter);
    }

    public static Resolved Resolve(
        IReadOnlyList<WlanSnapshot> interfaces,
        IReadOnlyList<IfRow> rows,
        Guid? guidFilter,
        string? ssidFilter)
    {
        var candidates = interfaces.Where(i => i.IsConnected).ToList();

        if (guidFilter is { } guid)
        {
            var exact = interfaces.FirstOrDefault(i => i.InterfaceGuid == guid);
            if (exact is null)
            {
                throw new ProbeException($"找不到 GUID 为 {guid} 的无线接口。请先运行 list 查看可用接口。");
            }

            return new Resolved(exact, rows.FirstOrDefault(r => r.InterfaceGuid == guid));
        }

        if (!string.IsNullOrEmpty(ssidFilter))
        {
            candidates = candidates
                .Where(i => string.Equals(i.Ssid, ssidFilter, StringComparison.Ordinal))
                .ToList();
        }

        if (candidates.Count == 0)
        {
            throw new ProbeException(
                "当前没有已连接的无线接口。请先连接目标热点，或用 --guid 指定一个无线接口（即使未连接也可以读数）。");
        }

        if (candidates.Count > 1)
        {
            var names = string.Join(", ", candidates.Select(c => $"{c.Ssid ?? "<无SSID>"} ({c.InterfaceGuid})"));
            throw new ProbeException($"匹配到多个已连接的无线接口：{names}。请用 --guid 明确指定一个。");
        }

        var target = candidates[0];
        return new Resolved(target, rows.FirstOrDefault(r => r.InterfaceGuid == target.InterfaceGuid));
    }
}