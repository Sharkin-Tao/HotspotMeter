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

    void ResetPosition();
}