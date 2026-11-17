using System.Security.Cryptography;
using System.Text;

namespace HotspotMeter.Core.TrafficGuard;

/// <summary>程序身份的来源。数据库与 UI 都使用同一组数值，禁止各自定义。</summary>
public enum AppIdentityKind
{
    /// <summary>普通桌面程序，身份来自规范化后的完整 EXE 路径。</summary>
    DesktopExecutable = 1,

    /// <summary>UWP/MSIX 程序，身份来自 Package Family Name。</summary>
    Packaged = 2,

    /// <summary>拿不到路径或包族名，只能使用降级身份；绝不能把它当成确定身份。</summary>
    Uncertain = 3,
}

/// <summary>
/// 一个稳定的程序身份。
///
/// 为什么用 <see cref="Key"/> 而不是进程名：
/// 1. 同名程序可能来自不同目录，按文件名聚合会把两个程序的流量混在一起；
/// 2. 同一个程序可能有多个 PID，按 PID 聚合会把同一程序拆成多份；
/// 3. 程序重启后 PID 会变，但 EXE 路径/PFN 不变。
///
/// 因此 <see cref="Key"/> 只由"路径或包族名"派生；只有在两者都拿不到时才退化为
/// 带 PID 与启动时间的临时身份，并明确标记 <see cref="IsCertain"/> = false。
/// </summary>
public sealed record AppIdentity(
    string Key,
    string DisplayName,
    AppIdentityKind Kind,
    bool IsCertain)
{
    /// <summary>桌面程序的稳定身份。路径会先做大小写/分隔符/长路径前缀规范化。</summary>
    public static AppIdentity Desktop(string executablePath, string? displayName = null)
    {
        var normalized = NormalizeExecutablePath(executablePath);
        var name = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileName(normalized)
            : displayName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = normalized;
        }

        return new AppIdentity($"exe:{normalized}", name, AppIdentityKind.DesktopExecutable, true);
    }

    /// <summary>UWP/MSIX 的稳定身份。PFN 本身不区分大小写。</summary>
    public static AppIdentity Packaged(string packageFamilyName, string? displayName = null)
    {
        var normalized = packageFamilyName.Trim().ToLowerInvariant();
        var name = string.IsNullOrWhiteSpace(displayName) ? normalized : displayName.Trim();
        return new AppIdentity($"pkg:{normalized}", name, AppIdentityKind.Packaged, true);
    }

    /// <summary>
    /// 无法取得完整身份时的降级身份。必须带 PID 与启动时间，避免同一进程名下的两个
    /// 无关程序被错误合并；调用方还会把 <c>IdentityCertain=0</c> 写入数据库。
    /// </summary>
    public static AppIdentity Uncertain(string? displayName, int processId, long startTimeUtcTicks)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? $"pid-{processId}" : displayName.Trim();
        return new AppIdentity(
            $"uncertain:{processId}:{startTimeUtcTicks}:{name.ToLowerInvariant()}",
            name,
            AppIdentityKind.Uncertain,
            false);
    }

    /// <summary>
    /// 仅从归属 Id 派生身份，用于 Windows API 返回了某程序、但该进程当前不在运行的情况。
    /// 空 Id 不产生身份（未归属流量必须单独统计，不能分配给任意程序）。
    /// </summary>
    public static AppIdentity? FromAttributionId(string? attributionId, string? attributionName = null)
    {
        if (string.IsNullOrWhiteSpace(attributionId))
        {
            return null;
        }

        var id = attributionId.Trim();
        if (id.Contains('\\') || id.Contains('/'))
        {
            var display = string.IsNullOrWhiteSpace(attributionName)
                ? Path.GetFileName(id.Replace('/', '\\'))
                : attributionName;

            // Windows 的归属 Id 不全是文件路径：也会出现 "IPv6 Control Message" 这类
            // 系统伪条目。把它们当 EXE 路径会凭空造出一个不存在的程序身份，必须降级。
            var path = id.Replace('/', '\\');
            if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return Desktop(id, display);
            }

            return Opaque(id, display);
        }

        // PFN 形如 Name_publisherId，且不含路径分隔符。
        if (id.Contains('_'))
        {
            return Packaged(id, attributionName);
        }

        return Opaque(id, attributionName);
    }

    private static AppIdentity Opaque(string id, string? displayName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16];
        var name = string.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim();
        return new AppIdentity($"opaque:{hash}", name, AppIdentityKind.Uncertain, false);
    }

    /// <summary>
    /// 统一路径文本：去长路径前缀、统一反斜杠、去掉尾部空格与点、转小写。
    /// 不在这里展开环境变量或做网络盘映射；调用方应传入已经解析过的绝对路径。
    /// </summary>
    public static string NormalizeExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("程序路径不能为空。", nameof(path));
        }

        var value = path.Trim();
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        value = value.Replace('/', '\\');
        try
        {
            value = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 非法路径不丢弃：保留清洗后的文本作为身份，并让调用方看到这是异常输入。
            value = value.TrimEnd(' ', '.');
        }

        return value.ToLowerInvariant();
    }
}