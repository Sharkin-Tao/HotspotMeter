using HotspotMeter.Core.Models;

namespace HotspotMeter.Core.Abstractions;

/// <summary>
/// 采样引擎。引擎只负责"取数据、喂帧、发事件"，业务判断在 Core 的累积器中，
/// 因此该接口不暴露任何 Windows 细节。
/// </summary>
public interface IMonitorEngine : IAsyncDisposable
{
    /// <summary>最近一次采样快照；尚未采样时为 <c>null</c>。</summary>
    MonitorSnapshot? Current { get; }

    /// <summary>当前采样状态。</summary>
    MonitorState State { get; }

    /// <summary>每完成一帧采样触发一次。</summary>
    event EventHandler<MonitorSnapshot>? SnapshotProduced;

    /// <summary>目标会话建立时触发。</summary>
    event EventHandler<MonitorSessionInfo>? SessionStarted;

    /// <summary>
    /// 恢复后继续原会话时触发（仅当连续性被证明为 Continuous）。
    /// 未确认的恢复走 <see cref="RecoveryCandidateDetected"/>。
    /// </summary>
    event EventHandler<MonitorSessionInfo>? SessionResumed;

    /// <summary>会话结束时触发（真断开 / 换了 SSID / 换了网卡 / 引擎停止）。</summary>
    event EventHandler<MonitorSessionEndedInfo>? SessionEnded;

    /// <summary>BSSID 变化（漫游）时触发，仅用于诊断，不影响会话。</summary>
    event EventHandler<BssidChangedInfo>? BssidChanged;

    /// <summary>任何被捕获的异常或可疑数据都通过它上报，绝不静默。</summary>
    event EventHandler<MonitorAnomaly>? AnomalyDetected;

    /// <summary>发现无法确认连续性的恢复增量时触发；阶段 3 应单独持久化。</summary>
    event EventHandler<RecoveryCandidate>? RecoveryCandidateDetected;

    /// <summary>启动后台采样循环。</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>停止循环并结束当前会话（原因记为 EngineStopped）。</summary>
    Task StopAsync();

    /// <summary>用持久化基线尝试恢复。阶段 3 在启动时调用。</summary>
    Task<ResumeResult> ResumeAsync(ResumeContext context, CancellationToken cancellationToken);

    /// <summary>导出当前基线，供阶段 3 定期写检查点；没有活动基线时为 <c>null</c>。</summary>
    BaselineState? CaptureBaseline();
}