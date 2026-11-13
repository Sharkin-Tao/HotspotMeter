using HotspotMeter.Core.Layout;

namespace HotspotMeter.Tests.Layout;

/// <summary>
/// 任务栏几何计算的单元测试。用合成矩形覆盖四边任务栏、多显示器、DPI 与空间不足，
/// 因为无法为了测试去改动真实用户的任务栏位置。
/// </summary>
public sealed class TaskbarLayoutCalculatorTests
{
    private static TaskbarLayoutInput Input(
        PixelRect monitor,
        PixelRect work,
        PixelRect? taskbar,
        TaskbarEdge edge,
        bool autoHide = false,
        double dpi = 1.0) =>
        new(monitor, work, taskbar, edge, autoHide, dpi);

    [Fact]
    public void BottomTaskbar_DocksAtBottomLeftAboveWorkArea()
    {
        // 2560×1600 @150%，底部任务栏高 64px，工作区到 1536px。
        var result = TaskbarLayoutCalculator.Compute(Input(
            new PixelRect(0, 0, 2560, 1600),
            new PixelRect(0, 0, 2560, 1536),
            new PixelRect(0, 1536, 2560, 1600),
            TaskbarEdge.Bottom,
            dpi: 1.5));

        Assert.Equal(PlacementKind.NearTaskbar, result.Kind);
        Assert.Equal(TaskbarEdge.Bottom, result.Edge);
        Assert.Equal(320, result.Width, 1);
        Assert.Equal(104, result.Height, 1);
        Assert.Equal(8.0, result.Left, 1);
        Assert.Equal(908.0, result.Top, 1);
    }

    [Fact]
    public void TopTaskbar_DocksBelowWorkAreaTop()
    {
        var result = TaskbarLayoutCalculator.Compute(Input(
            new PixelRect(0, 0, 2560, 1600),
            new PixelRect(0, 64, 2560, 1600),
            new PixelRect(0, 0, 2560, 64),
            TaskbarEdge.Top,
            dpi: 1.5));

        Assert.Equal(PlacementKind.NearTaskbar, result.Kind);
        // 工作区从任务栏下沿开始（64px），再让出 12 DIP 的间距。
        Assert.Equal(54.7, result.Top, 1);
        Assert.Equal(8.0, result.Left, 1);
    }

    [Fact]
    public void LeftTaskbar_DocksAtWorkAreaLeft()
    {
        var result = TaskbarLayoutCalculator.Compute(Input(
            new PixelRect(0, 0, 2560, 1600),
            new PixelRect(64, 0, 2560, 1600),
            new PixelRect(0, 0, 64, 1600),
            TaskbarEdge.Left,
            dpi: 1.5));

        Assert.Equal(PlacementKind.NearTaskbar, result.Kind);
        Assert.Equal(54.7, result.Left, 1);
        Assert.Equal(8.0, result.Top, 1);
    }

    [Fact]
    public void RightTaskbar_DocksAtWorkAreaRight()
    {
        var result = TaskbarLayoutCalculator.Compute(Input(
            new PixelRect(0, 0, 2560, 1600),
            new PixelRect(0, 0, 2496, 1600),
            new PixelRect(2496, 0, 2560, 1600),
            TaskbarEdge.Right,
            dpi: 1.5));

        Assert.Equal(PlacementKind.NearTaskbar, result.Kind);
        Assert.Equal(1332.0, result.Left, 1);
        Assert.Equal(8.0, result.Top, 1);
    }

    [Fact]
    public void AutoHideTaskbar_LeavesRoomForTaskbarThickness()
    {
        // 自动隐藏时工作区等于整屏，必须额外让出"厚度 + 间距"，否则任务栏滑出会盖住胶囊。
        var result = TaskbarLayoutCalculator.Compute(Input(
            new PixelRect(0, 0, 2560, 1600),
            new PixelRect(0, 0, 2560, 1600),
            new PixelRect(0, 1552, 2560, 1600),
            TaskbarEdge.Bottom,
            autoHide: true,
            dpi: 1.5));

        Assert.Equal(PlacementKind.NearTaskbar, result.Kind);
        Assert.Equal(918.7, result.Top, 1);
    }

    [Fact]
    public void SecondMonitor_UsesAbsoluteCoordinates()
    {
        // 副屏位于主屏右侧：x 从 1920 开始，结果不能偷偷回到 0 起点。
        var result = TaskbarLayoutCalculator.Compute(Input(
            new PixelRect(1920, 0, 4480, 1440),
            new PixelRect(1920, 0, 4480, 1400),
            new PixelRect(1920, 1400, 4480, 1440),
            TaskbarEdge.Bottom));

        Assert.Equal(PlacementKind.NearTaskbar, result.Kind);
        Assert.Equal(1928.0, result.Left, 1);
        Assert.Equal(1284.0, result.Top, 1);
    }

    [Fact]
    public void Dpi200_ConvertsPhysicalMarginAndWindowSize()
    {
        var result = TaskbarLayoutCalculator.Compute(Input(
            new PixelRect(0, 0, 3840, 2400),
            new PixelRect(0, 0, 3840, 2304),
            new PixelRect(0, 2304, 3840, 2400),
            TaskbarEdge.Bottom,
            dpi: 2.0));

        Assert.Equal(320, result.Width, 1);
        Assert.Equal(104, result.Height, 1);
        Assert.Equal(8.0, result.Left, 1);
        Assert.Equal(1036.0, result.Top, 1);
    }

    [Fact]
    public void NarrowSpace_ShrinksProportionallyButNeverBelowMinimum()
    {
        // 工作区宽 300 DIP：等比缩到 0.9375，仍高于最小可读宽度 220。
        var result = TaskbarLayoutCalculator.Compute(Input(
            new PixelRect(0, 0, 300, 900),
            new PixelRect(0, 0, 300, 860),
            new PixelRect(0, 860, 300, 900),
            TaskbarEdge.Bottom));

        Assert.Equal(PlacementKind.NearTaskbar, result.Kind);
        Assert.Equal(0.9375, result.Scale, 3);
        Assert.Equal(300, result.Width, 1);
        Assert.Equal(97.5, result.Height, 1);
    }

    [Fact]
    public void SpaceBelowMinimum_FallsBackToOppositeCornerWithReadableSize()
    {
        // 自动隐藏任务栏异常厚，留给胶囊的高度不足最小尺寸；工作区本身足够大，应退到顶部角落。
        var result = TaskbarLayoutCalculator.Compute(Input(
            new PixelRect(0, 0, 800, 600),
            new PixelRect(0, 0, 800, 600),
            new PixelRect(0, 50, 800, 600),
            TaskbarEdge.Bottom,
            autoHide: true));

        Assert.Equal(PlacementKind.FallbackCorner, result.Kind);
        Assert.Equal(TaskbarEdge.Top, result.Edge);
        Assert.Equal(220, result.Width, 1);
        Assert.Equal(71.5, result.Height, 1);
        Assert.Equal(572.0, result.Left, 1);
        Assert.Equal(8.0, result.Top, 1);
    }

    [Fact]
    public void WorkAreaSmallerThanMinimum_ForcesMinimumSize()
    {
        var result = TaskbarLayoutCalculator.Compute(Input(
            new PixelRect(0, 0, 200, 120),
            new PixelRect(0, 0, 200, 100),
            new PixelRect(0, 100, 200, 120),
            TaskbarEdge.Bottom));

        Assert.Equal(PlacementKind.MinimumForced, result.Kind);
        Assert.Equal(220, result.Width, 1);
        Assert.Equal(8.0, result.Left, 1);
    }

    [Fact]
    public void AutoFitNeverGrowsBeyondStandardSize()
    {
        // 4K 屏幕空间充足时保持标准尺寸，不放大到 480 宽，避免观感突变。
        var result = TaskbarLayoutCalculator.Compute(Input(
            new PixelRect(0, 0, 3840, 2160),
            new PixelRect(0, 0, 3840, 2120),
            new PixelRect(0, 2120, 3840, 2160),
            TaskbarEdge.Bottom,
            dpi: 1.5));

        Assert.Equal(1.0, result.Scale, 3);
        Assert.Equal(320, result.Width, 1);
        Assert.Equal(104, result.Height, 1);
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040, 1920, 1080, TaskbarEdge.Bottom)]
    [InlineData(0, 40, 1920, 1080, 1920, 1080, TaskbarEdge.Top)]
    [InlineData(64, 0, 1920, 1080, 1920, 1080, TaskbarEdge.Left)]
    [InlineData(0, 0, 1856, 1080, 1920, 1080, TaskbarEdge.Right)]
    [InlineData(0, 0, 1920, 1080, 1920, 1080, TaskbarEdge.Unknown)]
    public void InferEdge_UsesWorkAreaDifference(
        double workLeft, double workTop, double workRight, double workBottom,
        double monitorWidth, double monitorHeight, TaskbarEdge expected)
    {
        var monitor = new PixelRect(0, 0, monitorWidth, monitorHeight);
        var work = new PixelRect(workLeft, workTop, workRight, workBottom);

        Assert.Equal(expected, TaskbarLayoutCalculator.InferEdge(monitor, work));
    }
}