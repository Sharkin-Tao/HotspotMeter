using System.Globalization;
using HotspotMeter.ApiProbe.Model;
using HotspotMeter.ApiProbe.Native;
using HotspotMeter.ApiProbe.Support;

namespace HotspotMeter.ApiProbe.Commands;

/// <summary>
/// P1-1 / P1-2：枚举无线接口与 IP Helper 行，并验证 GUID 能否唯一对应到一行。
/// 判定标准写死在返回码里：0=全部通过，2=结构体自检失败，3=映射不唯一或类型不是 802.11。
/// </summary>
internal static class ListCommand
{
    public static int Run(ArgReader args)
    {
        Console.WriteLine("== 结构体布局自检 ==");
        var layoutOk = LayoutCheck.Verify(out var layoutReport);
        Console.WriteLine(layoutReport);
        if (!layoutOk)
        {
            Console.WriteLine();
            Console.WriteLine("结论: 失败 —— 结构体大小与 Windows 头文件不一致，后续数据不可信，已停止。");
            return 2;
        }

        using var client = WlanClient.Open();
        var interfaces = client.Enumerate();
        var rows = IpHelper.ReadRows();

        Console.WriteLine();
        Console.WriteLine($"== WLAN 接口（{interfaces.Count} 个）==");
        foreach (var wlan in interfaces)
        {
            PrintWlan(wlan);
        }

        Console.WriteLine();
        Console.WriteLine($"== IP Helper 接口表（{rows.Count} 行）==");
        foreach (var row in rows)
        {
            PrintRow(row);
        }

        Console.WriteLine();
        Console.WriteLine("== GUID 映射判定（P1-2）==");
        var mappings = new List<GuidMappingResult>();
        foreach (var wlan in interfaces)
        {
            var matches = rows.Where(r => r.InterfaceGuid == wlan.InterfaceGuid).ToList();
            var unique = matches.Count == 1;
            var typeOk = unique && matches[0].Type == IpHelper.IfTypeIeee80211;
            mappings.Add(new GuidMappingResult(wlan.InterfaceGuid, wlan.Description, matches.Count, unique, typeOk, matches.FirstOrDefault(), matches));

            var verdict = unique && typeOk ? "PASS" : "FAIL";
            Console.WriteLine($"[{verdict}] {wlan.InterfaceGuid}  匹配行数={matches.Count}  类型=71? {(typeOk ? "是" : "否")}");
            foreach (var match in matches)
            {
                Console.WriteLine($"        行: index={match.InterfaceIndex} alias=\"{match.Alias}\" type={match.Type} luid=0x{match.InterfaceLuid:X16}");
            }

            if (!unique)
            {
                Console.WriteLine("        说明: GUID 必须唯一对应一行；0 行表示 WLAN 与 IP Helper 视图不一致，多行表示需要用 LUID 进一步区分。");
            }
            else if (!typeOk)
            {
                Console.WriteLine($"        说明: 该行 Type={matches[0].Type}，不是 IF_TYPE_IEEE80211(71)，可能是虚拟网卡或驱动上报异常。");
            }
        }

        var allMapped = mappings.All(m => m.UniqueMatch && m.TypeIsIeee80211);

        Console.WriteLine();
        Console.WriteLine($"结论: 结构体={(layoutOk ? "OK" : "失败")}；GUID 映射={(allMapped ? "全部通过" : "存在失败项")}");

        if (args.Get("--json") is { } jsonPath)
        {
            JsonOutput.Write(jsonPath, new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                layout = new
                {
                    mibIfRow2 = System.Runtime.InteropServices.Marshal.SizeOf<MibIfRow2>(),
                    wlanConnectionAttributes = System.Runtime.InteropServices.Marshal.SizeOf<WlanConnectionAttributes>(),
                    wlanInterfaceInfo = System.Runtime.InteropServices.Marshal.SizeOf<WlanInterfaceInfo>(),
                    ok = layoutOk,
                },
                wlanInterfaces = interfaces,
                ifRows = rows,
                mappings,
                allMapped,
            });
        }

        return allMapped ? 0 : 3;
    }

    private static void PrintWlan(WlanSnapshot wlan)
    {
        Console.WriteLine($"- {wlan.InterfaceGuid}");
        Console.WriteLine($"  desc      : {wlan.Description}");
        Console.WriteLine($"  state     : {StateName(wlan.State)} ({wlan.State})   connected={wlan.IsConnected}");
        Console.WriteLine($"  ssid      : {(wlan.Ssid is null ? "<未连接>" : $"\"{wlan.Ssid}\"")}");
        Console.WriteLine($"  bssid     : {wlan.Bssid ?? "<无>"}");
        Console.WriteLine($"  profile   : {(wlan.ProfileName is null ? "<无>" : $"\"{wlan.ProfileName}\"")}");
        Console.WriteLine($"  signal    : {wlan.SignalQuality}%   rx={wlan.RxRateKbps} kbps   tx={wlan.TxRateKbps} kbps");
        Console.WriteLine($"  phy/auth/cipher : {wlan.PhyType}{PhyName(wlan.PhyType)} / {wlan.AuthAlgorithm} / {wlan.CipherAlgorithm}{CipherName(wlan.CipherAlgorithm)}   security={wlan.SecurityEnabled}   mode={wlan.ConnectionMode}");
    }

    private static void PrintRow(IfRow row)
    {
        var bytes = string.Create(
            CultureInfo.InvariantCulture,
            $"in={Format.Bytes(row.InOctets)} ({row.InOctets})  out={Format.Bytes(row.OutOctets)} ({row.OutOctets})");
        Console.WriteLine(
            $"[{row.InterfaceIndex,3}] {Format.Clamp(row.Alias, 20),-20} type={row.Type,-3} oper={row.OperStatus} admin={row.AdminStatus} "
            + $"media={row.MediaConnectState} mtu={row.Mtu} mac={row.PhysicalAddress}");
        Console.WriteLine(
            $"      guid=0x{row.InterfaceGuid} luid=0x{row.InterfaceLuid:X16} {bytes}");
    }

    private static string StateName(int state) => (WlanInterfaceState)state switch
    {
        WlanInterfaceState.NotReady => "未就绪",
        WlanInterfaceState.Connected => "已连接",
        WlanInterfaceState.AdHocNetworkFormed => "Ad-hoc",
        WlanInterfaceState.Disconnecting => "断开中",
        WlanInterfaceState.Disconnected => "未连接",
        WlanInterfaceState.Associating => "关联中",
        WlanInterfaceState.Discovering => "扫描中",
        WlanInterfaceState.Authenticating => "认证中",
        _ => "未知",
    };

    private static string PhyName(int phy) => phy switch
    {
        8 => "(HT/802.11n)",
        9 => "(VHT/802.11ac)",
        11 => "(HE/802.11ax)",
        12 => "(EHT/802.11be)",
        _ => string.Empty,
    };

    private static string CipherName(int cipher) => cipher switch
    {
        4 => "(CCMP)",
        2 => "(TKIP)",
        8 => "(GCMP)",
        _ => string.Empty,
    };
}