using System.Numerics;
using HotspotMeter.Infrastructure.Persistence;

namespace HotspotMeter.Tests.Unit.Persistence;

/// <summary>NightlySplitter 的守恒、边界与异常测试。</summary>
public class NightlySplitterTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static IReadOnlyList<TrafficSegment> Split(
        DateTimeOffset from,
        DateTimeOffset to,
        long download = 1000,
        long upload = 500,
        long milliseconds = 7_200_000,
        int maxSegments = 400)
        => NightlySplitter.Split(from, to, Utc, download, upload, milliseconds, maxSegments);

    private static DateTimeOffset At(int month, int day, int hour, int minute = 0, int second = 0)
        => new(2026, month, day, hour, minute, second, TimeSpan.Zero);

    private static void AssertConserved(IReadOnlyList<TrafficSegment> segments, long download, long upload, long ms)
    {
        BigInteger downloadSum = 0;
        BigInteger uploadSum = 0;
        BigInteger millisecondSum = 0;
        foreach (var segment in segments)
        {
            downloadSum += segment.DownloadBytes;
            uploadSum += segment.UploadBytes;
            millisecondSum += segment.ConnectedMilliseconds;
        }

        Assert.Equal(new BigInteger(download), downloadSum);
        Assert.Equal(new BigInteger(upload), uploadSum);
        Assert.Equal(new BigInteger(ms), millisecondSum);
    }

    [Fact]
    public void SingleDay_ReturnsOneSegmentWithInputTotals()
    {
        var segments = Split(At(5, 1, 10), At(5, 1, 12), 1000, 500, 7_200_000);

        var segment = Assert.Single(segments);
        Assert.Equal("2026-05-01", segment.LocalDate);
        Assert.Equal(At(5, 1, 10), segment.FromUtc);
        Assert.Equal(At(5, 1, 12), segment.ToUtc);
        Assert.Equal(1000, segment.DownloadBytes);
        Assert.Equal(500, segment.UploadBytes);
        Assert.Equal(7_200_000, segment.ConnectedMilliseconds);
    }

    [Fact]
    public void EndingExactlyAtMidnight_StillBelongsToPreviousDay()
    {
        var segments = Split(At(5, 1, 23), At(5, 2, 0), 600, 300, 3_600_000);

        var segment = Assert.Single(segments);
        Assert.Equal("2026-05-01", segment.LocalDate);
        Assert.Equal(At(5, 2, 0), segment.ToUtc);
    }

    [Fact]
    public void CrossingOneMidnight_SplitsProportionally()
    {
        // 23:50 → 次日 00:20：1200 秒中前 600 秒属前一天。
        var segments = Split(At(5, 1, 23, 50), At(5, 2, 0, 20), 3000, 1500, 1_800_000);

        Assert.Equal(2, segments.Count);
        Assert.Equal("2026-05-01", segments[0].LocalDate);
        Assert.Equal("2026-05-02", segments[1].LocalDate);
        Assert.Equal(1000, segments[0].DownloadBytes);
        Assert.Equal(2000, segments[1].DownloadBytes);
        Assert.Equal(500, segments[0].UploadBytes);
        Assert.Equal(1000, segments[1].UploadBytes);
        Assert.Equal(600_000, segments[0].ConnectedMilliseconds);
        Assert.Equal(1_200_000, segments[1].ConnectedMilliseconds);
        AssertConserved(segments, 3000, 1500, 1_800_000);
    }

    [Fact]
    public void CrossingTwoMidnights_ReturnsThreeChronologicalSegments()
    {
        var from = At(5, 1, 22);
        var to = At(5, 3, 2);

        var segments = Split(from, to, 9000, 4500, 28 * 3_600_000L);

        Assert.Equal(3, segments.Count);
        Assert.Equal(new[] { "2026-05-01", "2026-05-02", "2026-05-03" }, segments.Select(s => s.LocalDate));
        Assert.Equal(from, segments[0].FromUtc);
        Assert.Equal(to, segments[^1].ToUtc);
        AssertConserved(segments, 9000, 4500, 28 * 3_600_000L);
    }

    [Fact]
    public void ZeroLength_ReturnsEmpty()
    {
        Assert.Empty(Split(At(5, 1, 10), At(5, 1, 10)));
    }

    [Fact]
    public void InvertedRange_Throws()
    {
        Assert.Throws<ArgumentException>(() => Split(At(5, 2, 0), At(5, 1, 0)));
    }

    [Theory]
    [InlineData(-1L, 0L, 0L)]
    [InlineData(0L, -1L, 0L)]
    [InlineData(0L, 0L, -1L)]
    public void NegativeInputs_Throw(long download, long upload, long milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Split(At(5, 1, 10), At(5, 1, 12), download, upload, milliseconds));
    }

    [Fact]
    public void NullTimeZone_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => NightlySplitter.Split(At(5, 1, 10), At(5, 1, 12), null!, 1, 1, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidMaxSegments_Throw(int maxSegments)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Split(At(5, 1, 10), At(5, 1, 12), maxSegments: maxSegments));
    }

    [Fact]
    public void ExceedingMaxSegments_ThrowsInsteadOfTruncating()
    {
        Assert.Throws<InvalidOperationException>(
            () => Split(At(5, 1, 22), At(5, 4, 2), maxSegments: 2));
    }

    [Fact]
    public void MaxSegmentsExactlyEnough_DoesNotThrow()
    {
        var segments = Split(At(5, 1, 22), At(5, 3, 2), maxSegments: 3);

        Assert.Equal(3, segments.Count);
    }

    [Fact]
    public void OneByteAcrossMidnight_IsConserved()
    {
        var segments = Split(At(5, 1, 23, 50), At(5, 2, 0, 20), 1, 1, 1);

        AssertConserved(segments, 1, 1, 1);
        Assert.All(segments, s => Assert.True(s.DownloadBytes >= 0 && s.UploadBytes >= 0));
    }

    [Fact]
    public void LongMaxValue_SingleDay_IsExact()
    {
        var segments = Split(At(5, 1, 10), At(5, 1, 12), long.MaxValue, long.MaxValue, long.MaxValue);

        var segment = Assert.Single(segments);
        Assert.Equal(long.MaxValue, segment.DownloadBytes);
        Assert.Equal(long.MaxValue, segment.UploadBytes);
        Assert.Equal(long.MaxValue, segment.ConnectedMilliseconds);
    }

    [Fact]
    public void LongMaxValue_AcrossTwoMidnights_SumsExactly()
    {
        var segments = Split(At(5, 1, 20), At(5, 4, 4), long.MaxValue, long.MaxValue, long.MaxValue);

        Assert.Equal(4, segments.Count);
        AssertConserved(segments, long.MaxValue, long.MaxValue, long.MaxValue);
        Assert.All(segments, s => Assert.True(s.DownloadBytes >= 0));
    }

    [Fact]
    public void LongMaxValueMinusOne_ThreeWaySplit_IsExact()
    {
        const long value = long.MaxValue - 1;
        var segments = Split(At(5, 1, 1), At(5, 3, 5), value, value, value);

        AssertConserved(segments, value, value, value);
    }

    [Fact]
    public void ZeroByteSession_StillSplitsTime()
    {
        var segments = Split(At(5, 1, 23), At(5, 2, 1), 0, 0, 2 * 3_600_000L);

        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.Equal(0, s.DownloadBytes + s.UploadBytes));
        Assert.Equal(3_600_000, segments[0].ConnectedMilliseconds);
    }

    [Fact]
    public void SegmentsAreContiguousAndCoverInput()
    {
        var from = At(5, 1, 21, 13, 7);
        var to = At(5, 4, 3, 42, 11);

        var segments = Split(from, to, 123_456, 7_890, 200 * 3_600_000L);

        Assert.Equal(from, segments[0].FromUtc);
        Assert.Equal(to, segments[^1].ToUtc);
        for (var i = 1; i < segments.Count; i++)
        {
            Assert.Equal(segments[i - 1].ToUtc, segments[i].FromUtc);
            Assert.True(segments[i].FromUtc < segments[i].ToUtc);
        }
    }

    [Fact]
    public void LocalDate_MatchesResolverForSegmentStart()
    {
        var segments = Split(At(5, 1, 22), At(5, 3, 2));

        foreach (var segment in segments)
        {
            Assert.Equal(LocalDayResolver.GetLocalDate(segment.FromUtc, Utc), segment.LocalDate);
        }
    }

    [Fact]
    public void RangeStartingAtMidnight_BelongsToThatDay()
    {
        var segments = Split(At(5, 2, 0), At(5, 2, 1));

        var segment = Assert.Single(segments);
        Assert.Equal("2026-05-02", segment.LocalDate);
    }

    [Fact]
    public void OffsetInput_IsNormalizedToUtc()
    {
        var plus8 = TimeSpan.FromHours(8);
        var from = new DateTimeOffset(2026, 5, 1, 23, 50, 0, plus8);
        var to = new DateTimeOffset(2026, 5, 2, 0, 20, 0, plus8);

        var segments = NightlySplitter.Split(from, to, Utc, 300, 150, 1_200_000);

        var segment = Assert.Single(segments);
        Assert.Equal(from.ToUniversalTime(), segment.FromUtc);
        Assert.Equal(to.ToUniversalTime(), segment.ToUtc);
    }

    [Fact]
    public void BerlinSpringForwardDay_Covers23Hours()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var start = LocalDayResolver.GetDayStartUtc(new DateOnly(2026, 3, 29), berlin);
        var end = LocalDayResolver.GetDayStartUtc(new DateOnly(2026, 3, 30), berlin);

        var segments = NightlySplitter.Split(start, end, berlin, 1000, 500, 23 * 3_600_000L);

        var segment = Assert.Single(segments);
        Assert.Equal("2026-03-29", segment.LocalDate);
        Assert.Equal(23 * 3_600_000L, segment.ConnectedMilliseconds);
    }

    [Fact]
    public void BerlinFallBackDay_Covers25Hours()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var start = LocalDayResolver.GetDayStartUtc(new DateOnly(2026, 10, 25), berlin);
        var end = LocalDayResolver.GetDayStartUtc(new DateOnly(2026, 10, 26), berlin);

        var segments = NightlySplitter.Split(start, end, berlin, 1000, 500, 25 * 3_600_000L);

        var segment = Assert.Single(segments);
        Assert.Equal("2026-10-25", segment.LocalDate);
        Assert.Equal(25 * 3_600_000L, segment.ConnectedMilliseconds);
    }

    [Fact]
    public void MidnightInForwardGap_SplitsAtTransitionInstant()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2025, 1, 1),
            new DateTime(2027, 12, 31),
            TimeSpan.FromMinutes(7),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 15),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 15));
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "HSM-Gap7", TimeSpan.Zero, "HSM-Gap7", "Std", "Dst", new[] { rule });

        var from = new DateTimeOffset(2026, 3, 14, 22, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 15, 2, 0, 0, TimeSpan.Zero);
        var segments = NightlySplitter.Split(from, to, zone, 240, 120, 4 * 3_600_000L);

        Assert.Equal(2, segments.Count);
        Assert.Equal("2026-03-14", segments[0].LocalDate);
        Assert.Equal("2026-03-15", segments[1].LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero), segments[1].FromUtc);
        AssertConserved(segments, 240, 120, 4 * 3_600_000L);
    }

    [Fact]
    public void RandomRanges_AlwaysConserveBytesAndMilliseconds()
    {
        var random = new Random(1234);
        var origin = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 100; i++)
        {
            var from = origin.AddMinutes(random.Next(0, 60 * 24 * 30));
            var to = from.AddMinutes(random.Next(1, 60 * 24 * 5));
            var download = (long)random.NextInt64(0, long.MaxValue / 4);
            var upload = (long)random.NextInt64(0, long.MaxValue / 8);
            var ms = (long)random.NextInt64(0, long.MaxValue / 4);

            var segments = NightlySplitter.Split(from, to, Utc, download, upload, ms);

            AssertConserved(segments, download, upload, ms);
            Assert.Equal(from, segments[0].FromUtc);
            Assert.Equal(to, segments[^1].ToUtc);
            Assert.All(segments, s => Assert.True(s.FromUtc < s.ToUtc));
        }
    }
}