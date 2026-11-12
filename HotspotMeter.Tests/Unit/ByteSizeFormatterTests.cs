using System.Globalization;
using HotspotMeter.Core.Formatting;

namespace HotspotMeter.Tests.Unit;

public class ByteSizeFormatterTests
{
    private static readonly IFormatProvider Culture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(128 * 1024, "128 KB")]
    [InlineData(2 * 1024 * 1024, "2 MB")]
    [InlineData(2533593088, "2.36 GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1 TB")]
    public void Format_UsesExpectedUnitAndPrecision(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSizeFormatter.Format(bytes, Culture));
    }

    [Fact]
    public void Format_Uses1024Base_NotDecimal()
    {
        // 按十进制（1000）会得到 "1 MB"，按 1024 进制是 976.56 KB。
        Assert.Equal("976.56 KB", ByteSizeFormatter.Format(1_000_000, Culture));

        // 1000 字节按十进制是 "1 KB"，按 1024 进制仍是 "1000 B"。
        Assert.Equal("1000 B", ByteSizeFormatter.Format(1_000, Culture));
    }

    [Fact]
    public void Format_NegativeInput_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ByteSizeFormatter.Format(-1, Culture));
    }
}