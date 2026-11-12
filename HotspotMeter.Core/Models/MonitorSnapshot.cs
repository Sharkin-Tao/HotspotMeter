namespace HotspotMeter.Core.Models;

/// <summary>
/// 一个采样周期结束后的可观察状态。悬浮窗与统计窗口只依赖它，不依赖引擎内部对象。
/// </summary>
public sealed record MonitorSnapshot(
    MonitorState State,
    bool IsTargetConnected,
    string? Ssid,
    string? Bssid,
    bool HasSession,
    long? SessionId,
    long SessionDownloadBytes,
    long SessionUploadBytes,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    DateTimeOffset TimestampUtc,
    long GapMilliseconds,
    int WakeRebuilds,
    int PendingConfirmFrames,
    bool HasUnmergedRecovery,
    long UnmergedRecoveryBytes,
    string? Warning);