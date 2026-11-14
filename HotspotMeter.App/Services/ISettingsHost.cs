using HotspotMeter.App.Models;

namespace HotspotMeter.App.Services;

/// <summary>
/// 设置窗口需要的宿主能力。由 App 实现，设置窗口不直接持有窗口或监控引擎，
/// 这样"改尺寸/固定/复位"都只有一个真实来源。
/// </summary>
public interface ISettingsHost
{
    CapsuleSize CurrentSize { get; }

    bool IsPinned { get; }

    bool ShowCapsuleOnStartup { get; }

    /// <summary>是否根据任务栏位置与可用空间自动调整尺寸/停靠。</summary>
    bool AutoFitTaskbar { get; }

    /// <summary>最近一次任务栏检测的中文摘要，用于设置窗口告诉用户"依据是什么"。</summary>
    string TaskbarStatusText { get; }

    (double Left, double Top) CapsulePosition { get; }

    void ApplySize(CapsuleSize size);

    void SetAutoFitTaskbar(bool enabled);

    void SetPinned(bool pinned);

    void SetShowCapsuleOnStartup(bool show);

    /// <summary>开机自启动是否已对“当前路径”生效；读注册表真实状态，不读缓存。</summary>
    bool AutoStartEnabled { get; }

    /// <summary>启动项诊断文本：路径不一致、权限失败等必须如实显示。</summary>
    string StartupStatusText { get; }

    /// <summary>统计窗口是否显示任务栏按钮；旧配置缺省为 true。</summary>
    bool ShowStatsInTaskbar { get; }

    void SetAutoStart(bool enabled);

    void SetShowStatsInTaskbar(bool show);

    void ResetPosition();
}