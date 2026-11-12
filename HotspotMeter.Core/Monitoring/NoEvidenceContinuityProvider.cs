using HotspotMeter.Core.Abstractions;
using HotspotMeter.Core.Models;

namespace HotspotMeter.Core.Monitoring;

/// <summary>
/// 默认连续性证据源：永远返回"不确定"。
/// 阶段 2 没有任何可靠手段证明程序未运行期间连接是否中断（WLAN API 不提供关联起始时间），
/// 因此真实崩溃恢复一律只作为候选上报，不并入正式统计。
/// 将来接入事件日志等证据时，替换此实现即可，引擎与累积器无需改动。
/// </summary>
public sealed class NoEvidenceContinuityProvider : IConnectionContinuityProvider
{
    /// <inheritdoc />
    public Task<ContinuityAssessment> AssessAsync(RecoveryWindow window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(ContinuityAssessment.Unverified(
            "阶段 2 无可用证据源：WLAN API 不提供关联起始时间，无法证明该时段内连接未中断。"));
    }
}