using System.Diagnostics;
using HotspotMeter.Core.Abstractions;

namespace HotspotMeter.Infrastructure;

/// <summary>
/// 真实时钟实现。速度测量只能用 <see cref="SystemMonotonicClock"/>；
/// 墙钟只用于给样本打时间戳和自然日归属。
/// </summary>
public sealed class SystemMonotonicClock : IMonotonicClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    /// <inheritdoc />
    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;
}

/// <inheritdoc cref="IWallClock"/>
public sealed class SystemWallClock : IWallClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>基于 <see cref="Environment.TickCount64"/> 的开机时长，用于重启判定。</summary>
public sealed class SystemUptimeProvider : IUptimeProvider
{
    /// <inheritdoc />
    public long TickCount64 => Environment.TickCount64;
}