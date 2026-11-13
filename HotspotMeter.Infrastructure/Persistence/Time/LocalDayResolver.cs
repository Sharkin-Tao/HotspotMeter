using System.Globalization;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 本地自然日边界解析器，是“某个 UTC 瞬间属于哪一天”的唯一权威。
///
/// 为什么不用“本地时间逐日加一天”：
/// 1. 夏令时（DST）会让某些本地日期只有 23 或 25 小时，本地加法与真实经过时间不一致；
/// 2. 某些时区会在午夜发生跳变，导致 00:00 不存在（前跳）或出现两次（回拨）；
/// 3. 极少数时区变更会整日跳过。
///
/// 算法（先 UTC 分段，再段内解析求解）：
/// 1. 在目标日期前后各 2 天的 UTC 窗口内，每 15 分钟采样一次时区偏移；
/// 2. 相邻采样偏移不同时，在该区间内按 Ticks 二分定位第一个偏移不同的瞬间（精确到 1 tick）；
/// 3. 段内偏移恒定，本地时间 = UTC + 偏移，因此“本地午夜”可在段内直接求解；
/// 4. 多个段都能映射到午夜时取最小 UTC（重复午夜取第一次）；
/// 5. 午夜落在前跳缺口时，取跨过该缺口的跳变瞬间（即当天第一个合法本地时刻）。
///
/// 已知前提：同一 15 分钟窗口内发生两次以上真实偏移跳变无法表达。现实 tzdata
/// 不存在这种规则；测试用自定义时区显式覆盖单次跳变场景。
/// </summary>
public static class LocalDayResolver
{
    private const long SampleStepTicks = TimeSpan.TicksPerMinute * 15;
    private const int WindowDays = 2;

    /// <summary>返回 UTC 瞬间对应的本地日期（"yyyy-MM-dd"）。</summary>
    public static string GetLocalDate(DateTimeOffset utc, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = TimeZoneInfo.ConvertTime(utc.ToUniversalTime(), timeZone);
        return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 返回指定本地日期中最早的那个 UTC 瞬间。
    /// 若该日被时区变更整日跳过，返回跳过它的跳变瞬间（与次日边界相同）。
    /// </summary>
    public static DateTimeOffset GetDayStartUtc(DateOnly localDate, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var midnightTicks = localDate.ToDateTime(TimeOnly.MinValue).Ticks;
        var windowStart = Math.Max(0, midnightTicks - (TimeSpan.TicksPerDay * WindowDays));
        var windowEnd = Math.Min(
            DateTimeOffset.MaxValue.UtcTicks,
            midnightTicks + (TimeSpan.TicksPerDay * WindowDays));

        var segments = BuildOffsetSegments(windowStart, windowEnd, timeZone);

        long? earliest = null;
        foreach (var segment in segments)
        {
            var localStart = checked(segment.StartTicks + segment.OffsetTicks);
            var localEnd = checked(segment.EndTicks + segment.OffsetTicks);
            if (midnightTicks >= localStart && midnightTicks < localEnd)
            {
                var candidate = checked(midnightTicks - segment.OffsetTicks);
                if (earliest is null || candidate < earliest.Value)
                {
                    earliest = candidate;
                }
            }
        }

        if (earliest is null)
        {
            // 本地午夜落在前跳缺口（或整日被跳过）：当天第一个合法瞬间就是跨过缺口的跳变点。
            foreach (var segment in segments)
            {
                var localEnd = checked(segment.EndTicks + segment.OffsetTicks);
                if (localEnd > midnightTicks)
                {
                    earliest = segment.StartTicks;
                    break;
                }
            }
        }

        if (earliest is null)
        {
            throw new InvalidOperationException(
                $"无法解析本地日期 {localDate:yyyy-MM-dd} 的首个 UTC 瞬间（时区 {timeZone.Id}）。");
        }

        return new DateTimeOffset(earliest.Value, TimeSpan.Zero);
    }

    /// <summary>
    /// 枚举 (fromUtc, toUtc) 之间严格位于内部的日边界，按 UTC 升序。
    /// 半开区间 [from, to)：from 当天与 to 当天自身的边界不会被包含。
    /// 整日被跳过的本地日期不会产生重复边界，因此调用方不会得到零长度片段。
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> EnumerateDayStartsUtc(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        if (from >= to)
        {
            return [];
        }

        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, timeZone).DateTime);
        var lastDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, timeZone).DateTime);
        var guardLimit = (to - from).TotalDays + 4;

        var boundaries = new List<DateTimeOffset>();
        var previousBoundaryTicks = long.MinValue;
        var date = firstDate;

        while (date < lastDate.AddDays(1))
        {
            date = date.AddDays(1);

            var boundary = GetDayStartUtc(date, timeZone);
            if (boundary >= to)
            {
                break;
            }

            if (boundary > from && boundary.UtcTicks != previousBoundaryTicks)
            {
                boundaries.Add(boundary);
            }

            previousBoundaryTicks = boundary.UtcTicks;

            if (--guardLimit <= 0)
            {
                throw new InvalidOperationException(
                    $"日边界枚举超出合理范围（{firstDate:yyyy-MM-dd} → {lastDate:yyyy-MM-dd}）。");
            }
        }

        return boundaries;
    }

    private static List<OffsetSegment> BuildOffsetSegments(
        long startTicks,
        long endTicks,
        TimeZoneInfo timeZone)
    {
        var segments = new List<OffsetSegment>();
        if (startTicks >= endTicks)
        {
            return segments;
        }

        var segmentStart = startTicks;
        var cursor = startTicks;
        var currentOffset = OffsetAt(cursor, timeZone);

        while (cursor < endTicks)
        {
            var next = Math.Min(endTicks, checked(cursor + SampleStepTicks));
            var nextOffset = OffsetAt(next, timeZone);

            if (nextOffset != currentOffset)
            {
                var boundary = FindFirstDifferentOffset(cursor, next, currentOffset, timeZone);
                segments.Add(new OffsetSegment(segmentStart, boundary, currentOffset));
                segmentStart = boundary;
                currentOffset = OffsetAt(boundary, timeZone);

                // 从跳变点继续扫描，避免漏掉同一段内紧随其后的变化。
                cursor = boundary;
                continue;
            }

            cursor = next;
        }

        segments.Add(new OffsetSegment(segmentStart, endTicks, currentOffset));
        return segments;
    }

    /// <summary>定位 (low, high] 中第一个偏移不等于 offsetAtLow 的瞬间，精确到 1 tick。</summary>
    private static long FindFirstDifferentOffset(
        long low,
        long high,
        long offsetAtLow,
        TimeZoneInfo timeZone)
    {
        while (low + 1 < high)
        {
            var middle = low + ((high - low) / 2);
            if (OffsetAt(middle, timeZone) == offsetAtLow)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return high;
    }

    private static long OffsetAt(long utcTicks, TimeZoneInfo timeZone)
        => timeZone.GetUtcOffset(new DateTimeOffset(utcTicks, TimeSpan.Zero)).Ticks;

    private readonly record struct OffsetSegment(long StartTicks, long EndTicks, long OffsetTicks);
}