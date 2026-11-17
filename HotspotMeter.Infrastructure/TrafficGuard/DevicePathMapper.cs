using System.Runtime.InteropServices;
using System.Text;

namespace HotspotMeter.Infrastructure.TrafficGuard;

/// <summary>
/// 把 <c>\Device\HarddiskVolumeN\...</c> 映射成 <c>C:\...</c>。
///
/// 为什么需要：Windows 的逐应用归属 Id 常返回设备路径，而进程查询返回 DOS 路径；
/// 不映射就会被当成两个不同程序，导致同一程序出现“有流量但无进程”的假身份。
/// 映射只读、缓存 60 秒，不写注册表、不要求管理员权限。
/// </summary>
public sealed class DevicePathMapper
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    private readonly object _lock = new();
    private Dictionary<string, string> _prefixes = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _refreshedAt = DateTimeOffset.MinValue;

    public string MapToDosPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var value = path.Trim();
        if (value.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            value = value[3..];
        }

        if (!value.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var map = GetMap();
        var best = map
            .Where(pair => value.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Key.Length)
            .FirstOrDefault();

        if (best.Key is null)
        {
            return value;
        }

        return best.Value + value[best.Key.Length..];
    }

    private Dictionary<string, string> GetMap()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _refreshedAt < CacheLifetime)
            {
                return _prefixes;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                {
                    continue;
                }

                var letter = drive.Name.TrimEnd('\\');
                var target = QueryDosDevice(letter);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    map[target] = letter;
                }
            }

            _prefixes = map;
            _refreshedAt = now;
            return map;
        }
    }

    private static string? QueryDosDevice(string driveLetter)
    {
        var buffer = new StringBuilder(1024);
        var length = QueryDosDeviceW(driveLetter, buffer, (uint)buffer.Capacity);
        return length == 0 ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(
        string lpDeviceName,
        StringBuilder lpTargetPath,
        uint ucchMax);
}