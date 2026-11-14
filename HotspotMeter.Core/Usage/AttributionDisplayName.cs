using System;
using System.IO;

namespace HotspotMeter.Core.Usage;

/// <summary>
/// 把 Windows 的 AttributionName / AttributionId 转成可展示名称。
/// 该转换是纯字符串逻辑，放在 Core 便于单元测试；不猜测进程名，也不编造应用身份。
/// </summary>
public static class AttributionDisplayName
{
    public const string Unattributed = "未归属流量（系统/服务）";
    public const string Unknown = "未知应用";

    /// <param name="attributionId">Windows 返回的归属 ID：桌面程序通常是设备路径，打包应用是包族名。</param>
    /// <param name="attributionName">显示名；桌面程序大量为空。</param>
    public static string Describe(string? attributionId, string? attributionName)
    {
        if (!string.IsNullOrWhiteSpace(attributionName))
        {
            return attributionName.Trim();
        }

        if (string.IsNullOrWhiteSpace(attributionId))
        {
            return Unattributed;
        }

        var id = attributionId.Trim();
        if (id.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(id.Replace('/', '\\'));
            return string.IsNullOrWhiteSpace(name) ? Unknown : name;
        }

        // 包族名形如 MicrosoftWindows.CrossDevice_cw5n1h2txyewy，去掉发布者后缀便于阅读。
        var underscore = id.IndexOf('_');
        return underscore > 0 ? id[..underscore] : id;
    }
}