using System.Globalization;

namespace HotspotMeter.Core.Formatting;

/// <summary>
/// 把字节数转换成界面上的可读文本，例如 <c>2.36 GB</c>、<c>128 KB</c>。
/// <para>
/// 约定：按 1024 进制换算，但单位标签仍写 KB/MB/GB（Windows 的常见习惯）。
/// 小数最多两位，末尾的 0 不显示（<c>1 KB</c> 而不是 <c>1.00 KB</c>）。
/// 该格式属于暂定约定，最终以 docs/02_UI_Specification.md 的确认结果为准。
/// </para>
/// </summary>
public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// 格式化字节数。传入负数会抛出 <see cref="ArgumentOutOfRangeException"/>，
    /// 因为流量计数器出现负数通常意味着数据有问题，应当尽早暴露。
    /// </summary>
    /// <param name="bytes">字节数，必须大于或等于 0。</param>
    /// <param name="formatProvider">数字格式（小数点符号）。默认使用当前区域设置。</param>
    public static string Format(long bytes, IFormatProvider? formatProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        if (bytes < 1024)
        {
            return string.Concat(bytes.ToString(formatProvider), " B");
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Concat(
            value.ToString("0.##", formatProvider ?? CultureInfo.CurrentCulture),
            " ",
            Units[unit]);
    }
}