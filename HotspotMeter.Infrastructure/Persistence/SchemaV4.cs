namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 模式 v4：Traffic Guard 的配置、程序规则、增量样本与预留提醒表。
///
/// 原则：
/// 1. 只追加表/索引，绝不修改或重建 v1–v3 的表；
/// 2. 样本只记录“已确认的正增量”，缺失/重置/网络切换通过 QualityFlags 表达，不写 0；
/// 3. 未归属流量没有 AppKey，不能进 GuardTrafficSamples（禁止分摊给任意程序）；
/// 4. GuardAlertHistory 只建结构，本阶段不写业务；
/// 5. 所有唯一约束都是幂等写入的基础：重复提交同一 (AppKey,ProfileKey,From,To) 不会重复累计。
/// </summary>
public static class SchemaV4
{
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

    public static readonly IReadOnlyList<string> Statements = new[]
    {
        $"""
        CREATE TABLE IF NOT EXISTS GuardSettings (
            Id                       INTEGER PRIMARY KEY NOT NULL CHECK (Id = 1),
            Enabled                  INTEGER NOT NULL CHECK (Enabled IN (0, 1)),
            SamplingIntervalSeconds  INTEGER NOT NULL CHECK (SamplingIntervalSeconds BETWEEN 10 AND 300),
            DataRetentionDays        INTEGER NOT NULL CHECK (DataRetentionDays BETWEEN 1 AND 3650),
            UpdatedAtUtc             TEXT    NOT NULL CHECK ({TimeCheck("UpdatedAtUtc")})
        );
        """,
        """
        INSERT OR IGNORE INTO GuardSettings
            (Id, Enabled, SamplingIntervalSeconds, DataRetentionDays, UpdatedAtUtc)
        VALUES
            (1, 1, 30, 30, strftime('%Y-%m-%dT%H:%M:%f0000+00:00', 'now'));
        """,
        """
        CREATE TABLE IF NOT EXISTS GuardAppRules (
            AppKey          TEXT    PRIMARY KEY NOT NULL CHECK (length(AppKey) BETWEEN 8 AND 512),
            DisplayName     TEXT    NOT NULL CHECK (length(DisplayName) BETWEEN 1 AND 256),
            IdentityKind    INTEGER NOT NULL CHECK (IdentityKind IN (1, 2, 3)),
            IdentityCertain INTEGER NOT NULL CHECK (IdentityCertain IN (0, 1)),
            TrustState      INTEGER NOT NULL DEFAULT 0 CHECK (TrustState IN (0, 1, 2)),
            FirstSeenAtUtc  TEXT    NOT NULL CHECK (length(FirstSeenAtUtc) = 33),
            LastSeenAtUtc   TEXT    NOT NULL CHECK (length(LastSeenAtUtc) = 33),
            CHECK (LastSeenAtUtc >= FirstSeenAtUtc),
            CHECK (IdentityKind <> 3 OR IdentityCertain = 0)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS GuardTrafficSamples (
            SampleId       INTEGER PRIMARY KEY AUTOINCREMENT,
            AppKey         TEXT    NOT NULL,
            ProfileKey     TEXT    NOT NULL CHECK (
                length(ProfileKey) = 32 AND ProfileKey NOT GLOB '*[^0-9a-f]*'),
            FromUtc        TEXT    NOT NULL CHECK (length(FromUtc) = 33),
            ToUtc          TEXT    NOT NULL CHECK (length(ToUtc) = 33),
            CapturedAtUtc  TEXT    NOT NULL CHECK (length(CapturedAtUtc) = 33),
            DownloadBytes  INTEGER NOT NULL CHECK (DownloadBytes >= 0),
            UploadBytes    INTEGER NOT NULL CHECK (UploadBytes >= 0),
            ActivityState  INTEGER NOT NULL CHECK (ActivityState IN (0, 1, 2)),
            ProcessCount   INTEGER NOT NULL CHECK (ProcessCount >= 0),
            QualityFlags   INTEGER NOT NULL CHECK (QualityFlags BETWEEN 0 AND 63),
            CHECK (FromUtc <= ToUtc),
            CHECK (CapturedAtUtc >= ToUtc),
            CHECK (DownloadBytes > 0 OR UploadBytes > 0),
            UNIQUE (AppKey, ProfileKey, FromUtc, ToUtc),
            FOREIGN KEY (AppKey) REFERENCES GuardAppRules (AppKey)
                ON UPDATE RESTRICT ON DELETE RESTRICT
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_GuardSamples_Profile_ToUtc
            ON GuardTrafficSamples (ProfileKey, ToUtc);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_GuardSamples_AppKey_ToUtc
            ON GuardTrafficSamples (AppKey, ToUtc);
        """,
        $"""
        CREATE TABLE IF NOT EXISTS GuardAlertHistory (
            AlertId        INTEGER PRIMARY KEY AUTOINCREMENT,
            AppKey         TEXT    NULL,
            ProfileKey     TEXT    NULL CHECK (
                ProfileKey IS NULL OR (length(ProfileKey) = 32 AND ProfileKey NOT GLOB '*[^0-9a-f]*')),
            AlertType      INTEGER NOT NULL CHECK (AlertType BETWEEN 1 AND 99),
            ThresholdBytes INTEGER NULL CHECK (ThresholdBytes IS NULL OR ThresholdBytes >= 0),
            ActualBytes    INTEGER NOT NULL CHECK (ActualBytes >= 0),
            CreatedAtUtc   TEXT    NOT NULL CHECK ({TimeCheck("CreatedAtUtc")}),
            IsDismissed    INTEGER NOT NULL DEFAULT 0 CHECK (IsDismissed IN (0, 1)),
            Details        TEXT    NULL CHECK (Details IS NULL OR length(Details) <= 2000),
            FOREIGN KEY (AppKey) REFERENCES GuardAppRules (AppKey)
                ON UPDATE RESTRICT ON DELETE SET NULL
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_GuardAlerts_CreatedAt
            ON GuardAlertHistory (CreatedAtUtc);
        """,
    };
}