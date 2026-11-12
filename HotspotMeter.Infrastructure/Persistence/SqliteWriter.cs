using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 单消费者串行写入器（阶段 3A 骨架）。
///
/// 为什么是"单消费者"：SQLite 同一时刻只允许一个写事务；把写操作收敛到一个线程，
/// 既能避免 "database is locked" 重试风暴，也能让"先写会话、再写快照"的顺序天然成立。
///
/// 本阶段刻意**不**实现多队列、优先级或事件合并：
/// 阶段 2 已证明恢复、检查点、跨日快照之间存在依赖，优先级调度会让同一会话的事件乱序。
/// 最终写入架构要在 3B 的表结构与事务边界确定后再设计，这里只保留"先进先出 + 每项独立事务"。
///
/// 连接由写入线程独占：工作委托只在写入线程内拿得到连接，外部无法绕过队列直接写库。
/// </summary>
public sealed class SqliteWriter : IDatabaseWriter, IAsyncDisposable
{
    private const int QueueCapacity = 1024;

    private readonly SqliteConnectionFactory _factory;
    private readonly Channel<WorkItem> _channel;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private SqliteConnection? _connection;
    private Task? _loop;
    private Task? _stopTask;
    private bool _started;
    private bool _stopped;

    public SqliteWriter(SqliteConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>写入线程是否已启动。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _started && !_stopped;
            }
        }
    }

    /// <inheritdoc />
    public async Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        EnsureStarted();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem
        {
            Work = async (connection, transaction, token) => await work(connection, transaction, token).ConfigureAwait(false),
            OnSuccess = value => completion.TrySetResult((T)value!),
            OnFailure = exception => completion.TrySetException(exception),
            OnCanceled = () => completion.TrySetCanceled(cancellationToken),
            Token = cancellationToken,
        };

        await EnqueueAsync(item, cancellationToken).ConfigureAwait(false);
        return await completion.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task WriteAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return WriteAsync<object?>(
            async (connection, transaction, token) =>
            {
                await work(connection, transaction, token).ConfigureAwait(false);
                return null;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem
        {
            IsBarrier = true,
            Work = (_, _, _) => Task.FromResult<object?>(null),
            OnSuccess = _ => completion.TrySetResult(null),
            OnFailure = exception => completion.TrySetException(exception),
            OnCanceled = () => completion.TrySetCanceled(cancellationToken),
            Token = cancellationToken,
        };

        await EnqueueAsync(item, cancellationToken).ConfigureAwait(false);
        await completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 停止写入线程：先关闭队列入口，让已排队的工作执行完，再释放连接。
    /// 这样"正常退出时保存最后一次采样"不会因为停止而丢掉已提交的排队项。
    /// </summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            _stopped = true;
            _channel.Writer.TryComplete();
            _stopTask = StopCoreAsync();
            return _stopTask;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task StopCoreAsync()
    {
        try
        {
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        finally
        {
            _connection?.Dispose();
            _connection = null;
        }
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);

            if (_started)
            {
                return;
            }

            _connection = _factory.Create();
            _started = true;
            _loop = Task.Run(ProcessLoopAsync);
        }
    }

    private async Task EnqueueAsync(WorkItem item, CancellationToken cancellationToken)
    {
        try
        {
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(SqliteWriter), "写入器已停止，不能再接受新的写入。");
        }
        catch (OperationCanceledException)
        {
            item.OnCanceled();
            throw;
        }
    }

    private async Task ProcessLoopAsync()
    {
        var connection = _connection ?? throw new InvalidOperationException("写入连接尚未创建。");

        await foreach (var item in _channel.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
        {
            if (item.Token.IsCancellationRequested)
            {
                item.OnCanceled();
                continue;
            }

            if (item.IsBarrier)
            {
                item.OnSuccess(null);
                continue;
            }

            SqliteTransaction? transaction = null;
            try
            {
                transaction = connection.BeginTransaction();
                var result = await item.Work(connection, transaction, item.Token).ConfigureAwait(false);
                transaction.Commit();
                transaction.Dispose();
                transaction = null;
                item.OnSuccess(result);
            }
            catch (OperationCanceledException)
            {
                Rollback(transaction);
                item.OnCanceled();
            }
            catch (Exception exception)
            {
                Rollback(transaction);
                item.OnFailure(exception);
            }
        }
    }

    private static void Rollback(SqliteTransaction? transaction)
    {
        if (transaction is null)
        {
            return;
        }

        try
        {
            transaction.Rollback();
        }
        catch (InvalidOperationException)
        {
            // 连接已失效时回滚会失败；异常仍需交给调用方，不能在这里吞掉原始错误。
        }
        finally
        {
            transaction.Dispose();
        }
    }

    private sealed class WorkItem
    {
        public required Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<object?>> Work { get; init; }

        public required Action<object?> OnSuccess { get; init; }

        public required Action<Exception> OnFailure { get; init; }

        public required Action OnCanceled { get; init; }

        public required CancellationToken Token { get; init; }

        public bool IsBarrier { get; init; }
    }
}