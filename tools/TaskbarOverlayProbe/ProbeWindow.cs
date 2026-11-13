using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarOverlayProbe;
using Brushes = System.Windows.Media.Brushes;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Image = System.Drawing.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace TaskbarOverlayProbe;

/// <summary>
/// 独立实验窗口：尝试把胶囊放进任务栏矩形内部，并记录可核对的证据。
/// 不修改 Explorer，不注入任何进程；只使用公开的 AppBar / 窗口 API。
/// </summary>
internal sealed class ProbeWindow : Window
{
    private readonly ProbeLog _log;
    private readonly string _outputDirectory;
    private readonly int _runSeconds;
    private readonly bool _keepOpen;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _checkpoint;
    private IntPtr _hwnd;
    private IntPtr _taskbar;
    private NativeMethods.RECT _taskbarRect;
    private uint _dpi = 96;
    private string _edgeName = "unknown";

    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;
    private int _dragMoves;
    private uint _taskbarCreatedMessage;
    private System.Windows.Interop.HwndSource? _source;

    public ProbeWindow(ProbeLog log, string outputDirectory, int runSeconds, bool keepOpen)
    {
        _log = log;
        _outputDirectory = outputDirectory;
        _runSeconds = runSeconds;
        _keepOpen = keepOpen;

        Title = "TaskbarOverlayProbe";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = BuildSurface();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonUp += OnMouseRightButtonUp;

        _timer.Tick += OnTick;
    }

    private UIElement BuildSurface()
    {
        var title = new TextBlock
        {
            Text = "任务栏内测试胶囊",
            FontSize = 9,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xF5, 0xF7)),
            Opacity = 0.85,
        };

        var traffic = new TextBlock
        {
            Text = "1.23 GB",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
        };

        var speed = new TextBlock
        {
            Text = "↓ 2.4 MB/s   ↑ 128 KB/s",
            FontSize = 9,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xF5, 0xF7)),
            Opacity = 0.9,
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        stack.Children.Add(title);
        stack.Children.Add(traffic);
        stack.Children.Add(speed);

        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x8E, 0x3E)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x30, 0xD1, 0x58)),
            BorderThickness = new Thickness(1),
            Child = stack,
        };

        return border;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        // 不抢焦点、不进入 Alt+Tab：贴近“任务栏部件”的行为，同时避免打断用户正在做的事。
        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _source = System.Windows.Interop.HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        _log.Write($"hwnd=0x{_hwnd.ToInt64():X} taskbarCreatedMessage=0x{_taskbarCreatedMessage:X}");
        DetectTaskbar();
        PlaceInsideTaskbar();

        _timer.Start();
        _log.Write($"timer started; auto-exit in {_runSeconds}s unless --keep");
    }

    private void DetectTaskbar()
    {
        _taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (_taskbar == IntPtr.Zero)
        {
            _taskbar = NativeMethods.FindWindow("Shell_SecondaryTrayWnd", null);
        }

        var data = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>(),
        };

        var ok = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref data);
        if (ok == UIntPtr.Zero || data.rc.Width <= 0 || data.rc.Height <= 0)
        {
            _taskbarRect = default;
            _log.Write("SHAppBarMessage(ABM_GETTASKBARPOS) failed; no taskbar rect");
            return;
        }

        _taskbarRect = data.rc;
        _edgeName = data.uEdge switch
        {
            NativeMethods.ABE_LEFT => "left",
            NativeMethods.ABE_TOP => "top",
            NativeMethods.ABE_RIGHT => "right",
            NativeMethods.ABE_BOTTOM => "bottom",
            _ => "unknown",
        };

        var state = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>(),
        };
        var stateResult = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref state);
        var autoHide = ((int)stateResult.ToUInt64() & NativeMethods.ABS_AUTOHIDE) != 0;

        _dpi = _taskbar != IntPtr.Zero ? NativeMethods.GetDpiForWindow(_taskbar) : 96;
        if (_dpi == 0)
        {
            _dpi = 96;
        }

        var monitor = NativeMethods.MonitorFromRect(ref _taskbarRect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        var hasMonitor = NativeMethods.GetMonitorInfo(monitor, ref info);

        _log.Write(
            $"taskbar hwnd=0x{_taskbar.ToInt64():X} edge={_edgeName} rect={_taskbarRect.Left},{_taskbarRect.Top},{_taskbarRect.Right},{_taskbarRect.Bottom} " +
            $"size={_taskbarRect.Width}x{_taskbarRect.Height} dpi={_dpi} autoHide={autoHide} " +
            $"monitor={(hasMonitor ? $"{info.rcMonitor.Left},{info.rcMonitor.Top},{info.rcMonitor.Width}x{info.rcMonitor.Height} work={info.rcWork.Width}x{info.rcWork.Height}" : "unavailable")}");
    }

    private void PlaceInsideTaskbar()
    {
        if (_taskbarRect.Width <= 0 || _taskbarRect.Height <= 0)
        {
            _log.Write("place skipped: empty taskbar rect");
            return;
        }

        const double ratio = 320.0 / 104.0;
        var inset = 4;
        int width;
        int height;
        int x;
        int y;

        if (_edgeName is "left" or "right")
        {
            // 竖向任务栏：横向胶囊放不进高度方向，改为“按任务栏宽度缩放”，仍限制在矩形内部。
            width = Math.Max(24, _taskbarRect.Width - (inset * 2));
            height = Math.Max(12, (int)Math.Round(width / ratio));
            x = _taskbarRect.Left + inset;
            y = _taskbarRect.Top + 12;
        }
        else
        {
            height = Math.Max(20, _taskbarRect.Height - (inset * 2));
            width = Math.Max(60, (int)Math.Round(height * ratio));
            width = Math.Min(width, Math.Max(60, _taskbarRect.Width - (inset * 2)));
            x = _taskbarRect.Left + 12;
            y = _taskbarRect.Top + inset;

            // 任务栏按钮是真实交互区：优先放进最大的“空档”，而不是固定压在最左边。
            var gap = FindLargestHorizontalGap(width + 24);
            if (gap is { } free)
            {
                x = Math.Min(free.Left + 12, free.Right - width - 8);
                _log.Write($"uia gap selected: {free.Left}..{free.Right} -> x={x}");
            }
            else
            {
                _log.Write("uia gap: no gap large enough; falling back to left edge (may overlap system buttons)");
            }
        }

        Width = width * 96.0 / _dpi;
        Height = height * 96.0 / _dpi;

        var ok = NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        _log.Write($"place edge={_edgeName} physical={x},{y} {width}x{height} dpi={_dpi} dip={Width:F1}x{Height:F1} setWindowPos={ok}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage || msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            _log.Write($"window message 0x{msg:X}: taskbar/display changed; re-detect in 800 ms");
            ReDetectSoon();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ReDetectSoon()
    {
        // Explorer 重建任务栏时，新任务栏窗口可能在本消息之后才创建完成，稍等再检测。
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            DetectTaskbar();
            PlaceInsideTaskbar();
            var ok = NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            _log.Write($"re-detect done; reassert topmost={ok}");
        };
        delay.Start();
    }

    private (int Left, int Right)? FindLargestHorizontalGap(int minimumWidth)
    {
        if (_taskbar == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var root = AutomationElement.FromHandle(_taskbar);
            if (root is null)
            {
                return null;
            }

            var all = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            var intervals = new List<(int Left, int Right)>();
            foreach (AutomationElement element in all)
            {
                // 只看真实按钮（开始、搜索、任务视图、应用、托盘图标）；
                // 任务栏里的容器/文本会横跨整条，导致“无空隙”的假象。
                if (element.Current.ControlType != ControlType.Button || element.Current.IsOffscreen)
                {
                    continue;
                }

                var bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width <= 0)
                {
                    continue;
                }

                if (bounds.Bottom <= _taskbarRect.Top || bounds.Top >= _taskbarRect.Bottom)
                {
                    continue;
                }

                intervals.Add(((int)Math.Round(bounds.Left), (int)Math.Round(bounds.Right)));
                if (intervals.Count > 400)
                {
                    break;
                }
            }

            intervals.Sort((a, b) => a.Left.CompareTo(b.Left));

            var cursor = _taskbarRect.Left;
            (int Left, int Right)? best = null;
            foreach (var interval in intervals)
            {
                if (interval.Left > cursor)
                {
                    var right = Math.Min(interval.Left, _taskbarRect.Right);
                    if (right > cursor && (best is null || right - cursor > best.Value.Right - best.Value.Left))
                    {
                        best = (cursor, right);
                    }
                }

                cursor = Math.Max(cursor, interval.Right);
            }

            if (cursor < _taskbarRect.Right)
            {
                if (best is null || _taskbarRect.Right - cursor > best.Value.Right - best.Value.Left)
                {
                    best = (cursor, _taskbarRect.Right);
                }
            }

            _log.Write($"uia items={intervals.Count} bestGap={(best is { } g ? $"{g.Left}..{g.Right}" : "none")} required={minimumWidth}");
            return best is { } found && found.Right - found.Left >= minimumWidth ? found : null;
        }
        catch (Exception ex)
        {
            _log.Write($"uia failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _checkpoint++;

        if (!NativeMethods.GetWindowRect(_hwnd, out var overlayRect))
        {
            overlayRect = default;
        }

        var center = new NativeMethods.POINT
        {
            X = overlayRect.Left + (overlayRect.Width / 2),
            Y = overlayRect.Top + (overlayRect.Height / 2),
        };
        var hit = NativeMethods.WindowFromPoint(center);
        var hitClass = ClassNameOf(hit);
        var overlayRank = ZRankOf(_hwnd);
        var taskbarRank = ZRankOf(_taskbar);
        var foreground = ClassNameOf(NativeMethods.GetForegroundWindow());
        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var isTopmost = (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;
        var aboveTaskbar = overlayRank >= 0 && taskbarRank >= 0 && overlayRank < taskbarRank;

        _log.Write(
            $"checkpoint={_checkpoint} overlay={overlayRect.Left},{overlayRect.Top},{overlayRect.Width}x{overlayRect.Height} " +
            $"hit={hitClass} hitIsOverlay={hit == _hwnd} zOverlay={overlayRank} zTaskbar={taskbarRank} aboveTaskbar={aboveTaskbar} " +
            $"topmost={isTopmost} foreground={foreground}");

        SaveScreenshot($"checkpoint-{_checkpoint}");

        if (_checkpoint == 1)
        {
            RunSyntheticClickTest();
        }
        else if (_checkpoint == 4)
        {
            RunSyntheticDragTest();
        }
        else if (_checkpoint == 6)
        {
            RunSyntheticRightClickTest();
        }

        if (_checkpoint == 2)
        {
            // 模拟“用户去点别的程序”：把桌面 Shell 提到前台，观察任务栏与探针的层级。
            var shell = NativeMethods.GetShellWindow();
            var switched = NativeMethods.SetForegroundWindow(shell);
            _log.Write($"switch foreground to shell=0x{shell.ToInt64():X} result={switched}");
        }
        else if (_checkpoint == 3 && !aboveTaskbar)
        {
            var ok = NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            _log.Write($"reassert topmost after switch ok={ok}");
        }

        if (!_keepOpen && _checkpoint * 2 >= _runSeconds)
        {
            _log.Write("probe finished");
            Close();
        }
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "(null)";
        }

        var builder = new StringBuilder(256);
        var length = NativeMethods.GetClassName(hwnd, builder, builder.Capacity);
        return length > 0 ? builder.ToString(0, length) : "(unknown)";
    }

    private static int ZRankOf(IntPtr target)
    {
        if (target == IntPtr.Zero)
        {
            return -1;
        }

        var current = NativeMethods.GetTopWindow(IntPtr.Zero);
        var index = 0;
        while (current != IntPtr.Zero && index < 4000)
        {
            if (current == target)
            {
                return index;
            }

            current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT);
            index++;
        }

        return -1;
    }

    private void SaveScreenshot(string name)
    {
        try
        {
            if (_taskbarRect.Width <= 0)
            {
                return;
            }

            const int pad = 2;
            var x = Math.Max(0, _taskbarRect.Left - pad);
            var y = Math.Max(0, _taskbarRect.Top - pad);
            var width = _taskbarRect.Width + (pad * 2);
            var height = _taskbarRect.Height + (pad * 2);

            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            var memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            var bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            var previous = NativeMethods.SelectObject(memoryDc, bitmap);

            // CAPTUREBLT 把分层/透明窗口一起抓进来；否则 BitBlt 可能漏掉 WPF 窗口。
            var copied = NativeMethods.BitBlt(
                memoryDc, 0, 0, width, height, screenDc, x, y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            NativeMethods.SelectObject(memoryDc, previous);
            NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);

            if (!copied)
            {
                NativeMethods.DeleteObject(bitmap);
                _log.Write($"screenshot {name}: BitBlt failed");
                return;
            }

            using (var image = Image.FromHbitmap(bitmap))
            {
                var path = Path.Combine(_outputDirectory, $"{name}.png");
                image.Save(path, ImageFormat.Png);
                _log.Write($"screenshot {name} -> {path} ({width}x{height})");
            }

            NativeMethods.DeleteObject(bitmap);
        }
        catch (Exception ex)
        {
            _log.Write($"screenshot {name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        NativeMethods.GetCursorPos(out _dragStartCursor);
        NativeMethods.GetWindowRect(_hwnd, out _dragStartRect);
        _dragging = true;
        _dragMoves = 0;
        CaptureMouse();
        _log.Write("mouse: left down (drag start)");
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        NativeMethods.GetCursorPos(out var cursor);
        var dx = cursor.X - _dragStartCursor.X;
        var dy = cursor.Y - _dragStartCursor.Y;
        var moved = NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOPMOST,
            _dragStartRect.Left + dx, _dragStartRect.Top + dy, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        _dragMoves++;
        if (!moved)
        {
            _log.Write("mouse: drag SetWindowPos failed");
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        NativeMethods.GetWindowRect(_hwnd, out var rect);
        _log.Write($"mouse: drag end moves={_dragMoves} rect={rect.Left},{rect.Top},{rect.Width}x{rect.Height}");
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _log.Write("mouse: right click");

        var menu = new ContextMenu();
        var probeItem = new MenuItem { Header = "测试菜单项（写入日志）" };
        probeItem.Click += (_, _) => _log.Write("menu: test item clicked");
        var exitItem = new MenuItem { Header = "退出探针" };
        exitItem.Click += (_, _) =>
        {
            _log.Write("menu: exit clicked");
            Close();
        };
        menu.Items.Add(probeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        menu.Opened += (_, _) => _log.Write("menu: opened");
        menu.Closed += (_, _) => _log.Write("menu: closed");
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void RunSyntheticClickTest()
    {
        NativeMethods.GetWindowRect(_hwnd, out var rect);
        var x = rect.Left + (rect.Width / 2);
        var y = rect.Top + (rect.Height / 2);

        NativeMethods.SetCursorPos(x, y);
        Thread.Sleep(80);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        _log.Write($"interaction: synthetic left click at {x},{y}");
    }

    private void RunSyntheticDragTest()
    {
        NativeMethods.GetWindowRect(_hwnd, out var before);
        var x = before.Left + (before.Width / 2);
        var y = before.Top + (before.Height / 2);

        NativeMethods.SetCursorPos(x, y);
        Thread.Sleep(60);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        for (var step = 1; step <= 6; step++)
        {
            NativeMethods.SetCursorPos(x + (step * 3), y);
            Thread.Sleep(40);
        }

        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        NativeMethods.GetWindowRect(_hwnd, out var after);
        _log.Write($"interaction: synthetic drag from {before.Left},{before.Top} to {after.Left},{after.Top} (inside taskbar={after.Top >= _taskbarRect.Top && after.Bottom <= _taskbarRect.Bottom})");
    }

    private void RunSyntheticRightClickTest()
    {
        NativeMethods.GetWindowRect(_hwnd, out var rect);
        var x = rect.Left + (rect.Width / 2);
        var y = rect.Top + (rect.Height / 2);

        NativeMethods.SetCursorPos(x, y);
        Thread.Sleep(60);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        _log.Write($"interaction: synthetic right click at {x},{y}");

        var closer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        closer.Tick += (_, _) =>
        {
            closer.Stop();
            NativeMethods.keybd_event(NativeMethods.VK_ESCAPE, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_ESCAPE, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            _log.Write("interaction: escape sent to close menu");
        };
        closer.Start();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        NativeMethods.GetWindowRect(_hwnd, out var rect);
        _log.Write($"closing; final rect={rect.Left},{rect.Top},{rect.Width}x{rect.Height}");
        _log.Flush();
    }
}