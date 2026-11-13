using System.Runtime.InteropServices;
using HotspotMeter.Core.Layout;

namespace HotspotMeter.App.Services;

/// <summary>一次任务栏检测的结果。保留原始输入，便于自检和设置窗口显示"依据是什么"。</summary>
public sealed record TaskbarDetection(TaskbarLayoutInput Input, string Description, string Source, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// 任务栏与显示器几何检测。
/// 主路径是 SHAppBarMessage(ABM_GETTASKBARPOS)，失败时用"显示器矩形 vs 工作区"推断边缘，
/// 两者都失败则回退到主显示器 + 100% DPI，绝不让检测异常影响窗口显示。
/// </summary>
public sealed class TaskbarDetector
{
    private const uint AbmGetState = 0x00000004;
    private const uint AbmGetTaskbarPos = 0x00000005;
    private const uint AbsAutoHide = 0x00000001;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    /// <summary>检测一次。必须在拥有窗口句柄的 UI 线程调用；异常路径返回降级结果而不抛出。</summary>
    public TaskbarDetection Detect(IntPtr windowHandle)
    {
        try
        {
            var monitor = GetTargetMonitor(windowHandle, out var taskbarRect);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return Fallback("GetMonitorInfo 失败");
            }

            var monitorRect = ToPixelRect(info.Monitor);
            var workRect = ToPixelRect(info.Work);
            var dpi = GetScale(monitor, windowHandle);

            // 任务栏可能不在窗口所在显示器上（多屏时用户只给主屏配了任务栏）。
            var hasTaskbar = taskbarRect is { IsEmpty: false };
            var edge = hasTaskbar
                ? DetectEdge(monitorRect, taskbarRect!.Value)
                : TaskbarLayoutCalculator.InferEdge(monitorRect, workRect);

            if (edge == TaskbarEdge.Unknown)
            {
                edge = TaskbarLayoutCalculator.InferEdge(monitorRect, workRect);
            }

            var autoHide = taskbarRect is not null && IsAutoHide();
            var input = new TaskbarLayoutInput(monitorRect, workRect, taskbarRect, edge, autoHide, dpi);
            var source = hasTaskbar ? "SHAppBarMessage(ABM_GETTASKBARPOS)" : "GetMonitorInfo 工作区推断";
            var description = Describe(input, source);
            return new TaskbarDetection(input, description, source, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return Fallback(ex.Message);
        }
    }

    /// <summary>把检测结果转成一行中文说明，设置窗口和自检日志都会用到。</summary>
    public static string Describe(TaskbarLayoutInput input, string source)
    {
        var dpi = input.DpiScale > 0 ? input.DpiScale : 1.0;
        var edge = input.Edge switch
        {
            TaskbarEdge.Top => "顶部",
            TaskbarEdge.Bottom => "底部",
            TaskbarEdge.Left => "左侧",
            TaskbarEdge.Right => "右侧",
            _ => "未识别",
        };

        var autoHide = input.AutoHide ? " · 自动隐藏" : string.Empty;
        return $"{edge}任务栏 · {dpi * 100:0}% · 工作区 {input.WorkArea.Width / dpi:0}×{input.WorkArea.Height / dpi:0} DIP{autoHide}（{source}）";
    }

    /// <summary>
    /// 选择窗口所在显示器，并只在任务栏属于同一显示器时采用它的矩形。
    /// 旧实现直接使用任务栏所在显示器，多屏时会把窗口算到主屏去。
    /// </summary>
    private static IntPtr GetTargetMonitor(IntPtr windowHandle, out PixelRect? taskbarRect)
    {
        taskbarRect = null;
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
        PixelRect? detectedTaskbar = null;
        if (SHAppBarMessage(AbmGetTaskbarPos, ref data) != IntPtr.Zero && !IsEmpty(data.Rect))
        {
            detectedTaskbar = ToPixelRect(data.Rect);
        }

        IntPtr monitor;
        if (windowHandle != IntPtr.Zero)
        {
            monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        }
        else if (detectedTaskbar is { } rectForMonitor)
        {
            var native = ToNative(rectForMonitor);
            monitor = MonitorFromRect(ref native, MonitorDefaultToNearest);
        }
        else
        {
            monitor = MonitorFromWindow(IntPtr.Zero, MonitorDefaultToPrimary);
        }

        // 任务栏通常只挂在主屏：窗口在副屏时不要拿主屏任务栏去裁剪副屏几何。
        if (detectedTaskbar is { } rect)
        {
            var native = ToNative(rect);
            if (MonitorFromRect(ref native, MonitorDefaultToNearest) == monitor)
            {
                taskbarRect = rect;
            }
        }

        return monitor;
    }

    private static NativeRect ToNative(PixelRect rect) => new()
    {
        Left = (int)rect.Left,
        Top = (int)rect.Top,
        Right = (int)rect.Right,
        Bottom = (int)rect.Bottom,
    };

    private static TaskbarEdge DetectEdge(PixelRect monitor, PixelRect taskbar)
    {
        // 用"任务栏矩形贴住哪条显示器边"判定，而不是用分辨率猜。
        var candidates = new (TaskbarEdge Edge, double Distance, bool Spans)[]
        {
            (TaskbarEdge.Bottom, Math.Abs(monitor.Bottom - taskbar.Bottom), taskbar.Width >= monitor.Width * 0.5),
            (TaskbarEdge.Top, Math.Abs(taskbar.Top - monitor.Top), taskbar.Width >= monitor.Width * 0.5),
            (TaskbarEdge.Left, Math.Abs(taskbar.Left - monitor.Left), taskbar.Height >= monitor.Height * 0.5),
            (TaskbarEdge.Right, Math.Abs(monitor.Right - taskbar.Right), taskbar.Height >= monitor.Height * 0.5),
        };

        var best = candidates.Where(c => c.Spans).OrderBy(c => c.Distance).FirstOrDefault();
        return best.Spans ? best.Edge : TaskbarEdge.Unknown;
    }

    private static bool IsAutoHide()
    {
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
        var state = (uint)SHAppBarMessage(AbmGetState, ref data).ToInt64();
        return (state & AbsAutoHide) != 0;
    }

    private static double GetScale(IntPtr monitor, IntPtr windowHandle)
    {
        try
        {
            if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (DllNotFoundException)
        {
            // Windows 8.1 以下没有 Shcore，退回窗口 DPI。
        }
        catch (EntryPointNotFoundException)
        {
        }

        var windowDpi = windowHandle != IntPtr.Zero ? GetDpiForWindow(windowHandle) : 96u;
        return windowDpi > 0 ? windowDpi / 96.0 : 1.0;
    }

    private static TaskbarDetection Fallback(string error)
    {
        // 最保守的降级：主显示器、整屏工作区、100% DPI，默认按底部任务栏处理。
        var monitor = MonitorFromWindow(IntPtr.Zero, MonitorDefaultToPrimary);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            var monitorRect = ToPixelRect(info.Monitor);
            var workRect = ToPixelRect(info.Work);
            var input = new TaskbarLayoutInput(
                monitorRect,
                workRect,
                null,
                TaskbarLayoutCalculator.InferEdge(monitorRect, workRect) is var e && e != TaskbarEdge.Unknown ? e : TaskbarEdge.Bottom,
                false,
                1.0);
            return new TaskbarDetection(input, Describe(input, "降级路径"), "降级路径", error);
        }

        var fallback = new TaskbarLayoutInput(
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            null,
            TaskbarEdge.Bottom,
            false,
            1.0);
        return new TaskbarDetection(fallback, Describe(fallback, "固定兜底"), "固定兜底", error);
    }

    private static bool IsEmpty(NativeRect rect) =>
        rect.Right <= rect.Left || rect.Bottom <= rect.Top;

    private static PixelRect ToPixelRect(NativeRect rect) =>
        new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Rect;
        public IntPtr Param;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}