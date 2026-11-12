namespace HotspotMeter.Core.Abstractions;

/// <summary>
/// 单调时钟（基于 <c>Stopwatch</c>）。速度计算只能用它的差值，绝不能用墙钟。
/// </summary>
public interface IMonotonicClock
{
    /// <summary>自进程启动以来的毫秒数，必然单调不减。</summary>
    long ElapsedMilliseconds { get; }
}

/// <summary>墙钟。只用于给样本打时间戳与自然日归属，不参与速度计算。</summary>
public interface IWallClock
{
    /// <summary>当前 UTC 时间。</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// 系统开机以来的毫秒数（对应 <c>Environment.TickCount64</c>）。
/// 重启判定优先使用它：同一开机内它不会变小。
/// </summary>
public interface IUptimeProvider
{
    /// <summary>开机以来的毫秒数。</summary>
    long TickCount64 { get; }
}