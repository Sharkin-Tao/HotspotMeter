using System.Globalization;
using HotspotMeter.ApiProbe.Native;
using HotspotMeter.ApiProbe.Support;

namespace HotspotMeter.ApiProbe.Commands;

/// <summary>
/// P1-9 / P1-10 / P1-11：跨进程、跨重启的计数器连续性验证。
///
/// 要回答的问题：
/// 1) 程序刚启动时（热点早已连接），基线是否只是"当前读数"，不会把历史累计值算成本次会话；
/// 2) 进程退出后计数器是否继续增长——这决定了崩溃期间的字节量在原理上能否恢复；
/// 3) 重启电脑/重置网卡后计数器归零，能否识别出来并拒绝"恢复"，避免把历史流量重复累计。
/// </summary>
internal static class ContinuityCommand
{
    /// <summary>开机时刻的近似值：当前 UTC 减去系统运行毫秒数。重启或长时间休眠后会明显变化。</summary>
    private static DateTimeOffset BootTimeApprox() =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static int Run(ArgReader args)
    {
        var savePath = args.Get("--save");
        var verifyPath = args.Get("--verify");

        if (savePath is null && verifyPath is null)
        {
            Console.WriteLine("用法: continuity --save <state.json>   或   continuity --verify <state.json>");
            return 1;
        }

        var layoutOk = LayoutCheck.Verify(out var layoutReport);
        Console.WriteLine(layoutReport);
        if (!layoutOk)
        {
            Console.WriteLine("结论: 失败 —— 结构体布局不正确，已停止。");
            return 2;
        }

        return savePath is not null ? Save(savePath, args) : Verify(verifyPath!, args);
    }

    private static int Save(string path, ArgReader args)
    {
        var resolved = TargetResolver.Resolve(args.GetGuid("--guid"), args.Get("--ssid"));
        var target = resolved.Wlan;
        if (resolved.Row is null)
        {
            throw new ProbeException($"无线接口 {target.InterfaceGuid} 在 IP Helper 表中没有对应行，无法保存基线。");
        }

        var row = resolved.Row;
        var state = new ContinuityState(
            DateTimeOffset.UtcNow,
            BootTimeApprox(),
            Environment.TickCount64,
            target.InterfaceGuid,
            target.Ssid,
            row.InterfaceLuid,
            row.InterfaceIndex,
            row.InOctets,
            row.OutOctets,
            row.Alias,
            target.Bssid);

        JsonOutput.Write(path, state);

        Console.WriteLine();
        Console.WriteLine("已保存基线：");
        Console.WriteLine($"  接口      : {state.InterfaceGuid}  \"{state.Ssid}\"  alias=\"{state.Alias}\"");
        Console.WriteLine($"  LUID      : 0x{state.InterfaceLuid:X16}  ifIndex={state.InterfaceIndex}");
        Console.WriteLine($"  计数      : in={state.InOctets}  out={state.OutOctets}");
        Console.WriteLine($"  开机近似  : {state.BootTimeApproxUtc:O}（TickCount64={state.TickCount64}）");
        Console.WriteLine();
        Console.WriteLine("下一步：退出本进程（不要重启电脑），产生一段流量，再运行 continuity --verify 同一个文件。");

        return 0;
    }

    private static int Verify(string path, ArgReader args)
    {
        var saved = JsonOutput.Read<ContinuityState>(path)
                    ?? throw new ProbeException("状态文件为空或无法解析。");

        var rows = IpHelper.ReadRows();
        var row = rows.FirstOrDefault(r => r.InterfaceGuid == saved.InterfaceGuid);
        var present = row is not null;

        var tickNow = Environment.TickCount64;
        var bootNow = BootTimeApprox();
        var bootShiftMinutes = (bootNow - saved.BootTimeApproxUtc).TotalMinutes;

        // 同一开机的 TickCount64 只会增长；变小说明重启过。
        var rebootDetected = tickNow < saved.TickCount64 || Math.Abs(bootShiftMinutes) > 10;
        var sameInterface = present && row!.InterfaceLuid == saved.InterfaceLuid;
        var counterWentBackwards = present && (row!.InOctets < saved.InOctets || row.OutOctets < saved.OutOctets);

        string verdict;
        string explanation;
        ulong gapIn = 0;
        ulong gapOut = 0;

        if (!present)
        {
            verdict = "INTERFACE_ABSENT";
            explanation = "该 GUID 当前不在接口表中（网卡被禁用/拔出/驱动卸载）。此时无法比较计数，也不能恢复任何流量。";
        }
        else if (rebootDetected)
        {
            verdict = "REBOOT_DETECTED";
            explanation = $"检测到系统重启（TickCount64 {saved.TickCount64} → {tickNow}，开机时刻偏移 {bootShiftMinutes:0.0} 分钟）。"
                          + "重启后网卡计数器从 0 重新开始，绝不能把当前读数当作增量恢复，否则会重复累计。";
        }
        else if (!sameInterface)
        {
            verdict = "INTERFACE_CHANGED";
            explanation = $"LUID 变了（0x{saved.InterfaceLuid:X16} → 0x{row!.InterfaceLuid:X16}），说明网卡被重新枚举。"
                          + "即便 GUID 相同，计数器也已被重置，不能跨这次变化恢复流量。";
        }
        else if (counterWentBackwards)
        {
            verdict = "COUNTER_RESET";
            explanation = $"计数器比保存时更小（in {saved.InOctets} → {row!.InOctets}，out {saved.OutOctets} → {row.OutOctets}），"
                          + "说明网卡/驱动重置过。必须重建基线，不能恢复。";
        }
        else
        {
            gapIn = row!.InOctets - saved.InOctets;
            gapOut = row.OutOctets - saved.OutOctets;
            verdict = "GAP_RECOVERABLE";
            explanation = "同一开机的同一接口，计数器只增不减。因此进程退出期间产生的字节总量可以被准确算出，"
                          + "但这些字节发生在哪一天、哪一秒无法还原——恢复时只能计入恢复当天并打标记。";
        }

        Console.WriteLine();
        Console.WriteLine("== 连续性判定 ==");
        Console.WriteLine($"判定结果        : {verdict}");
        Console.WriteLine($"说明            : {explanation}");
        Console.WriteLine();
        Console.WriteLine($"保存时（UTC）   : {saved.SavedAtUtc:O}");
        Console.WriteLine($"当前（UTC）     : {DateTimeOffset.UtcNow:O}");
        Console.WriteLine($"接口            : {saved.InterfaceGuid}  \"{saved.Ssid}\"  alias=\"{saved.Alias}\"");
        Console.WriteLine($"计数保存 → 当前 : in {saved.InOctets} → {(present ? row!.InOctets : 0)}   out {saved.OutOctets} → {(present ? row!.OutOctets : 0)}");

        if (verdict == "GAP_RECOVERABLE")
        {
            Console.WriteLine($"可恢复增量      : in={gapIn} ({Format.Bytes(gapIn)})  out={gapOut} ({Format.Bytes(gapOut)})");
            Console.WriteLine($"合计            : {Format.Bytes(gapIn + gapOut)}");
        }

        Console.WriteLine($"开机时刻偏移    : {bootShiftMinutes:0.0} 分钟（超过 ±10 分钟视为重启；长时间休眠可能造成误判，"
                          + "但误判方向是“放弃恢复”，只会少算、不会多算）");

        if (args.Get("--json") is { } jsonPath)
        {
            JsonOutput.Write(jsonPath, new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                verdict,
                explanation,
                saved,
                present,
                sameInterface,
                rebootDetected,
                counterWentBackwards,
                bootShiftMinutes,
                currentInOctets = present ? row!.InOctets : (ulong?)null,
                currentOutOctets = present ? row!.OutOctets : (ulong?)null,
                gapInBytes = gapIn,
                gapOutBytes = gapOut,
                recoverable = verdict == "GAP_RECOVERABLE",
            });
        }

        return 0;
    }
}

/// <summary>continuity --save 写出的状态文件结构。</summary>
internal sealed record ContinuityState(
    DateTimeOffset SavedAtUtc,
    DateTimeOffset BootTimeApproxUtc,
    long TickCount64,
    Guid InterfaceGuid,
    string? Ssid,
    ulong InterfaceLuid,
    uint InterfaceIndex,
    ulong InOctets,
    ulong OutOctets,
    string Alias,
    string? Bssid);