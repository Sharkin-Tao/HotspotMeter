using System.Windows.Threading;
using HotspotMeter.App.ViewModels;
using HotspotMeter.Core.Models;
using HotspotMeter.Core.Monitoring;
using HotspotMeter.Infrastructure;
using HotspotMeter.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace HotspotMeter.App.Services;

/// <summary>
/// UI 与监控引擎之间的唯一桥梁：负责选择目标热点、启动/停止引擎，
/// 并把引擎事件切回 UI 线程。窗口本身不碰 WLAN 与计数器。
/// </summary>
public sealed class MonitorSessionService : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly WlanHotspotDetector _detector = new();
    private readonly InterfaceTrafficCounter _counter = new();

    private MonitorEngine? _engine;
    private CapsuleViewModel? _viewModel;

    /// <summary>已处理的快照数量，仅用于开发期自检证明"隐藏窗口不影响采样"。</summary>
    public long SnapshotCount { get; private set; }

    public bool IsRunning => _engine is not null;

    public MonitorSessionService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// 目标热点选择（阶段 4 临时策略）：取当前已连接、信号最强的 Wi-Fi。
    /// 正式的"用户选择热点"需要持久化配置，属于后续设置阶段；此处绝不猜测第二个热点。
    /// </summary>
    public async Task InitializeAsync(CapsuleViewModel viewModel, CancellationToken cancellationToken = default)
    {
        _viewModel = viewModel;

        try
        {
            var candidates = await _detector.GetCandidatesAsync(cancellationToken);
            var candidate = candidates
                .Where(c => c.IsConnected)
                .OrderByDescending(c => c.SignalQuality)
                .FirstOrDefault();

            if (candidate is null)
            {
                viewModel.ShowUnavailable("未检测到已连接的 Wi-Fi。请先连接手机热点，再重新启动程序。");
                return;
            }

            var target = new HotspotTarget(
                Guid.NewGuid(),
                candidate.InterfaceGuid,
                candidate.Ssid,
                candidate.Bssid);

            var engine = new MonitorEngine(
                new MonitorOptions(),
                target,
                _detector,
                _counter,
                new SystemMonotonicClock(),
                new SystemWallClock(),
                new SystemUptimeProvider(),
                new NoEvidenceContinuityProvider(),
                NullLogger<MonitorEngine>.Instance);

            engine.SnapshotProduced += (_, snapshot) => Post(() =>
            {
                SnapshotCount++;
                _viewModel?.Apply(snapshot);
            });
            engine.SessionStarted += (_, _) => Post(() => _viewModel?.ApplySessionStarted());
            engine.SessionEnded += (_, info) => Post(() => _viewModel?.ApplySessionEnded(info));
            engine.AnomalyDetected += (_, anomaly) => Post(() => _viewModel?.SetWarning(anomaly.Message));
            engine.RecoveryCandidateDetected += (_, candidateInfo) =>
                Post(() => _viewModel?.SetWarning($"检测到未确认的恢复流量：{candidateInfo.Gap.TotalBytes} 字节"));

            _engine = engine;
            await engine.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            viewModel.ShowUnavailable(ex.Message);
        }
    }

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is { } engine)
        {
            try
            {
                await engine.StopAsync();
            }
            catch
            {
                // 退出路径不再向用户抛异常。
            }

            await engine.DisposeAsync();
            _engine = null;
        }

        _detector.Dispose();
    }
}