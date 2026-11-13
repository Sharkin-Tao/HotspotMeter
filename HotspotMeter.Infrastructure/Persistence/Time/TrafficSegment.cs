namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 一个已按本地自然日切分的流量片段。
///
/// 不变量：<see cref="FromUtc"/> &lt; <see cref="ToUtc"/>，所有数值字段非负；
/// 同一区间的所有片段首尾相接，字节与连接毫秒之和等于输入总量。
/// </summary>
public readonly record struct TrafficSegment(
    string LocalDate,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long DownloadBytes,
    long UploadBytes,
    long ConnectedMilliseconds);