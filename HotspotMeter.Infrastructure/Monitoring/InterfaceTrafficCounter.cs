using HotspotMeter.Core.Abstractions;
using HotspotMeter.Core.Models;
using HotspotMeter.Infrastructure.Interop;

namespace HotspotMeter.Infrastructure.Monitoring;

/// <summary>
/// IP Helper 实现的网卡计数器。按 GUID 精确取唯一一行：
/// 阶段 1 实测同一 WLAN 在表中会出现 5 行同值过滤层（另有 Radmin VPN 多行同值），
/// 按类型或别名求和会把真值放大 6 倍。
/// </summary>
public sealed class InterfaceTrafficCounter : ITrafficCounter
{
    /// <inheritdoc />
    public Task<InterfaceReading?> TryReadAsync(Guid interfaceGuid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var row in IpHelper.ReadRows())
        {
            if (row.InterfaceGuid == interfaceGuid)
            {
                var reading = new InterfaceReading(
                    new NetworkInterfaceKey(row.InterfaceGuid, row.InterfaceLuid),
                    new InterfaceCounters(row.InOctets, row.OutOctets));
                return Task.FromResult<InterfaceReading?>(reading);
            }
        }

        // 表里没有该 GUID：接口暂时缺失（例如刚被禁用），由引擎暂停而不是结束会话。
        return Task.FromResult<InterfaceReading?>(null);
    }
}