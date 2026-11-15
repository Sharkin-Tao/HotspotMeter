using HotspotMeter.Core.Models;
using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Unit.Persistence;

public class SessionRepositoryTests
{
    private static readonly Guid InterfaceGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Attach_CreatesSessionAndActiveMapping()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var runId = NewRunId();

        var dbSessionId = await context.Sessions.AttachSessionAsync(Request(runId, 1, profileId));

        Assert.True(dbSessionId > 0);
        Assert.Equal(1, context.Count("ConnectionSessions"));
        Assert.Equal(1, context.Count("SessionRunMappings"));
        Assert.Equal(0, context.Scalar<int>($"SELECT SessionStatus FROM ConnectionSessions WHERE SessionId = {dbSessionId};"));
        Assert.Equal(1, context.Scalar<int>("SELECT MappingStatus FROM SessionRunMappings;"));
    }

    [Fact]
    public async Task Attach_SameRunAndCore_IsIdempotent()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var runId = NewRunId();
        var request = Request(runId, 7, profileId);

        var first = await context.Sessions.AttachSessionAsync(request);
        var second = await context.Sessions.AttachSessionAsync(request);

        Assert.Equal(first, second);
        Assert.Equal(1, context.Count("ConnectionSessions"));
        Assert.Equal(1, context.Count("SessionRunMappings"));
    }

    [Fact]
    public async Task SameCoreSessionId_InDifferentRuns_IsNotReused()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);

        var first = await context.Sessions.AttachSessionAsync(Request(NewRunId(), 1, profileId));
        var second = await context.Sessions.AttachSessionAsync(Request(NewRunId(), 1, profileId));

        Assert.NotEqual(first, second);
        Assert.Equal(2, context.Count("ConnectionSessions"));
    }

    [Fact]
    public async Task CloseSession_NormalEnd_SetsClosedAndDetachesMapping()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var sessionId = await context.Sessions.AttachSessionAsync(Request(NewRunId(), 1, profileId));

        var closed = await context.Sessions.CloseSessionAsync(
            sessionId,
            SessionEndReason.Disconnected,
            resumeOutcome: null,
            DateTimeOffset.UtcNow);

        Assert.True(closed);
        Assert.Equal(1, context.Scalar<int>($"SELECT SessionStatus FROM ConnectionSessions WHERE SessionId = {sessionId};"));
        Assert.Equal(2, context.Scalar<int>($"SELECT MappingStatus FROM SessionRunMappings WHERE SessionId = {sessionId};"));

        var again = await context.Sessions.CloseSessionAsync(
            sessionId,
            SessionEndReason.Disconnected,
            resumeOutcome: null,
            DateTimeOffset.UtcNow);
        Assert.False(again);
    }

    [Fact]
    public async Task CloseSession_RecoveryEnd_UsesRecoveredClosedStatus()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var sessionId = await context.Sessions.AttachSessionAsync(Request(NewRunId(), 1, profileId));

        await context.Sessions.CloseSessionAsync(
            sessionId,
            SessionEndReason.RecoveryRejected,
            ResumeOutcome.Rebooted,
            DateTimeOffset.UtcNow);

        Assert.Equal(2, context.Scalar<int>($"SELECT SessionStatus FROM ConnectionSessions WHERE SessionId = {sessionId};"));
        Assert.Equal(5, context.Scalar<int>($"SELECT EndReason FROM ConnectionSessions WHERE SessionId = {sessionId};"));
        Assert.Equal(3, context.Scalar<int>($"SELECT ResumeOutcome FROM ConnectionSessions WHERE SessionId = {sessionId};"));
    }

    [Fact]
    public async Task CloseSession_RejectsRecoveryWithoutOutcome()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var sessionId = await context.Sessions.AttachSessionAsync(Request(NewRunId(), 1, profileId));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            context.Sessions.CloseSessionAsync(
                sessionId,
                SessionEndReason.RecoveryRejected,
                resumeOutcome: null,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task AbandonStaleMappings_OnlyAffectsOtherRuns()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var oldRun = NewRunId();
        var sessionId = await context.Sessions.AttachSessionAsync(Request(oldRun, 1, profileId));

        var currentRun = NewRunId();
        var affected = await context.Sessions.AbandonStaleMappingsAsync(currentRun, DateTimeOffset.UtcNow);

        Assert.Equal(1, affected);
        Assert.Equal(3, context.Scalar<int>($"SELECT MappingStatus FROM SessionRunMappings WHERE SessionId = {sessionId};"));
        Assert.Equal(1, context.Scalar<int>($"SELECT COUNT(*) FROM SessionRunMappings WHERE DetachedAtUtc IS NOT NULL;"));

        // 会话本身不被猜测结论，仍是 Active，进入孤儿列表交给 3D 判定。
        Assert.Equal(0, context.Scalar<int>($"SELECT SessionStatus FROM ConnectionSessions WHERE SessionId = {sessionId};"));
        var orphans = await context.Sessions.GetOrphanedActiveSessionsAsync();
        Assert.Contains(sessionId, orphans);
    }

    [Fact]
    public async Task Attach_WithUnknownProfile_RollsBackCompletely()
    {
        using var context = new PersistenceContext();

        await Assert.ThrowsAsync<SqliteException>(() =>
            context.Sessions.AttachSessionAsync(Request(NewRunId(), 1, profileId: 9999)));

        Assert.Equal(0, context.Count("ConnectionSessions"));
        Assert.Equal(0, context.Count("SessionRunMappings"));
    }

    [Fact]
    public async Task ConcurrentAttach_SamePair_CreatesOneSession()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var runId = NewRunId();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            context.Sessions.AttachSessionAsync(Request(runId, 3, profileId)))));

        Assert.Single(results.Distinct());
        Assert.Equal(1, context.Count("ConnectionSessions"));
        Assert.Equal(1, context.Count("SessionRunMappings"));
    }

    [Fact]
    public async Task GetOrphanedActiveSessions_ExcludesClosedSessions()
    {
        using var context = new PersistenceContext();
        var profileId = await CreateProfileAsync(context);
        var sessionId = await context.Sessions.AttachSessionAsync(Request(NewRunId(), 1, profileId));

        await context.Sessions.CloseSessionAsync(
            sessionId,
            SessionEndReason.EngineStopped,
            resumeOutcome: null,
            DateTimeOffset.UtcNow);

        var orphans = await context.Sessions.GetOrphanedActiveSessionsAsync();
        Assert.DoesNotContain(sessionId, orphans);
    }

    private static AttachSessionRequest Request(string runId, long coreSessionId, long profileId) =>
        new(
            runId,
            coreSessionId,
            profileId,
            InterfaceGuid.ToString("D"),
            "TestHotspot",
            "02:00:00:00:00:01",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static async Task<long> CreateProfileAsync(PersistenceContext context) =>
        await context.Profiles.GetOrCreateAsync(Guid.NewGuid(), InterfaceGuid, "TestHotspot");

    private static string NewRunId() => Guid.NewGuid().ToString("N");
}