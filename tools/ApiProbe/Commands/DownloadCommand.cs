using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using HotspotMeter.ApiProbe.Native;
using HotspotMeter.ApiProbe.Support;

namespace HotspotMeter.ApiProbe.Commands;

/// <summary>
/// P1-3：用一次已知体积的下载，检验 MIB_IF_ROW2 的 InOctets/OutOctets 是否真的反映网卡收发。
/// 判定思路：计数器增量应不小于文件字节数（链路层与 TCP/TLS 头会额外增加若干百分比的字节），
/// 并且与 .NET 的 NetworkInterface 统计（另一条 API 路径）在同量级。
/// 原型只报告实测数值与偏差，不为了"通过"而修改预期。
/// </summary>
internal static class DownloadCommand
{
    public static async Task<int> RunAsync(ArgReader args)
    {
        var layoutOk = LayoutCheck.Verify(out var layoutReport);
        Console.WriteLine(layoutReport);
        if (!layoutOk)
        {
            Console.WriteLine("结论: 失败 —— 结构体布局不正确，已停止。");
            return 2;
        }

        var requestedBytes = args.GetLong("--bytes", 157_286_400);
        // 注意：Cloudflare 的 __down 端点在部分网络会直接返回 403（本机实测如此），
        // 因此默认使用支持 Range 的 Hetzner 镜像，通过多段请求凑足目标字节数。
        var url = args.Get("--url") ?? "https://ash-speed.hetzner.com/100MB.bin";

        var resolved = TargetResolver.Resolve(args.GetGuid("--guid"), args.Get("--ssid"));
        var target = resolved.Wlan;
        if (resolved.Row is null)
        {
            throw new ProbeException($"无线接口 {target.InterfaceGuid} 在 IP Helper 表中没有对应行，无法测量。");
        }

        var guid = target.InterfaceGuid;
        var before = ReadCounters(guid);
        if (before is null)
        {
            throw new ProbeException("下载前读取目标接口计数器失败。");
        }

        var bclBefore = ReadBcl(guid);

        Console.WriteLine();
        Console.WriteLine($"目标接口 : {guid}  \"{target.Ssid}\"  alias=\"{before.Value.Alias}\"");
        Console.WriteLine($"URL      : {url}");
        Console.WriteLine($"计划体积 : {Format.Bytes((ulong)Math.Max(requestedBytes, 0))}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HotspotMeter-ApiProbe/1.0 (local feasibility test)");

        long total = 0;
        var lastReport = 0L;
        var sw = Stopwatch.StartNew();
        Console.WriteLine("开始下载 ...");

        var remaining = requestedBytes > 0 ? requestedBytes : long.MaxValue;
        while (remaining > 0)
        {
            // 每段 16 MiB：单个请求失败时不会浪费太多已下载的流量，同时总字节数仍然可控。
            var chunk = (int)Math.Min(remaining, 16L * 1024 * 1024);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, chunk - 1);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                throw new ProbeException(
                    $"下载失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}（URL: {url}）。"
                    + "请换一个支持 Range 的镜像地址后重试。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[128 * 1024];
            var chunkRead = 0;
            int read;
            while (chunkRead < chunk && (read = await stream.ReadAsync(buffer)) > 0)
            {
                var take = Math.Min(read, chunk - chunkRead);
                chunkRead += take;
                total += take;
                if (total - lastReport >= 20L * 1024 * 1024)
                {
                    lastReport = total;
                    Console.WriteLine($"  ... 已接收 {Format.Bytes((ulong)total)}");
                }
            }

            remaining -= chunkRead;
            if (chunkRead == 0)
            {
                throw new ProbeException("服务端没有返回数据（chunkRead=0），请检查 URL 是否可用。");
            }
        }

        sw.Stop();

        // 驱动/NDIS 的计数器不是同帧更新的，等一会儿再读，否则可能少算最后一段。
        await Task.Delay(1500);

        var after = ReadCounters(guid);
        if (after is null)
        {
            throw new ProbeException("下载后读取目标接口计数器失败。");
        }

        var bclAfter = ReadBcl(guid);

        var counterReset = after.Value.In < before.Value.In || after.Value.Out < before.Value.Out;
        var inDelta = counterReset ? 0UL : after.Value.In - before.Value.In;
        var outDelta = counterReset ? 0UL : after.Value.Out - before.Value.Out;
        var elapsedSeconds = sw.Elapsed.TotalSeconds;
        var averageBps = elapsedSeconds > 0 ? total / elapsedSeconds : 0;

        Console.WriteLine();
        Console.WriteLine("== 实测结果 ==");
        Console.WriteLine($"实际接收字节        : {total} ({Format.Bytes((ulong)total)})");
        Console.WriteLine($"耗时 / 平均速率     : {elapsedSeconds:0.000} s  /  {Format.Rate(averageBps)}");
        Console.WriteLine($"InOctets  增量      : {inDelta} ({Format.Bytes(inDelta)})");
        Console.WriteLine($"OutOctets 增量      : {outDelta} ({Format.Bytes(outDelta)})");

        if (counterReset)
        {
            Console.WriteLine("警告: 计数器在测量期间回退（网卡重置/计数清零），本次测量无效。");
        }
        else if (inDelta == 0)
        {
            Console.WriteLine("警告: InOctets 没有变化，测量无效（可能目标接口不是实际承载流量的网卡）。");
        }
        else
        {
            var ratio = (double)inDelta / total;
            var overhead = (long)inDelta - total;
            Console.WriteLine($"增量 / 文件字节     : {ratio:0.0000} 倍");
            Console.WriteLine($"额外字节（协议+后台）: {overhead:+#;-#;0} 字节");
            Console.WriteLine($"说明: 比值 >1 属正常（以太网/802.11 帧头、TCP/IP、TLS 记录层开销，以及其他后台流量）；"
                              + "比值 <1 则说明计数器漏计或读取时机不当，需要记录为异常。");
        }

        if (bclBefore is { } bb && bclAfter is { } ba)
        {
            var bclIn = ba.Received - bb.Received;
            var bclOut = ba.Sent - bb.Sent;
            Console.WriteLine($"BCL 统计增量        : in={bclIn} out={bclOut}（\"{bb.Name}\" 的 NetworkInterface.GetIPStatistics，另一条读取路径，口径可能含 IPv4/IPv6 差异）");
            if (!counterReset && inDelta > 0 && bclIn > 0)
            {
                var diff = Math.Abs(bclIn - (long)inDelta);
                var diffRatio = (double)diff / inDelta;
                Console.WriteLine($"MIB 与 BCL 的差异   : {diff} 字节（{diffRatio:P2}）");
            }
        }
        else
        {
            Console.WriteLine("BCL 统计增量        : <无法读取该接口的 .NET 统计>");
        }

        if (args.Get("--json") is { } jsonPath)
        {
            JsonOutput.Write(jsonPath, new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                targetGuid = guid,
                ssid = target.Ssid,
                url,
                requestedBytes,
                downloadedBytes = total,
                elapsedSeconds,
                averageBytesPerSecond = averageBps,
                counterReset,
                inOctetsBefore = before.Value.In,
                inOctetsAfter = after.Value.In,
                inOctetsDelta = inDelta,
                outOctetsBefore = before.Value.Out,
                outOctetsAfter = after.Value.Out,
                outOctetsDelta = outDelta,
                ratioInDeltaToFile = total > 0 && !counterReset ? (double)inDelta / total : (double?)null,
                bcl = bclBefore is { } b && bclAfter is { } a
                    ? new { receivedDelta = a.Received - b.Received, sentDelta = a.Sent - b.Sent }
                    : null,
            });
        }

        return counterReset ? 4 : 0;
    }

    private static (ulong In, ulong Out, string Alias)? ReadCounters(Guid guid)
    {
        var rows = IpHelper.ReadRows();
        var row = rows.FirstOrDefault(r => r.InterfaceGuid == guid);
        return row is null ? null : (row.InOctets, row.OutOctets, row.Alias);
    }

    private static (long Received, long Sent, string Name)? ReadBcl(Guid guid)
    {
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                // NetworkInterface.Id 在 Windows 上是网卡 GUID 的字符串形式，可能带花括号，
                // 因此先去掉花括号再解析，不能直接做字符串比较。
                var rawId = networkInterface.Id.Trim('{', '}');
                if (!Guid.TryParse(rawId, out var parsed) || parsed != guid)
                {
                    continue;
                }

                var stats = networkInterface.GetIPStatistics();
                return (stats.BytesReceived, stats.BytesSent, networkInterface.Name);
            }

            return null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}