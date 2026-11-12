using HotspotMeter.Core.Models;

namespace HotspotMeter.Core.Abstractions;

/// <summary>
/// 热点检测。实现位于 Infrastructure（WLAN API）；测试用假实现脚本化返回。
/// </summary>
public interface IHotspotDetector
{
    /// <summary>枚举所有无线接口，供设置页选择目标热点。</summary>
    Task<IReadOnlyList<HotspotCandidate>> GetCandidatesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 查询指定接口的当前连接。返回 <c>null</c> 表示<strong>查询成功且确实未连接</strong>；
    /// 查询失败必须抛异常，由引擎按"暂时不可用"处理，绝不能把异常吞成 null。
    /// </summary>
    Task<HotspotConnection?> GetConnectionAsync(Guid interfaceGuid, CancellationToken cancellationToken);
}