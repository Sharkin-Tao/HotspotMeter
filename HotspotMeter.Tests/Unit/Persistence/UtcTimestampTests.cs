using HotspotMeter.Infrastructure.Persistence;

namespace HotspotMeter.Tests.Unit.Persistence;

/// <summary>
/// 验证 UTC 持久化时间的固定格式、严格解析与往返一致性。
///
/// 这些断言不是形式化的"能格式化就行"：SQLite 的 CHECK 只认 33 字符、7 位小数、+00:00 字面量，
/// 任何宽松解析都会在写入时才失败，因此必须在类型层就把格式钉死。
/// </summary>
public sealed class UtcTimestampTests
{
    [Fact]
    public void FromUtc_NormalizesNonZeroOffset()
    {
        var value = new DateTimeOffset(2026, 11, 13, 18, 43, 53, TimeSpan.FromHours(8)).AddTicks(1_234_567);

        var text = UtcTimestamp.FromUtc(value);

        Assert.Equal("2026-11-13T10:43:53.1234567+00:00", text);
        Assert.Equal(UtcTimestamp.Length, text.Length);
    }

    [Fact]
    public void FromUtc_AlwaysUsesSevenFractionDigits()
    {
        var text = UtcTimestamp.FromUtc(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        Assert.Equal("2026-01-02T03:04:05.0000000+00:00", text);
    }

    [Theory]
    [InlineData("2026-11-13T18:43:53.1234567+00:00")]
    [InlineData("2026-11-13T23:59:59.9999999+00:00")]
    [InlineData("2024-02-29T00:00:00.0000000+00:00")]
    [InlineData("0001-01-01T00:00:00.0000000+00:00")]
    [InlineData("9999-12-31T23:59:59.9999999+00:00")]
    public void TryParse_AcceptsWellFormedValues(string text)
    {
        Assert.True(UtcTimestamp.TryParse(text, out var parsed));
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal(text, UtcTimestamp.FromUtc(parsed));
    }

    [Theory]
    [InlineData("2026-11-13T24:00:00.0000000+00:00")] // SQLite 的 strftime 认为合法，但小时位越界
    [InlineData("2026-11-13T29:00:00.0000000+00:00")] // strftime 返回 NULL
    [InlineData("2026-11-13T18:60:00.0000000+00:00")] // 分钟 60
    [InlineData("2026-02-30T00:00:00.0000000+00:00")] // 不存在日期
    [InlineData("2025-02-29T00:00:00.0000000+00:00")] // 非闰年
    [InlineData("2026-11-13T18:43:53.123456+00:00")]  // 6 位小数
    [InlineData("2026-11-13T18:43:53.12345678+00:00")] // 8 位小数
    [InlineData("2026-11-13 18:43:53.1234567+00:00")] // 空格分隔
    [InlineData("2026-11-13T18:43:53.1234567+08:00")] // 非零偏移
    [InlineData("2026-11-13T18:43:53.1234567+00:00 ")] // 尾部空格
    [InlineData("0000-01-01T00:00:00.0000000+00:00")] // 年份 0000
    [InlineData("2026-13-01T00:00:00.0000000+00:00")] // 月份 13
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsNonCanonicalValues(string? text)
    {
        Assert.False(UtcTimestamp.TryParse(text, out _));
        Assert.False(UtcTimestamp.IsWellFormed(text));
    }

    [Fact]
    public void TryParse_RejectsOffsetThatCannotRoundTrip()
    {
        // +00:30 可被 DateTimeOffset 解析，但规范化后会变成 +00:00，与原文不一致。
        Assert.False(UtcTimestamp.TryParse("2026-11-13T18:43:53.1234567+00:30", out _));
    }

    [Fact]
    public void Parse_InvalidText_ThrowsFormatExceptionWithText()
    {
        var exception = Assert.Throws<FormatException>(() => UtcTimestamp.Parse("not-a-time"));

        Assert.Contains("not-a-time", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromUtc_IsStableAcrossCultures()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            var text = UtcTimestamp.FromUtc(new DateTimeOffset(2026, 11, 13, 1, 2, 3, TimeSpan.Zero));
            Assert.Equal("2026-11-13T01:02:03.0000000+00:00", text);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}