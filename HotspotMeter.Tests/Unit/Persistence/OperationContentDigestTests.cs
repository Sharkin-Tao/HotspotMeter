using HotspotMeter.Infrastructure.Persistence;

namespace HotspotMeter.Tests.Unit.Persistence;

/// <summary>
/// 操作摘要的确定性、灵敏度与编码边界。
///
/// 目标不是"能算出 64 个字符"，而是证明：
/// 1. 相同内容永远得到相同摘要（跨进程、跨文化、跨偏移表示）；
/// 2. 任一持久化字段变化都会改变摘要（否则幂等判断会把不同内容当成重试）；
/// 3. 请求摘要不包含只属于"写入结果"的字段（采集时间、分段），负载摘要则包含全部实际落库字段。
/// </summary>
public sealed class OperationContentDigestTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddMinutes(10);
    private static readonly DateTimeOffset Captured = To;

    private static SnapshotWriteCommand Command() => new(
        OperationId: "op:1:1:0:0",
        OperationSequence: 1,
        Kind: SnapshotKind.Checkpoint,
        DbSessionId: 1,
        ProfileId: 1,
        FromUtc: From,
        ToUtc: To,
        CapturedAtUtc: Captured,
        DownloadBytesAfter: 1_000,
        UploadBytesAfter: 100,
        ConnectedMilliseconds: 600_000,
        TimeZoneId: "Asia/Shanghai",
        QualityFlags: SnapshotQualityFlags.None,
        Segments: new[]
        {
            new SegmentInput(0, "2026-11-13", From, To, 1_000, 100, 600_000),
        });

    [Fact]
    public void Request_IsDeterministic()
    {
        var first = OperationContentDigest.ComputeRequest(Command());
        var second = OperationContentDigest.ComputeRequest(Command());

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Payload_IsDeterministicAndDiffersFromRequest()
    {
        var command = Command();
        var request = OperationContentDigest.ComputeRequest(command);
        var payload = OperationContentDigest.ComputePayload(command);

        Assert.Equal(payload, OperationContentDigest.ComputePayload(Command()));
        Assert.Equal(64, payload.Length);
        Assert.Matches("^[0-9a-f]{64}$", payload);
        Assert.NotEqual(request, payload);
    }

    [Fact]
    public void Request_IgnoresFieldsThatAreNotPartOfTheRequest()
    {
        var baseline = OperationContentDigest.ComputeRequest(Command());

        var differentOperationId = Command() with { OperationId = "op:2:1:0:0", OperationSequence = 99 };
        var differentCaptured = Command() with { CapturedAtUtc = To.AddHours(1) };
        var differentSegments = Command() with
        {
            Segments = new[] { new SegmentInput(0, "2026-11-13", From, To, 999, 9, 1) },
        };

        Assert.Equal(baseline, OperationContentDigest.ComputeRequest(differentOperationId));
        Assert.Equal(baseline, OperationContentDigest.ComputeRequest(differentCaptured));
        Assert.Equal(baseline, OperationContentDigest.ComputeRequest(differentSegments));
    }

    [Fact]
    public void Request_IsIndependentOfOffsetRepresentation()
    {
        var utc = Command();
        var offset = Command() with
        {
            FromUtc = From.ToOffset(TimeSpan.FromHours(8)),
            ToUtc = To.ToOffset(TimeSpan.FromHours(8)),
        };

        Assert.Equal(
            OperationContentDigest.ComputeRequest(utc),
            OperationContentDigest.ComputeRequest(offset));
    }

    public static TheoryData<string> RequestFields() => new()
    {
        "kind",
        "session",
        "profile",
        "from",
        "to",
        "download",
        "upload",
        "connected",
        "timezone",
        "quality",
    };

    [Theory]
    [MemberData(nameof(RequestFields))]
    public void Request_ChangesWhenAnyRequestFieldChanges(string field)
    {
        var baseline = OperationContentDigest.ComputeRequest(Command());
        var mutated = field switch
        {
            "kind" => Command() with { Kind = SnapshotKind.Settlement },
            "session" => Command() with { DbSessionId = 2 },
            "profile" => Command() with { ProfileId = 2 },
            "from" => Command() with { FromUtc = From.AddTicks(1) },
            "to" => Command() with { ToUtc = To.AddTicks(1), CapturedAtUtc = To.AddTicks(1) },
            "download" => Command() with { DownloadBytesAfter = 1_001 },
            "upload" => Command() with { UploadBytesAfter = 101 },
            "connected" => Command() with { ConnectedMilliseconds = 600_001 },
            "timezone" => Command() with { TimeZoneId = "UTC" },
            "quality" => Command() with { QualityFlags = SnapshotQualityFlags.DailySplitEstimated },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.NotEqual(baseline, OperationContentDigest.ComputeRequest(mutated));
    }

    public static TheoryData<string> PayloadFields() => new()
    {
        "opid",
        "sequence",
        "kind",
        "session",
        "profile",
        "from",
        "to",
        "captured",
        "download",
        "upload",
        "connected",
        "timezone",
        "quality",
        "segmentDate",
        "segmentFrom",
        "segmentTo",
        "segmentDownload",
        "segmentUpload",
        "segmentConnected",
        "segmentCount",
    };

    [Theory]
    [MemberData(nameof(PayloadFields))]
    public void Payload_ChangesWhenAnyPersistedFieldChanges(string field)
    {
        var baseline = OperationContentDigest.ComputePayload(Command());
        var mutated = field switch
        {
            "opid" => Command() with { OperationId = "op:1:1:0:1" },
            "sequence" => Command() with { OperationSequence = 2 },
            "kind" => Command() with { Kind = SnapshotKind.RecoveryMerge },
            "session" => Command() with { DbSessionId = 2 },
            "profile" => Command() with { ProfileId = 2 },
            "from" => Command() with { FromUtc = From.AddTicks(1) },
            "to" => Command() with { ToUtc = To.AddTicks(1), CapturedAtUtc = To.AddTicks(1) },
            "captured" => Command() with { CapturedAtUtc = Captured.AddTicks(1) },
            "download" => Command() with { DownloadBytesAfter = 1_001 },
            "upload" => Command() with { UploadBytesAfter = 101 },
            "connected" => Command() with { ConnectedMilliseconds = 600_001 },
            "timezone" => Command() with { TimeZoneId = "UTC" },
            "quality" => Command() with { QualityFlags = SnapshotQualityFlags.DailySplitEstimated },
            "segmentDate" => Command() with
            {
                Segments = new[] { new SegmentInput(0, "2026-11-12", From, To, 1_000, 100, 600_000) },
            },
            "segmentFrom" => Command() with
            {
                Segments = new[] { new SegmentInput(0, "2026-11-13", From.AddTicks(1), To, 1_000, 100, 600_000) },
            },
            "segmentTo" => Command() with
            {
                Segments = new[] { new SegmentInput(0, "2026-11-13", From, To.AddTicks(1), 1_000, 100, 600_000) },
            },
            "segmentDownload" => Command() with
            {
                Segments = new[] { new SegmentInput(0, "2026-11-13", From, To, 1_001, 100, 600_000) },
            },
            "segmentUpload" => Command() with
            {
                Segments = new[] { new SegmentInput(0, "2026-11-13", From, To, 1_000, 101, 600_000) },
            },
            "segmentConnected" => Command() with
            {
                Segments = new[] { new SegmentInput(0, "2026-11-13", From, To, 1_000, 100, 600_001) },
            },
            "segmentCount" => Command() with
            {
                Segments = new[]
                {
                    new SegmentInput(0, "2026-11-13", From, From.AddMinutes(5), 500, 50, 300_000),
                    new SegmentInput(1, "2026-11-13", From.AddMinutes(5), To, 500, 50, 300_000),
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.NotEqual(baseline, OperationContentDigest.ComputePayload(mutated));
    }

    [Fact]
    public void Payload_DistinguishesStringBoundaryAmbiguity()
    {
        // 若用无长度前缀的拼接，"op:1:1:0:9" + "AB" 与 "op:1:1:0:9A" + "B" 会拼成同一串；
        // 长度前缀编码必须把它们区分开。
        var left = Command() with { OperationId = "op:1:1:0:9", TimeZoneId = "AB" };
        var right = Command() with { OperationId = "op:1:1:0:9A", TimeZoneId = "B" };

        Assert.Equal("op:1:1:0:9AB2026-11-13", left.OperationId + left.TimeZoneId + left.Segments[0].LocalDate);
        Assert.Equal(
            left.OperationId + left.TimeZoneId,
            right.OperationId + right.TimeZoneId);

        Assert.NotEqual(
            OperationContentDigest.ComputePayload(left),
            OperationContentDigest.ComputePayload(right));
    }

    [Fact]
    public void Payload_RejectsSegmentIndexGaps()
    {
        var command = Command() with
        {
            Segments = new[] { new SegmentInput(1, "2026-11-13", From, To, 1_000, 100, 600_000) },
        };

        Assert.Throws<ArgumentException>(() => OperationContentDigest.ComputePayload(command));
    }

    [Fact]
    public void Validation_RejectsImpossibleRanges()
    {
        Assert.Throws<ArgumentException>(() => (Command() with { FromUtc = To.AddTicks(1) }).Validate());
        Assert.Throws<ArgumentException>(() => (Command() with { CapturedAtUtc = To.AddTicks(-1) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Command() with { DownloadBytesAfter = -1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Command() with { OperationSequence = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Command() with { DbSessionId = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Command() with { ProfileId = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Command() with { Segments = Array.Empty<SegmentInput>() }).Validate());
        Assert.Throws<ArgumentException>(() => (Command() with { OperationId = "short" }).Validate());
    }
}