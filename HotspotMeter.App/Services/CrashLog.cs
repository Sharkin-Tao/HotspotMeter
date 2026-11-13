using System.IO;
using System.Text;

namespace HotspotMeter.App.Services;

/// <summary>
/// 崩溃诊断记录。这里**不吞异常**：只把完整堆栈落到文件，随后仍按原路径处理，
/// 这样既保留真实崩溃行为，也能在用户机器上拿到根因。
/// </summary>
internal static class CrashLog
{
    private static readonly object Gate = new();

    public static void Write(Exception? exception, string source)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HotspotMeter",
                "logs");
            Directory.CreateDirectory(dir);

            var file = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            var text = new StringBuilder()
                .AppendLine($"time={DateTimeOffset.Now:O}")
                .AppendLine($"source={source}")
                .AppendLine($"process={Environment.ProcessId}")
                .AppendLine()
                .AppendLine(exception.ToString())
                .ToString();

            lock (Gate)
            {
                File.WriteAllText(file, text, Encoding.UTF8);
            }
        }
        catch
        {
            // 记录失败不能改变崩溃本身的行为。
        }
    }
}