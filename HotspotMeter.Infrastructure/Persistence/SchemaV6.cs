namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 模式 v6：为 Traffic Guard 第三阶段补齐“详情”和“处理记录”所需字段。
///
/// 仍然只做增量 <c>ALTER TABLE</c>：v5 已有的提醒、规则和配置一条都不动。
/// 新增字段全部带默认值，旧记录升级后仍然可读；样本表不做任何修改。
/// </summary>
public static class SchemaV6
{
    public static readonly IReadOnlyList<string> Statements = new[]
    {
        """
        ALTER TABLE GuardAppRules ADD COLUMN ExecutablePath TEXT NULL
            CHECK (ExecutablePath IS NULL OR length(ExecutablePath) BETWEEN 1 AND 1024);
        """,
        """
        ALTER TABLE GuardAlertHistory ADD COLUMN MetricKind INTEGER NOT NULL DEFAULT 2
            CHECK (MetricKind IN (0, 1, 2));
        """,
        """
        ALTER TABLE GuardAlertHistory ADD COLUMN ThresholdSource INTEGER NOT NULL DEFAULT 0
            CHECK (ThresholdSource IN (0, 1));
        """,
        """
        ALTER TABLE GuardAlertHistory ADD COLUMN ActivityState INTEGER NOT NULL DEFAULT 0
            CHECK (ActivityState IN (0, 1, 2));
        """,
        """
        ALTER TABLE GuardAlertHistory ADD COLUMN HandlingAction INTEGER NOT NULL DEFAULT 0
            CHECK (HandlingAction BETWEEN 0 AND 5);
        """,
        // v4 曾创建过同名的单列索引 (CreatedAtUtc)。SQLite 的 CREATE INDEX
        // IF NOT EXISTS 只按名字判断，不会替换定义；必须先删除旧索引，
        // 否则 v6 新建库仍会留下错误的单列索引。v7 对已发布的 v6 库做同样修复。
        """
        DROP INDEX IF EXISTS IX_GuardAlerts_CreatedAt;
        """,
        """
        CREATE INDEX IX_GuardAlerts_CreatedAt
            ON GuardAlertHistory (CreatedAtUtc DESC, AlertId DESC);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_GuardAlerts_Notification
            ON GuardAlertHistory (NotificationStatus, CreatedAtUtc DESC);
        """,
    };
}