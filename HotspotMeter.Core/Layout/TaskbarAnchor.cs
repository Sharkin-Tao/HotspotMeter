namespace HotspotMeter.Core.Layout;

/// <summary>自动适配开启时，胶囊的初始摆放位置（DIP）。</summary>
public sealed record TaskbarAnchor(
    bool Supported,
    double Left,
    double Top,
    string Explanation);

/// <summary>
/// 纯几何计算：自动适配开启时把胶囊放到"底部任务栏内部偏左"的位置。
/// <para>
/// 只支持底部任务栏：Windows 11 的默认布局下，任务栏内部唯一稳定可用的横向空间在底部。
/// 顶部/左侧/右侧任务栏返回 <see cref="TaskbarAnchor.Supported"/> = false，
/// 调用方应保持用户当前拖动位置，而不是猜一个位置把窗口放到任务栏外面。
/// </para>
/// <para>
/// 坐标全部使用物理像素输入、DIP 输出，避免多显示器与 100/125/150/200% 缩放下混用单位。
/// </para>
/// </summary>
public static class TaskbarAnchorCalculator
{
    /// <summary>距显示器左边缘的初始偏移；不能贴边，否则会压住开始按钮。</summary>
    public const double DefaultLeftOffsetDips = 120;

    /// <summary>与显示器左右边缘保留的最小安全距离。</summary>
    public const double EdgeMarginDips = 8;

    public static TaskbarAnchor Compute(
        TaskbarLayoutInput input,
        double capsuleWidthDips,
        double capsuleHeightDips,
        double leftOffsetDips = DefaultLeftOffsetDips)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.TaskbarRect is not { } taskbar || taskbar.IsEmpty)
        {
            return new TaskbarAnchor(false, 0, 0, "未检测到任务栏矩形，自动定位未执行，保持当前位置。");
        }

        if (input.Edge != TaskbarEdge.Bottom)
        {
            return new TaskbarAnchor(
                false,
                0,
                0,
                $"当前任务栏位于{DescribeEdge(input.Edge)}，自动定位仅支持底部任务栏，保持当前位置。");
        }

        var dpi = input.DpiScale > 0 ? input.DpiScale : 1;
        var monitor = input.MonitorBounds;

        var widthPx = Math.Max(1, capsuleWidthDips * dpi);
        var heightPx = Math.Max(1, capsuleHeightDips * dpi);

        if (heightPx > taskbar.Height + 0.5)
        {
            return new TaskbarAnchor(
                false,
                0,
                0,
                $"胶囊高 {capsuleHeightDips:0.#} DIP，任务栏仅 {taskbar.Height / dpi:0.#} DIP，无法完整放入任务栏内部，自动定位未执行。");
        }

        var marginPx = EdgeMarginDips * dpi;
        var minLeft = monitor.Left + marginPx;
        var maxLeft = Math.Max(minLeft, monitor.Right - widthPx - marginPx);
        var leftPx = Math.Clamp(monitor.Left + (leftOffsetDips * dpi), minLeft, maxLeft);

        // 垂直居中于任务栏矩形：胶囊完整落在任务栏内部，不压到任务栏上方。
        var topPx = taskbar.Top + ((taskbar.Height - heightPx) / 2);

        // 显示器边界保护：任务栏异常（例如横跨两台显示器）时不把窗口推到屏幕外。
        // 垂直方向不再扣边距：任务栏本身就贴着显示器底边，扣 8 DIP 会把胶囊顶回任务栏上方。
        var maxTop = Math.Max(taskbar.Top, monitor.Bottom - heightPx);
        topPx = Math.Clamp(topPx, taskbar.Top, maxTop);

        return new TaskbarAnchor(
            true,
            Math.Round(leftPx / dpi, 1),
            Math.Round(topPx / dpi, 1),
            $"已自动定位到底部任务栏左侧：左偏移 {leftOffsetDips:0.#} DIP，垂直居中于任务栏。");
    }

    internal static string DescribeEdge(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top => "顶部",
        TaskbarEdge.Left => "左侧",
        TaskbarEdge.Right => "右侧",
        TaskbarEdge.Bottom => "底部",
        _ => "未知边缘",
    };
}