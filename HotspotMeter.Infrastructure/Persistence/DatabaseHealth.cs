using Microsoft.Data.Sqlite;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>健康检查结果。<see cref="Detail"/> 会写入日志，便于事后定位。</summary>
public sealed record DatabaseHealthResult(bool IsHealthy, string Detail, int? SqliteErrorCode = null);

/// <summary>
/// 数据库健康检查。
///
/// 使用 <c>PRAGMA quick_check</c> 而不是 <c>integrity_check</c>：前者不做索引与内容的
/// 全量交叉验证，启动耗时更可控；对"文件根本不是数据库/页损坏"这类问题已经足够。
/// 检查失败时绝不继续写入——写坏的数据比缺失的数据更难修复。
/// </summary>
public sealed class DatabaseHealth
{
    /// <summary>
    /// 检查数据库文件。文件不存在视为健康（由迁移器创建），因为"尚未初始化"不是损坏。
    /// </summary>
    public DatabaseHealthResult Check(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        if (!File.Exists(databasePath))
        {
            return new DatabaseHealthResult(true, "数据库文件尚不存在，将创建新库。");
        }

        try
        {
            var factory = new SqliteConnectionFactory(databasePath);
            using var connection = factory.Create();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";

            var messages = new List<string>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    messages.Add(reader.GetString(0));
                }
            }

            if (messages.Count == 1 && string.Equals(messages[0], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new DatabaseHealthResult(true, "quick_check: ok");
            }

            return new DatabaseHealthResult(
                false,
                "quick_check 返回异常结果：" + string.Join(" | ", messages.Take(5)));
        }
        catch (SqliteException exception)
        {
            return new DatabaseHealthResult(
                false,
                $"SQLite 错误 {exception.SqliteErrorCode}（扩展码 {exception.SqliteExtendedErrorCode}）：{exception.Message}",
                exception.SqliteErrorCode);
        }
        catch (Exception exception)
        {
            return new DatabaseHealthResult(false, $"{exception.GetType().Name}: {exception.Message}");
        }
    }
}