using System.Globalization;

namespace HotspotMeter.ApiProbe.Support;

/// <summary>
/// 原型自用的显示格式化。刻意不复用 HotspotMeter.Core 的 ByteSizeFormatter：
/// 原型必须能在"不引用产品代码"的前提下独立运行，才能证明 API 结论与产品实现无关。
/// </summary>
internal static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(ulong bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {Units[unit]}");
    }

    public static string Rate(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return "0 B/s";
        }

        return Bytes((ulong)bytesPerSecond) + "/s";
    }

    public static string LocalTime(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>把值截断到指定宽度，避免表格被长网卡名撑破。</summary>
    public static string Clamp(string value, int width) =>
        value.Length <= width ? value : value[..Math.Max(width - 1, 0)] + "…";
}