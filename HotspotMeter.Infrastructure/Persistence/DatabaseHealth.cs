using Microsoft.Data.Sqlite;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 健康检查结果。<see cref="Detail"/> 会写入日志，便于事后定位。
/// <see cref="CanQuarantine"/> 只在"确定文件损坏"时为 true：临时占用/权限/IO 错误
/// 绝不能触发隔离，否则一次安全软件扫描就会让用户历史统计静默清零。
/// </summary>
public sealed record DatabaseHealthResult(
    bool IsHealthy,
    string Detail,
    int? SqliteErrorCode = null,
    bool CanQuarantine = false);

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

        // BUSY/LOCKED 常见于启动瞬间（安全软件扫描、备份工具持有句柄）。
        // 短暂退避比直接判损坏或直接放弃更符合真实桌面环境。
        DatabaseHealthResult result = null!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            result = CheckOnce(databasePath);
            if (result.IsHealthy || result.SqliteErrorCode is not (5 or 6))
            {
                return result;
            }

            Thread.Sleep(attempt == 0 ? 200 : 500);
        }

        return result;
    }

    private static DatabaseHealthResult CheckOnce(string databasePath)
    {
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
                "quick_check 返回异常结果：" + string.Join(" | ", messages.Take(5)),
                CanQuarantine: true);
        }
        catch (SqliteException exception)
        {
            // 5=BUSY、6=LOCKED、10=IOERR：多数是暂时占用/瞬时 IO，必须保留文件。
            // 14=CANTOPEN 仍允许尝试隔离：真正被占用的文件 Move 也会失败，
            // 不会出现“临时占用却被静默移走”的结果。
            var transient = exception.SqliteErrorCode is 5 or 6 or 10;
            return new DatabaseHealthResult(
                false,
                $"SQLite 错误 {exception.SqliteErrorCode}（扩展码 {exception.SqliteExtendedErrorCode}）：{exception.Message}",
                exception.SqliteErrorCode,
                CanQuarantine: !transient);
        }
        catch (Exception exception)
        {
            return new DatabaseHealthResult(false, $"{exception.GetType().Name}: {exception.Message}");
        }
    }
}