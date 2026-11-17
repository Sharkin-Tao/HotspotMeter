using HotspotMeter.Core.TrafficGuard;
using HotspotMeter.Infrastructure.TrafficGuard;
using Windows.Networking.Connectivity;

namespace HotspotMeter.App.Services;

/// <summary>
/// 读取当前目标连接配置的逐应用累计流量。
///
/// 重要事实：
/// 1. Windows 的逐应用累计值约 30 秒才刷新一次，不能按“实时”使用；
/// 2. 返回的累计值从当天 00:00 起算，跨午夜会回退，增量计算必须处理；
/// 3. 逐应用列表包含 0 字节程序，必须保留这些条目，否则下一轮重新建基线会漏掉首段流量；
/// 4. 空 AttributionId 是未归属流量，保留在快照里由上层显式跳过，不在这里分摊。
/// </summary>
public sealed class GuardUsageSource : IGuardUsageSource
{
    private readonly DevicePathMapper _pathMapper = new();

    public async Task<GuardUsageSnapshot> ReadAsync(
        GuardTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            var profile = FindProfile(target.InterfaceGuid, target.Ssid);
            if (profile is null)
            {
                return GuardUsageSnapshot.Unavailable("TargetProfileNotFound", DateTimeOffset.UtcNow);
            }

            var start = DateTimeOffset.Now.Date;
            var end = DateTimeOffset.Now;
            var states = new NetworkUsageStates
            {
                Roaming = TriStates.DoNotCare,
                Shared = TriStates.DoNotCare,
            };

            var attributed = await profile
                .GetAttributedNetworkUsageAsync(start, end, states)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            var entries = attributed
                .Select(item => new GuardUsageEntry(
                    _pathMapper.MapToDosPath(item.AttributionId ?? string.Empty),
                    item.AttributionName ?? string.Empty,
                    (long)item.BytesReceived,
                    (long)item.BytesSent))
                .ToArray();

            return new GuardUsageSnapshot(
                true,
                $"Entries={entries.Length}",
                DateTimeOffset.UtcNow,
                entries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return GuardUsageSnapshot.Unavailable($"{ex.GetType().Name}: {ex.Message}", DateTimeOffset.UtcNow);
        }
    }

    private static ConnectionProfile? FindProfile(Guid adapterId, string ssid)
    {
        if (adapterId == Guid.Empty || string.IsNullOrWhiteSpace(ssid))
        {
            return null;
        }

        foreach (var profile in NetworkInformation.GetConnectionProfiles())
        {
            if (!profile.IsWlanConnectionProfile)
            {
                continue;
            }

            var profileSsid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
            if (!string.Equals(profileSsid, ssid, StringComparison.Ordinal))
            {
                continue;
            }

            if (profile.NetworkAdapter is { } adapter && adapter.NetworkAdapterId == adapterId)
            {
                return profile;
            }
        }

        return null;
    }
}