using Microsoft.Data.Sqlite;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 数据库写入入口。阶段 3A 只定义"串行、事务化"的最小契约，
/// 具体业务命令（会话/快照/日统计）在 3B 之后按审核结果扩展。
/// </summary>
public interface IDatabaseWriter
{
    /// <summary>在写入线程中执行一个工作单元，并返回结果。</summary>
    Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default);

    /// <summary>在写入线程中执行一个工作单元。</summary>
    Task WriteAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> work,
        CancellationToken cancellationToken = default);

    /// <summary>等待此前排队的写入全部完成。</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>停止写入线程并释放连接。可重复调用。</summary>
    Task StopAsync();
}