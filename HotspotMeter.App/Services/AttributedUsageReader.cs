using HotspotMeter.Core.Usage;
using Windows.Networking.Connectivity;

namespace HotspotMeter.App.Services;

/// <summary>一条逐应用归属流量。名称来自系统 AttributionId，未识别时明确写“未知应用”。</summary>
public sealed record ProgramUsage(
    string AttributionId,
    string DisplayName,
    long DownloadBytes,
    long UploadBytes)
{
    public long TotalBytes => DownloadBytes + UploadBytes;

    public bool IsUnattributed => string.IsNullOrWhiteSpace(AttributionId);
}

public sealed record AttributedUsageResult(
    bool IsAvailable,
    string Status,
    long TotalDownloadBytes,
    long TotalUploadBytes,
    IReadOnlyList<ProgramUsage> Programs)
{
    public static AttributedUsageResult Unavailable(string status) => new(false, status, 0, 0, []);
}

/// <summary>
/// Windows.Networking.Connectivity 的逐应用流量读取器。
///
/// 事实（2026-11-14 探针验证）：
/// - 非打包 WPF 进程、无管理员、无受限能力即可调用；
/// - GetNetworkUsageAsync 返回该连接配置的总收发，GetAttributedNetworkUsageAsync 返回逐应用；
/// - 逐应用合计通常小于总量，差额是系统/服务/未归属流量，不能强行分摊；
/// - 桌面程序的 AttributionName 常为空，需要从 AttributionId 路径派生显示名。
///
/// 本类只读取系统已经归属的数据，不做估算，也不把实时网卡流量当成逐应用流量。
/// </summary>
public sealed class AttributedUsageReader
{
    public async Task<AttributedUsageResult> ReadAsync(
        DateOnly date,
        Guid? adapterId,
        string? ssid,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = FindProfile(adapterId, ssid);
            if (profile is null)
            {
                return AttributedUsageResult.Unavailable("未找到当前热点对应的 Windows 连接配置。");
            }

            var start = date.ToDateTime(TimeOnly.MinValue);
            var end = date == DateOnly.FromDateTime(DateTime.Now)
                ? DateTimeOffset.Now
                : date.AddDays(1).ToDateTime(TimeOnly.MinValue);

            var states = new NetworkUsageStates
            {
                Roaming = TriStates.DoNotCare,
                Shared = TriStates.DoNotCare,
            };

            var total = await profile.GetNetworkUsageAsync(
                start, end, DataUsageGranularity.PerDay, states).AsTask(cancellationToken);
            long totalDown = 0;
            long totalUp = 0;
            foreach (var item in total)
            {
                totalDown += (long)item.BytesReceived;
                totalUp += (long)item.BytesSent;
            }

            var attributed = await profile.GetAttributedNetworkUsageAsync(start, end, states)
                .AsTask(cancellationToken);
            var programs = attributed
                .Select(item => new ProgramUsage(
                    item.AttributionId ?? string.Empty,
                    Describe(item.AttributionId, item.AttributionName),
                    (long)item.BytesReceived,
                    (long)item.BytesSent))
                .Where(item => item.TotalBytes > 0)
                .OrderByDescending(item => item.TotalBytes)
                .ToArray();

            var status = programs.Length == 0
                ? "系统没有返回逐应用数据（可能尚未生成归属统计）。"
                : $"系统归属数据：{programs.Length} 项；未归属差额 {FormatBytes(Math.Max(0, totalDown + totalUp - programs.Sum(p => p.TotalBytes)))}。";

            return new AttributedUsageResult(true, status, totalDown, totalUp, programs);
        }
        catch (Exception ex)
        {
            return AttributedUsageResult.Unavailable($"逐应用流量不可用：{ex.Message}");
        }
    }

    private static ConnectionProfile? FindProfile(Guid? adapterId, string? ssid)
    {
        // 目标身份不完整时宁可返回“不可用”，也不能取第一个 WLAN 配置：
        // 那会把另一张网卡/另一个热点的归属数据冒充成当前目标。
        if (adapterId is null || string.IsNullOrWhiteSpace(ssid))
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
            if (string.IsNullOrEmpty(profileSsid) ||
                !string.Equals(profileSsid, ssid, StringComparison.Ordinal))
            {
                continue;
            }

            if (profile.NetworkAdapter is not { } adapter || adapter.NetworkAdapterId != adapterId.Value)
            {
                continue;
            }

            return profile;
        }

        return null;
    }

    private static string Describe(string? attributionId, string? attributionName) =>
        AttributionDisplayName.Describe(attributionId, attributionName);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }
}