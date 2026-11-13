using HotspotMeter.Core.Layout;
using Xunit;

namespace HotspotMeter.Tests.Layout;

/// <summary>
/// 自动定位的坐标规则：只支持底部任务栏、左侧留 120 DIP 偏移、垂直居中于任务栏、
/// 多显示器负坐标与 DPI 换算必须正确。位置一旦算出即为 DIP，窗口不再二次猜测。
/// </summary>
public sealed class TaskbarAnchorCalculatorTests
{
    private static TaskbarLayoutInput Input(
        TaskbarEdge edge = TaskbarEdge.Bottom,
        PixelRect? monitor = null,
        PixelRect? taskbar = null,
        double dpi = 1.5,
        bool autoHide = false)
    {
        var monitorRect = monitor ?? new PixelRect(0, 0, 2560, 1600);
        var taskbarRect = taskbar ?? new PixelRect(monitorRect.Left, 1528, monitorRect.Right, 1600);
        var work = new PixelRect(monitorRect.Left, monitorRect.Top, monitorRect.Right, taskbarRect.Top);
        return new TaskbarLayoutInput(monitorRect, work, taskbarRect, edge, autoHide, dpi);
    }

    [Fact]
    public void BottomTaskbarPlacesCapsuleAt120DipsAndCentersVertically()
    {
        var anchor = TaskbarAnchorCalculator.Compute(Input(), 135.4, 44);

        Assert.True(anchor.Supported);
        Assert.Equal(120, anchor.Left, 1);
        // 任务栏 72 px、胶囊 66 px：top = 1528 + 3 = 1531 px → 1020.7 DIP。
        Assert.Equal(1020.7, anchor.Top, 1);
    }

    [Fact]
    public void NarrowMonitorClampsLeftEdgeInsteadOfOverflowing()
    {
        var monitor = new PixelRect(0, 0, 240, 1000);
        var taskbar = new PixelRect(0, 952, 240, 1000);
        var anchor = TaskbarAnchorCalculator.Compute(Input(monitor: monitor, taskbar: taskbar, dpi: 1), 135.4, 44);

        Assert.True(anchor.Supported);
        // 240 - 135.4 - 8 = 96.6 DIP，不能继续保持 120。
        Assert.Equal(96.6, anchor.Left, 1);
        Assert.Equal(954, anchor.Top, 1);
    }

    [Fact]
    public void SecondaryMonitorWithNegativeCoordinatesKeepsAbsolutePosition()
    {
        var monitor = new PixelRect(-1920, 0, 0, 1080);
        var taskbar = new PixelRect(-1920, 1032, 0, 1080);
        var anchor = TaskbarAnchorCalculator.Compute(Input(monitor: monitor, taskbar: taskbar, dpi: 1), 320, 44);

        Assert.True(anchor.Supported);
        Assert.Equal(-1800, anchor.Left, 1);
        Assert.Equal(1034, anchor.Top, 1);
    }

    [Fact]
    public void Dpi200ConvertsOffsetAndTaskbarHeight()
    {
        var monitor = new PixelRect(0, 0, 3840, 2400);
        var taskbar = new PixelRect(0, 2304, 3840, 2400);
        var anchor = TaskbarAnchorCalculator.Compute(Input(monitor: monitor, taskbar: taskbar, dpi: 2), 135.4, 44);

        Assert.True(anchor.Supported);
        // 偏移量是 DIP：120 DIP 在 200% 下仍是 120 DIP（物理 240 px）。
        Assert.Equal(120, anchor.Left, 1);
        // (96 - 88) / 2 = 4 px → 2308 px → 1154 DIP。
        Assert.Equal(1154, anchor.Top, 1);
    }

    [Theory]
    [InlineData(TaskbarEdge.Top)]
    [InlineData(TaskbarEdge.Left)]
    [InlineData(TaskbarEdge.Right)]
    public void NonBottomTaskbarIsNotAutomaticallyPositioned(TaskbarEdge edge)
    {
        var anchor = TaskbarAnchorCalculator.Compute(Input(edge: edge), 135.4, 44);

        Assert.False(anchor.Supported);
        Assert.Contains("仅支持底部任务栏", anchor.Explanation);
    }

    [Fact]
    public void MissingTaskbarRectKeepsUserPosition()
    {
        var input = Input() with { TaskbarRect = null };

        var anchor = TaskbarAnchorCalculator.Compute(input, 135.4, 44);

        Assert.False(anchor.Supported);
        Assert.Contains("未检测到任务栏矩形", anchor.Explanation);
    }

    [Fact]
    public void CapsuleTallerThanTaskbarIsNotPositionedInsideIt()
    {
        var anchor = TaskbarAnchorCalculator.Compute(Input(), 320, 104);

        Assert.False(anchor.Supported);
        Assert.Contains("无法完整放入任务栏内部", anchor.Explanation);
    }
}