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
    private readonly TrafficHistoryStore _history = new();
    private readonly object _historyQueueLock = new();

    private Task _historyTail = Task.CompletedTask;
    private MonitorEngine? _engine;
    private CapsuleViewModel? _viewModel;
    private Guid _profileKey;
    private bool _historyAvailable;

    /// <summary>当前目标网卡 GUID；未识别时为 null。统计窗口用它选 Windows 连接配置。</summary>
    public Guid? TargetInterfaceGuid { get; private set; }

    /// <summary>当前目标 SSID；未识别时为 null。</summary>
    public string? TargetSsid { get; private set; }

    public bool HistoryAvailable => _historyAvailable;

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

            try
            {
                await _history.InitializeAsync(cancellationToken);
                _historyAvailable = true;
            }
            catch (Exception ex)
            {
                // 历史库失败不能拖垮实时监控：界面继续显示会话流量，统计窗口随后会报告不可用。
                _historyAvailable = false;
                AppLog.Info($"历史库初始化失败：{ex.Message}");
            }

            var target = new HotspotTarget(
                TrafficHistoryStore.StableProfileKey(candidate.InterfaceGuid, candidate.Ssid),
                candidate.InterfaceGuid,
                candidate.Ssid,
                candidate.Bssid);
            _profileKey = target.ProfileId;
            TargetInterfaceGuid = candidate.InterfaceGuid;
            TargetSsid = candidate.Ssid;

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

            engine.SnapshotProduced += (_, snapshot) =>
            {
                Post(() =>
                {
                    SnapshotCount++;
                    _viewModel?.Apply(snapshot);
                });

                if (_historyAvailable && snapshot.HasSession && snapshot.SessionId is long coreSessionId)
                {
                    EnqueueHistory(
                        () => _history.RecordAsync(coreSessionId, snapshot, force: false),
                        "历史检查点写入");
                }
            };
            engine.SessionStarted += (_, info) =>
            {
                if (_historyAvailable)
                {
                    EnqueueHistory(
                        () => _history.BeginSessionAsync(
                            _profileKey,
                            info.InterfaceGuid,
                            info.Ssid,
                            info.Bssid,
                            info.SessionId,
                            info.StartedAtUtc),
                        "历史会话创建");
                }

                Post(() => _viewModel?.ApplySessionStarted());
            };
            engine.SessionEnded += (_, info) =>
            {
                if (_historyAvailable)
                {
                    EnqueueHistory(() => _history.EndSessionAsync(info), "历史会话结算");
                }

                Post(() => _viewModel?.ApplySessionEnded(info));
            };
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

    /// <summary>
    /// 历史写入必须严格按事件顺序执行：会话创建 → 检查点 → 结算。
    /// 引擎事件是同步回调，若直接 fire-and-forget，检查点可能先于会话行落库而丢失。
    /// 这里用单条任务链串行化，不阻塞采样与 UI；失败只记日志，不影响实时显示。
    /// </summary>
    private void EnqueueHistory(Func<Task> work, string label)
    {
        lock (_historyQueueLock)
        {
            _historyTail = _historyTail.ContinueWith(
                async _ =>
                {
                    try
                    {
                        await work().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Info($"{label}失败：{ex.Message}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
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

        // 先让队列里已排队的结算/检查点落库，再关闭写入器，避免正常退出丢失最后一段。
        Task tail;
        lock (_historyQueueLock)
        {
            tail = _historyTail;
        }

        try
        {
            await tail;
        }
        catch
        {
            // 队列内部已记录失败，退出路径不再抛出。
        }

        if (_historyAvailable)
        {
            await _history.DisposeAsync();
            _historyAvailable = false;
        }
    }
}