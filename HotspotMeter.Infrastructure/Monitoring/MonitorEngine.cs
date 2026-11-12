using HotspotMeter.Core.Abstractions;
using HotspotMeter.Core.Models;
using HotspotMeter.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace HotspotMeter.Infrastructure.Monitoring;

/// <summary>
/// 采样引擎：只负责取数据、驱动累积器、转发事件。所有业务判断都在 Core，
/// 因此本类可以被"假检测器 + 假计数器"完整测试。
/// <para>
/// 恢复职责边界：本类负责协调——<see cref="BaselineResumeEvaluator"/> 做纯判定，
/// <see cref="IConnectionContinuityProvider"/> 独立评估连续性证据，累积器执行合并。
/// 同一个 RecoveryId 只会被处理一次，保证幂等。
/// </para>
/// </summary>
public sealed class MonitorEngine : IMonitorEngine
{
    private readonly MonitorOptions _options;
    private readonly HotspotTarget _target;
    private readonly IHotspotDetector _detector;
    private readonly ITrafficCounter _counter;
    private readonly IMonotonicClock _monotonicClock;
    private readonly IWallClock _wallClock;
    private readonly IUptimeProvider _uptime;
    private readonly IConnectionContinuityProvider _continuity;
    private readonly ILogger<MonitorEngine>? _logger;
    private readonly TrafficAccumulator _accumulator;
    private readonly HashSet<string> _processedRecoveryIds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _resumeGate = new(1, 1);

    private CancellationTokenSource? _cancellation;
    private Task? _loopTask;
    private int _running;
    private bool _disposed;

    /// <summary>创建引擎。参数非法时立即抛异常，而不是在循环里悄悄降级。</summary>
    public MonitorEngine(
        MonitorOptions options,
        HotspotTarget target,
        IHotspotDetector detector,
        ITrafficCounter counter,
        IMonotonicClock monotonicClock,
        IWallClock wallClock,
        IUptimeProvider uptime,
        IConnectionContinuityProvider continuity,
        ILogger<MonitorEngine>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(monotonicClock);
        ArgumentNullException.ThrowIfNull(wallClock);
        ArgumentNullException.ThrowIfNull(uptime);
        ArgumentNullException.ThrowIfNull(continuity);

        options.Validate();

        _options = options;
        _target = target;
        _detector = detector;
        _counter = counter;
        _monotonicClock = monotonicClock;
        _wallClock = wallClock;
        _uptime = uptime;
        _continuity = continuity;
        _logger = logger;
        _accumulator = new TrafficAccumulator(target, options);
    }

    /// <inheritdoc />
    public MonitorSnapshot? Current => _accumulator.Current;

    /// <inheritdoc />
    public MonitorState State => _accumulator.State;

    /// <inheritdoc />
    public event EventHandler<MonitorSnapshot>? SnapshotProduced;

    /// <inheritdoc />
    public event EventHandler<MonitorSessionInfo>? SessionStarted;

    /// <inheritdoc />
    public event EventHandler<MonitorSessionInfo>? SessionResumed;

    /// <inheritdoc />
    public event EventHandler<MonitorSessionEndedInfo>? SessionEnded;

    /// <inheritdoc />
    public event EventHandler<BssidChangedInfo>? BssidChanged;

    /// <inheritdoc />
    public event EventHandler<MonitorAnomaly>? AnomalyDetected;

    /// <inheritdoc />
    public event EventHandler<RecoveryCandidate>? RecoveryCandidateDetected;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return Task.CompletedTask;
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => LoopAsync(_cancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
        {
            return;
        }

        var cancellation = _cancellation;
        _cancellation = null;
        cancellation?.Cancel();

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止路径。
            }

            _loopTask = null;
        }

        cancellation?.Dispose();

        // 停止时把会话封存，阶段 3 才有机会保存最后一段流量。
        Publish(_accumulator.Stop(_wallClock.UtcNow));
    }

    /// <inheritdoc />
    public async Task<ResumeResult> ResumeAsync(ResumeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _resumeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HotspotConnection? connection = null;
            InterfaceReading? reading = null;
            var readFailed = false;
            var now = _wallClock.UtcNow;

            try
            {
                connection = await _detector.GetConnectionAsync(context.Baseline.InterfaceGuid, cancellationToken).ConfigureAwait(false);
                if (connection is not null)
                {
                    reading = await _counter.TryReadAsync(context.Baseline.InterfaceGuid, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                readFailed = true;
                PublishAnomaly(AnomalyKind.DetectorFailure, $"恢复时读取 WLAN/计数器失败：{ex.Message}");
            }

            if (readFailed)
            {
                Publish(_accumulator.ResetForResume(context, ResumeOutcome.InterfaceMissing, now));
                return new ResumeResult(
                    ResumeOutcome.InterfaceMissing,
                    null,
                    "恢复时无法读取 WLAN 或计数器，不能确认连续性。",
                    RebootEvidence.None);
            }

            var tickCount64 = _uptime.TickCount64;
            var evaluation = BaselineResumeEvaluator.Evaluate(
                context.Baseline, reading, connection, tickCount64, now, _options.BootEpochTolerance);

            if (evaluation.Outcome != ResumeOutcome.Continued)
            {
                Publish(_accumulator.ResetForResume(context, evaluation.Outcome, now));
                return new ResumeResult(evaluation.Outcome, null, evaluation.Reason, evaluation.Evidence);
            }

            var window = new RecoveryWindow(
                context.Baseline.CapturedAtUtc,
                now,
                context.Baseline.InterfaceGuid,
                context.Baseline.Ssid);
            var assessment = await _continuity.AssessAsync(window, cancellationToken).ConfigureAwait(false);

            var recoveryId = RecoveryId.Create(context.SessionId, context.Baseline);
            var gap = new RecoveryGap(
                recoveryId,
                evaluation.DownloadBytes,
                evaluation.UploadBytes,
                window.FromUtc,
                window.ToUtc,
                assessment.Continuity,
                assessment.EvidenceSummary);

            // 幂等闸门：同一会话 + 同一基线的恢复窗口只处理一次。
            if (!_processedRecoveryIds.Add(recoveryId))
            {
                return new ResumeResult(
                    ResumeOutcome.Continued,
                    gap,
                    "该恢复窗口已处理过（幂等键命中），本次不合并、也不会重复产生持久化记录。",
                    evaluation.Evidence,
                    AlreadyApplied: true);
            }

            var frame = new MonitorFrame(now, _monotonicClock.ElapsedMilliseconds, connection, reading, tickCount64);
            var step = _accumulator.AdoptResumedSession(context, gap, gap.MayMergeIntoSession, frame);

            if (!gap.MayMergeIntoSession)
            {
                step = step with
                {
                    Recovery = new RecoveryCandidate(
                        recoveryId,
                        context.SessionId,
                        gap,
                        assessment.Continuity == RecoveryContinuity.Interrupted
                            ? "有证据表明窗口内连接中断，增量不并入会话。"
                            : "无法证明窗口内连接连续，增量不并入会话。",
                        now),
                };
            }

            Publish(step);
            return new ResumeResult(evaluation.Outcome, gap, evaluation.Reason, evaluation.Evidence);
        }
        finally
        {
            _resumeGate.Release();
        }
    }

    /// <inheritdoc />
    public BaselineState? CaptureBaseline() => _accumulator.CaptureBaseline();

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        var interval = _options.SamplingInterval;

        while (!cancellationToken.IsCancellationRequested)
        {
            var startedAt = _monotonicClock.ElapsedMilliseconds;

            try
            {
                var step = await TickAsync(cancellationToken).ConfigureAwait(false);
                Publish(step);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PublishAnomaly(AnomalyKind.DetectorFailure, $"采样循环异常：{ex.Message}");
            }

            var elapsed = _monotonicClock.ElapsedMilliseconds - startedAt;
            var remaining = interval - TimeSpan.FromMilliseconds(elapsed);
            if (remaining < TimeSpan.FromMilliseconds(1))
            {
                remaining = TimeSpan.FromMilliseconds(1);
            }

            try
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<MonitorStep> TickAsync(CancellationToken cancellationToken)
    {
        HotspotConnection? connection = null;
        InterfaceReading? reading = null;
        var detectorFailed = false;

        try
        {
            connection = await _detector.GetConnectionAsync(_target.InterfaceGuid, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            detectorFailed = true;
            PublishAnomaly(AnomalyKind.DetectorFailure, $"WLAN 查询失败：{ex.Message}");
        }

        if (!detectorFailed && connection is not null)
        {
            try
            {
                reading = await _counter.TryReadAsync(_target.InterfaceGuid, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PublishAnomaly(AnomalyKind.CounterFailure, $"IP Helper 读取失败：{ex.Message}");
            }
        }

        var frame = new MonitorFrame(
            _wallClock.UtcNow,
            _monotonicClock.ElapsedMilliseconds,
            connection,
            reading,
            _uptime.TickCount64,
            detectorFailed);

        return _accumulator.Process(frame);
    }

    private void PublishAnomaly(AnomalyKind kind, string message)
    {
        _logger?.LogWarning("{AnomalyKind}: {Message}", kind, message);
        AnomalyDetected?.Invoke(this, new MonitorAnomaly(kind, message, _wallClock.UtcNow));
    }

    private void Publish(MonitorStep step)
    {
        SnapshotProduced?.Invoke(this, step.Snapshot);

        if (step.SessionStarted is not null)
        {
            SessionStarted?.Invoke(this, step.SessionStarted);
        }

        if (step.SessionResumed is not null)
        {
            SessionResumed?.Invoke(this, step.SessionResumed);
        }

        if (step.SessionEnded is not null)
        {
            SessionEnded?.Invoke(this, step.SessionEnded);
        }

        if (step.BssidChanged is not null)
        {
            BssidChanged?.Invoke(this, step.BssidChanged);
        }

        if (step.Anomaly is not null)
        {
            AnomalyDetected?.Invoke(this, step.Anomaly);
        }

        if (step.Recovery is not null)
        {
            RecoveryCandidateDetected?.Invoke(this, step.Recovery);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _resumeGate.Dispose();
    }
}