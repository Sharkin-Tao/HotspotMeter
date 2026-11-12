using HotspotMeter.Core.Models;

namespace HotspotMeter.Core.Monitoring;

/// <summary>
/// 恢复判定（纯逻辑）。只回答"这段基线还能不能用、能算出多少增量、依据是什么"，
/// 不接触连续性证据、不合并会话、不发事件——那些由 <c>MonitorEngine</c> 协调。
/// </summary>
public static class BaselineResumeEvaluator
{
    /// <summary>
    /// 按"确定性证据优先"的顺序判定。任何非 <see cref="ResumeOutcome.Continued"/> 的结果，
    /// 增量都必须为零，避免跨重启/跨接口的脏数据进入会话。
    /// </summary>
    public static ResumeEvaluation Evaluate(
        BaselineState baseline,
        InterfaceReading? current,
        HotspotConnection? connection,
        long tickCount64,
        DateTimeOffset nowUtc,
        TimeSpan bootEpochTolerance)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        if (bootEpochTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bootEpochTolerance), bootEpochTolerance, "容差不能为负。");
        }

        // 1. 单调证据：同一开机内 TickCount64 不可能变小。
        if (tickCount64 < baseline.TickCount64)
        {
            return new ResumeEvaluation(
                ResumeOutcome.Rebooted, 0, 0,
                $"TickCount64 从 {baseline.TickCount64} 回退到 {tickCount64}，同一开机内不可能发生。",
                RebootEvidence.TickRegression);
        }

        // 2. 当前必须仍连接目标热点，否则增量里必然混入别的网络流量。
        if (connection is null
            || connection.InterfaceGuid != baseline.InterfaceGuid
            || !string.Equals(connection.Ssid, baseline.Ssid, StringComparison.Ordinal))
        {
            return new ResumeEvaluation(
                ResumeOutcome.NotOnTarget, 0, 0,
                "当前未连接目标 SSID 或接口不一致。",
                RebootEvidence.None);
        }

        // 3. 当前读不到行：连接还在但计数行消失，视为接口暂时缺失。
        if (current is null)
        {
            return new ResumeEvaluation(
                ResumeOutcome.InterfaceMissing, 0, 0,
                "当前接口表中找不到该 GUID 的计数行。",
                RebootEvidence.None);
        }

        // 4. LUID 变化：GUID 没变但底层设备换了，旧基线失效。
        if (current.Key.InterfaceLuid != baseline.InterfaceLuid)
        {
            return new ResumeEvaluation(
                ResumeOutcome.InterfaceChanged, 0, 0,
                $"接口 LUID 从 0x{baseline.InterfaceLuid:X16} 变为 0x{current.Key.InterfaceLuid:X16}。",
                RebootEvidence.None);
        }

        // 5. 计数器回退。
        if (current.Counters.InOctets < baseline.InOctets || current.Counters.OutOctets < baseline.OutOctets)
        {
            return new ResumeEvaluation(
                ResumeOutcome.CounterReset, 0, 0,
                "计数器小于基线，基线失效。",
                RebootEvidence.None);
        }

        // 6. 辅助证据：开机近似时刻偏移超过容差时，无法区分"重启"与"改了时间"，保守判重启。
        var bootEpoch = BootSignatures.ComputeBootEpoch(nowUtc, tickCount64);
        var drift = (bootEpoch - baseline.BootEpochUtc).Duration();
        if (drift > bootEpochTolerance)
        {
            return new ResumeEvaluation(
                ResumeOutcome.Rebooted, 0, 0,
                $"开机近似时刻偏移 {drift.TotalMinutes:F1} 分钟，超过容差 {bootEpochTolerance.TotalMinutes:F0} 分钟。",
                RebootEvidence.BootEpochShift);
        }

        var download = checked((long)(current.Counters.InOctets - baseline.InOctets));
        var upload = checked((long)(current.Counters.OutOctets - baseline.OutOctets));
        return new ResumeEvaluation(
            ResumeOutcome.Continued, download, upload,
            "同开机、同接口、计数递增且仍连接目标热点；增量可作为恢复候选。",
            RebootEvidence.None);
    }
}