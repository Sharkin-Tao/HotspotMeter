using HotspotMeter.Core.Layout;
using Xunit;

namespace HotspotMeter.Tests.Layout;

/// <summary>
/// 自动尺寸计算的边界：任务栏厚度、DPI、纵横任务栏、可读下限。
/// 这些用例不启动 WPF，只验证"多大"的决策，位置由用户拖动，不在这里计算。
/// </summary>
public sealed class TaskbarSizingCalculatorTests
{
    private static readonly PixelRect Monitor = new(0, 0, 2560, 1600);

    private static TaskbarSizingInput Input(
        double taskbarHeightPx = 72,
        double dpi = 1.5,
        TaskbarEdge edge = TaskbarEdge.Bottom,
        PixelRect? taskbar = null,
        PixelRect? work = null)
    {
        var rect = taskbar ?? new PixelRect(0, Monitor.Bottom - taskbarHeightPx, Monitor.Right, Monitor.Bottom);
        var workRect = work ?? new PixelRect(0, 0, Monitor.Right, Monitor.Bottom - taskbarHeightPx);
        return new TaskbarSizingInput(Monitor, workRect, rect, edge, dpi);
    }

    [Fact]
    public void BottomTaskbarAt150PercentFitsInside48Dips()
    {
        var fit = TaskbarSizingCalculator.Compute(Input());

        Assert.Equal(48, fit.TaskbarThicknessDips, 1);
        Assert.Equal(44, fit.Height, 1);
        Assert.Equal(135.4, fit.Width, 1);
        Assert.Equal(CapsuleDensity.Minimal, fit.Density);
        Assert.True(fit.FitsTaskbar);
        Assert.False(fit.ShowSpeedLabels);
    }

    [Theory]
    [InlineData(48, 1.0)]
    [InlineData(96, 2.0)]
    [InlineData(36, 0.75)]
    public void TaskbarThicknessIsNormalizedToDips(double taskbarPx, double dpi)
    {
        var monitor = new PixelRect(0, 0, 1920, 1080);
        var input = new TaskbarSizingInput(
            monitor,
            new PixelRect(0, 0, 1920, 1080 - taskbarPx),
            new PixelRect(0, 1080 - taskbarPx, 1920, 1080),
            TaskbarEdge.Bottom,
            dpi);

        var fit = TaskbarSizingCalculator.Compute(input);

        Assert.Equal(48, fit.TaskbarThicknessDips, 1);
        Assert.Equal(44, fit.Height, 1);
    }

    [Fact]
    public void TallTaskbarUsesCompactDensityAndKeepsLabels()
    {
        var fit = TaskbarSizingCalculator.Compute(Input(taskbarHeightPx: 90, dpi: 1.5));

        Assert.Equal(60, fit.TaskbarThicknessDips, 1);
        Assert.Equal(56, fit.Height, 1);
        Assert.Equal(CapsuleDensity.Compact, fit.Density);
        Assert.True(fit.ShowSpeedLabels);
        Assert.True(fit.FitsTaskbar);
    }

    [Fact]
    public void ThicknessAtLeast88DipsUsesNormalDensity()
    {
        var fit = TaskbarSizingCalculator.Compute(Input(taskbarHeightPx: 138, dpi: 1.5));

        Assert.Equal(92, fit.TaskbarThicknessDips, 1);
        Assert.Equal(88, fit.Height, 1);
        Assert.Equal(CapsuleDensity.Normal, fit.Density);
        Assert.True(fit.ShowSpeedLabels);
    }

    [Fact]
    public void HeightIsCappedAtStandardSize()
    {
        var fit = TaskbarSizingCalculator.Compute(Input(taskbarHeightPx: 180, dpi: 1.5));

        Assert.Equal(104, fit.Height, 1);
        Assert.Equal(320, fit.Width, 1);
        Assert.Equal(CapsuleDensity.Normal, fit.Density);
    }

    [Fact]
    public void TooThinTaskbarIsReportedInsteadOfPretendingToFit()
    {
        var fit = TaskbarSizingCalculator.Compute(Input(taskbarHeightPx: 60, dpi: 1.5));

        Assert.Equal(40, fit.TaskbarThicknessDips, 1);
        Assert.Equal(40, fit.Height, 1);
        Assert.False(fit.FitsTaskbar);
        Assert.Contains("低于", fit.Explanation);
    }

    [Fact]
    public void MissingTaskbarRectFallsBackToWorkAreaDifference()
    {
        var work = new PixelRect(0, 0, 2560, 1528);
        var fit = TaskbarSizingCalculator.Compute(Input(taskbar: null, work: work) with { TaskbarRect = null });

        Assert.Equal(48, fit.TaskbarThicknessDips, 1);
    }

    [Fact]
    public void VerticalTaskbarUsesWorkAreaHeightInsteadOfThickness()
    {
        var monitor = new PixelRect(0, 0, 2560, 1600);
        var input = new TaskbarSizingInput(
            monitor,
            new PixelRect(64, 0, 2560, 1600),
            new PixelRect(0, 0, 64, 1600),
            TaskbarEdge.Left,
            1.5);

        var fit = TaskbarSizingCalculator.Compute(input);

        Assert.Equal(CapsuleDensity.Normal, fit.Density);
        Assert.Equal(104, fit.Height, 1);
        Assert.True(fit.FitsTaskbar);
        Assert.Contains("不限制高度", fit.Explanation);
    }

    [Fact]
    public void WidthNeverExceedsNarrowWorkArea()
    {
        var monitor = new PixelRect(0, 0, 140, 400);
        var input = new TaskbarSizingInput(
            monitor,
            new PixelRect(0, 0, 140, 352),
            new PixelRect(0, 352, 140, 400),
            TaskbarEdge.Bottom,
            1.0);

        var fit = TaskbarSizingCalculator.Compute(input);

        Assert.Equal(132, fit.Width, 1);
        Assert.True(fit.Width <= 140 - 16 + 8);
    }

    [Fact]
    public void NormalDensityKeepsAspectRatio()
    {
        var fit = TaskbarSizingCalculator.Compute(Input(taskbarHeightPx: 150, dpi: 1.5));

        Assert.Equal(TaskbarSizingCalculator.AspectRatio, fit.Width / fit.Height, 2);
    }
}