using HotspotMeter.Core.Models;

namespace HotspotMeter.Core.Abstractions;

/// <summary>
/// 恢复窗口连接连续性证据源。职责单一：只看窗口与外部证据，回答"能不能确认没断过"，
/// 不计算字节、不决定是否合并（那是引擎与累积器的职责）。
/// </summary>
public interface IConnectionContinuityProvider
{
    /// <summary>评估给定窗口内的连接连续性。实现不得抛出"没有证据"以外的异常。</summary>
    Task<ContinuityAssessment> AssessAsync(RecoveryWindow window, CancellationToken cancellationToken);
}