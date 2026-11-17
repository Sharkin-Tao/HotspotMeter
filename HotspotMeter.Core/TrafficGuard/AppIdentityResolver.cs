namespace HotspotMeter.Core.TrafficGuard;

/// <summary>
/// 把进程/归属 Id 解析成稳定 AppKey。
///
/// 优先级：
/// 1. 有 Package Family Name → 包身份（UWP/MSIX 多进程也天然合并）；
/// 2. 有完整 EXE 路径 → 规范化路径身份（同名不同目录不会合并）；
/// 3. 两者都没有 → 带 PID + 启动时间的降级身份，明确标记不确定。
///
/// 注意：绝不把“进程名”单独作为 Key；同名程序的路径不同，必须是两个 AppKey。
/// </summary>
public sealed class AppIdentityResolver
{
    public AppIdentity ResolveProcess(ProcessObservation process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!string.IsNullOrWhiteSpace(process.PackageFamilyName))
        {
            return AppIdentity.Packaged(process.PackageFamilyName, process.ProcessName);
        }

        if (!string.IsNullOrWhiteSpace(process.ExecutablePath))
        {
            return AppIdentity.Desktop(process.ExecutablePath, process.ProcessName);
        }

        return AppIdentity.Uncertain(process.ProcessName, process.ProcessId, process.StartTimeUtcTicks);
    }

    public AppIdentity? ResolveUsage(GuardUsageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return AppIdentity.FromAttributionId(entry.AttributionId, entry.DisplayName);
    }
}