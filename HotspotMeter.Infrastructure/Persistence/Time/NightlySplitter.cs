using System.Numerics;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 按本地自然日午夜切分一个 UTC 区间，并把字节数与连接时长按时间比例分到各日。
///
/// 为什么用 BigInteger 累计取整而不是按段独立四舍五入：
/// 独立取整会让总和偏离输入值（10 段每段误差 1 字节，总量就能差 10 字节）；
/// 累计取整 <c>floor(T·cum_i / cum_n) - floor(T·cum_{i-1} / cum_n)</c> 天然满足
/// 逐段非负且总和严格等于 T，同时避免 <c>T·cum_i</c> 在 long 上溢出。
///
/// 本类型刻意不识别会话，也不产生 SessionCount / FirstSessionAtUtc / LastSessionAtUtc。
/// 会话计数属于“会话开始事件”的语义，由未来的持久化服务唯一生成，避免两处重复计数。
/// </summary>
public static class NightlySplitter
{
    public const int DefaultMaxSegments = 400;

    /// <summary>
    /// 切分 <paramref name="fromUtc"/>（含）到 <paramref name="toUtc"/>（不含）的区间。
    /// 零长度区间返回空列表；超过 <paramref name="maxSegments"/> 抛异常而不是静默截断。
    /// </summary>
    public static IReadOnlyList<TrafficSegment> Split(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeZoneInfo timeZone,
        long downloadBytes,
        long uploadBytes,
        long connectedMilliseconds,
        int maxSegments = DefaultMaxSegments)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        if (maxSegments < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSegments), maxSegments, "maxSegments 必须大于等于 1。");
        }

        if (downloadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(downloadBytes), downloadBytes, "下载字节数不能为负。");
        }

        if (uploadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(uploadBytes), uploadBytes, "上传字节数不能为负。");
        }

        if (connectedMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectedMilliseconds), connectedMilliseconds, "连接毫秒数不能为负。");
        }

        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        if (from > to)
        {
            throw new ArgumentException("区间起点不能晚于终点。", nameof(fromUtc));
        }

        if (from == to)
        {
            return [];
        }

        var boundaries = new List<long> { from.UtcTicks };
        foreach (var dayStart in LocalDayResolver.EnumerateDayStartsUtc(from, to, timeZone))
        {
            var ticks = dayStart.UtcTicks;
            if (ticks > boundaries[^1] && ticks < to.UtcTicks)
            {
                boundaries.Add(ticks);
            }
        }

        boundaries.Add(to.UtcTicks);

        var segmentCount = boundaries.Count - 1;
        if (segmentCount > maxSegments)
        {
            throw new InvalidOperationException(
                $"区间需要 {segmentCount} 个片段，超过上限 {maxSegments}；请缩短区间或确认是否需要跨多年补记。");
        }

        var weights = new long[segmentCount];
        for (var index = 0; index < segmentCount; index++)
        {
            weights[index] = boundaries[index + 1] - boundaries[index];
        }

        var downloads = Allocate(downloadBytes, weights);
        var uploads = Allocate(uploadBytes, weights);
        var milliseconds = Allocate(connectedMilliseconds, weights);

        var segments = new TrafficSegment[segmentCount];
        for (var index = 0; index < segmentCount; index++)
        {
            var segmentFrom = new DateTimeOffset(boundaries[index], TimeSpan.Zero);
            var segmentTo = new DateTimeOffset(boundaries[index + 1], TimeSpan.Zero);
            segments[index] = new TrafficSegment(
                LocalDayResolver.GetLocalDate(segmentFrom, timeZone),
                segmentFrom,
                segmentTo,
                downloads[index],
                uploads[index],
                milliseconds[index]);
        }

        return segments;
    }

    private static long[] Allocate(long total, IReadOnlyList<long> weights)
    {
        var result = new long[weights.Count];

        BigInteger denominator = BigInteger.Zero;
        foreach (var weight in weights)
        {
            denominator += weight;
        }

        BigInteger totalValue = total;
        BigInteger cumulative = BigInteger.Zero;
        BigInteger previous = BigInteger.Zero;

        for (var index = 0; index < weights.Count; index++)
        {
            cumulative += weights[index];
            var current = BigInteger.Divide(BigInteger.Multiply(totalValue, cumulative), denominator);
            result[index] = (long)(current - previous);
            previous = current;
        }

        return result;
    }
}