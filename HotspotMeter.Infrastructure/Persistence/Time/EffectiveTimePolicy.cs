namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>有效时间当前处于哪种时钟调整状态。</summary>
public enum ClockAdjustmentState
{
    /// <summary>墙钟与单调时钟一致，没有正在进行的调整。</summary>
    None,

    /// <summary>墙钟落后于有效时间（回拨或时钟变慢），正在用单调时钟维持前进。</summary>
    Backward,

    /// <summary>墙钟领先超过容差（前跳或校时），有效时间刚被重新同步。</summary>
    Forward,
}

/// <summary>
/// 把“墙钟 + 单调时钟”转换为单调不减的“有效 UTC”。
///
/// 为什么不能直接用 DateTimeOffset.UtcNow 做时间戳：
/// NTP 校时、手动改时间、时区/夏令时切换都会让墙钟回拨或前跳。直接用墙钟会产生
/// 倒置区间、重复日期，甚至同一段流量被记两次。这里用单调时钟维持前进方向，
/// 墙钟只负责在偏差超过容差时重新校准。
///
/// 计数语义是“事件级”而不是“采样级”：一次持续 30 分钟的回拨只算 1 次调整，
/// 而不是 1800 次；墙钟恢复正常（进入 <see cref="ClockAdjustmentState.None"/>）后
/// 再次发生回拨，才会记为第 2 次。
/// </summary>
public sealed class EffectiveTimePolicy
{
    /// <summary>墙钟与有效时间的偏差超过该值才视为一次调整，吸收 NTP 的小幅抖动。</summary>
    public const long ClockJumpToleranceMilliseconds = 2_000;

    private const long ToleranceTicks = ClockJumpToleranceMilliseconds * TimeSpan.TicksPerMillisecond;

    private long _lastEffectiveTicks;
    private long _lastMonotonic;
    private long _forwardAdjustments;
    private long _backwardAdjustments;
    private long _monotonicRegressions;

    /// <param name="wallUtc">初始墙钟时间，内部会规范化为 UTC。</param>
    /// <param name="monotonicMilliseconds">
    /// 任意单调时钟读数（允许为负，只有差值有意义）。
    /// </param>
    public EffectiveTimePolicy(DateTimeOffset wallUtc, long monotonicMilliseconds)
    {
        _lastEffectiveTicks = wallUtc.ToUniversalTime().UtcTicks;
        _lastMonotonic = monotonicMilliseconds;
    }

    /// <summary>当前有效 UTC。</summary>
    public DateTimeOffset Current => new(_lastEffectiveTicks, TimeSpan.Zero);

    /// <summary>当前是否处于一次持续的时钟调整事件中。</summary>
    public ClockAdjustmentState AdjustmentState { get; private set; }

    /// <summary>墙钟前跳事件次数（持续前跳只算一次）。</summary>
    public long ForwardAdjustmentCount => _forwardAdjustments;

    /// <summary>墙钟回拨事件次数（持续回拨只算一次）。</summary>
    public long BackwardAdjustmentCount => _backwardAdjustments;

    /// <summary>单调时钟回退事件次数（异常防御，每次发生都计一次）。</summary>
    public long MonotonicRegressionCount => _monotonicRegressions;

    /// <summary>三类事件之和；求和同样使用受检运算。</summary>
    public long ClockAdjustmentCount
    {
        get
        {
            checked
            {
                return _forwardAdjustments + _backwardAdjustments + _monotonicRegressions;
            }
        }
    }

    /// <summary>
    /// 推进一步并返回有效 UTC。保证返回值单调不减；越界或算术溢出抛
    /// <see cref="OverflowException"/>，绝不返回一个错误的时间。
    /// </summary>
    public DateTimeOffset Advance(DateTimeOffset wallUtc, long monotonicMilliseconds)
    {
        var wallTicks = wallUtc.ToUniversalTime().UtcTicks;

        long deltaMilliseconds;
        try
        {
            deltaMilliseconds = checked(monotonicMilliseconds - _lastMonotonic);
        }
        catch (OverflowException exception)
        {
            throw new OverflowException("单调时钟读数差值超出 Int64 表示范围。", exception);
        }

        if (deltaMilliseconds < 0)
        {
            // 进程内单调时钟不应回退；若发生，保持有效时间不动并单独计数，
            // 不用墙钟去“修正”会话时长，避免把异常放大成跳变。
            _monotonicRegressions = checked(_monotonicRegressions + 1);
            AdjustmentState = ClockAdjustmentState.None;
            _lastMonotonic = monotonicMilliseconds;
            return new DateTimeOffset(_lastEffectiveTicks, TimeSpan.Zero);
        }

        long candidate;
        try
        {
            candidate = checked(_lastEffectiveTicks + (deltaMilliseconds * TimeSpan.TicksPerMillisecond));
        }
        catch (OverflowException exception)
        {
            throw new OverflowException("有效时间推进超出 DateTimeOffset 可表示范围。", exception);
        }

        if (candidate < DateTimeOffset.MinValue.UtcTicks || candidate > DateTimeOffset.MaxValue.UtcTicks)
        {
            throw new OverflowException("有效时间推进超出 DateTimeOffset 可表示范围。");
        }

        // 两个时间点都落在 DateTime 的 tick 范围内，差值不会溢出 Int64。
        var driftTicks = wallTicks - candidate;
        var desired = driftTicks > ToleranceTicks
            ? ClockAdjustmentState.Forward
            : driftTicks < -ToleranceTicks
                ? ClockAdjustmentState.Backward
                : ClockAdjustmentState.None;

        if (desired != AdjustmentState)
        {
            if (desired == ClockAdjustmentState.Forward)
            {
                _forwardAdjustments = checked(_forwardAdjustments + 1);
            }
            else if (desired == ClockAdjustmentState.Backward)
            {
                _backwardAdjustments = checked(_backwardAdjustments + 1);
            }

            AdjustmentState = desired;
        }

        // 有效时间永远按单调增量推进：墙钟前跳只计事件，不把会话时长一起“拉长”。
        var effective = candidate;
        if (effective < _lastEffectiveTicks)
        {
            effective = _lastEffectiveTicks;
        }

        _lastEffectiveTicks = effective;
        _lastMonotonic = monotonicMilliseconds;
        return new DateTimeOffset(effective, TimeSpan.Zero);
    }
}