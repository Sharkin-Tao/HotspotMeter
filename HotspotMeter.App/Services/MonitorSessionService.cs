using System.Windows.Threading;
using HotspotMeter.App.Localization;
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
    private bool _historyInitialized;
    private DispatcherTimer? _targetRetryTimer;
    private bool _targetRetryBusy;
    private bool _disposed;

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

        if (_engine is not null)
        {
            StopTargetRetry();
            return;
        }

        try
        {
            var candidates = await _detector.GetCandidatesAsync(cancellationToken);
            var candidate = candidates
                .Where(c => c.IsConnected)
                .OrderByDescending(c => c.SignalQuality)
                .FirstOrDefault();

            if (candidate is null)
            {
                // 开机自启常发生在热点关联之前：不要一判定“没有热点”就永久停摆。
                // 低频重试只在未识别到目标时运行，识别成功后立即停止。
                viewModel.ShowUnavailable(LocalizationService.T("Capsule_WaitingForHotspot"));
                StartTargetRetry();
                return;
            }

            if (!_historyInitialized)
            {
                try
                {
                    await _history.InitializeAsync(cancellationToken);
                    _historyAvailable = true;
                    _historyInitialized = true;
                }
                catch (Exception ex)
                {
                    // 历史库失败不能拖垮实时监控：界面继续显示会话流量，统计窗口随后会报告不可用。
                    // 本进程内不再反复重建 writer，避免每次热点重试都泄漏一个写入器。
                    _historyAvailable = false;
                    _historyInitialized = true;
                    AppLog.Info($"历史库初始化失败：{ex.Message}");
                }
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
            engine.AnomalyDetected += (_, anomaly) => Post(() => _viewModel?.SetWarning(LocalizationService.T(anomaly.Kind switch
            {
                AnomalyKind.DetectorFailure => "Warning_DetectorFailure",
                AnomalyKind.CounterFailure => "Warning_CounterFailure",
                _ => "Warning_ImplausibleDelta",
            })));
            engine.RecoveryCandidateDetected += (_, candidateInfo) =>
                Post(() => _viewModel?.SetWarning(LocalizationService.F("Warning_RecoveryUnconfirmed", candidateInfo.Gap.TotalBytes)));

            _engine = engine;
            await engine.StartAsync(cancellationToken);
            StopTargetRetry();
        }
        catch (Exception ex)
        {
            // 启动失败时 _engine 已经被赋值（事件已挂接），若不清理，
            // 重试定时器会被 "engine is not null" 永久挡住。
            if (_engine is not null)
            {
                try
                {
                    await _engine.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeEx)
                {
                    AppLog.Info($"清理启动失败的监控引擎时出错：{disposeEx.Message}");
                }

                _engine = null;
            }

            viewModel.ShowUnavailable(ex.Message);
            StartTargetRetry();
        }
    }

    private void StartTargetRetry()
    {
        if (_disposed || _targetRetryTimer is not null)
        {
            return;
        }

        _targetRetryTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _targetRetryTimer.Tick += async (_, _) =>
        {
            if (_targetRetryBusy || _engine is not null || _viewModel is null)
            {
                return;
            }

            _targetRetryBusy = true;
            try
            {
                await InitializeAsync(_viewModel);
            }
            catch (Exception ex)
            {
                AppLog.Info($"重新检测热点失败：{ex.Message}");
            }
            finally
            {
                _targetRetryBusy = false;
            }
        };
        _targetRetryTimer.Start();
    }

    private void StopTargetRetry()
    {
        _targetRetryTimer?.Stop();
        _targetRetryTimer = null;
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
                        await ExecuteWithRetryAsync(work, label).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Info($"{label}失败（已重试 3 次）：{ex.Message}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
        }
    }

    private static async Task ExecuteWithRetryAsync(Func<Task> work, string label)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await work().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < 2)
            {
                AppLog.Info($"{label}第 {attempt + 1} 次失败，准备重试：{ex.Message}");
                await Task.Delay(attempt == 0 ? 200 : 1000).ConfigureAwait(false);
            }
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
        _disposed = true;
        StopTargetRetry();

        if (_engine is { } engine)
        {
            try
            {
                await engine.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // 退出路径不再向用户抛异常。
            }

            await engine.DisposeAsync().ConfigureAwait(false);
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
            await tail.ConfigureAwait(false);
        }
        catch
        {
            // 队列内部已记录失败，退出路径不再抛出。
        }

        // 无条件释放：初始化可能在创建 writer 之后失败（_historyAvailable=false），
        // 此时仍持有 WAL 连接与后台写入循环，不能因为标志位假而泄漏。
        await _history.DisposeAsync().ConfigureAwait(false);
        _historyAvailable = false;
    }
}