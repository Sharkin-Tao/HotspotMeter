using HotspotMeter.Core.Models;
using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Unit.Persistence;

public class OperationIdFactoryTests
{
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddMinutes(5);

    [Fact]
    public void SameInputs_ProduceIdenticalId()
    {
        var first = OperationIdFactory.Create(42, SnapshotKind.Checkpoint, From, To);
        var second = OperationIdFactory.Create(42, SnapshotKind.Checkpoint, From, To);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentComponents_DoNotCollide()
    {
        var baseline = OperationIdFactory.Create(42, SnapshotKind.Checkpoint, From, To);
        var otherSession = OperationIdFactory.Create(43, SnapshotKind.Checkpoint, From, To);
        var otherKind = OperationIdFactory.Create(42, SnapshotKind.Settlement, From, To);
        var otherFrom = OperationIdFactory.Create(42, SnapshotKind.Checkpoint, From.AddSeconds(1), To);
        var otherTo = OperationIdFactory.Create(42, SnapshotKind.Checkpoint, From, To.AddSeconds(1));

        var all = new[] { baseline, otherSession, otherKind, otherFrom, otherTo };
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Fact]
    public void EquivalentOffsets_ForSameInstant_ProduceSameId()
    {
        var utc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var shanghai = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(8));
        var newYork = new DateTimeOffset(2025, 12, 31, 19, 0, 0, TimeSpan.FromHours(-5));

        var idUtc = OperationIdFactory.Create(1, SnapshotKind.Checkpoint, utc, utc.AddHours(1));
        var idShanghai = OperationIdFactory.Create(1, SnapshotKind.Checkpoint, shanghai, shanghai.AddHours(1));
        var idNewYork = OperationIdFactory.Create(1, SnapshotKind.Checkpoint, newYork, newYork.AddHours(1));

        Assert.Equal(idUtc, idShanghai);
        Assert.Equal(idUtc, idNewYork);
    }

    [Fact]
    public void ZeroLengthInterval_IsAllowed()
    {
        var id = OperationIdFactory.Create(1, SnapshotKind.Settlement, From, From);

        Assert.True(OperationIdFactory.TryParse(id, out var parsed));
        Assert.Equal(From.UtcTicks, parsed.FromUtcTicks);
        Assert.Equal(From.UtcTicks, parsed.ToUtcTicks);
    }

    [Fact]
    public void ReversedInterval_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            OperationIdFactory.Create(1, SnapshotKind.Checkpoint, To, From));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveSession_IsRejected(long sessionId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OperationIdFactory.Create(sessionId, SnapshotKind.Checkpoint, From, To));
    }

    [Fact]
    public void UndefinedKind_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OperationIdFactory.Create(1, (SnapshotKind)99, From, To));
    }

    [Fact]
    public void IdFitsSchemaLengthConstraint()
    {
        var id = OperationIdFactory.Create(long.MaxValue, SnapshotKind.RecoveryMerge, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        Assert.StartsWith("op:", id, StringComparison.Ordinal);
        Assert.InRange(id.Length, 8, OperationIdFactory.MaxLength);
    }

    [Fact]
    public void TryParse_RoundTrips()
    {
        var id = OperationIdFactory.Create(7, SnapshotKind.Settlement, From, To);

        Assert.True(OperationIdFactory.TryParse(id, out var key));
        Assert.Equal(7, key.DbSessionId);
        Assert.Equal(SnapshotKind.Settlement, key.Kind);
        Assert.Equal(From.UtcTicks, key.FromUtcTicks);
        Assert.Equal(To.UtcTicks, key.ToUtcTicks);
        Assert.Equal(id, key.ToOperationId());
    }

    [Theory]
    [InlineData("")]
    [InlineData("op:1:1:1")]
    [InlineData("xx:1:1:1:2")]
    [InlineData("op:0:1:1:2")]
    [InlineData("op:1:9:1:2")]
    [InlineData("op:1:1:5:4")]
    [InlineData("op:-1:1:0:1")]
    public void TryParse_RejectsMalformedIds(string id)
    {
        Assert.False(OperationIdFactory.TryParse(id, out _));
    }

    [Fact]
    public void OperationId_IsAcceptedBySchemaV2_AndEnforcesIdempotencyKey()
    {
        using var context = new PersistenceContext();
        var profileId = CreateProfile(context);
        var sessionId = CreateSession(context, profileId);
        var operationId = OperationIdFactory.Create(sessionId, SnapshotKind.Checkpoint, From, To);

        using (var connection = context.Factory.Create())
        {
            InsertSnapshot(connection, operationId, sessionId, profileId);
        }

        Assert.Equal(1, context.Count("TrafficSnapshots"));
        Assert.InRange(context.Scalar<int>("SELECT length(OperationId) FROM TrafficSnapshots;"), 8, 200);

        // 同一 OperationId + SegmentIndex 是幂等键：重复写入必须被数据库拒绝，
        // 由后续持久化层在捕获后比较内容，而不是静默覆盖。
        using (var connection = context.Factory.Create())
        {
            var exception = Record.Exception(() => InsertSnapshot(connection, operationId, sessionId, profileId));
            Assert.IsType<SqliteException>(exception);
            Assert.Equal(19, ((SqliteException)exception!).SqliteErrorCode);
        }
    }

    private static long CreateProfile(PersistenceContext context) =>
        context.Profiles.GetOrCreateAsync(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "TestHotspot").GetAwaiter().GetResult();

    private static long CreateSession(PersistenceContext context, long profileId) =>
        context.Sessions.AttachSessionAsync(new AttachSessionRequest(
            Guid.NewGuid().ToString("N"),
            1,
            profileId,
            "11111111-1111-1111-1111-111111111111",
            "TestHotspot",
            "02:00:00:00:00:01",
            From)).GetAwaiter().GetResult();

    private static void InsertSnapshot(SqliteConnection connection, string operationId, long sessionId, long profileId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TrafficSnapshots
                (OperationId, OperationSequence, SegmentIndex, ProfileId, SessionId, Kind, IsFinal,
                 FromUtc, ToUtc, CapturedAtUtc, LocalDate, TimeZoneId, DownloadBytes, UploadBytes, CreatedAtUtc)
            VALUES ($op, 1, 0, $pid, $sid, 1, 0, $from, $to, $captured, '2026-01-01', 'Asia/Shanghai', 100, 20, $created);
            """;
        command.Parameters.AddWithValue("$op", operationId);
        command.Parameters.AddWithValue("$pid", profileId);
        command.Parameters.AddWithValue("$sid", sessionId);
        command.Parameters.AddWithValue("$from", From.ToString("O"));
        command.Parameters.AddWithValue("$to", To.ToString("O"));
        command.Parameters.AddWithValue("$captured", To.AddSeconds(1).ToString("O"));
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}