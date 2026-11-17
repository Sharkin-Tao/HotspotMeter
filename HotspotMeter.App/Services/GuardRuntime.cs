using HotspotMeter.Core.TrafficGuard;

namespace HotspotMeter.App.Services;

/// <summary>把当前监控目标适配成 Guard 的目标来源；目标未连接时返回 null。</summary>
public sealed class MonitorGuardTargetProvider : IGuardTargetProvider
{
    private readonly MonitorSessionService _monitor;

    public MonitorGuardTargetProvider(MonitorSessionService monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    public GuardTarget? GetTarget()
    {
        var guid = _monitor.TargetInterfaceGuid;
        var ssid = _monitor.TargetSsid;
        if (guid is null || string.IsNullOrWhiteSpace(ssid))
        {
            return null;
        }

        var profileKey = TrafficHistoryStore.StableProfileKey(guid.Value, ssid).ToString("N");
        return new GuardTarget(profileKey, guid.Value, ssid);
    }
}

/// <summary>Guard 诊断输出。只写已有 AppLog，不弹窗、不参与 UI 线程。</summary>
public sealed class GuardDiagnostics : IGuardDiagnostics
{
    public void Report(string message, Exception? exception = null)
    {
        AppLog.Info(exception is null ? message : $"{message} | {exception.Message}");
    }
}