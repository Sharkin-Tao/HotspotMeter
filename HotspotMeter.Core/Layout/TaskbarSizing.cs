namespace HotspotMeter.Core.Layout;

/// <summary>
/// 胶囊在自动适配模式下使用的排版密度。
/// <para>
/// 任务栏通常只有约 48 DIP 高，而普通四档尺寸的最小高度是 71.5 DIP。
/// 若继续按 320×104 等比缩小，字号会低于可读下限，因此自动模式使用一套独立的
/// "高度优先"排版：高度先贴近任务栏，字号按密度档位取值，而不是一味乘同一个比例。
/// </para>
/// </summary>
public enum CapsuleDensity
{
    /// <summary>高度 ≥ 88 DIP：接近标准尺寸，保留完整信息（含"下载/上传"文字）。</summary>
    Normal = 0,

    /// <summary>高度 56–87 DIP：紧凑排版，保留"下载/上传"文字。</summary>
    Compact = 1,

    /// <summary>高度 40–55 DIP：最小可读排版，只保留方向箭头与速度数值。</summary>
    Minimal = 2,
}

/// <summary>自动尺寸计算的输入，全部来自任务栏检测（物理像素 + DPI）。</summary>
public sealed record TaskbarSizingInput(
    PixelRect MonitorBounds,
    PixelRect WorkArea,
    PixelRect? TaskbarRect,
    TaskbarEdge Edge,
    double DpiScale);

/// <summary>自动尺寸计算的结果（DIP），可直接赋给 WPF Window 的宽高。</summary>
public sealed record TaskbarCapsuleFit(
    double Width,
    double Height,
    double TaskbarThicknessDips,
    CapsuleDensity Density,
    bool FitsTaskbar,
    bool ShowSpeedLabels,
    string Explanation);

/// <summary>
/// 纯计算：根据任务栏实际厚度与当前显示器 DPI，算出胶囊在任务栏高度内可读的尺寸。
/// <para>
/// 关键约束：只决定"多大"，不决定"放哪"。用户仍然自己拖动胶囊；
/// 这里不查询任务栏按钮、不寻找 UIA 空隙，也不把窗口挪到任务栏上方。
/// </para>
/// </summary>
public static class TaskbarSizingCalculator
{
    public const double BaseWidth = 320;
    public const double BaseHeight = 104;
    public const double AspectRatio = BaseWidth / BaseHeight;   // 3.0769...

    /// <summary>胶囊与任务栏上下边缘之间保留的视觉余量。</summary>
    public const double VerticalBreathingRoom = 4;

    /// <summary>低于此高度时流量数字已难以辨认，不再继续压缩。</summary>
    public const double MinReadableHeight = 40;

    /// <summary>自动模式最大高度与标准尺寸一致，避免突然出现超大窗口。</summary>
    public const double MaxReadableHeight = 104;

    /// <summary>任务栏厚度低于"最小高度 + 余量"时无法完整容纳可读布局，必须如实标记。</summary>
    public const double MinFittingThickness = MinReadableHeight + VerticalBreathingRoom;

    public const double MinWidth = 132;

    /// <summary>没有任务栏矩形时的保守回退值；Windows 11 默认任务栏约 48 DIP。</summary>
    public const double FallbackThicknessDips = 48;

    public static TaskbarCapsuleFit Compute(TaskbarSizingInput input)
    {
        var dpi = input.DpiScale > 0 ? input.DpiScale : 1.0;
        var work = input.WorkArea.IsEmpty ? input.MonitorBounds : input.WorkArea;
        var workWidthDips = work.Width / dpi;
        var workHeightDips = work.Height / dpi;

        // 纵向任务栏（左/右）不限制胶囊高度，用工作区高度作为可读上限；
        // 横向任务栏（上/下）才用任务栏实际厚度作为高度基准。
        var verticalEdge = input.Edge is TaskbarEdge.Left or TaskbarEdge.Right;
        var thicknessDips = verticalEdge
            ? Math.Max(0, workHeightDips)
            : ResolveThicknessDips(input, dpi);

        var targetHeight = verticalEdge
            ? Math.Min(workHeightDips - 16, MaxReadableHeight)
            : thicknessDips - VerticalBreathingRoom;

        var height = Math.Clamp(targetHeight, MinReadableHeight, MaxReadableHeight);
        var width = Math.Round(height * AspectRatio, 1);

        // 宽度不能超出工作区，否则等比例缩放后仍会被屏幕裁掉。
        if (workWidthDips > 0)
        {
            var maxWidth = Math.Max(MinWidth, workWidthDips - 16);
            if (width > maxWidth)
            {
                width = Math.Round(maxWidth, 1);
            }
        }

        var fits = verticalEdge || thicknessDips >= MinFittingThickness;
        var density = height >= 88
            ? CapsuleDensity.Normal
            : height >= 56
                ? CapsuleDensity.Compact
                : CapsuleDensity.Minimal;
        var showLabels = density != CapsuleDensity.Minimal;

        var densityText = density switch
        {
            CapsuleDensity.Normal => "标准排版",
            CapsuleDensity.Compact => "紧凑排版",
            _ => "最简排版",
        };

        var explanation = verticalEdge
            ? $"{EdgeText(input.Edge)}任务栏不限制高度，胶囊 {height:0.#}×{width:0.#} DIP（{densityText}）"
            : $"任务栏厚 {thicknessDips:0.#} DIP → 胶囊 {height:0.#}×{width:0.#} DIP（{densityText}）";

        if (!fits)
        {
            explanation += $"；任务栏低于 {MinFittingThickness:0.#} DIP 的可读下限，胶囊可能超出任务栏";
        }

        return new TaskbarCapsuleFit(
            width,
            height,
            Math.Round(thicknessDips, 1),
            density,
            fits,
            showLabels,
            explanation);
    }

    /// <summary>任务栏厚度（DIP）：优先取任务栏矩形与显示器边缘的距离，缺失时用工作区差异推断。</summary>
    private static double ResolveThicknessDips(TaskbarSizingInput input, double dpi)
    {
        var monitor = input.MonitorBounds;
        if (input.TaskbarRect is { IsEmpty: false } rect)
        {
            var thickness = TaskbarLayoutCalculator.GetThickness(rect, monitor, input.Edge);
            if (thickness > 0)
            {
                return thickness / dpi;
            }
        }

        // 没有任务栏矩形时（例如 ABM_GETTASKBARPOS 失败），用工作区与显示器边缘的差值。
        var work = input.WorkArea;
        if (!work.IsEmpty && !monitor.IsEmpty)
        {
            var inferred = input.Edge switch
            {
                TaskbarEdge.Bottom => monitor.Bottom - work.Bottom,
                TaskbarEdge.Top => work.Top - monitor.Top,
                TaskbarEdge.Left => work.Left - monitor.Left,
                TaskbarEdge.Right => monitor.Right - work.Right,
                _ => 0,
            };

            if (inferred > 0)
            {
                return inferred / dpi;
            }
        }

        return FallbackThicknessDips;
    }

    private static string EdgeText(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top => "顶部",
        TaskbarEdge.Bottom => "底部",
        TaskbarEdge.Left => "左侧",
        TaskbarEdge.Right => "右侧",
        _ => "底部",
    };
}