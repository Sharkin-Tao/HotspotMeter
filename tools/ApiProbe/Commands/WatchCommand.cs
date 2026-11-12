using System.Diagnostics;
using System.Globalization;
using HotspotMeter.ApiProbe.Model;
using HotspotMeter.ApiProbe.Native;
using HotspotMeter.ApiProbe.Support;

namespace HotspotMeter.ApiProbe.Commands;

/// <summary>
/// P1-4 / P1-6 / P1-8：按固定间隔采样目标网卡，逐帧打印增量与速度。
/// 关键点：
/// - 时间差只用 <see cref="Stopwatch"/>（单调时钟），不受系统时间修改影响；
/// - 计数器回退（网卡重置）或接口行消失时，本帧增量按 0 处理并重建基线，绝不产生负数；
/// - 同时采集其他接口行，用于证明"没有把 VPN/虚拟网卡的流量加进来"。
/// </summary>
internal static class WatchCommand
{
    public static int Run(ArgReader args)
    {
        var layoutOk = LayoutCheck.Verify(out var layoutReport);
        Console.WriteLine(layoutReport);
        if (!layoutOk)
        {
            Console.WriteLine("结论: 失败 —— 结构体布局不正确，已停止。");
            return 2;
        }

        var seconds = Math.Max(args.GetInt("--seconds", 60), 1);
        var intervalMs = Math.Max(args.GetInt("--interval", 1000), 100);
        var compareOthers = !args.Has("--no-others");

        var resolved = TargetResolver.Resolve(args.GetGuid("--guid"), args.Get("--ssid"));
        var target = resolved.Wlan;
        if (resolved.Row is null)
        {
            throw new ProbeException(
                $"无线接口 {target.InterfaceGuid} 在 IP Helper 表中没有对应行，无法读取计数器。"
                + "请运行 list 确认映射关系。");
        }

        var csvPath = args.Get("--csv")
                      ?? Path.Combine("tools", "ApiProbe", "out", $"watch-{target.InterfaceGuid:D}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        Console.WriteLine();
        Console.WriteLine($"目标接口 : {target.InterfaceGuid}  \"{target.Ssid}\"  state={target.State}  alias=\"{resolved.Row.Alias}\"");
        Console.WriteLine($"起始计数 : in={resolved.Row.InOctets}  out={resolved.Row.OutOctets}");
        Console.WriteLine($"采样设置 : 每 {intervalMs} ms 一次，共 {seconds} 秒；其他行{(compareOthers ? "参与" : "不参与")}对比");
        Console.WriteLine($"CSV      : {Path.GetFullPath(csvPath)}");
        Console.WriteLine();

        using var csv = new CsvSampleWriter(csvPath);
        var startedAtUtc = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var previous = new CounterSample(DateTimeOffset.UtcNow, false, target.InterfaceGuid, null, 0, 0, 0);
        var previousRows = new Dictionary<Guid, (ulong In, ulong Out)>();
        var previousElapsedMs = 0L;

        var samples = 0;
        var resets = 0;
        var missing = 0;
        var wakeRebuilds = 0;
        var activeMs = 0L;
        ulong totalIn = 0;
        ulong totalOut = 0;
        double maxInBps = 0;
        double maxOutBps = 0;

        // 用"有效采样时长"而不是墙钟时长作为结束条件：睡眠期间 Stopwatch 仍在走，
        // 若按墙钟计时，唤醒瞬间就会因为超过目标时长而直接退出，测不到唤醒后的行为。
        while (activeMs < seconds * 1000L)
        {
            var elapsedMs = sw.ElapsedMilliseconds;
            var rows = IpHelper.ReadRows();
            var row = rows.FirstOrDefault(r => r.InterfaceGuid == target.InterfaceGuid);

            var sample = new CounterSample(
                DateTimeOffset.UtcNow,
                row is not null,
                target.InterfaceGuid,
                row?.Alias,
                row?.InterfaceIndex ?? 0,
                row?.InOctets ?? 0,
                row?.OutOctets ?? 0);

            ulong inDelta = 0;
            ulong outDelta = 0;
            var reset = false;
            var gapMs = elapsedMs - previousElapsedMs;

            // 睡眠/休眠/长挂起：两次采样之间被系统冻结了远超一个周期的时长。
            // 规格要求唤醒后重建基线，而不是把这段不可控区间的计数差当成一次"突发流量"。
            var sleepGap = samples > 0 && gapMs > intervalMs * 3L;

            if (sleepGap)
            {
                wakeRebuilds++;
            }
            else if (!sample.RowPresent)
            {
                missing++;
            }
            else if (previous.RowPresent)
            {
                if (sample.InOctets >= previous.InOctets && sample.OutOctets >= previous.OutOctets)
                {
                    inDelta = sample.InOctets - previous.InOctets;
                    outDelta = sample.OutOctets - previous.OutOctets;
                    totalIn += inDelta;
                    totalOut += outDelta;
                }
                else
                {
                    // 计数器回退：网卡被禁用/驱动重置/唤醒后重启计数。重建基线，不累计也不报负数。
                    reset = true;
                    resets++;
                }
            }

            var deltaSeconds = (sleepGap ? 0 : gapMs) / 1000.0;
            var inBps = deltaSeconds > 0 ? inDelta / deltaSeconds : 0;
            var outBps = deltaSeconds > 0 ? outDelta / deltaSeconds : 0;
            maxInBps = Math.Max(maxInBps, inBps);
            maxOutBps = Math.Max(maxOutBps, outBps);

            var otherDeltas = compareOthers ? DescribeOtherDeltas(rows, target.InterfaceGuid, previousRows) : string.Empty;

            csv.Write(
                sample.TimestampUtc,
                elapsedMs,
                gapMs,
                target.InterfaceGuid,
                sample.Alias ?? string.Empty,
                sample.InterfaceIndex,
                sample.RowPresent,
                sample.InOctets,
                sample.OutOctets,
                inDelta,
                outDelta,
                inBps,
                outBps,
                reset,
                sleepGap,
                otherDeltas);

            var flags = string.Empty;
            if (reset)
            {
                flags += " [计数器回退→重建基线]";
            }

            if (sleepGap)
            {
                flags += $" [睡眠/长暂停 {gapMs} ms→重建基线]";
            }

            if (!sample.RowPresent)
            {
                flags += " [接口行缺失]";
            }

            if (otherDeltas.Length > 0)
            {
                flags += $" [其他行: {otherDeltas}]";
            }

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[{Format.LocalTime(sample.TimestampUtc)}] in +{Format.Bytes(inDelta),-9} out +{Format.Bytes(outDelta),-9} ↓ {Format.Rate(inBps),-11} ↑ {Format.Rate(outBps),-11} 累计 in={Format.Bytes(sample.InOctets)} out={Format.Bytes(sample.OutOctets)}{flags}"));

            previous = sample;
            previousElapsedMs = elapsedMs;
            previousRows = rows.ToDictionary(r => r.InterfaceGuid, r => (r.InOctets, r.OutOctets));
            samples++;
            if (!sleepGap)
            {
                activeMs += gapMs;
            }

            csv.Flush();

            var sleepMs = intervalMs - (sw.ElapsedMilliseconds - elapsedMs);
            if (sleepMs > 0)
            {
                Thread.Sleep((int)sleepMs);
            }
        }

        var endedAtUtc = DateTimeOffset.UtcNow;
        Console.WriteLine();
        Console.WriteLine("== 汇总 ==");
        Console.WriteLine($"采样帧数   : {samples}（间隔 {intervalMs} ms，目标有效时长 {seconds} 秒，实际 {activeMs / 1000.0:0.0} 秒）");
        Console.WriteLine($"会话增量   : in {Format.Bytes(totalIn)} ({totalIn}) / out {Format.Bytes(totalOut)} ({totalOut})");
        Console.WriteLine($"峰值速度   : ↓ {Format.Rate(maxInBps)}  ↑ {Format.Rate(maxOutBps)}");
        Console.WriteLine($"计数器回退 : {resets} 次；接口行缺失 : {missing} 帧；睡眠/长暂停重建 : {wakeRebuilds} 次");
        Console.WriteLine($"CSV        : {csv.Path}");

        if (args.Get("--json") is { } jsonPath)
        {
            JsonOutput.Write(jsonPath, new
            {
                targetGuid = target.InterfaceGuid,
                ssid = target.Ssid,
                ssidFilterMatched = args.Get("--ssid") is not null,
                intervalMs,
                requestedSeconds = seconds,
                samples,
                resets,
                missingFrames = missing,
                wakeRebuilds,
                activeSeconds = activeMs / 1000.0,
                totalInBytes = totalIn,
                totalOutBytes = totalOut,
                maxInBytesPerSecond = maxInBps,
                maxOutBytesPerSecond = maxOutBps,
                startedAtUtc,
                endedAtUtc,
                csvPath = csv.Path,
            });
        }

        return 0;
    }

    /// <summary>把其他接口行的增量压成一行文本；为 0 的行不打印，避免刷屏。</summary>
    private static string DescribeOtherDeltas(
        IReadOnlyList<IfRow> rows,
        Guid targetGuid,
        IReadOnlyDictionary<Guid, (ulong In, ulong Out)> previousRows)
    {
        var parts = new List<string>();
        foreach (var row in rows)
        {
            if (row.InterfaceGuid == targetGuid)
            {
                continue;
            }

            if (!previousRows.TryGetValue(row.InterfaceGuid, out var previous))
            {
                continue;
            }

            if (row.InOctets < previous.In || row.OutOctets < previous.Out)
            {
                continue;
            }

            var inDelta = row.InOctets - previous.In;
            var outDelta = row.OutOctets - previous.Out;
            if (inDelta == 0 && outDelta == 0)
            {
                continue;
            }

            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{row.Alias}=+{inDelta}/{outDelta}"));
        }

        return string.Join('|', parts);
    }
}