using System.Text;
using System.Text.Json;
using HotspotMeter.Core.Formatting;
using HotspotMeter.Core.Models;
using HotspotMeter.Core.Monitoring;
using HotspotMeter.Infrastructure;
using HotspotMeter.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace HotspotMeter.MonitorHost;

/// <summary>
/// 阶段 2 的独立控制台宿主：用真实 WLAN + IP Helper 驱动 <see cref="MonitorEngine"/>，
/// 便于与"设置 → 网络和 Internet → 数据使用量"人工比对。
/// 它不属于产品 UI，也不引用任何 WPF 代码。
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        var ssid = GetOption(args, "--ssid");
        var guidText = GetOption(args, "--guid");
        var seconds = GetIntOption(args, "--seconds", 0);
        var emitJson = args.Contains("--json");

        Guid? guid = Guid.TryParse(guidText, out var parsedGuid) ? parsedGuid : null;

        using var detector = new WlanHotspotDetector();
        var counter = new InterfaceTrafficCounter();

        if (args.Contains("--list"))
        {
            var all = await detector.GetCandidatesAsync(CancellationToken.None);
            PrintCandidates(all);
            return all.Count == 0 ? 3 : 0;
        }

        var candidates = await detector.GetCandidatesAsync(CancellationToken.None);
        var connection = ResolveTarget(candidates, guid, ssid);
        if (connection is null)
        {
            Console.Error.WriteLine(ssid is null && guid is null
                ? "当前没有已连接的无线网络；请连接手机热点后再运行，或用 --ssid / --guid 指定。"
                : $"找不到匹配的已连接热点（ssid={ssid ?? "*"}, guid={guid?.ToString() ?? "*"}）。");
            Console.Error.WriteLine();
            PrintCandidates(candidates);
            return 3;
        }

        var target = new HotspotTarget(
            Guid.NewGuid(),
            connection.InterfaceGuid,
            connection.Ssid,
            connection.Bssid);

        var options = new MonitorOptions();
        var engine = new MonitorEngine(
            options,
            target,
            detector,
            counter,
            new SystemMonotonicClock(),
            new SystemWallClock(),
            new SystemUptimeProvider(),
            new NoEvidenceContinuityProvider(),
            NullLogger<MonitorEngine>.Instance);

        Console.WriteLine($"目标热点：{target.Ssid}（BSSID {target.BssidForDisplay ?? "未知"}）");
        Console.WriteLine($"接口 GUID：{target.InterfaceGuid}");
        Console.WriteLine($"采样间隔：{options.SamplingInterval.TotalMilliseconds:F0} ms；Ctrl+C 结束并打印汇总。");
        Console.WriteLine();

        var startedCount = 0;
        var endedCount = 0;
        engine.SessionStarted += (_, session) =>
        {
            Interlocked.Increment(ref startedCount);
            if (!emitJson)
            {
                Console.WriteLine($"[会话开始 #{session.SessionId}] {session.StartedAtUtc.ToLocalTime():HH:mm:ss}");
            }
        };

        engine.SessionEnded += (_, session) =>
        {
            Interlocked.Increment(ref endedCount);
            if (!emitJson)
            {
                Console.WriteLine(
                    $"[会话结束 #{session.SessionId}] 原因={session.Reason} 下行={ByteSizeFormatter.Format(session.DownloadBytes)} 上行={ByteSizeFormatter.Format(session.UploadBytes)}");
            }
        };

        engine.AnomalyDetected += (_, anomaly) =>
        {
            if (!emitJson)
            {
                Console.Error.WriteLine($"[异常 {anomaly.Kind}] {anomaly.Message}");
            }
        };

        engine.RecoveryCandidateDetected += (_, candidate) =>
        {
            if (!emitJson)
            {
                Console.WriteLine(
                    $"[恢复候选] 会话 #{candidate.SessionId} 增量={ByteSizeFormatter.Format(candidate.Gap.TotalBytes)} 连续性={candidate.Gap.Continuity}（阶段 3 决定是否入账）");
            }
        };

        engine.SnapshotProduced += (_, snapshot) =>
        {
            if (emitJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions));
                return;
            }

            Console.WriteLine(FormatSnapshot(snapshot));
        };

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        await engine.StartAsync(cancellation.Token);

        try
        {
            if (seconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellation.Token);
            }
            else
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常结束。
        }

        await engine.StopAsync();

        var final = engine.Current;
        Console.WriteLine();
        Console.WriteLine("—— 本次运行汇总 ——");
        Console.WriteLine($"会话数：开始 {startedCount}，结束 {endedCount}");
        Console.WriteLine($"最终状态：{engine.State}");
        if (final is not null)
        {
            Console.WriteLine(
                $"最后快照：会话 #{final.SessionId?.ToString() ?? "-"} 下行={ByteSizeFormatter.Format(final.SessionDownloadBytes)} 上行={ByteSizeFormatter.Format(final.SessionUploadBytes)} 状态={final.State}");
        }

        var baseline = engine.CaptureBaseline();
        Console.WriteLine(baseline is null
            ? "未留下可恢复基线（会话已结束或未采样）。"
            : $"当前基线：in={baseline.InOctets} out={baseline.OutOctets} tick={baseline.TickCount64}（阶段 3 用它写检查点）。");

        return 0;
    }

    private static HotspotConnection? ResolveTarget(
        IReadOnlyList<HotspotCandidate> candidates,
        Guid? guid,
        string? ssid)
    {
        foreach (var candidate in candidates)
        {
            if (!candidate.IsConnected)
            {
                continue;
            }

            if (guid is not null && candidate.InterfaceGuid != guid)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(ssid) && !string.Equals(candidate.Ssid, ssid, StringComparison.Ordinal))
            {
                continue;
            }

            return new HotspotConnection(
                candidate.InterfaceGuid,
                candidate.Ssid,
                candidate.Bssid,
                candidate.RxLinkSpeedBps,
                candidate.TxLinkSpeedBps);
        }

        return null;
    }

    private static void PrintCandidates(IReadOnlyList<HotspotCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            Console.WriteLine("（WLAN 服务没有报告任何无线接口）");
            return;
        }

        Console.WriteLine("无线接口：");
        foreach (var candidate in candidates)
        {
            Console.WriteLine(
                $"  {(candidate.IsConnected ? "已连接" : "未连接")}  {candidate.Ssid,-24} {candidate.InterfaceDescription}  {candidate.InterfaceGuid}");
        }
    }

    private static string FormatSnapshot(MonitorSnapshot snapshot)
    {
        var time = snapshot.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
        var state = snapshot.State switch
        {
            MonitorState.Running => "运行中",
            MonitorState.InterfaceMissing => "接口缺失",
            MonitorState.CounterReset => "计数回退",
            MonitorState.Suspended => "已暂停",
            MonitorState.Rebooted => "重启后",
            _ => "已停止",
        };

        var speed = $"{ByteSizeFormatter.Format((long)snapshot.DownloadBytesPerSecond)}/s ↓  {ByteSizeFormatter.Format((long)snapshot.UploadBytesPerSecond)}/s ↑";
        var warning = string.IsNullOrEmpty(snapshot.Warning) ? string.Empty : $"  ⚠ {snapshot.Warning}";

        return $"[{time}] {state,-6} 下载 {ByteSizeFormatter.Format(snapshot.SessionDownloadBytes),10}  上传 {ByteSizeFormatter.Format(snapshot.SessionUploadBytes),10}  {speed}{warning}";
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int GetIntOption(string[] args, string name, int fallback)
        => int.TryParse(GetOption(args, name), out var value) ? value : fallback;

    private static void PrintHelp()
    {
        Console.WriteLine("MonitorHost —— HotspotMeter 阶段 2 控制台宿主");
        Console.WriteLine("  --list              列出无线接口与连接状态");
        Console.WriteLine("  --ssid <名称>       指定目标热点 SSID");
        Console.WriteLine("  --guid <GUID>       指定目标无线接口 GUID");
        Console.WriteLine("  --seconds <秒>      运行指定秒数后自动停止（默认一直运行到 Ctrl+C）");
        Console.WriteLine("  --json              每个快照输出一行 JSON，便于脚本处理");
        Console.WriteLine();
        Console.WriteLine("示例：dotnet run --project tools/MonitorHost -- --ssid \"TestHotspot\" --seconds 60");
    }
}