using System.Globalization;
using System.Text;

namespace HotspotMeter.ApiProbe.Support;

/// <summary>
/// 逐秒采样 CSV。列固定，方便用 Excel 或脚本复核；
/// 时间戳一律 UTC ISO-8601，本地时间只在控制台显示。
/// </summary>
internal sealed class CsvSampleWriter : IDisposable
{
    // gapMs   = 本帧与上一帧的单调时钟差，睡眠唤醒后会远大于 interval；
    // sleepGap= 1 表示这一帧属于"睡眠/长挂起后的首帧"，增量按 0 处理并重建基线。
    private const string Header =
        "timestampUtc,elapsedMs,gapMs,guid,alias,ifIndex,present,inOctets,outOctets,inDelta,outDelta,inBps,outBps,reset,sleepGap,otherRows";

    private readonly StreamWriter _writer;

    public CsvSampleWriter(string path)
    {
        // 注意：本类有一个 Path 属性，直接用 Path.GetFullPath 会被解析成该属性，必须写全 System.IO.Path。
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(fullPath, append: false, new UTF8Encoding(false));
        _writer.WriteLine(Header);
        Path = fullPath;
    }

    public string Path { get; }

    public void Write(
        DateTimeOffset timestampUtc,
        long elapsedMs,
        long gapMs,
        Guid guid,
        string alias,
        uint ifIndex,
        bool present,
        ulong inOctets,
        ulong outOctets,
        ulong inDelta,
        ulong outDelta,
        double inBps,
        double outBps,
        bool reset,
        bool sleepGap,
        string otherRows)
    {
        var line = string.Join(
            ',',
            timestampUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            elapsedMs.ToString(CultureInfo.InvariantCulture),
            gapMs.ToString(CultureInfo.InvariantCulture),
            guid.ToString("D"),
            Escape(alias),
            ifIndex.ToString(CultureInfo.InvariantCulture),
            present ? "1" : "0",
            inOctets.ToString(CultureInfo.InvariantCulture),
            outOctets.ToString(CultureInfo.InvariantCulture),
            inDelta.ToString(CultureInfo.InvariantCulture),
            outDelta.ToString(CultureInfo.InvariantCulture),
            inBps.ToString("0.##", CultureInfo.InvariantCulture),
            outBps.ToString("0.##", CultureInfo.InvariantCulture),
            reset ? "1" : "0",
            sleepGap ? "1" : "0",
            Escape(otherRows));

        _writer.WriteLine(line);
    }

    public void Flush() => _writer.Flush();

    public void Dispose() => _writer.Dispose();

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
}