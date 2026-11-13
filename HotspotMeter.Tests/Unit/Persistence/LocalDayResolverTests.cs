using HotspotMeter.Infrastructure.Persistence;

namespace HotspotMeter.Tests.Unit.Persistence;

/// <summary>
/// LocalDayResolver 的测试。
///
/// 时区规则一律用 TimeZoneInfo.CreateCustomTimeZone 构造，避免依赖机器上的 tzdata 版本；
/// 唯一例外是“整日跳过”的 Samoa 2011-12-30，因为该现象无法用单一 AdjustmentRule 表达。
/// </summary>
public class LocalDayResolverTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

    private static TimeZoneInfo CustomZone(
        TimeSpan baseOffset,
        TimeSpan daylightDelta,
        DateTime standardTransitionStart,
        DateTime standardTransitionEnd,
        int year = 2025)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(year, 1, 1),
            new DateTime(year + 2, 12, 31),
            daylightDelta,
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(
                new DateTime(1, 1, 1, standardTransitionStart.Hour, standardTransitionStart.Minute, standardTransitionStart.Second),
                standardTransitionStart.Month,
                standardTransitionStart.Day),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(
                new DateTime(1, 1, 1, standardTransitionEnd.Hour, standardTransitionEnd.Minute, standardTransitionEnd.Second),
                standardTransitionEnd.Month,
                standardTransitionEnd.Day));

        return TimeZoneInfo.CreateCustomTimeZone(
            $"HSM-Test-{Guid.NewGuid():N}",
            baseOffset,
            "HotspotMeter test zone",
            "Standard",
            "Daylight",
            new[] { rule });
    }

    [Fact]
    public void GetLocalDate_UsesZoneOffset()
    {
        var plus8 = TimeZoneInfo.CreateCustomTimeZone("HSM-Plus8", TimeSpan.FromHours(8), "Plus8", "Plus8");
        var instant = new DateTimeOffset(2026, 1, 1, 17, 30, 0, TimeSpan.Zero);

        Assert.Equal("2026-01-02", LocalDayResolver.GetLocalDate(instant, plus8));
        Assert.Equal("2026-01-01", LocalDayResolver.GetLocalDate(instant, TimeZoneInfo.Utc));
        Assert.Equal("2026-01-01", LocalDayResolver.GetLocalDate(new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.FromHours(8)), plus8));
    }

    [Fact]
    public void GetDayStartUtc_NormalDay_IsMidnightLocal()
    {
        var plus8 = TimeZoneInfo.CreateCustomTimeZone("HSM-Plus8", TimeSpan.FromHours(8), "Plus8", "Plus8");

        var start = LocalDayResolver.GetDayStartUtc(new DateOnly(2026, 5, 1), plus8);

        Assert.Equal(new DateTimeOffset(2026, 4, 30, 16, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(TimeSpan.FromHours(8), plus8.GetUtcOffset(start));
        Assert.Equal("2026-05-01", LocalDayResolver.GetLocalDate(start, plus8));
    }

    [Fact]
    public void GetDayStartUtc_BerlinDstSpringDay_StartsAt22UtcPreviousDay()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

        var start = LocalDayResolver.GetDayStartUtc(new DateOnly(2026, 3, 29), berlin);
        var next = LocalDayResolver.GetDayStartUtc(new DateOnly(2026, 3, 30), berlin);

        Assert.Equal(new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero), next);
        Assert.Equal(TimeSpan.FromHours(23), next - start);
    }

    [Fact]
    public void GetDayStartUtc_BerlinDstFallDay_Lasts25Hours()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

        var start = LocalDayResolver.GetDayStartUtc(new DateOnly(2026, 10, 25), berlin);
        var next = LocalDayResolver.GetDayStartUtc(new DateOnly(2026, 10, 26), berlin);

        Assert.Equal(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero), next);
        Assert.Equal(TimeSpan.FromHours(25), next - start);
    }

    [Fact]
    public void GetDayStartUtc_MidnightInsideForwardGap_ReturnsTransitionInstant()
    {
        // 2026-03-15 00:00 标准时间拨快 7 分钟：本地 23:59:59 → 00:07。
        // 本地午夜 00:00 不存在；当天第一个合法瞬间是跳变瞬间（UTC 00:00，本地 00:07）。
        var zone = CustomZone(
            TimeSpan.Zero,
            TimeSpan.FromMinutes(7),
            new DateTime(2026, 3, 15, 0, 0, 0),
            new DateTime(2026, 10, 15, 3, 0, 0));

        var start = LocalDayResolver.GetDayStartUtc(new DateOnly(2026, 3, 15), zone);

        Assert.Equal(new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new TimeSpan(0, 7, 0), zone.GetUtcOffset(start));
        Assert.Equal("2026-03-15", LocalDayResolver.GetLocalDate(start, zone));
    }

    [Fact]
    public void GetDayStartUtc_RepeatedMidnight_ReturnsEarliestUtc()
    {
        // 2026-10-20 01:00 夏令时回拨到 00:00：本地 00:00 出现两次。
        // 第一次是夏令时偏移（UTC 前一日 23:00），第二次是标准偏移（UTC 00:00）。
        var zone = CustomZone(
            TimeSpan.Zero,
            TimeSpan.FromHours(1),
            new DateTime(2026, 3, 15, 0, 0, 0),
            new DateTime(2026, 10, 20, 1, 0, 0));

        var start = LocalDayResolver.GetDayStartUtc(new DateOnly(2026, 10, 20), zone);

        Assert.Equal(new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new TimeSpan(1, 0, 0), zone.GetUtcOffset(start));
    }

    [Fact]
    public void GetDayStartUtc_ApiaSkippedDay_ProducesMonotonicBoundaries()
    {
        // 萨摩亚 2011-12-30 前后发生跨日期的偏移跳变。
        // Windows 与 IANA 对该跳变的建模并不完全一致，因此这里只断言
        // “边界单调、不重复、不倒退”这些与时区数据无关的硬性性质；
        // 整日跳过时的去重由 EnumerateDayStartsUtc 的唯一性断言覆盖。
        TimeZoneInfo? apia = null;
        foreach (var id in new[] { "Pacific/Apia", "Samoa Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found))
            {
                apia = found;
                break;
            }
        }

        if (apia is null)
        {
            return; // 本机 tzdata 缺该时区；不伪造结论。
        }

        var skipped = LocalDayResolver.GetDayStartUtc(new DateOnly(2011, 12, 30), apia);
        var next = LocalDayResolver.GetDayStartUtc(new DateOnly(2011, 12, 31), apia);

        Assert.True(skipped <= next);
        Assert.Equal("2011-12-31", LocalDayResolver.GetLocalDate(next, apia));
    }

    [Fact]
    public void EnumerateDayStartsUtc_ReturnsStrictlyInteriorBoundaries()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);

        var starts = LocalDayResolver.EnumerateDayStartsUtc(from, to, TimeZoneInfo.Utc);

        Assert.Equal(
            new[]
            {
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero),
            },
            starts);
    }

    [Fact]
    public void EnumerateDayStartsUtc_BerlinDstWindow_MatchesDayStarts()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var from = new DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);

        var starts = LocalDayResolver.EnumerateDayStartsUtc(from, to, berlin);

        Assert.Equal(
            new[]
            {
                new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 30, 22, 0, 0, TimeSpan.Zero),
            },
            starts);
    }

    [Fact]
    public void EnumerateDayStartsUtc_SkippedDay_YieldsNoZeroLengthBoundary()
    {
        TimeZoneInfo? apia = null;
        foreach (var id in new[] { "Pacific/Apia", "Samoa Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found))
            {
                apia = found;
                break;
            }
        }

        if (apia is null)
        {
            return;
        }

        var from = new DateTimeOffset(2011, 12, 29, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2012, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var starts = LocalDayResolver.EnumerateDayStartsUtc(from, to, apia);

        Assert.All(starts, s => Assert.True(s > from && s < to));
        Assert.Equal(starts.Count, starts.Distinct().Count());
    }

    [Fact]
    public void EnumerateDayStartsUtc_EmptyOrInvertedWindow_ReturnsEmpty()
    {
        var instant = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Empty(LocalDayResolver.EnumerateDayStartsUtc(instant, instant, TimeZoneInfo.Utc));
        Assert.Empty(LocalDayResolver.EnumerateDayStartsUtc(instant.AddHours(1), instant, TimeZoneInfo.Utc));
    }

    [Fact]
    public void GetDayStartUtc_TickPrecision_NotRoundedToMinutes()
    {
        // 本地 23:53:17 拨快 14 分钟 → 本地 00:07:17，午夜落在缺口内。
        // 边界必须是精确的跳变瞬间 23:53:17Z，而不是整分钟或 15 分钟刻度。
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2025, 1, 1),
            new DateTime(2027, 12, 31),
            TimeSpan.FromMinutes(14),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 23, 53, 17), 3, 14),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 15));
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "HSM-Precise", TimeSpan.Zero, "HSM-Precise", "Std", "Dst", new[] { rule });

        var start = LocalDayResolver.GetDayStartUtc(new DateOnly(2026, 3, 15), zone);

        Assert.Equal(new DateTimeOffset(2026, 3, 14, 23, 53, 17, TimeSpan.Zero), start);
        Assert.Equal(TimeSpan.FromMinutes(14), zone.GetUtcOffset(start));
    }
}