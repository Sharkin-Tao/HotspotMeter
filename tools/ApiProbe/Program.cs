using System.Text;
using HotspotMeter.ApiProbe.Commands;
using HotspotMeter.ApiProbe.Native;
using HotspotMeter.ApiProbe.Support;

namespace HotspotMeter.ApiProbe;

/// <summary>
/// 阶段 1 的一次性验证工具入口。它不属于产品，只负责把 Windows API 的真实行为测出来。
/// 用法见 <see cref="PrintUsage"/>；所有证据写入 out/ 目录（该目录不进仓库）。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 输出被重定向到文件时可能失败，忽略即可。
        }

        var reader = new ArgReader(args);

        try
        {
            switch (reader.Command)
            {
                case "list":
                    return ListCommand.Run(reader);
                case "watch":
                    return WatchCommand.Run(reader);
                case "download":
                    return await DownloadCommand.RunAsync(reader);
                case "continuity":
                    return ContinuityCommand.Run(reader);
                case "selftest":
                    return SelfTest();
                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (ProbeException ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"未处理异常: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>不产生副作用的最小自检：结构体布局 + WLAN 句柄 + 接口表可读。</summary>
    private static int SelfTest()
    {
        var ok = LayoutCheck.Verify(out var report);
        Console.WriteLine(report);

        using var client = WlanClient.Open();
        var interfaces = client.Enumerate();
        var rows = IpHelper.ReadRows();

        Console.WriteLine($"WLAN 接口数: {interfaces.Count}，已连接: {interfaces.Count(i => i.IsConnected)}");
        Console.WriteLine($"IP Helper 行数: {rows.Count}");

        var allMapped = interfaces.All(i => rows.Count(r => r.InterfaceGuid == i.InterfaceGuid) == 1);
        Console.WriteLine($"GUID 一一对应: {(allMapped ? "是" : "否")}");
        Console.WriteLine($"结论: {(ok && allMapped ? "OK" : "存在问题，请运行 list 查看明细")}");

        return ok && allMapped ? 0 : 3;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            HotspotMeter 阶段 1 验证工具（ApiProbe）

              selftest                         只做结构体与接口映射的快速自检
              list [--json tools/ApiProbe/out/list.json]
                                               枚举 WLAN 与 IP Helper，验证 GUID 映射（P1-1、P1-2）

              watch [--seconds 60] [--interval 1000] [--csv tools/ApiProbe/out/watch.csv]
                    [--guid <GUID>] [--ssid <SSID>] [--no-others] [--json tools/ApiProbe/out/watch.json]
                                               逐秒采样，验证空闲稳定/睡眠唤醒/网卡隔离（P1-4、P1-6、P1-8）

              download [--bytes 157286400] [--url <URL>] [--json tools/ApiProbe/out/download.json]
                                               下载已知体积文件并对比计数器增量（P1-3）

              continuity --save tools/ApiProbe/out/state.json   保存基线（P1-9）
              continuity --verify tools/ApiProbe/out/state.json
                                               验证跨进程/跨重启的计数器连续性（P1-10、P1-11）

            默认输出目录为 tools/ApiProbe/out/（已加入 .gitignore，证据文件不会误入库）。

            公共参数：--guid <GUID>  --ssid <SSID>（用于多无线接口时指定目标）
            """);
    }
}