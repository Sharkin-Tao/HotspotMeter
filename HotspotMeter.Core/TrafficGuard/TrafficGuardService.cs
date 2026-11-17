namespace HotspotMeter.Core.TrafficGuard;

/// <summary>守护服务的生命周期状态。暂停与停止的区别：暂停保留基线，停止清空。</summary>
public enum TrafficGuardState
{
    Stopped,
    Running,
    Paused,
}

/// <summary>
/// Traffic Guard 的采样循环。
///
/// 本类只做编排：取目标热点 → 读逐应用累计值 → 算增量 → 判定前后台 → 写样本。
/// 所有平台相关能力（WinRT、进程/窗口查询、SQLite）都通过接口注入，因此可以用
/// 模拟数据测试暂停/恢复、重复启动、网络切换、缺失数据等边界。
///
/// 并发约束：启动/暂停/恢复/停止用同一把锁串行化；采样循环本身只允许一个实例，
/// 重复 Start 不会产生第二个循环。
/// </summary>
public sealed class TrafficGuardService : IAsyncDisposable
{
    private readonly IGuardUsageSource _usageSource;
    private readonly IProcessInspector _processInspector;
    private readonly IGuardSampleSink _sink;
    private readonly IGuardTargetProvider _targetProvider;
    private readonly IGuardDiagnostics _diagnostics;
    private readonly AppIdentityResolver _identityResolver;
    private readonly TrafficDeltaCalculator _calculator;
    private readonly GuardOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateLock = new();

    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private TrafficGuardState _state = TrafficGuardState.Stopped;
    private GuardCaptureResult? _lastCapture;
    private bool _disposed;

    public TrafficGuardService(
        IGuardUsageSource usageSource,
        IProcessInspector processInspector,
        IGuardSampleSink sink,
        IGuardTargetProvider targetProvider,
        IGuardDiagnostics diagnostics,
        GuardOptions? options = null,
        TimeProvider? timeProvider = null,
        AppIdentityResolver? identityResolver = null,
        TrafficDeltaCalculator? calculator = null)
    {
        _usageSource = usageSource ?? throw new ArgumentNullException(nameof(usageSource));
        _processInspector = processInspector ?? throw new ArgumentNullException(nameof(processInspector));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _targetProvider = targetProvider ?? throw new ArgumentNullException(nameof(targetProvider));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _identityResolver = identityResolver ?? new AppIdentityResolver();
        _calculator = calculator ?? new TrafficDeltaCalculator();
        _options = options ?? GuardOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_options.SamplingInterval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "采样周期不能小于 1 秒。");
        }
    }

    public TrafficGuardState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public bool IsRunning => State == TrafficGuardState.Running;

    /// <summary>最近一次采样的结果，供诊断/测试读取；不参与业务判断。</summary>
    public GuardCaptureResult? LastCapture
    {
        get
        {
            lock (_stateLock)
            {
                return _lastCapture;
            }
        }
    }

    /// <summary>启动循环。已在运行时不重复启动；暂停状态会转为运行。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == TrafficGuardState.Running)
            {
                return;
            }

            StartLoopUnderLock();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>暂停：取消当前等待与正在进行的读取，但保留增量基线。</summary>
    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != TrafficGuardState.Running)
            {
                return;
            }

            lock (_stateLock)
            {
                _state = TrafficGuardState.Paused;
            }

            await StopLoopAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>恢复：仅从暂停状态启动；已运行或已停止时不重复启动。</summary>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != TrafficGuardState.Paused)
            {
                return;
            }

            StartLoopUnderLock();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>停止：取消循环并清空基线；再次启动会重新建立基线。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                _state = TrafficGuardState.Stopped;
            }

            await StopLoopAsync().ConfigureAwait(false);
            _calculator.Reset();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 执行一次采样。公开是为了让测试不依赖真实计时器；正常运行由 <see cref="StartAsync"/> 驱动。
    /// 返回结果而不是抛出业务异常：目标未连接、API 不可用都属于正常状态。
    /// </summary>
    public async Task<GuardCaptureResult> CaptureOnceAsync(CancellationToken cancellationToken = default)
    {
        var target = _targetProvider.GetTarget();
        if (target is null)
        {
            _calculator.Reset();
            return Publish(new GuardCaptureResult(false, false, 0, 0, false, "TargetUnavailable"));
        }

        GuardUsageSnapshot snapshot;
        try
        {
            snapshot = await _usageSource.ReadAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _diagnostics.Report($"Traffic Guard 读取逐应用流量失败：{ex.GetType().Name}", ex);
            _calculator.Reset();
            return Publish(new GuardCaptureResult(true, false, 0, 0, false, "ReadFailed"));
        }

        if (!snapshot.IsAvailable)
        {
            // 数据不可用必须显式记录；绝不能用空列表冒充“所有程序 0 流量”。
            _diagnostics.Report($"Traffic Guard 数据不可用：{snapshot.Status}");
            _calculator.Reset();
            return Publish(new GuardCaptureResult(true, false, 0, 0, false, "DataUnavailable"));
        }

        var observations = _processInspector.Capture();
        var processMap = BuildProcessMap(observations);
        var observedAt = _timeProvider.GetUtcNow();
        var savedState = _calculator.CaptureState();
        var samples = new List<GuardTrafficSample>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        var profileChanged = false;

        foreach (var entry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var identity = _identityResolver.ResolveUsage(entry);
            if (identity is null)
            {
                // 空归属 Id 是未归属/系统流量，必须丢弃而不是分摊给任意程序。
                skipped++;
                continue;
            }

            seen.Add(identity.Key);
            var delta = _calculator.Compute(
                target.ProfileKey,
                identity.Key,
                entry.DownloadBytes,
                entry.UploadBytes,
                observedAt,
                _options.GapThreshold);

            profileChanged |= delta.ProfileChanged;
            if (!delta.HasSample)
            {
                continue;
            }

            var processes = processMap.GetValueOrDefault(identity.Key);
            var foreground = processes?.Any(process => process.IsForeground) == true;
            var visible = processes?.Any(process => process.HasVisibleWindow) == true;
            var activity = ProcessStateClassifier.Classify(
                foreground,
                visible,
                delta.DownloadBytes + delta.UploadBytes > 0);

            var flags = delta.QualityFlags;
            if (!identity.IsCertain)
            {
                flags |= GuardQualityFlags.IdentityUncertain;
            }

            samples.Add(new GuardTrafficSample(
                identity.Key,
                identity.DisplayName,
                identity.Kind,
                identity.IsCertain,
                target.ProfileKey,
                delta.FromUtc,
                delta.ToUtc,
                delta.DownloadBytes,
                delta.UploadBytes,
                activity,
                processes?.Count ?? 0,
                flags));
        }

        var removed = _calculator.CompleteCycle(seen);
        if (samples.Count > 0)
        {
            try
            {
                await _sink.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _calculator.RestoreState(savedState);
                throw;
            }
            catch (Exception ex)
            {
                // 写库失败：回滚基线，下一轮从旧基线重算；宁可把区间拉长，也不重复/丢失字节。
                _calculator.RestoreState(savedState);
                _diagnostics.Report($"Traffic Guard 写入样本失败：{ex.GetType().Name}", ex);
                return Publish(new GuardCaptureResult(true, true, 0, skipped, profileChanged, "WriteFailed"));
            }
        }

        var status = samples.Count > 0
            ? "Sampled"
            : profileChanged
                ? "ProfileBaseline"
                : "NoNewTraffic";

        return Publish(new GuardCaptureResult(
            true,
            true,
            samples.Count,
            skipped,
            profileChanged,
            removed > 0 ? $"{status};Removed={removed}" : status));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    private void StartLoopUnderLock()
    {
        var cancellation = new CancellationTokenSource();
        lock (_stateLock)
        {
            _loopCancellation = cancellation;
            _state = TrafficGuardState.Running;
        }

        _loopTask = Task.Run(() => RunLoopAsync(cancellation), CancellationToken.None);
    }

    private async Task StopLoopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? loop;
        lock (_stateLock)
        {
            cancellation = _loopCancellation;
            loop = _loopTask;
            _loopCancellation = null;
            _loopTask = null;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常暂停/停止路径。
            }
        }

        cancellation.Dispose();
    }

    private async Task RunLoopAsync(CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await CaptureOnceAsync(cancellation.Token).ConfigureAwait(false);
                await Task.Delay(_options.SamplingInterval, _timeProvider, cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 暂停/停止。
        }
        catch (Exception ex)
        {
            _diagnostics.Report($"Traffic Guard 循环异常终止：{ex.GetType().Name}", ex);
            lock (_stateLock)
            {
                _state = TrafficGuardState.Stopped;
            }
        }
    }

    private Dictionary<string, List<ProcessObservation>> BuildProcessMap(
        IReadOnlyList<ProcessObservation> observations)
    {
        var map = new Dictionary<string, List<ProcessObservation>>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            var identity = _identityResolver.ResolveProcess(observation);
            if (!map.TryGetValue(identity.Key, out var list))
            {
                list = [];
                map[identity.Key] = list;
            }

            list.Add(observation);
        }

        return map;
    }

    private GuardCaptureResult Publish(GuardCaptureResult result)
    {
        lock (_stateLock)
        {
            _lastCapture = result;
        }

        return result;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}