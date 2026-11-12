namespace HotspotMeter.Core.Monitoring;

/// <summary>对"单帧增量是否合理"的处理策略。</summary>
public enum PlausibilityMode
{
    /// <summary>不做任何检查。</summary>
    Off,

    /// <summary>默认策略：只上报告警，字节照常累加。阈值未经真机标定前不得升级为丢弃。</summary>
    Warn,

    /// <summary>丢弃可疑帧。默认关闭，仅用于测试与将来标定后手动开启。</summary>
    Drop,
}

/// <summary>
/// 采样参数。全部有安全默认值；<see cref="Validate"/> 在引擎与累积器构造时调用。
/// </summary>
public sealed record MonitorOptions
{
    /// <summary>采样间隔，默认 1 秒。低于 500ms 会显著增加 WLAN/IP Helper 调用压力。</summary>
    public TimeSpan SamplingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>间隔超过"采样间隔 × 该倍数"即视为睡眠/长暂停。</summary>
    public int SuspendedGapMultiplier { get; init; } = 3;

    /// <summary>非正常状态恢复后，需要多少个健康帧才继续累计（默认 2 帧）。</summary>
    public int RecoveryConfirmFrames { get; init; } = 2;

    /// <summary>合理性检查策略，默认只告警。</summary>
    public PlausibilityMode Plausibility { get; init; } = PlausibilityMode.Warn;

    /// <summary>上限按链路速率放宽的倍数。真实链路很难持续超过理论值的 2 倍。</summary>
    public double PlausibilityRateMultiplier { get; init; } = 2.0;

    /// <summary>上限的固定余量，避免低速率链路误判。</summary>
    public long PlausibilitySlackBytes { get; init; } = 1024 * 1024;

    /// <summary>重启判定时允许的开机近似时刻漂移（辅助证据的容差）。</summary>
    public TimeSpan BootEpochTolerance { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>校验参数，非法值直接抛异常而不是悄悄纠正。</summary>
    public void Validate()
    {
        if (SamplingInterval < TimeSpan.FromMilliseconds(500))
        {
            throw new ArgumentOutOfRangeException(nameof(SamplingInterval), SamplingInterval, "采样间隔不得小于 500ms。");
        }

        if (SuspendedGapMultiplier < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(SuspendedGapMultiplier), SuspendedGapMultiplier, "长暂停倍数不得小于 2。");
        }

        if (RecoveryConfirmFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(RecoveryConfirmFrames), RecoveryConfirmFrames, "恢复确认帧数至少为 1。");
        }

        if (PlausibilityRateMultiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PlausibilityRateMultiplier), PlausibilityRateMultiplier, "速率倍数必须大于 0。");
        }

        if (PlausibilitySlackBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PlausibilitySlackBytes), PlausibilitySlackBytes, "余量不能为负。");
        }

        if (BootEpochTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BootEpochTolerance), BootEpochTolerance, "容差不能为负。");
        }
    }
}