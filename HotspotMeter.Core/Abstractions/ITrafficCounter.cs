using HotspotMeter.Core.Models;

namespace HotspotMeter.Core.Abstractions;

/// <summary>
/// 目标网卡的累计计数器读取。必须按 GUID 精确定位唯一行，禁止按类型或别名求和。
/// </summary>
public interface ITrafficCounter
{
    /// <summary>
    /// 读取累计收发字节。返回 <c>null</c> 表示表中没有该 GUID 的行（接口暂时缺失），
    /// 读取过程失败必须抛异常。
    /// </summary>
    Task<InterfaceReading?> TryReadAsync(Guid interfaceGuid, CancellationToken cancellationToken);
}