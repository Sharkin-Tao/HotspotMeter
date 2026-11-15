using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Integration.Persistence;

/// <summary>
/// DailyTrafficRepository 的真实 SQLite 测试：UPSERT 累加、MIN/MAX、事务回滚、
/// 外键与 CHECK 约束、以及“重复应用会翻倍”这一非幂等语义。
/// </summary>
public class DailyTrafficRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<long> CreateProfileAsync(PersistenceContext context, string ssid = "TestHotspot")
        => await context.Profiles.GetOrCreateAsync(
            Guid.NewGuid(), Guid.Parse("11111111-1111-1111-1111-111111111111"), ssid);

    private static DailyTrafficDelta Delta(
        long profileId,
        string localDate,
        long download = 1000,
        long upload = 500,
        int sessionCount = 1,
        long connectedMs = 60_000,
        DateTimeOffset? first = null,
        DateTimeOffset? last = null,
        DateTimeOffset? updatedAt = null)
        => DailyTrafficDelta.Create(
            profileId,
            localDate,
            download,
            upload,
            sessionCount,
            connectedMs,
            first ?? (sessionCount == 1 ? Now : null),
            last ?? (sessionCount == 1 ? Now : null),
            updatedAt ?? Now);

    private static async Task ApplyAsync(PersistenceContext context, DailyTrafficDelta delta)
    {
        var repository = new DailyTrafficRepository();
        await using var connection = context.Factory.Create();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await repository.ApplyAsync(connection, transaction, delta);
        await transaction.CommitAsync();
    }

    private static async Task<DailyTrafficRow?> GetAsync(PersistenceContext context, string date, long profileId)
    {
        var repository = new DailyTrafficRepository();
        await using var connection = context.Factory.Create();
        return await repository.GetAsync(connection, date, profileId);
    }

    [Fact]
    public async Task ApplyAsync_InsertsAllFields()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var first = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);
        var last = first.AddMinutes(30);

        await ApplyAsync(context, Delta(profileId, "2026-05-01", 1234, 567, 1, 1_800_000, first, last));

        var row = await GetAsync(context, "2026-05-01", profileId);
        Assert.NotNull(row);
        Assert.Equal(1234, row.DownloadBytes);
        Assert.Equal(567, row.UploadBytes);
        Assert.Equal(1, row.SessionCount);
        Assert.Equal(1_800_000, row.ConnectedMilliseconds);
        Assert.Equal(first, row.FirstSessionAtUtc);
        Assert.Equal(last, row.LastSessionAtUtc);
        Assert.Equal(Now, row.UpdatedAtUtc);
    }

    [Fact]
    public async Task ApplyAsync_AccumulatesExistingRow()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);

        await ApplyAsync(context, Delta(profileId, "2026-05-01", 1000, 500, 1, 60_000));
        await ApplyAsync(context, Delta(profileId, "2026-05-01", 2000, 700, 1, 30_000));

        var row = await GetAsync(context, "2026-05-01", profileId);
        Assert.NotNull(row);
        Assert.Equal(3000, row.DownloadBytes);
        Assert.Equal(1200, row.UploadBytes);
        Assert.Equal(2, row.SessionCount);
        Assert.Equal(90_000, row.ConnectedMilliseconds);
    }

    [Fact]
    public async Task ApplyAsync_FirstAndLastUseMinAndMax()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var t1 = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddHours(1);
        var t3 = t1.AddHours(2);

        await ApplyAsync(context, Delta(profileId, "2026-05-01", first: t2, last: t2));
        await ApplyAsync(context, Delta(profileId, "2026-05-01", first: t1, last: t3));
        await ApplyAsync(context, Delta(profileId, "2026-05-01", first: t3, last: t3));

        var row = await GetAsync(context, "2026-05-01", profileId);
        Assert.Equal(t1, row!.FirstSessionAtUtc);
        Assert.Equal(t3, row.LastSessionAtUtc);
    }

    [Fact]
    public async Task ApplyAsync_ZeroSessionCount_KeepsExistingCountAndTimestamps()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var first = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

        await ApplyAsync(context, Delta(profileId, "2026-05-01", 1000, 500, 1, 60_000, first, first));
        await ApplyAsync(context, Delta(profileId, "2026-05-01", 500, 250, 0, 30_000));

        var row = await GetAsync(context, "2026-05-01", profileId);
        Assert.Equal(1, row!.SessionCount);
        Assert.Equal(first, row.FirstSessionAtUtc);
        Assert.Equal(first, row.LastSessionAtUtc);
    }

    [Fact]
    public async Task CrossMidnightSession_AddsSessionCountOnlyToStartDate()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var start = new DateTimeOffset(2026, 5, 1, 23, 50, 0, TimeSpan.Zero);

        await ApplyAsync(context, Delta(profileId, "2026-05-01", 800, 200, 1, 600_000, start, start.AddMinutes(10)));
        await ApplyAsync(context, Delta(profileId, "2026-05-02", 1200, 300, 0, 1_200_000));

        var day1 = await GetAsync(context, "2026-05-01", profileId);
        var day2 = await GetAsync(context, "2026-05-02", profileId);

        Assert.Equal(1, day1!.SessionCount);
        Assert.Equal(0, day2!.SessionCount);
        Assert.Null(day2.FirstSessionAtUtc);
        Assert.Equal(2000, day1.DownloadBytes + day2.DownloadBytes);
        Assert.Equal(500, day1.UploadBytes + day2.UploadBytes);
        Assert.Equal(1_800_000, day1.ConnectedMilliseconds + day2.ConnectedMilliseconds);
    }

    [Fact]
    public async Task ApplyAsync_NewRowWithoutSession_LeavesTimestampsNull()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);

        await ApplyAsync(context, Delta(profileId, "2026-05-03", sessionCount: 0));

        var row = await GetAsync(context, "2026-05-03", profileId);
        Assert.Equal(0, row!.SessionCount);
        Assert.Null(row.FirstSessionAtUtc);
        Assert.Null(row.LastSessionAtUtc);
    }

    [Fact]
    public async Task RepeatedApply_DoublesValues_ProvingNonIdempotence()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var delta = Delta(profileId, "2026-05-04", 700, 300, 1, 10_000);

        await ApplyAsync(context, delta);
        await ApplyAsync(context, delta);

        var row = await GetAsync(context, "2026-05-04", profileId);
        Assert.Equal(1400, row!.DownloadBytes);
        Assert.Equal(600, row.UploadBytes);
        Assert.Equal(2, row.SessionCount);
        Assert.Equal(20_000, row.ConnectedMilliseconds);
    }

    [Fact]
    public async Task GetAsync_MissingRow_ReturnsNull()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);

        Assert.Null(await GetAsync(context, "2026-05-05", profileId));
    }

    [Fact]
    public async Task ApplyAsync_UnknownProfile_ViolatesForeignKey()
    {
        using var context = new PersistenceContext();
        var repository = new DailyTrafficRepository();

        await using var connection = context.Factory.Create();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var error = await Assert.ThrowsAsync<SqliteException>(
            () => repository.ApplyAsync(connection, transaction, Delta(999_999, "2026-05-01")));
        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Fact]
    public async Task ApplyAsync_RolledBackWithTransaction_LeavesNoRow()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var repository = new DailyTrafficRepository();

        await using (var connection = context.Factory.Create())
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            await repository.ApplyAsync(connection, transaction, Delta(profileId, "2026-05-06"));
            await transaction.RollbackAsync();
        }

        Assert.Null(await GetAsync(context, "2026-05-06", profileId));
    }

    [Fact]
    public async Task TwoProfiles_SameDay_AreIndependent()
    {
        using var context = new PersistenceContext();
        var profileA = await CreateProfileAsync(context, "热点 A");
        var profileB = await CreateProfileAsync(context, "热点 B");

        await ApplyAsync(context, Delta(profileA, "2026-05-01", 100, 10));
        await ApplyAsync(context, Delta(profileB, "2026-05-01", 200, 20));

        var rowA = await GetAsync(context, "2026-05-01", profileA);
        var rowB = await GetAsync(context, "2026-05-01", profileB);

        Assert.Equal(100, rowA!.DownloadBytes);
        Assert.Equal(200, rowB!.DownloadBytes);
    }

    [Fact]
    public async Task ApplyAsync_OverwritesUpdatedAtUtc()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var later = Now.AddHours(5);

        await ApplyAsync(context, Delta(profileId, "2026-05-01", updatedAt: Now));
        await ApplyAsync(context, Delta(profileId, "2026-05-01", updatedAt: later));

        var row = await GetAsync(context, "2026-05-01", profileId);
        Assert.Equal(later, row!.UpdatedAtUtc);
    }

    [Fact]
    public async Task ApplyAsync_DefaultDelta_IsRejected()
    {
        using var context = new PersistenceContext();
        var repository = new DailyTrafficRepository();

        await using var connection = context.Factory.Create();
        await using var transaction = connection.BeginTransaction(deferred: false);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ApplyAsync(connection, transaction, default));
    }

    [Theory]
    [InlineData(0, "2026-05-01T10:00:00Z", "2026-05-01T10:00:00Z")]
    [InlineData(1, null, null)]
    [InlineData(1, "2026-05-01T10:00:00Z", null)]
    [InlineData(1, "2026-05-01T10:00:00Z", "2026-05-01T09:00:00Z")]
    [InlineData(2, null, null)]
    [InlineData(-1, null, null)]
    public void Create_RejectsIllegalSessionCountAndTimestampCombinations(
        int sessionCount,
        string? firstText,
        string? lastText)
    {
        var first = firstText is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(firstText);
        var last = lastText is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(lastText);

        Assert.ThrowsAny<ArgumentException>(
            () => DailyTrafficDelta.Create(1, "2026-05-01", 1, 1, sessionCount, 1, first, last, Now));
    }

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("2026-13-01")]
    [InlineData("2026-1-1")]
    [InlineData("2026-01-01 ")]
    [InlineData("")]
    public void Create_RejectsMalformedDates(string date)
    {
        Assert.Throws<ArgumentException>(
            () => DailyTrafficDelta.Create(1, date, 1, 1, 0, 0, null, null, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Create_RejectsNonPositiveProfileId(long profileId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DailyTrafficDelta.Create(profileId, "2026-05-01", 1, 1, 0, 0, null, null, Now));
    }

    [Fact]
    public void Create_RejectsNegativeByteOrDurationValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DailyTrafficDelta.Create(1, "2026-05-01", -1, 0, 0, 0, null, null, Now));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DailyTrafficDelta.Create(1, "2026-05-01", 0, -1, 0, 0, null, null, Now));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DailyTrafficDelta.Create(1, "2026-05-01", 0, 0, 0, -1, null, null, Now));
    }

    [Fact]
    public void Create_NormalizesTimestampsToUtc()
    {
        var plus8 = TimeSpan.FromHours(8);
        var first = new DateTimeOffset(2026, 5, 1, 9, 0, 0, plus8);
        var last = first.AddMinutes(5);

        var delta = DailyTrafficDelta.Create(1, "2026-05-01", 1, 1, 1, 1, first, last, Now);

        Assert.Equal(TimeSpan.Zero, delta.FirstSessionAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, delta.LastSessionAtUtc!.Value.Offset);
        Assert.Equal(first.ToUniversalTime(), delta.FirstSessionAtUtc.Value);
    }
}