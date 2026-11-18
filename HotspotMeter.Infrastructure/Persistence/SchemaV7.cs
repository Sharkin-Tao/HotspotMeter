namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 模式 v7：修复 v4 遗留的同名单列提醒索引。
///
/// v6 原本使用 <c>CREATE INDEX IF NOT EXISTS IX_GuardAlerts_CreatedAt</c>，
/// 但 SQLite 只按索引名判断是否存在，不会比较定义。v4 已经创建过
/// <c>IX_GuardAlerts_CreatedAt (CreatedAtUtc)</c>，因此从 v5 升级到 v6 后，
/// 复合索引没有生效，历史列表仍按单列索引查询。
///
/// 本迁移只重建这一个索引，不重建 <c>GuardAlertHistory</c>、不增加/删除列、
/// 不修改任何提醒记录。先 DROP 再 CREATE，使同一组语句可以被安全重复执行；
/// 整个迁移仍由 <see cref="SchemaMigrator"/> 放在一个事务里，失败会连同
/// DROP 一起回滚，旧索引和旧数据都不会处于半成品状态。
/// </summary>
public static class SchemaV7
{
    public static readonly IReadOnlyList<string> Statements = new[]
    {
        """
        DROP INDEX IF EXISTS IX_GuardAlerts_CreatedAt;
        """,
        """
        CREATE INDEX IX_GuardAlerts_CreatedAt
            ON GuardAlertHistory (CreatedAtUtc DESC, AlertId DESC);
        """,
    };
}