namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 模式 v5：给 Traffic Guard 增加规则/提醒配置与提醒历史字段。
///
/// v4 已经建好四张 Guard 表，但 AlertHistory 只预留了最小结构。v5 只做
/// <c>ALTER TABLE ... ADD COLUMN</c> 与索引追加，不重建表、不删除任何旧列，
/// 因此已有样本、规则和历史记录都会原样保留。
/// </summary>
public static class SchemaV5
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
        """
        ALTER TABLE GuardSettings ADD COLUMN AlertsEnabled INTEGER NOT NULL DEFAULT 1
            CHECK (AlertsEnabled IN (0, 1));
        """,
        """
        ALTER TABLE GuardSettings ADD COLUMN DefaultThresholdBytes INTEGER NOT NULL DEFAULT 104857600
            CHECK (DefaultThresholdBytes > 0);
        """,
        """
        ALTER TABLE GuardSettings ADD COLUMN PeriodKind INTEGER NOT NULL DEFAULT 1
            CHECK (PeriodKind IN (0, 1, 2));
        """,
        """
        ALTER TABLE GuardSettings ADD COLUMN MetricKind INTEGER NOT NULL DEFAULT 2
            CHECK (MetricKind IN (0, 1, 2));
        """,
        """
        ALTER TABLE GuardSettings ADD COLUMN BackgroundOnly INTEGER NOT NULL DEFAULT 1
            CHECK (BackgroundOnly IN (0, 1));
        """,
        """
        ALTER TABLE GuardSettings ADD COLUMN CooldownSeconds INTEGER NOT NULL DEFAULT 3600
            CHECK (CooldownSeconds BETWEEN 60 AND 86400);
        """,
        """
        ALTER TABLE GuardSettings ADD COLUMN SecondaryEnabled INTEGER NOT NULL DEFAULT 1
            CHECK (SecondaryEnabled IN (0, 1));
        """,
        """
        ALTER TABLE GuardSettings ADD COLUMN SecondaryGrowthBytes INTEGER NOT NULL DEFAULT 52428800
            CHECK (SecondaryGrowthBytes > 0);
        """,
        """
        ALTER TABLE GuardAppRules ADD COLUMN RuleMode INTEGER NOT NULL DEFAULT 0
            CHECK (RuleMode IN (0, 1, 2, 3));
        """,
        """
        ALTER TABLE GuardAppRules ADD COLUMN ThresholdOverrideBytes INTEGER NULL
            CHECK (ThresholdOverrideBytes IS NULL OR ThresholdOverrideBytes > 0);
        """,
        $"""
        ALTER TABLE GuardAppRules ADD COLUMN IgnoreUntilUtc TEXT NULL
            CHECK (IgnoreUntilUtc IS NULL OR ({TimeCheck("IgnoreUntilUtc")}));
        """,
        """
        ALTER TABLE GuardAlertHistory ADD COLUMN PeriodKind INTEGER NULL
            CHECK (PeriodKind IS NULL OR PeriodKind IN (0, 1, 2));
        """,
        $"""
        ALTER TABLE GuardAlertHistory ADD COLUMN PeriodStartUtc TEXT NULL
            CHECK (PeriodStartUtc IS NULL OR ({TimeCheck("PeriodStartUtc")}));
        """,
        """
        ALTER TABLE GuardAlertHistory ADD COLUMN DownloadBytes INTEGER NULL
            CHECK (DownloadBytes IS NULL OR DownloadBytes >= 0);
        """,
        """
        ALTER TABLE GuardAlertHistory ADD COLUMN UploadBytes INTEGER NULL
            CHECK (UploadBytes IS NULL OR UploadBytes >= 0);
        """,
        """
        ALTER TABLE GuardAlertHistory ADD COLUMN NotificationStatus INTEGER NOT NULL DEFAULT 0
            CHECK (NotificationStatus IN (0, 1, 2, 3));
        """,
        """
        ALTER TABLE GuardAlertHistory ADD COLUMN NotificationError TEXT NULL
            CHECK (NotificationError IS NULL OR length(NotificationError) <= 512);
        """,
        $"""
        ALTER TABLE GuardAlertHistory ADD COLUMN HandledAtUtc TEXT NULL
            CHECK (HandledAtUtc IS NULL OR ({TimeCheck("HandledAtUtc")}));
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_GuardAlerts_App_Period
            ON GuardAlertHistory (AppKey, PeriodKind, CreatedAtUtc);
        """,
    };
}