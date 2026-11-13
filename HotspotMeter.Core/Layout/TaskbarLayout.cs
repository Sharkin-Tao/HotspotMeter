namespace HotspotMeter.Core.Layout;

/// <summary>任务栏所在的屏幕边缘。</summary>
public enum TaskbarEdge
{
    Unknown = 0,
    Top = 1,
    Bottom = 2,
    Left = 3,
    Right = 4,
}

/// <summary>物理像素矩形（Win32 坐标系，可能是负坐标，用于多显示器）。</summary>
public readonly record struct PixelRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Math.Max(0, Right - Left);

    public double Height => Math.Max(0, Bottom - Top);

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>一次任务栏检测的输入快照，全部为物理像素与显示器 DPI 缩放。</summary>
public sealed record TaskbarLayoutInput(
    PixelRect MonitorBounds,
    PixelRect WorkArea,
    PixelRect? TaskbarRect,
    TaskbarEdge Edge,
    bool AutoHide,
    double DpiScale);

/// <summary>自动适配的可调参数；默认值与 4C-3 的胶囊尺寸体系一致。</summary>
public sealed record CapsuleLayoutOptions
{
    public double BaseWidth { get; init; } = 320;

    public double BaseHeight { get; init; } = 104;

    /// <summary>最小可读宽度；再小则辅助文字无法辨认。</summary>
    public double MinWidth { get; init; } = 220;

    /// <summary>自动适配最多放大到标准尺寸，避免突然变成超大窗口。</summary>
    public double MaxScale { get; init; } = 1.0;

    public double MarginDips { get; init; } = 8;

    public double GapDips { get; init; } = 12;
}

/// <summary>停靠结果的来源，便于自检与设置窗口解释"为什么放在这里"。</summary>
public enum PlacementKind
{
    /// <summary>任务栏相邻的常规位置，尺寸在最小可读尺寸以上。</summary>
    NearTaskbar = 0,

    /// <summary>任务栏附近空间不足，退到对侧角落，仍保证可读。</summary>
    FallbackCorner = 1,

    /// <summary>连最小尺寸都放不下（极小工作区），只能按最小尺寸强制放置。</summary>
    MinimumForced = 2,
}

/// <summary>胶囊最终位置与尺寸（DIP，可直接赋给 WPF Window）。</summary>
public sealed record CapsulePlacement(
    double Left,
    double Top,
    double Width,
    double Height,
    double Scale,
    PlacementKind Kind,
    TaskbarEdge Edge,
    string Explanation);

/// <summary>
/// 纯几何计算：根据"任务栏矩形 + 显示器工作区 + 边缘 + DPI"算出胶囊应该多大、放哪。
/// 故意不依赖 WPF/Win32，这样四边任务栏、多显示器、不同 DPI 都能用单元测试覆盖，
/// 而不必真的把用户的任务栏拖到屏幕顶部。
/// </summary>
public static class TaskbarLayoutCalculator
{
    public static CapsulePlacement Compute(TaskbarLayoutInput input, CapsuleLayoutOptions? options = null)
    {
        options ??= new CapsuleLayoutOptions();

        var dpi = input.DpiScale > 0 ? input.DpiScale : 1.0;
        var margin = options.MarginDips * dpi;
        var gap = options.GapDips * dpi;

        var monitor = input.MonitorBounds;
        var work = input.WorkArea.IsEmpty ? monitor : input.WorkArea;

        var edge = input.Edge == TaskbarEdge.Unknown ? TaskbarEdge.Bottom : input.Edge;
        var clearance = input.AutoHide && input.TaskbarRect is { } autoHideRect
            ? gap + GetThickness(autoHideRect, monitor, edge)
            : 0;

        // 常驻任务栏：工作区已经把它排除了，但仍留出 GapDips 的视觉间距，
        // 避免胶囊紧贴任务栏边缘；自动隐藏时还要加上任务栏厚度。
        var edgeGap = input.AutoHide ? clearance : gap;

        // 工作区已经排除了常驻任务栏；自动隐藏时任务栏浮在屏幕边缘，
        // 需要额外让出"厚度 + 间距"，否则任务栏滑出时会盖住胶囊。
        var interiorHeight = Math.Max(0, work.Height - (edge is TaskbarEdge.Top or TaskbarEdge.Bottom ? edgeGap : 0));
        var interiorWidth = Math.Max(0, work.Width - (edge is TaskbarEdge.Left or TaskbarEdge.Right ? edgeGap : 0));

        var minScale = Math.Max(0.1, options.MinWidth / options.BaseWidth);
        var scaleByWidth = interiorWidth / (options.BaseWidth * dpi);
        var scaleByHeight = interiorHeight / (options.BaseHeight * dpi);
        var scale = Math.Min(options.MaxScale, Math.Min(scaleByWidth, scaleByHeight));

        if (scale >= minScale && interiorWidth > 0 && interiorHeight > 0)
        {
            var size = new PixelRect(0, 0, options.BaseWidth * scale * dpi, options.BaseHeight * scale * dpi);

            // 横向任务栏停靠在工作区左端（靠近开始按钮一侧，避开时钟/通知区）；
            // 纵向任务栏停靠在工作区顶端。位置只依赖任务栏边缘，不猜开始按钮坐标。
            var (left, top) = PlaceNearEdge(work, size, edge, edgeGap, margin);
            var clamped = ClampInto(left, top, size.Width, size.Height, work, margin);
            return ToDips(clamped, size, scale, PlacementKind.NearTaskbar, edge, dpi,
                $"已停靠在{EdgeText(edge)}任务栏旁，距边缘 {options.GapDips:0.#} DIP，缩放 {scale:0.###} 倍");
        }

        // 附近空间不足：退到任务栏对侧角落，用最小可读尺寸，绝不缩到看不清。
        var minSize = new PixelRect(0, 0, options.BaseWidth * minScale * dpi, options.BaseHeight * minScale * dpi);
        var fallbackEdge = Opposite(edge);
        var fallback = PlaceAtCorner(work, minSize, fallbackEdge, margin);
        var fits = minSize.Width <= work.Width && minSize.Height <= work.Height;
        var fallbackClamped = ClampInto(fallback.Left, fallback.Top, minSize.Width, minSize.Height, work, margin);

        var kind = fits ? PlacementKind.FallbackCorner : PlacementKind.MinimumForced;
        var explanation = fits
            ? $"任务栏附近空间不足，已退到{EdgeText(fallbackEdge)}角落并保持最小可读尺寸"
            : "工作区小于最小可读尺寸，只能强制按最小尺寸放置";

        return ToDips(fallbackClamped, minSize, minScale, kind, fallbackEdge, dpi, explanation);
    }

    /// <summary>任务栏厚度：用任务栏矩形与显示器边缘的距离推算，不猜分辨率。</summary>
    public static double GetThickness(PixelRect taskbar, PixelRect monitor, TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Bottom => Math.Clamp(monitor.Bottom - taskbar.Top, 0, monitor.Height),
        TaskbarEdge.Top => Math.Clamp(taskbar.Bottom - monitor.Top, 0, monitor.Height),
        TaskbarEdge.Left => Math.Clamp(taskbar.Right - monitor.Left, 0, monitor.Width),
        TaskbarEdge.Right => Math.Clamp(monitor.Right - taskbar.Left, 0, monitor.Width),
        _ => 0,
    };

    /// <summary>从显示器与工作区的差异推断边缘；任务栏 API 不可用时作为降级路径。</summary>
    public static TaskbarEdge InferEdge(PixelRect monitor, PixelRect work)
    {
        if (work.IsEmpty || monitor.IsEmpty)
        {
            return TaskbarEdge.Unknown;
        }

        const double tolerance = 1.0;
        if (work.Bottom < monitor.Bottom - tolerance)
        {
            return TaskbarEdge.Bottom;
        }

        if (work.Top > monitor.Top + tolerance)
        {
            return TaskbarEdge.Top;
        }

        if (work.Left > monitor.Left + tolerance)
        {
            return TaskbarEdge.Left;
        }

        if (work.Right < monitor.Right - tolerance)
        {
            return TaskbarEdge.Right;
        }

        return TaskbarEdge.Unknown;
    }

    private static (double Left, double Top) PlaceNearEdge(
        PixelRect work, PixelRect size, TaskbarEdge edge, double edgeGap, double margin)
    {
        // 停靠方向：横向任务栏靠左（开始按钮一侧），纵向任务栏靠上；
        // 任务栏那一侧再让出 edgeGap，保证不贴边、不被滑出的任务栏盖住。
        return edge switch
        {
            TaskbarEdge.Top => (work.Left + margin, work.Top + edgeGap),
            TaskbarEdge.Bottom => (work.Left + margin, work.Bottom - edgeGap - size.Height),
            TaskbarEdge.Left => (work.Left + edgeGap, work.Top + margin),
            TaskbarEdge.Right => (work.Right - edgeGap - size.Width, work.Top + margin),
            _ => (work.Left + margin, work.Bottom - edgeGap - size.Height),
        };
    }

    private static (double Left, double Top) PlaceAtCorner(
        PixelRect work, PixelRect size, TaskbarEdge cornerEdge, double margin)
    {
        return cornerEdge switch
        {
            TaskbarEdge.Bottom => (work.Right - margin - size.Width, work.Bottom - margin - size.Height),
            TaskbarEdge.Top => (work.Right - margin - size.Width, work.Top + margin),
            TaskbarEdge.Left => (work.Left + margin, work.Top + margin),
            TaskbarEdge.Right => (work.Right - margin - size.Width, work.Top + margin),
            _ => (work.Right - margin - size.Width, work.Bottom - margin - size.Height),
        };
    }

    private static (double Left, double Top) ClampInto(
        double left, double top, double width, double height, PixelRect work, double margin)
    {
        var minLeft = work.Left + margin;
        var minTop = work.Top + margin;
        var maxLeft = Math.Max(minLeft, work.Right - margin - width);
        var maxTop = Math.Max(minTop, work.Bottom - margin - height);
        return (Math.Clamp(left, minLeft, maxLeft), Math.Clamp(top, minTop, maxTop));
    }

    private static CapsulePlacement ToDips(
        (double Left, double Top) position,
        PixelRect size,
        double scale,
        PlacementKind kind,
        TaskbarEdge edge,
        double dpi,
        string explanation) =>
        new(
            Math.Round(position.Left / dpi, 1),
            Math.Round(position.Top / dpi, 1),
            Math.Round(size.Width / dpi, 1),
            Math.Round(size.Height / dpi, 1),
            Math.Round(scale, 4),
            kind,
            edge,
            explanation);

    private static TaskbarEdge Opposite(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top => TaskbarEdge.Bottom,
        TaskbarEdge.Bottom => TaskbarEdge.Top,
        TaskbarEdge.Left => TaskbarEdge.Right,
        TaskbarEdge.Right => TaskbarEdge.Left,
        _ => TaskbarEdge.Top,
    };

    private static string EdgeText(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top => "顶部",
        TaskbarEdge.Bottom => "底部",
        TaskbarEdge.Left => "左侧",
        TaskbarEdge.Right => "右侧",
        _ => "未知",
    };
}