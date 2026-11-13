namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 模式 v3：快照操作表。
///
/// 为什么需要单独的操作表：
/// 1. <c>TrafficSnapshots</c> 的唯一键是 <c>(OperationId, SegmentIndex)</c>，它只能证明"某个分段写过"，
///    无法表达"整个操作是否成功、请求内容是什么、是否已被应用"。一次跨日操作会拆成多段，
///    若只有快照表，段写了一半时无法判断这是完整操作还是残缺写入。
/// 2. 操作级摘要（RequestDigest/PayloadDigest）必须与快照行同事务落库，才能用于幂等重试与冲突识别。
/// 3. <c>OperationSequence</c> 是每个会话内的严格递增序号，配合 <c>UNIQUE(DbSessionId, OperationSequence)</c>
///    可以检测"同一会话出现两个不同操作抢同一个序号"，而不是靠时间戳猜测。
///
/// 本版本只新增表与索引，不修改 v2 的任何既有表；v2 已发布，不允许改写。
/// </summary>
public static class SchemaV3
{
    /// <summary>
    /// UTC 时间文本列的格式约束。
    ///
    /// 不能只依赖 <c>strftime</c> 往返：SQLite 3.53.3 会把 <c>24:00:00</c> 视为合法并原样返回，
    /// 也会让 <c>29:00:00</c> 的 <c>strftime</c> 返回 NULL；而 CHECK 只拒绝 FALSE，NULL 会被放行。
    /// 因此必须显式限制小时位，并加 <c>IS NOT NULL</c>，两者缺一不可。
    /// 该约束已在 SQLite 3.53.3 上逐例实测（见 SchemaV3Tests）。
    /// </summary>
    private static string TimeCheck(string column) =>
        $"""
        length({column}) = 33
                  AND {column} GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]T[0-2][0-9]:[0-5][0-9]:[0-5][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'
                  AND substr({column}, 1, 4) BETWEEN '0001' AND '9999'
                  AND substr({column}, 6, 2) BETWEEN '01' AND '12'
                  AND substr({column}, 9, 2) BETWEEN '01' AND '31'
                  AND substr({column}, 12, 2) BETWEEN '00' AND '23'
                  AND strftime('%Y-%m-%dT%H:%M:%S', {column}) IS NOT NULL
                  AND strftime('%Y-%m-%dT%H:%M:%S', {column}) = substr({column}, 1, 19)
        """;

    /// <summary>v3 的全部 DDL。顺序固定：先建表，再建索引。</summary>
    public static readonly IReadOnlyList<string> Statements = new[]
    {
        $"""
        CREATE TABLE IF NOT EXISTS TrafficOperations (
            OperationRowId        INTEGER PRIMARY KEY AUTOINCREMENT,
            OperationId           TEXT    NOT NULL UNIQUE,
            OperationSequence     INTEGER NOT NULL CHECK (OperationSequence >= 1),
            OperationType         INTEGER NOT NULL CHECK (OperationType IN (1, 2, 3)),
            DbSessionId           INTEGER NOT NULL,
            ProfileId             INTEGER NOT NULL,
            FromUtc               TEXT    NOT NULL CHECK ({TimeCheck("FromUtc")}),
            ToUtc                 TEXT    NOT NULL CHECK ({TimeCheck("ToUtc")}),
            CapturedAtUtc         TEXT    NOT NULL CHECK ({TimeCheck("CapturedAtUtc")}),
            AppliedAtUtc          TEXT    NOT NULL CHECK ({TimeCheck("AppliedAtUtc")}),
            DownloadBytesAfter    INTEGER NOT NULL CHECK (DownloadBytesAfter >= 0),
            UploadBytesAfter      INTEGER NOT NULL CHECK (UploadBytesAfter >= 0),
            ConnectedMilliseconds INTEGER NOT NULL CHECK (ConnectedMilliseconds >= 0),
            SegmentCount          INTEGER NOT NULL CHECK (SegmentCount BETWEEN 1 AND 400),
            TimeZoneId            TEXT    NOT NULL CHECK (length(TimeZoneId) BETWEEN 1 AND 128),
            QualityFlags          INTEGER NOT NULL CHECK (QualityFlags BETWEEN 0 AND 255),
            RequestDigest         TEXT    NOT NULL CHECK (length(RequestDigest) = 64 AND RequestDigest NOT GLOB '*[^0-9a-f]*'),
            PayloadDigest         TEXT    NOT NULL CHECK (length(PayloadDigest) = 64 AND PayloadDigest NOT GLOB '*[^0-9a-f]*'),
            CHECK (length(OperationId) BETWEEN 8 AND 200),
            CHECK (FromUtc <= ToUtc),
            CHECK (CapturedAtUtc >= ToUtc),
            UNIQUE (DbSessionId, OperationSequence),
            FOREIGN KEY (DbSessionId, ProfileId) REFERENCES ConnectionSessions (SessionId, ProfileId)
                ON UPDATE RESTRICT ON DELETE RESTRICT
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_Operations_Session_FromUtc
            ON TrafficOperations (DbSessionId, FromUtc);
        """,
    };
}