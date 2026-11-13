using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;

namespace HotspotMeter.Tests.Unit.Persistence;

public class BssidRepositoryTests
{
    private const string Bssid = "1A:44:DD:0E:E5:B0";
    private static readonly Guid InterfaceGuid = Guid.Parse("2f96e651-0898-4f18-ab35-41096d91e1b5");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordObservation_Twice_IncrementsSeenCount()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);

        await context.Bssids.RecordObservationAsync(profileId, Bssid.ToLowerInvariant(), Now);
        await context.Bssids.RecordObservationAsync(profileId, Bssid, Now.AddSeconds(1));

        Assert.Equal(1, context.Count("HotspotProfileBssids"));
        Assert.Equal(2, context.Scalar<int>("SELECT SeenCount FROM HotspotProfileBssids;"));
        Assert.Equal(Bssid, context.Scalar<string>("SELECT Bssid FROM HotspotProfileBssids;"));
        Assert.Equal(0, context.Scalar<int>("SELECT IsConfirmed FROM HotspotProfileBssids;"));
    }

    [Fact]
    public async Task Unconfirmed_CanCoexistAcrossProfiles()
    {
        using var context = new PersistenceContext();
        var first = await CreateProfileAsync(context);
        var second = await CreateProfileAsync(context);

        await context.Bssids.RecordObservationAsync(first, Bssid, Now);
        await context.Bssids.RecordObservationAsync(second, Bssid, Now);

        Assert.Equal(2, context.Count("HotspotProfileBssids"));
        Assert.Equal(0, context.Scalar<int>("SELECT COUNT(*) FROM HotspotProfileBssids WHERE IsConfirmed = 1;"));
    }

    [Fact]
    public async Task Confirm_MakesBssidGloballyUnique()
    {
        using var context = new PersistenceContext();
        var owner = await CreateProfileAsync(context);
        var other = await CreateProfileAsync(context);

        await context.Bssids.RecordObservationAsync(owner, Bssid, Now);
        await context.Bssids.RecordObservationAsync(other, Bssid, Now);

        var confirmed = await context.Bssids.ConfirmAsync(owner, Bssid, Now);
        Assert.Equal(BssidConfirmationOutcome.Confirmed, confirmed.Outcome);
        Assert.Equal(owner, confirmed.OwnerProfileId);

        var conflict = await context.Bssids.ConfirmAsync(other, Bssid, Now);
        Assert.Equal(BssidConfirmationOutcome.ConflictWithOtherProfile, conflict.Outcome);
        Assert.Equal(owner, conflict.OwnerProfileId);

        Assert.Equal(1, context.Scalar<int>("SELECT COUNT(*) FROM HotspotProfileBssids WHERE IsConfirmed = 1;"));
        Assert.False(await context.Bssids.IsConfirmedAsync(other, Bssid));
    }

    [Fact]
    public async Task Confirm_SameProfileTwice_IsIdempotent()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        await context.Bssids.RecordObservationAsync(profileId, Bssid, Now);

        var first = await context.Bssids.ConfirmAsync(profileId, Bssid, Now);
        var second = await context.Bssids.ConfirmAsync(profileId, Bssid, Now);

        Assert.Equal(BssidConfirmationOutcome.Confirmed, first.Outcome);
        Assert.Equal(BssidConfirmationOutcome.AlreadyConfirmed, second.Outcome);
        Assert.Equal(1, context.Scalar<int>("SELECT COUNT(*) FROM HotspotProfileBssids WHERE IsConfirmed = 1;"));
    }

    [Fact]
    public async Task Confirm_WithoutPriorObservation_CreatesConfirmedRow()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);

        var result = await context.Bssids.ConfirmAsync(profileId, Bssid, Now);

        Assert.Equal(BssidConfirmationOutcome.Confirmed, result.Outcome);
        Assert.Equal(1, context.Count("HotspotProfileBssids"));
        Assert.Equal(1, context.Scalar<int>("SELECT IsConfirmed FROM HotspotProfileBssids;"));
    }

    [Fact]
    public async Task ConcurrentConfirm_OnlyOneProfileWins()
    {
        using var context = new PersistenceContext();
        var first = await CreateProfileAsync(context);
        var second = await CreateProfileAsync(context);
        await context.Bssids.RecordObservationAsync(first, Bssid, Now);
        await context.Bssids.RecordObservationAsync(second, Bssid, Now);

        var profileIds = new[] { first, second };
        var tasks = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            context.Bssids.ConfirmAsync(profileIds[index % 2], Bssid, Now)));
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(result => result.Outcome == BssidConfirmationOutcome.Confirmed));
        Assert.Equal(1, context.Scalar<int>("SELECT COUNT(*) FROM HotspotProfileBssids WHERE IsConfirmed = 1;"));
    }

    [Fact]
    public async Task GetConfirmed_IsProfileScoped()
    {
        using var context = new PersistenceContext();
        var first = await CreateProfileAsync(context);
        var second = await CreateProfileAsync(context);
        await context.Bssids.ConfirmAsync(first, Bssid, Now);

        var confirmedForFirst = await context.Bssids.GetConfirmedAsync(first);
        var confirmedForSecond = await context.Bssids.GetConfirmedAsync(second);

        Assert.Equal(new[] { Bssid }, confirmedForFirst);
        Assert.Empty(confirmedForSecond);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1A:44:DD:0E:E5")]
    [InlineData("1A:44:DD:0E:E5:B0:CC")]
    [InlineData("1A-44-DD-0E-E5-B0")]
    [InlineData("GG:44:DD:0E:E5:B0")]
    public async Task RecordObservation_RejectsInvalidFormat(string bssid)
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            context.Bssids.RecordObservationAsync(profileId, bssid, Now));
    }

    private static async Task<long> CreateProfileAsync(PersistenceContext context) =>
        await context.Profiles.GetOrCreateAsync(Guid.NewGuid(), InterfaceGuid, "OPPO Find X9");
}