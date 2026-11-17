namespace HotspotMeter.Core.TrafficGuard;

/// <summary>
/// 把“Windows 返回的程序累计字节”转换成“本段增量”。
///
/// 必须处理的边界：
/// 1. 第一次看到某程序：只建立基线，不产出增量；
/// 2. 同一时刻重复采样：不产出增量，避免重复累计；
/// 3. 累计值回退（计数器重置/跨天/网络重连）：重建基线，绝不产出负数或把回退量当流量；
/// 4. ProfileKey 变化：清空全部基线，新网络从 0 开始，不能把两个网络拼在一起；
/// 5. 某个程序从 API 列表消失：删除其基线，避免下次出现时把空档流量算到一个长区间；
/// 6. 时间间隔明显超过采样周期：标 <see cref="GuardQualityFlags.IntervalGap"/>，如实说明归属不精确。
///
/// 本类不读时钟、不写数据库、不判断前后台，只做纯增量运算，便于用模拟数据测试。
/// </summary>
public sealed class TrafficDeltaCalculator
{
    private readonly Dictionary<string, Baseline> _baselines = new(StringComparer.Ordinal);
    private string? _profileKey;

    /// <summary>当前网络身份，供诊断使用。</summary>
    public string? ProfileKey => _profileKey;

    /// <summary>
    /// 计算一个程序的增量。调用方必须按 ProfileKey 分组后再调用；
    /// 若 ProfileKey 变化，本方法会自动清空旧基线并返回基线结果。
    /// </summary>
    public DeltaComputation Compute(
        string profileKey,
        string appKey,
        long cumulativeDownloadBytes,
        long cumulativeUploadBytes,
        DateTimeOffset observedAtUtc,
        TimeSpan gapThreshold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        if (cumulativeDownloadBytes < 0 || cumulativeUploadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cumulativeDownloadBytes),
                "累计字节数不能为负；负数说明数据源已损坏。");
        }

        var profileChanged = false;
        if (!string.Equals(_profileKey, profileKey, StringComparison.Ordinal))
        {
            _baselines.Clear();
            _profileKey = profileKey;
            profileChanged = true;
        }

        if (!_baselines.TryGetValue(appKey, out var baseline))
        {
            _baselines[appKey] = new Baseline(
                cumulativeDownloadBytes,
                cumulativeUploadBytes,
                observedAtUtc);
            return DeltaComputation.Baseline(profileChanged);
        }

        if (observedAtUtc <= baseline.ObservedAtUtc)
        {
            // 同一时刻的重复采样，不能产生第二份增量。
            return DeltaComputation.Duplicate(profileChanged);
        }

        if (cumulativeDownloadBytes < baseline.DownloadBytes ||
            cumulativeUploadBytes < baseline.UploadBytes)
        {
            // 两个方向任一回退都重建整个基线：只重置一个方向会把另一方向的旧累计混进新周期。
            _baselines[appKey] = new Baseline(
                cumulativeDownloadBytes,
                cumulativeUploadBytes,
                observedAtUtc);
            return DeltaComputation.CounterReset(profileChanged);
        }

        var download = cumulativeDownloadBytes - baseline.DownloadBytes;
        var upload = cumulativeUploadBytes - baseline.UploadBytes;
        var gap = observedAtUtc - baseline.ObservedAtUtc > gapThreshold;

        _baselines[appKey] = new Baseline(
            cumulativeDownloadBytes,
            cumulativeUploadBytes,
            observedAtUtc);

        if (download == 0 && upload == 0)
        {
            return DeltaComputation.NoChange(profileChanged);
        }

        return new DeltaComputation(
            HasSample: true,
            DownloadBytes: download,
            UploadBytes: upload,
            FromUtc: baseline.ObservedAtUtc,
            ToUtc: observedAtUtc,
            QualityFlags: gap ? GuardQualityFlags.IntervalGap : GuardQualityFlags.None,
            ProfileChanged: profileChanged);
    }

    /// <summary>
    /// 一轮采样结束后调用：删除本轮未出现在 API 中的程序基线。
    /// 这样程序被 API 临时遗漏后再次出现时只会重建基线，不会把遗漏期间的流量一次性算入。
    /// </summary>
    /// <returns>被移除的基线数量。</returns>
    public int CompleteCycle(IEnumerable<string> seenAppKeys)
    {
        ArgumentNullException.ThrowIfNull(seenAppKeys);
        var seen = new HashSet<string>(seenAppKeys, StringComparer.Ordinal);
        var stale = _baselines.Keys.Where(key => !seen.Contains(key)).ToArray();
        foreach (var key in stale)
        {
            _baselines.Remove(key);
        }

        return stale.Length;
    }

    /// <summary>清空全部基线（目标断开、API 失败、恢复后重新开始时使用）。</summary>
    public void Reset()
    {
        _baselines.Clear();
        _profileKey = null;
    }

    /// <summary>
    /// 复制当前基线。服务在写库前保存它，写库失败时恢复，保证下一轮从旧基线重新计算，
    /// 而不是把这一轮已经推进的基线当成“已保存”，造成字节静默丢失。
    /// </summary>
    public CalculatorState CaptureState()
    {
        return new CalculatorState(
            _profileKey,
            new Dictionary<string, Baseline>(_baselines, StringComparer.Ordinal));
    }

    public void RestoreState(CalculatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _profileKey = state.ProfileKey;
        _baselines.Clear();
        foreach (var pair in state.Baselines)
        {
            _baselines[pair.Key] = pair.Value;
        }
    }

    /// <summary>一条累计基线。公开类型只为让快照返回值可访问，不参与数据库持久化。</summary>
    public sealed record Baseline(
        long DownloadBytes,
        long UploadBytes,
        DateTimeOffset ObservedAtUtc);

    /// <summary>基线快照。只供本类与服务恢复使用，不参与持久化。</summary>
    public sealed record CalculatorState(
        string? ProfileKey,
        IReadOnlyDictionary<string, Baseline> Baselines);
}

/// <summary>单个程序一次增量的结果。Baseline/Duplicate/CounterReset 都不可持久化为流量。</summary>
public readonly record struct DeltaComputation(
    bool HasSample,
    long DownloadBytes,
    long UploadBytes,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    GuardQualityFlags QualityFlags,
    bool ProfileChanged)
{
    public static DeltaComputation Baseline(bool profileChanged) => new(
        false, 0, 0, default, default,
        GuardQualityFlags.Baseline | (profileChanged ? GuardQualityFlags.ProfileChanged : GuardQualityFlags.None),
        profileChanged);

    public static DeltaComputation Duplicate(bool profileChanged) => new(
        false, 0, 0, default, default,
        GuardQualityFlags.Baseline | (profileChanged ? GuardQualityFlags.ProfileChanged : GuardQualityFlags.None),
        profileChanged);

    public static DeltaComputation CounterReset(bool profileChanged) => new(
        false, 0, 0, default, default,
        GuardQualityFlags.CounterReset | (profileChanged ? GuardQualityFlags.ProfileChanged : GuardQualityFlags.None),
        profileChanged);

    public static DeltaComputation NoChange(bool profileChanged) => new(
        false, 0, 0, default, default,
        GuardQualityFlags.None,
        profileChanged);
}