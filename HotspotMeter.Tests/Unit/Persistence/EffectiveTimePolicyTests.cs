using HotspotMeter.Infrastructure.Persistence;

namespace HotspotMeter.Tests.Unit.Persistence;

/// <summary>
/// EffectiveTimePolicy 的状态机测试：事件级计数、容差、单调回退与溢出。
/// </summary>
public class EffectiveTimePolicyTests
{
    private static readonly DateTimeOffset Wall = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InitialState_IsNoneWithZeroCounters()
    {
        var policy = new EffectiveTimePolicy(Wall, 1000);

        Assert.Equal(ClockAdjustmentState.None, policy.AdjustmentState);
        Assert.Equal(Wall, policy.Current);
        Assert.Equal(0, policy.ClockAdjustmentCount);
        Assert.Equal(0, policy.ForwardAdjustmentCount);
        Assert.Equal(0, policy.BackwardAdjustmentCount);
        Assert.Equal(0, policy.MonotonicRegressionCount);
    }

    [Fact]
    public void NormalAdvance_FollowsMonotonicDelta()
    {
        var policy = new EffectiveTimePolicy(Wall, 0);

        var result = policy.Advance(Wall.AddMilliseconds(1000), 1000);

        Assert.Equal(Wall.AddMilliseconds(1000), result);
        Assert.Equal(Wall.AddMilliseconds(1000), policy.Current);
        Assert.Equal(ClockAdjustmentState.None, policy.AdjustmentState);
        Assert.Equal(0, policy.ClockAdjustmentCount);
    }

    [Fact]
    public void BackwardJump_IsCountedAsSingleEvent()
    {
        var policy = new EffectiveTimePolicy(Wall, 0);
        var back = Wall.AddHours(-1);

        var result = policy.Advance(back, 1000);

        Assert.Equal(Wall.AddMilliseconds(1000), result);
        Assert.Equal(ClockAdjustmentState.Backward, policy.AdjustmentState);
        Assert.Equal(1, policy.BackwardAdjustmentCount);
        Assert.Equal(1, policy.ClockAdjustmentCount);
    }

    [Fact]
    public void ContinuousBackwardAdjustment_HundredSamplesCountOnce()
    {
        var policy = new EffectiveTimePolicy(Wall, 0);
        for (var i = 0; i < 100; i++)
        {
            policy.Advance(Wall.AddSeconds(-i), 1000 + i * 1000L);
        }

        Assert.Equal(1, policy.BackwardAdjustmentCount);
        Assert.Equal(1, policy.ClockAdjustmentCount);
    }

    [Fact]
    public void RecoveryThenSecondBackward_CountsTwice()
    {
        var policy = new EffectiveTimePolicy(Wall, 0);

        policy.Advance(Wall.AddHours(-1), 1000);
        var recovered = policy.Current;
        policy.Advance(recovered, 2000);
        Assert.Equal(ClockAdjustmentState.None, policy.AdjustmentState);

        policy.Advance(recovered.AddHours(-2), 3000);

        Assert.Equal(2, policy.BackwardAdjustmentCount);
        Assert.Equal(2, policy.ClockAdjustmentCount);
    }

    [Fact]
    public void ForwardJump_IsCountedAsSingleEvent()
    {
        var policy = new EffectiveTimePolicy(Wall, 0);

        var result = policy.Advance(Wall.AddHours(2), 1000);

        Assert.Equal(Wall.AddMilliseconds(1000), result);
        Assert.Equal(ClockAdjustmentState.Forward, policy.AdjustmentState);
        Assert.Equal(1, policy.ForwardAdjustmentCount);
        Assert.Equal(1, policy.ClockAdjustmentCount);
    }

    [Fact]
    public void ContinuousForwardAdjustment_CountsOnce_ThenRecoveryCountsAgain()
    {
        var policy = new EffectiveTimePolicy(Wall, 0);

        policy.Advance(Wall.AddHours(2), 1000);
        policy.Advance(Wall.AddHours(2).AddSeconds(1), 2000);
        policy.Advance(Wall.AddHours(2).AddSeconds(2), 3000);
        Assert.Equal(1, policy.ForwardAdjustmentCount);

        policy.Advance(policy.Current, 4000);
        Assert.Equal(0, policy.ClockAdjustmentCount - policy.ForwardAdjustmentCount);

        policy.Advance(policy.Current.AddHours(1), 5000);
        Assert.Equal(2, policy.ForwardAdjustmentCount);
    }

    [Fact]
    public void DriftExactlyAtTolerance_IsNotAnAdjustment()
    {
        var policy = new EffectiveTimePolicy(Wall, 0);
        var candidate = Wall.AddMilliseconds(1000);

        policy.Advance(candidate.AddMilliseconds(EffectiveTimePolicy.ClockJumpToleranceMilliseconds), 1000);

        Assert.Equal(ClockAdjustmentState.None, policy.AdjustmentState);
        Assert.Equal(0, policy.ClockAdjustmentCount);
    }

    [Fact]
    public void DriftOneMillisecondBeyondTolerance_IsAnAdjustment()
    {
        var policy = new EffectiveTimePolicy(Wall, 0);
        var candidate = Wall.AddMilliseconds(1000);

        policy.Advance(candidate.AddMilliseconds(EffectiveTimePolicy.ClockJumpToleranceMilliseconds + 1), 1000);

        Assert.Equal(ClockAdjustmentState.Forward, policy.AdjustmentState);
        Assert.Equal(1, policy.ForwardAdjustmentCount);
    }

    [Fact]
    public void MonotonicRegression_IsCountedAndDoesNotMoveEffectiveTime()
    {
        var policy = new EffectiveTimePolicy(Wall, 5000);
        var before = policy.Current;

        var result = policy.Advance(Wall, 4000);

        Assert.Equal(before, result);
        Assert.Equal(1, policy.MonotonicRegressionCount);
        Assert.Equal(0, policy.ForwardAdjustmentCount);
        Assert.Equal(0, policy.BackwardAdjustmentCount);
        Assert.Equal(1, policy.ClockAdjustmentCount);
    }

    [Fact]
    public void ValidSequence_IsMonotonicallyNonDecreasing()
    {
        var random = new Random(42);
        var policy = new EffectiveTimePolicy(Wall, 0);
        var previous = policy.Current;
        long mono = 0;

        for (var i = 0; i < 500; i++)
        {
            mono += random.Next(0, 2000);
            var wall = Wall.AddMilliseconds(mono + random.Next(-5000, 5000));

            var current = policy.Advance(wall, mono);

            Assert.True(current >= previous);
            previous = current;
        }
    }

    [Fact]
    public void ClockAdjustmentCount_IsSumOfForwardAndBackward()
    {
        var policy = new EffectiveTimePolicy(Wall, 0);

        policy.Advance(Wall.AddHours(1), 1000);
        policy.Advance(policy.Current, 2000);
        policy.Advance(policy.Current.AddHours(-1), 3000);

        Assert.Equal(policy.ForwardAdjustmentCount + policy.BackwardAdjustmentCount, policy.ClockAdjustmentCount);
    }

    [Fact]
    public void NegativeInitialMonotonicValue_IsAllowedBecauseOnlyDeltasMatter()
    {
        var policy = new EffectiveTimePolicy(Wall, -5000);

        var result = policy.Advance(Wall.AddMilliseconds(1000), -4000);

        Assert.Equal(Wall.AddMilliseconds(1000), result);
        Assert.Equal(ClockAdjustmentState.None, policy.AdjustmentState);
    }

    [Fact]
    public void MonotonicDeltaOverflow_Throws()
    {
        var policy = new EffectiveTimePolicy(Wall, long.MaxValue);

        Assert.Throws<OverflowException>(() => policy.Advance(Wall, long.MinValue));
    }

    [Fact]
    public void EffectiveTimeOverflow_Throws()
    {
        var policy = new EffectiveTimePolicy(DateTimeOffset.MaxValue, 0);

        Assert.Throws<OverflowException>(() => policy.Advance(DateTimeOffset.MaxValue, 1000));
    }

    [Fact]
    public void OffsetInput_IsNormalizedToUtc()
    {
        var plus8 = TimeSpan.FromHours(8);
        var policy = new EffectiveTimePolicy(Wall, 0);
        var wallInOffset = new DateTimeOffset(2026, 1, 1, 8, 0, 1, plus8);

        var result = policy.Advance(wallInOffset, 1000);

        Assert.Equal(TimeSpan.Zero, result.Offset);
        Assert.Equal(Wall.AddMilliseconds(1000), result);
    }

    [Fact]
    public void TicksSubMillisecondPrecision_IsPreservedForCurrent()
    {
        var policy = new EffectiveTimePolicy(Wall, 0);

        policy.Advance(Wall, 0);

        Assert.Equal(Wall, policy.Current);
        Assert.Equal(0, policy.Current.Ticks % TimeSpan.TicksPerMillisecond);
    }
}