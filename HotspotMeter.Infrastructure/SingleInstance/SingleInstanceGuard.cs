namespace HotspotMeter.Infrastructure.SingleInstance;

/// <summary>
/// 进程级单实例保护。
///
/// 为什么不能只用数据库文件锁：文件锁只能防止"同时写同一个文件"，
/// 但第二个实例仍会启动第二套监控循环、重复采样、重复计算会话——
/// 流量数字会翻倍，退出时还会互相覆盖检查点。这里用 Windows 命名 Mutex
/// 把"整个程序只允许一个监控实例"作为前提条件。
///
/// 名称放在 <c>Local\</c> 命名空间：数据库本身位于当前用户的
/// %LOCALAPPDATA%，不同 Windows 用户本就应该各自运行、各自统计。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>默认互斥体名称。修改名称等于允许新旧版本并存，需谨慎。</summary>
    public const string DefaultName = @"Local\HotspotMeter.SingleInstance.v1";

    private static readonly object ProcessGate = new();
    private static readonly HashSet<string> ProcessOwnedNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _name;
    private Mutex? _mutex;

    private SingleInstanceGuard(string name, Mutex mutex)
    {
        _name = name;
        _mutex = mutex;
    }

    /// <summary>
    /// 尝试获取单实例所有权。已有实例（同进程或同会话的其他进程）持有时返回 null。
    /// </summary>
    /// <param name="name">互斥体名称；测试传入唯一名称以避免相互干扰。</param>
    public static SingleInstanceGuard? TryAcquire(string? name = null)
    {
        var mutexName = name ?? DefaultName;
        if (string.IsNullOrWhiteSpace(mutexName))
        {
            throw new ArgumentException("互斥体名称不能为空。", nameof(name));
        }

        lock (ProcessGate)
        {
            // Mutex 对“持有它的线程”是可重入的：同一进程内重复获取会成功，
            // 因此必须在进程内再加一道记录，否则测试和误用都会得到两个“单实例”。
            if (ProcessOwnedNames.Contains(mutexName))
            {
                return null;
            }

            var mutex = new Mutex(initiallyOwned: false, mutexName);
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者异常退出（未 Release），但所有权已转移给我们；
                // 这正是异常退出后允许重新启动的场景。
                acquired = true;
            }
            catch (UnauthorizedAccessException)
            {
                mutex.Dispose();
                throw;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }

            ProcessOwnedNames.Add(mutexName);
            return new SingleInstanceGuard(mutexName, mutex);
        }
    }

    /// <summary>释放所有权。可重复调用。</summary>
    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        lock (ProcessGate)
        {
            ProcessOwnedNames.Remove(_name);
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 理论上不会发生：只有持有者能 Release。异常退出路径由系统回收。
        }
        finally
        {
            mutex.Dispose();
        }
    }
}