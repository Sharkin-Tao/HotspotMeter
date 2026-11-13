namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 模式 v2：业务表结构与约束。
///
/// 设计约束：
/// 1. 公开身份用 <c>ProfileKey</c>（Core 的 Guid，32 位 N 格式）；整数 <c>ProfileId</c> 只是数据库内部主键，
///    绝不让 Core 依赖数据库自增 Id。
/// 2. 所有 CHECK 都写成显式 <c>IS NULL / IS NOT NULL</c> 或 <c>CASE</c> 形式。SQLite 中 CHECK 结果为 NULL
///    视为通过，若直接写 <c>(status = 0 AND ended IS NULL) OR ...</c>，NULL 会让非法行漏过。
/// 3. 时间统一存 UTC ISO-8601 文本；自然日单独存 <c>LocalDate</c>，用 <c>strftime</c> 反校验真实性
///    （SQLite 会把 2026-02-30 规范化成 2026-03-02，等值比较即可拒绝非法日期）。
/// 4. 复合外键 <c>(SessionId, ProfileId)</c> 保证快照、恢复候选不会把两个不同热点的会话混在一起；
///    冗余的 ProfileId 不是“可选项”，而是被外键钉死的身份。
/// </summary>
public static class SchemaV2
{
    /// <summary>按顺序执行的 DDL。每个语句独立执行，失败时整个版本回滚。</summary>
    public static readonly IReadOnlyList<string> Statements = new[]
    {
        """
        CREATE TABLE HotspotProfiles (
            ProfileId        INTEGER PRIMARY KEY AUTOINCREMENT,
            ProfileKey       TEXT    NOT NULL UNIQUE,
            DisplayName      TEXT    NOT NULL,
            InterfaceGuid    TEXT    NOT NULL,
            Ssid             TEXT    NOT NULL,
            BssidEnforcement INTEGER NOT NULL DEFAULT 0,
            CreatedAtUtc     TEXT    NOT NULL,
            LastSeenUtc      TEXT    NOT NULL,
            CHECK (length(ProfileKey) = 32),
            CHECK (ProfileKey NOT GLOB '*[^0-9A-Fa-f]*'),
            CHECK (length(DisplayName) > 0),
            CHECK (length(InterfaceGuid) = 36),
            CHECK (length(Ssid) > 0),
            CHECK (BssidEnforcement IN (0, 1))
        );
        """,
        """
        CREATE TABLE HotspotProfileBssids (
            ProfileId    INTEGER NOT NULL,
            Bssid        TEXT    NOT NULL,
            IsConfirmed  INTEGER NOT NULL DEFAULT 0,
            Source       INTEGER NOT NULL DEFAULT 0,
            FirstSeenUtc TEXT    NOT NULL,
            LastSeenUtc  TEXT    NOT NULL,
            SeenCount    INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (ProfileId, Bssid),
            FOREIGN KEY (ProfileId) REFERENCES HotspotProfiles(ProfileId) ON DELETE RESTRICT,
            CHECK (Bssid GLOB '[0-9A-Fa-f][0-9A-Fa-f]:[0-9A-Fa-f][0-9A-Fa-f]:[0-9A-Fa-f][0-9A-Fa-f]:[0-9A-Fa-f][0-9A-Fa-f]:[0-9A-Fa-f][0-9A-Fa-f]:[0-9A-Fa-f][0-9A-Fa-f]'),
            CHECK (IsConfirmed IN (0, 1)),
            CHECK (Source IN (0, 1)),
            CHECK (IsConfirmed = 0 OR Source = 1),
            CHECK (SeenCount > 0),
            CHECK (FirstSeenUtc <= LastSeenUtc)
        );
        """,
        // 未确认的 BSSID 允许多个 Profile 同时观察（换 Profile、两台同名设备过渡期）；
        // 只有人工确认后才全局唯一，避免“谁先看到谁永久占有”。
        """
        CREATE UNIQUE INDEX UX_Bssid_Confirmed
            ON HotspotProfileBssids(Bssid) WHERE IsConfirmed = 1;
        """,
        """
        CREATE TABLE ConnectionSessions (
            SessionId              INTEGER PRIMARY KEY AUTOINCREMENT,
            ProfileId              INTEGER NOT NULL,
            SessionStatus          INTEGER NOT NULL DEFAULT 0,
            InterfaceGuid          TEXT    NOT NULL,
            Ssid                   TEXT    NOT NULL,
            Bssid                  TEXT    NULL,
            StartedAtUtc           TEXT    NOT NULL,
            EndedAtUtc             TEXT    NULL,
            EndReason              INTEGER NULL,
            ResumeOutcome          INTEGER NULL,
            DownloadBytes          INTEGER NOT NULL DEFAULT 0,
            UploadBytes            INTEGER NOT NULL DEFAULT 0,
            PersistedDownloadBytes INTEGER NOT NULL DEFAULT 0,
            PersistedUploadBytes   INTEGER NOT NULL DEFAULT 0,
            LastOperationSequence  INTEGER NOT NULL DEFAULT 0,
            LastPersistedToUtc     TEXT    NULL,
            RecoveredFromSessionId INTEGER NULL,
            LastBaselineCapturedAtUtc TEXT NULL,
            LastBaselineTickCount64   INTEGER NULL,
            LastBaselineBootEpochUtc  TEXT NULL,
            LastBaselineLuid          TEXT NULL,
            LastBaselineInOctets      INTEGER NULL,
            LastBaselineOutOctets     INTEGER NULL,
            CreatedAtUtc           TEXT    NOT NULL,
            FOREIGN KEY (ProfileId) REFERENCES HotspotProfiles(ProfileId) ON DELETE RESTRICT,
            FOREIGN KEY (RecoveredFromSessionId, ProfileId)
                REFERENCES ConnectionSessions(SessionId, ProfileId) ON DELETE RESTRICT,

            CHECK (SessionStatus IN (0, 1, 2)),
            CHECK (DownloadBytes >= 0 AND UploadBytes >= 0),
            CHECK (PersistedDownloadBytes >= 0 AND PersistedUploadBytes >= 0),
            CHECK (PersistedDownloadBytes <= DownloadBytes),
            CHECK (PersistedUploadBytes <= UploadBytes),
            CHECK (LastOperationSequence >= 0),
            CHECK (LastPersistedToUtc IS NULL OR StartedAtUtc <= LastPersistedToUtc),

            -- 三态互斥且完备：Active 不带结束信息；正常结束不带恢复结局；
            -- 恢复结束必须带与 EndReason 匹配的 ResumeOutcome。
            CHECK (CASE SessionStatus
                WHEN 0 THEN (EndedAtUtc IS NULL AND EndReason IS NULL AND ResumeOutcome IS NULL)
                WHEN 1 THEN (EndedAtUtc IS NOT NULL AND EndReason IN (0, 1, 2, 3) AND ResumeOutcome IS NULL)
                WHEN 2 THEN (EndedAtUtc IS NOT NULL
                             AND ((EndReason = 4 AND ResumeOutcome = 0)
                               OR (EndReason = 5 AND ResumeOutcome BETWEEN 1 AND 5)))
                ELSE 0 END),
            CHECK (EndedAtUtc IS NULL OR StartedAtUtc <= EndedAtUtc),

            -- 基线六字段必须整体为空或整体有效，避免“有基线时间但没有计数器”这种半状态。
            CHECK (
                (LastBaselineCapturedAtUtc IS NULL AND LastBaselineTickCount64 IS NULL AND
                 LastBaselineBootEpochUtc IS NULL AND LastBaselineLuid IS NULL AND
                 LastBaselineInOctets IS NULL AND LastBaselineOutOctets IS NULL) OR
                (LastBaselineCapturedAtUtc IS NOT NULL AND LastBaselineTickCount64 IS NOT NULL AND
                 LastBaselineBootEpochUtc IS NOT NULL AND LastBaselineLuid IS NOT NULL AND
                 LastBaselineInOctets IS NOT NULL AND LastBaselineOutOctets IS NOT NULL)
            ),
            CHECK (RecoveredFromSessionId IS NULL OR RecoveredFromSessionId <> SessionId)
        );
        """,
        // 复合外键要求父键唯一；同时充当“同一会话只能属于一个 Profile”的硬约束。
        """
        CREATE UNIQUE INDEX UX_Sessions_SessionProfile
            ON ConnectionSessions(SessionId, ProfileId);
        """,
        """
        CREATE INDEX IX_Sessions_Profile_StartedAt
            ON ConnectionSessions(ProfileId, StartedAtUtc);
        """,
        """
        CREATE TABLE SessionRunMappings (
            MappingId     INTEGER PRIMARY KEY AUTOINCREMENT,
            SessionId     INTEGER NOT NULL,
            RunId         TEXT    NOT NULL,
            CoreSessionId INTEGER NOT NULL,
            AttachedAtUtc TEXT    NOT NULL,
            DetachedAtUtc TEXT    NULL,
            MappingStatus INTEGER NOT NULL DEFAULT 1,
            FOREIGN KEY (SessionId) REFERENCES ConnectionSessions(SessionId) ON DELETE RESTRICT,
            CHECK (length(RunId) = 32),
            CHECK (RunId NOT GLOB '*[^0-9A-Fa-f]*'),
            CHECK (CoreSessionId > 0),
            CHECK (MappingStatus IN (1, 2, 3)),
            CHECK (CASE MappingStatus
                WHEN 1 THEN DetachedAtUtc IS NULL
                ELSE DetachedAtUtc IS NOT NULL END),
            CHECK (DetachedAtUtc IS NULL OR AttachedAtUtc <= DetachedAtUtc),
            UNIQUE (RunId, CoreSessionId)
        );
        """,
        // 一个 DB 会话同一时刻只能有一个 Active 映射；历史（Closed/Abandoned）行永久保留。
        """
        CREATE UNIQUE INDEX UX_Mapping_OneActive
            ON SessionRunMappings(SessionId) WHERE MappingStatus = 1;
        """,
        """
        CREATE INDEX IX_Mappings_Run
            ON SessionRunMappings(RunId);
        """,
        """
        CREATE TABLE TrafficSnapshots (
            SnapshotId        INTEGER PRIMARY KEY AUTOINCREMENT,
            OperationId       TEXT    NOT NULL,
            OperationSequence INTEGER NOT NULL,
            SegmentIndex      INTEGER NOT NULL DEFAULT 0,
            ProfileId         INTEGER NOT NULL,
            SessionId         INTEGER NOT NULL,
            Kind              INTEGER NOT NULL,
            IsFinal           INTEGER NOT NULL DEFAULT 0,
            FromUtc           TEXT    NOT NULL,
            ToUtc             TEXT    NOT NULL,
            CapturedAtUtc     TEXT    NOT NULL,
            LocalDate         TEXT    NOT NULL,
            TimeZoneId        TEXT    NOT NULL,
            DownloadBytes     INTEGER NOT NULL,
            UploadBytes       INTEGER NOT NULL,
            QualityFlags      INTEGER NOT NULL DEFAULT 0,
            CreatedAtUtc      TEXT    NOT NULL,
            FOREIGN KEY (SessionId, ProfileId)
                REFERENCES ConnectionSessions(SessionId, ProfileId) ON DELETE RESTRICT,
            CHECK (Kind IN (1, 2, 3)),
            CHECK (IsFinal IN (0, 1)),
            CHECK ((Kind = 2) = (IsFinal = 1)),
            CHECK (FromUtc <= ToUtc),
            CHECK (CapturedAtUtc >= ToUtc),
            CHECK (DownloadBytes >= 0 AND UploadBytes >= 0),
            CHECK (SegmentIndex >= 0),
            CHECK (OperationSequence >= 1),
            CHECK (length(LocalDate) = 10),
            CHECK (LocalDate GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'),
            CHECK (strftime('%Y-%m-%d', LocalDate) = LocalDate),
            CHECK (length(TimeZoneId) > 0),
            CHECK (length(OperationId) BETWEEN 8 AND 200),
            UNIQUE (OperationId, SegmentIndex)
        );
        """,
        """
        CREATE INDEX IX_Snapshots_Session_FromUtc
            ON TrafficSnapshots(SessionId, FromUtc);
        """,
        """
        CREATE INDEX IX_Snapshots_Profile_LocalDate
            ON TrafficSnapshots(ProfileId, LocalDate);
        """,
        """
        CREATE TABLE DailyTraffic (
            LocalDate             TEXT    NOT NULL,
            ProfileId             INTEGER NOT NULL,
            DownloadBytes         INTEGER NOT NULL DEFAULT 0,
            UploadBytes           INTEGER NOT NULL DEFAULT 0,
            SessionCount          INTEGER NOT NULL DEFAULT 0,
            ConnectedMilliseconds INTEGER NOT NULL DEFAULT 0,
            FirstSessionAtUtc     TEXT    NULL,
            LastSessionAtUtc      TEXT    NULL,
            UpdatedAtUtc          TEXT    NOT NULL,
            PRIMARY KEY (LocalDate, ProfileId),
            FOREIGN KEY (ProfileId) REFERENCES HotspotProfiles(ProfileId) ON DELETE RESTRICT,
            CHECK (length(LocalDate) = 10),
            CHECK (LocalDate GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'),
            CHECK (strftime('%Y-%m-%d', LocalDate) = LocalDate),
            CHECK (DownloadBytes >= 0 AND UploadBytes >= 0),
            CHECK (SessionCount >= 0),
            CHECK (ConnectedMilliseconds >= 0),
            CHECK ((FirstSessionAtUtc IS NULL) = (LastSessionAtUtc IS NULL)),
            CHECK (FirstSessionAtUtc IS NULL OR FirstSessionAtUtc <= LastSessionAtUtc)
        );
        """,
        """
        CREATE TABLE RecoveryGaps (
            GapId           TEXT    PRIMARY KEY,
            ProfileId       INTEGER NOT NULL,
            OldSessionId    INTEGER NOT NULL,
            NewSessionId    INTEGER NOT NULL,
            FromUtc         TEXT    NOT NULL,
            ToUtc           TEXT    NOT NULL,
            CapturedAtUtc   TEXT    NOT NULL,
            DownloadBytes   INTEGER NOT NULL DEFAULT 0,
            UploadBytes     INTEGER NOT NULL DEFAULT 0,
            Continuity      INTEGER NOT NULL,
            CandidateStatus INTEGER NOT NULL DEFAULT 0,
            MergeSource     INTEGER NOT NULL DEFAULT 0,
            EvidenceSummary TEXT    NULL,
            FirstSeenUtc    TEXT    NOT NULL,
            UpdatedAtUtc    TEXT    NOT NULL,
            FOREIGN KEY (OldSessionId, ProfileId)
                REFERENCES ConnectionSessions(SessionId, ProfileId) ON DELETE RESTRICT,
            FOREIGN KEY (NewSessionId, ProfileId)
                REFERENCES ConnectionSessions(SessionId, ProfileId) ON DELETE RESTRICT,
            CHECK (FromUtc <= ToUtc),
            CHECK (CapturedAtUtc >= ToUtc),
            CHECK (DownloadBytes >= 0 AND UploadBytes >= 0),
            CHECK (Continuity IN (1, 2)),
            CHECK (NewSessionId <> OldSessionId),
            CHECK (OldSessionId < NewSessionId),
            CHECK (CandidateStatus IN (0, 1)),
            CHECK (MergeSource IN (0, 1, 2)),
            CHECK (CASE CandidateStatus
                WHEN 0 THEN (MergeSource = 0)
                WHEN 1 THEN (MergeSource IN (1, 2))
                ELSE 0 END)
        );
        """,
        """
        CREATE INDEX IX_RecoveryGaps_Profile_Status
            ON RecoveryGaps(ProfileId, CandidateStatus);
        """,
        """
        CREATE TABLE AppSettings (
            Key          TEXT PRIMARY KEY NOT NULL,
            Value        TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            CHECK (length(Key) > 0)
        );
        """,
    };
}