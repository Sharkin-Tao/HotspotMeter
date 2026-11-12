using HotspotMeter.Core.Models;

namespace HotspotMeter.Core.Monitoring;

/// <summary>
/// 会话累积状态机（纯逻辑、无 I/O）。输入一帧原始采样，输出快照与本帧事件。
/// <para>
/// 三条不可违反的规则：
/// 1. 会话流量 = 每帧 delta 的累加，绝不用网卡累计计数器直接当流量；
/// 2. 计数器回退、接口消失、睡眠唤醒、重启后，先重建基线再做"两帧确认"，确认前的增量一律不计；
/// 3. 速度只用单调时钟差计算，墙钟只用于打时间戳。
/// </para>
/// </summary>
public sealed class TrafficAccumulator
{
    private sealed class SessionState
    {
        public SessionState(long id, Guid interfaceGuid, string ssid, string? bssid, DateTimeOffset startedAtUtc)
        {
            Id = id;
            InterfaceGuid = interfaceGuid;
            Ssid = ssid;
            Bssid = bssid;
            StartedAtUtc = startedAtUtc;
        }

        public long Id { get; }

        public Guid InterfaceGuid { get; }

        public string Ssid { get; }

        public string? Bssid { get; set; }

        public DateTimeOffset StartedAtUtc { get; }

        public long DownloadBytes { get; set; }

        public long UploadBytes { get; set; }

        public MonitorSessionInfo ToInfo() => new(Id, InterfaceGuid, Ssid, Bssid, StartedAtUtc, DownloadBytes, UploadBytes);
    }

    private readonly record struct SampleBaseline(
        ulong InOctets,
        ulong OutOctets,
        ulong InterfaceLuid,
        long MonotonicMilliseconds,
        DateTimeOffset AtUtc,
        long TickCount64);

    private readonly HotspotTarget _target;
    private readonly MonitorOptions _options;

    private SessionState? _session;
    private SampleBaseline? _baseline;
    private MonitorState _state = MonitorState.Stopped;
    private int _wakeRebuilds;
    private int _pendingConfirm;
    private long _nextSessionId = 1;
    private bool _hasUnmergedRecovery;
    private long _unmergedRecoveryBytes;
    private long _lastGapMilliseconds;
    private double _downloadBytesPerSecond;
    private double _uploadBytesPerSecond;
    private string? _warning;

    /// <summary>创建累积器。目标与参数在生命周期内不变。</summary>
    public TrafficAccumulator(HotspotTarget target, MonitorOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _target = target;
        _options = options;
    }

    /// <summary>最近一次处理后的快照；未处理过任何帧时为 <c>null</c>。</summary>
    public MonitorSnapshot? Current { get; private set; }

    /// <summary>当前状态（供测试与诊断）。</summary>
    public MonitorState State => _state;

    /// <summary>处理一帧采样。</summary>
    public MonitorStep Process(in MonitorFrame frame)
    {
        MonitorSessionInfo? started = null;
        MonitorSessionEndedInfo? ended = null;
        BssidChangedInfo? bssidChanged = null;
        MonitorAnomaly? anomaly = null;
        _warning = null;
        _lastGapMilliseconds = 0;

        // WLAN 查询失败：只是"暂时看不到"，绝不能当成"热点断开"去结束会话。
        if (frame.DetectorFailed)
        {
            PauseWithoutEnd(MonitorState.InterfaceMissing, "WLAN 查询失败，采样暂停，会话保留。");
            return BuildStep(frame, started, null, ended, bssidChanged, anomaly);
        }

        var match = HotspotMatcher.Match(_target, frame.Connection);

        if (match != HotspotMatchResult.Matched)
        {
            ended = match switch
            {
                HotspotMatchResult.NoConnection => EndSession(frame.TimestampUtc, SessionEndReason.Disconnected),
                HotspotMatchResult.SsidMismatch => EndSession(frame.TimestampUtc, SessionEndReason.SsidChanged),
                HotspotMatchResult.InterfaceMismatch => EndSession(frame.TimestampUtc, SessionEndReason.InterfaceChanged),
                _ => null,
            };

            _baseline = null;
            _pendingConfirm = 0;
            _state = MonitorState.Running;
            ZeroRates();
            return BuildStep(frame, started, null, ended, bssidChanged, anomaly);
        }

        var connection = frame.Connection!;
        if (_session is null)
        {
            _session = new SessionState(_nextSessionId++, connection.InterfaceGuid, connection.Ssid, connection.Bssid, frame.TimestampUtc);
            started = _session.ToInfo();
        }
        else if (!string.Equals(_session.Bssid, connection.Bssid, StringComparison.Ordinal))
        {
            // 漫游只产生诊断事件，不结束会话、不清零累计。
            bssidChanged = new BssidChangedInfo(_session.Id, connection.InterfaceGuid, _session.Bssid, connection.Bssid, frame.TimestampUtc);
            _session.Bssid = connection.Bssid;
        }

        if (frame.Reading is null)
        {
            PauseWithoutEnd(MonitorState.InterfaceMissing, "接口暂时不可用，已暂停累计。");
        }
        else
        {
            ProcessReading(frame, frame.Reading, ref anomaly);
        }

        return BuildStep(frame, started, null, ended, bssidChanged, anomaly);
    }

    /// <summary>停止引擎：结束当前会话（原因 EngineStopped），状态置为 Stopped。</summary>
    public MonitorStep Stop(DateTimeOffset nowUtc)
    {
        var ended = EndSession(nowUtc, SessionEndReason.EngineStopped);
        _baseline = null;
        _pendingConfirm = 0;
        _state = MonitorState.Stopped;
        ZeroRates();

        var snapshot = new MonitorSnapshot(
            _state, false, null, null, false, null, 0, 0, 0, 0, nowUtc, 0,
            _wakeRebuilds, _pendingConfirm, _hasUnmergedRecovery, _unmergedRecoveryBytes, null);
        Current = snapshot;
        return new MonitorStep(snapshot, SessionEnded: ended);
    }

    /// <summary>
    /// 恢复成功后接管原会话：恢复 Id 与累计，用当前计数重建基线。
    /// <paramref name="mergeGap"/> 只有在连续性被证明后才为 true。
    /// </summary>
    public MonitorStep AdoptResumedSession(ResumeContext context, RecoveryGap gap, bool mergeGap, in MonitorFrame frame)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gap);
        if (frame.Connection is null || frame.Reading is null)
        {
            throw new InvalidOperationException("接管会话需要有效的连接与读数。");
        }

        _session = new SessionState(
            context.SessionId,
            frame.Connection.InterfaceGuid,
            frame.Connection.Ssid,
            frame.Connection.Bssid,
            context.SessionStartedAtUtc)
        {
            DownloadBytes = context.SessionDownloadBytes,
            UploadBytes = context.SessionUploadBytes,
        };

        if (mergeGap)
        {
            _session.DownloadBytes += gap.DownloadBytes;
            _session.UploadBytes += gap.UploadBytes;
        }
        else
        {
            _hasUnmergedRecovery = true;
            _unmergedRecoveryBytes += gap.TotalBytes;
        }

        var counters = frame.Reading.Counters;
        _baseline = new SampleBaseline(
            counters.InOctets, counters.OutOctets, frame.Reading.Key.InterfaceLuid,
            frame.MonotonicMilliseconds, frame.TimestampUtc, frame.TickCount64);
        _pendingConfirm = 0;
        _state = MonitorState.Running;
        _nextSessionId = Math.Max(_nextSessionId, context.SessionId + 1);
        ZeroRates();

        return BuildStep(frame, null, mergeGap ? _session.ToInfo() : null, null, null, null);
    }

    /// <summary>恢复失败时清空会话与基线，并标记对应状态；下一次正常采样会开新会话。</summary>
    public MonitorStep ResetForResume(ResumeOutcome outcome, DateTimeOffset nowUtc)
    {
        _session = null;
        _baseline = null;
        _pendingConfirm = 0;
        ZeroRates();
        _state = outcome switch
        {
            ResumeOutcome.Rebooted => MonitorState.Rebooted,
            ResumeOutcome.InterfaceMissing => MonitorState.InterfaceMissing,
            ResumeOutcome.CounterReset => MonitorState.CounterReset,
            _ => MonitorState.Running,
        };
        _warning = outcome switch
        {
            ResumeOutcome.Rebooted => "判定为重启过，禁止跨重启恢复流量。",
            ResumeOutcome.InterfaceMissing => "恢复时接口不存在，等待重新连接。",
            ResumeOutcome.CounterReset => "恢复时计数器已回退，旧基线作废。",
            ResumeOutcome.InterfaceChanged => "恢复时接口已变化，旧会话不再延续。",
            ResumeOutcome.NotOnTarget => "恢复时未连接目标热点，旧会话不再延续。",
            _ => null,
        };

        var snapshot = new MonitorSnapshot(
            _state, false, null, null, false, null, 0, 0, 0, 0, nowUtc, 0,
            _wakeRebuilds, _pendingConfirm, _hasUnmergedRecovery, _unmergedRecoveryBytes, _warning);
        Current = snapshot;
        return new MonitorStep(snapshot);
    }

    /// <summary>导出当前基线供阶段 3 写检查点；没有活动会话或基线时为 <c>null</c>。</summary>
    public BaselineState? CaptureBaseline()
    {
        if (_baseline is null || _session is null)
        {
            return null;
        }

        var b = _baseline.Value;
        return new BaselineState(
            _session.InterfaceGuid,
            b.InterfaceLuid,
            b.InOctets,
            b.OutOctets,
            b.AtUtc,
            b.TickCount64,
            BootSignatures.ComputeBootEpoch(b.AtUtc, b.TickCount64),
            _session.Ssid,
            _session.Bssid);
    }

    private void ProcessReading(in MonitorFrame frame, InterfaceReading reading, ref MonitorAnomaly? anomaly)
    {
        var counters = reading.Counters;

        if (_baseline is null)
        {
            var recovering = _state is MonitorState.InterfaceMissing
                or MonitorState.CounterReset
                or MonitorState.Suspended
                or MonitorState.Rebooted;

            _baseline = new SampleBaseline(
                counters.InOctets, counters.OutOctets, reading.Key.InterfaceLuid,
                frame.MonotonicMilliseconds, frame.TimestampUtc, frame.TickCount64);
            _pendingConfirm = recovering ? _options.RecoveryConfirmFrames : 0;
            _state = MonitorState.Running;
            ZeroRates();
            return;
        }

        var baseline = _baseline.Value;
        var gapMilliseconds = frame.MonotonicMilliseconds - baseline.MonotonicMilliseconds;
        _lastGapMilliseconds = gapMilliseconds;

        var longGap = gapMilliseconds > _options.SamplingInterval.TotalMilliseconds * _options.SuspendedGapMultiplier;
        if (longGap)
        {
            _wakeRebuilds++;
            Rebaseline(frame, reading);
            _pendingConfirm = _options.RecoveryConfirmFrames;
            _state = MonitorState.Suspended;
            ZeroRates();
            _warning = $"检测到 {gapMilliseconds} ms 长暂停（睡眠或进程挂起），已重建基线。";
            return;
        }

        if (gapMilliseconds < 0 || counters.InOctets < baseline.InOctets || counters.OutOctets < baseline.OutOctets)
        {
            Rebaseline(frame, reading);
            _pendingConfirm = _options.RecoveryConfirmFrames;
            _state = MonitorState.CounterReset;
            ZeroRates();
            _warning = "计数器回退，已重建基线。";
            return;
        }

        if (_pendingConfirm > 0)
        {
            _pendingConfirm--;
            Rebaseline(frame, reading);
            _state = MonitorState.Running;
            ZeroRates();
            return;
        }

        var downloadDelta = (long)(counters.InOctets - baseline.InOctets);
        var uploadDelta = (long)(counters.OutOctets - baseline.OutOctets);
        var gapSeconds = gapMilliseconds / 1000.0;

        if (_options.Plausibility != PlausibilityMode.Off
            && frame.Connection is { HasLinkSpeed: true } connection
            && downloadDelta + uploadDelta > ComputeLimit(connection.MaxLinkSpeedBps, gapSeconds))
        {
            var limit = ComputeLimit(connection.MaxLinkSpeedBps, gapSeconds);
            anomaly = new MonitorAnomaly(
                AnomalyKind.ImplausibleDelta,
                $"单帧增量 {downloadDelta + uploadDelta} B 超过链路速率推算上限 {limit} B。",
                frame.TimestampUtc,
                downloadDelta + uploadDelta,
                limit,
                connection.MaxLinkSpeedBps);

            if (_options.Plausibility == PlausibilityMode.Drop)
            {
                Rebaseline(frame, reading);
                ZeroRates();
                _state = MonitorState.Running;
                _warning = "可疑增量已按策略丢弃。";
                return;
            }
        }

        if (_session is not null)
        {
            _session.DownloadBytes += downloadDelta;
            _session.UploadBytes += uploadDelta;
        }

        _downloadBytesPerSecond = gapSeconds > 0 ? downloadDelta / gapSeconds : 0;
        _uploadBytesPerSecond = gapSeconds > 0 ? uploadDelta / gapSeconds : 0;
        Rebaseline(frame, reading);
        _state = MonitorState.Running;
    }

    private long ComputeLimit(ulong linkSpeedBps, double gapSeconds)
    {
        var limit = linkSpeedBps / 8.0 * gapSeconds * _options.PlausibilityRateMultiplier + _options.PlausibilitySlackBytes;
        return limit >= long.MaxValue ? long.MaxValue : (long)limit;
    }

    private void Rebaseline(in MonitorFrame frame, InterfaceReading reading)
        => _baseline = new SampleBaseline(
            reading.Counters.InOctets,
            reading.Counters.OutOctets,
            reading.Key.InterfaceLuid,
            frame.MonotonicMilliseconds,
            frame.TimestampUtc,
            frame.TickCount64);

    private void PauseWithoutEnd(MonitorState state, string warning)
    {
        _baseline = null;
        _pendingConfirm = 0;
        _state = state;
        _warning = warning;
        ZeroRates();
    }

    private MonitorSessionEndedInfo? EndSession(DateTimeOffset endedAtUtc, SessionEndReason reason)
    {
        if (_session is null)
        {
            return null;
        }

        var session = _session;
        _session = null;
        return new MonitorSessionEndedInfo(
            session.Id,
            endedAtUtc,
            session.DownloadBytes,
            session.UploadBytes,
            reason,
            session.InterfaceGuid,
            session.Ssid);
    }

    private void ZeroRates()
    {
        _downloadBytesPerSecond = 0;
        _uploadBytesPerSecond = 0;
    }

    private MonitorStep BuildStep(
        in MonitorFrame frame,
        MonitorSessionInfo? started,
        MonitorSessionInfo? resumed,
        MonitorSessionEndedInfo? ended,
        BssidChangedInfo? bssidChanged,
        MonitorAnomaly? anomaly)
    {
        var connected = frame.Connection is not null
            && frame.Connection.InterfaceGuid == _target.InterfaceGuid
            && string.Equals(frame.Connection.Ssid, _target.Ssid, StringComparison.Ordinal);

        var snapshot = new MonitorSnapshot(
            _state,
            connected,
            frame.Connection?.Ssid,
            frame.Connection?.Bssid,
            _session is not null,
            _session?.Id,
            _session?.DownloadBytes ?? 0,
            _session?.UploadBytes ?? 0,
            _downloadBytesPerSecond,
            _uploadBytesPerSecond,
            frame.TimestampUtc,
            _lastGapMilliseconds,
            _wakeRebuilds,
            _pendingConfirm,
            _hasUnmergedRecovery,
            _unmergedRecoveryBytes,
            _warning);

        Current = snapshot;
        return new MonitorStep(snapshot, started, resumed, ended, bssidChanged, anomaly);
    }
}