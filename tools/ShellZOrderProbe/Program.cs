using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ShellZOrderProbe;

/// <summary>
/// 临时诊断工具：记录前台窗口、其顶层祖先与顶层窗口 Z 序，模拟 Win 键/点击任务栏，
/// 验证 HWND_TOPMOST 重断言是否能把胶囊抬到开始菜单之上。只读窗口信息 + 发送输入。
/// </summary>
internal static class Program
{
    private const int GwlExstyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const uint GwHwndNext = 2;
    private const uint GaRoot = 2;
    private const uint GaParent = 1;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const int DwmwaCloaked = 14;
    private static readonly IntPtr HwndTopmost = new(-1);

    private static readonly List<string> Lines = [];
    private static string _logPath = "shell-zorder.log";

    private static void Main(string[] args)
    {
        _logPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "shell-zorder.log");
        TrySetDpiAwareness();

        Log($"probe start {DateTime.Now:O} dpiAwareness=permonitorv2");
        // 先点桌面空白，确保没有残留的开始菜单/搜索浮层影响基线。
        Click(1200, 300);
        Thread.Sleep(1000);
        TapKey(0x1B);
        Thread.Sleep(500);
        Log("== baseline ==");
        Dump();
        Log($"pixels baseline: {PixelText(150, 1510, 300, 100)}");
        DumpTopColors(150, 1510, 300, 100);
        PrintCapsuleColors();

        var tbDiag = FindWindow("Shell_TrayWnd", null);
        if (tbDiag != IntPtr.Zero)
        {
            ShowWindow(tbDiag, 0);
            Thread.Sleep(500);
            Log($"pixels taskbarHidden: {PixelText(150, 1510, 300, 100)}");
            ShowWindow(tbDiag, 5);
            Thread.Sleep(1500);
            Log($"pixels taskbarShown+1.5s: {PixelText(150, 1510, 300, 100)} bandTaskbar={Band(tbDiag)}");
            var capDiag = FindCapsule();
            if (capDiag != IntPtr.Zero)
            {
                SetWindowPos(capDiag, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
                Thread.Sleep(500);
                Log($"pixels taskbarShown+reassert: {PixelText(150, 1510, 300, 100)} bandCapsule={Band(capDiag)} bandTaskbar={Band(tbDiag)} capZ={ZIndex(capDiag)} tbZ={ZIndex(tbDiag)}");
            }
        }

        Log("== send Win key ==");
        TapKey(0x5B);
        Thread.Sleep(1200);
        Dump("afterWin");
        DumpShellWindows("afterWin shell windows");

        var capsule = FindCapsule();
        var fg = Foreground();
        var fgRoot = Root(fg);
        Log($"capsule=0x{capsule.ToInt64():X} fg=0x{fg.ToInt64():X} fgRoot=0x{fgRoot.ToInt64():X}");
        Log($"indices: capsule={ZIndex(capsule)} fg={ZIndex(fg)} fgRoot={ZIndex(fgRoot)}");
        Log($"capsuleAboveFgRoot={IsAbove(capsule, fgRoot)}");
        Log($"bands: capsule={Band(capsule)} fg={Band(fg)} taskbar={Band(FindWindow("Shell_TrayWnd", null))}");
        Log($"pixels afterWin: {PixelText(150, 1510, 300, 100)}");

        if (capsule != IntPtr.Zero)
        {
            var ok = SetWindowPos(capsule, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
            Log($"reassert topmost ok={ok} err={Marshal.GetLastWin32Error()}");
            Thread.Sleep(300);
            Dump("afterReassert");
            Log($"indices after reassert: capsule={ZIndex(capsule)} fgRoot={ZIndex(Root(Foreground()))} above={IsAbove(capsule, Root(Foreground()))}");
            Log($"pixels afterReassert: {PixelText(150, 1510, 300, 100)} bandCapsule={Band(capsule)} bandFg={Band(Foreground())} bg={Band(FindWindow("Shell_TrayWnd", null))}");
        }

        Log("== close start with Esc ==");
        TapKey(0x1B);
        Thread.Sleep(700);
        Log($"foreground afterEsc={Describe(Foreground())}");
        Thread.Sleep(1500);
        var tbAfterEsc = FindWindow("Shell_TrayWnd", null);
        Log($"afterEsc+1.5s: {PixelText(150, 1510, 300, 100)} bandCapsule={Band(FindCapsule())} bandTaskbar={Band(tbAfterEsc)} capZ={ZIndex(FindCapsule())} tbZ={ZIndex(tbAfterEsc)}");
        var capAfterEsc = FindCapsule();
        if (capAfterEsc != IntPtr.Zero)
        {
            SetWindowPos(capAfterEsc, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
            Thread.Sleep(400);
            Log($"afterEsc+reassert: {PixelText(150, 1510, 300, 100)} bandCapsule={Band(capAfterEsc)} bandTaskbar={Band(tbAfterEsc)} capZ={ZIndex(capAfterEsc)} tbZ={ZIndex(tbAfterEsc)}");
        }

        Log("== click taskbar blank ==");
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out var tb))
        {
            var x = tb.Left + (tb.Right - tb.Left) * 3 / 5;
            var y = tb.Top + (tb.Bottom - tb.Top) / 2;
            Click(x, y);
            Thread.Sleep(800);
            Dump("afterTaskbarBlankClick");
            capsule = FindCapsule();
            fg = Foreground();
            fgRoot = Root(fg);
            Log($"blank click: capsule={ZIndex(capsule)} fg={ZIndex(fg)} fgRoot={ZIndex(fgRoot)} capsuleAboveFgRoot={IsAbove(capsule, fgRoot)}");
            Log($"pixels afterTaskbarBlankClick: {PixelText(150, 1510, 300, 100)} bandCapsule={Band(capsule)} bandTaskbar={Band(FindWindow("Shell_TrayWnd", null))}");
            if (capsule != IntPtr.Zero)
            {
                SetWindowPos(capsule, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
                Thread.Sleep(200);
                Log($"pixels afterTaskbarReassert: {PixelText(150, 1510, 300, 100)} capsuleZ={ZIndex(capsule)} taskbarZ={ZIndex(FindWindow("Shell_TrayWnd", null))}");
            }
        }

        Log("== click Start button ==");
        taskbar = FindWindow("Shell_TrayWnd", null);
        var startButton = taskbar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(taskbar, IntPtr.Zero, "Start", null);
        Log($"startButton=0x{startButton.ToInt64():X}");
        if (startButton != IntPtr.Zero && GetWindowRect(startButton, out var sb))
        {
            Click((sb.Left + sb.Right) / 2, (sb.Top + sb.Bottom) / 2);
            Thread.Sleep(1200);
            Dump("afterStartButtonClick");
            DumpShellWindows("afterStartButtonClick shell windows");
            capsule = FindCapsule();
            fg = Foreground();
            fgRoot = Root(fg);
            Log($"start click: capsule={ZIndex(capsule)} fg={ZIndex(fg)} fgRoot={ZIndex(fgRoot)} capsuleAboveFgRoot={IsAbove(capsule, fgRoot)}");
            TapKey(0x1B);
            Thread.Sleep(600);
        }

        Log("== done ==");
        File.WriteAllLines(_logPath, Lines, Encoding.UTF8);
        Console.WriteLine($"log: {_logPath}");
    }

    private static void Dump(string? label = null)
    {
        var fg = Foreground();
        Log($"[{label}] foreground=0x{fg.ToInt64():X} {Describe(fg)} root=0x{Root(fg).ToInt64():X} {Describe(Root(fg))} cloaked={Cloaked(fg)}");
        var h = GetTopWindow(IntPtr.Zero);
        var i = 0;
        while (h != IntPtr.Zero && i < 60)
        {
            var ex = GetWindowLong(h, GwlExstyle);
            GetWindowRect(h, out var r);
            Log($"  z{i:00} vis={(IsWindowVisible(h) ? 1 : 0)} 0x{h.ToInt64():X} topmost={((ex & WsExTopmost) != 0 ? 1 : 0)} {Describe(h)} rect={r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top} cloaked={Cloaked(h)}");
            h = GetWindow(h, GwHwndNext);
            i++;
        }
    }

    private static void DumpShellWindows(string label)
    {
        Log($"[{label}]");
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            var proc = ProcessName(pid);
            if (proc is not ("explorer" or "SearchHost" or "StartMenuExperienceHost" or "TextInputHost"))
            {
                return true;
            }

            GetWindowRect(h, out var r);
            var w = r.Right - r.Left;
            var ht = r.Bottom - r.Top;
            if (w <= 0 || ht <= 0)
            {
                return true;
            }

            Log($"  shell 0x{h.ToInt64():X} vis={(IsWindowVisible(h) ? 1 : 0)} proc={proc} {Describe(h)} rect={r.Left},{r.Top},{w}x{ht} cloaked={Cloaked(h)} root=0x{Root(h).ToInt64():X}");
            return true;
        }, IntPtr.Zero);
    }

    private static IntPtr FindCapsule()
    {
        var pid = Process.GetProcessesByName("HotspotMeter").FirstOrDefault()?.Id ?? 0;
        if (pid == 0)
        {
            return IntPtr.Zero;
        }

        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p != pid || !IsWindowVisible(h))
            {
                return true;
            }

            var cls = ClassName(h);
            GetWindowRect(h, out var r);
            var w = r.Right - r.Left;
            var ht = r.Bottom - r.Top;
            if (cls.StartsWith("HwndWrapper", StringComparison.Ordinal) && w is > 120 and < 900 && ht is > 30 and < 300)
            {
                found = h;
                Log($"capsule candidate 0x{h.ToInt64():X} class={cls} rect={r.Left},{r.Top},{w}x{ht} topmost={(GetWindowLong(h, GwlExstyle) & WsExTopmost) != 0}");
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static int ZIndex(IntPtr target)
    {
        if (target == IntPtr.Zero)
        {
            return int.MaxValue;
        }

        var h = GetTopWindow(IntPtr.Zero);
        var i = 0;
        while (h != IntPtr.Zero && i < 500)
        {
            if (h == target)
            {
                return i;
            }

            h = GetWindow(h, GwHwndNext);
            i++;
        }

        return int.MaxValue;
    }

    private static bool IsAbove(IntPtr a, IntPtr b) => a != IntPtr.Zero && b != IntPtr.Zero && ZIndex(a) < ZIndex(b);

    private static IntPtr Foreground() => GetForegroundWindow();

    private static IntPtr Root(IntPtr h) => h == IntPtr.Zero ? IntPtr.Zero : GetAncestor(h, GaRoot);

    private static int Cloaked(IntPtr h)
    {
        try
        {
            return DwmGetWindowAttribute(h, DwmwaCloaked, out var v, sizeof(int)) == 0 ? v : -1;
        }
        catch { return -1; }
    }

    private static int Band(IntPtr h)
    {
        if (h == IntPtr.Zero)
        {
            return -99;
        }

        try
        {
            return GetWindowBand(h, out var band) ? (int)band : -1;
        }
        catch (EntryPointNotFoundException)
        {
            return -2;
        }
    }

    /// <summary>直接抓屏统计胶囊绿色/红色像素；分层窗口用 BitBlt+CAPTUREBLT 仍可抓到。</summary>
    private static string PixelText(int x, int y, int w, int h)
    {
        CaptureColors(x, y, w, h, out var green, out var red, out var total);
        return $"green={green} red={red} total={total}";
    }

    private static void PrintCapsuleColors()
    {
        var h = FindCapsule();
        if (h == IntPtr.Zero || !GetWindowRect(h, out var r))
        {
            Log("printwindow: no capsule");
            return;
        }

        var w = r.Right - r.Left;
        var ht = r.Bottom - r.Top;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -ht,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };
        var dib = CreateDIBSection(mem, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(mem, dib);
        var ok = PrintWindow(h, mem, 2);
        Log($"printwindow ok={ok} size={w}x{ht}");
        if (ok)
        {
            var buf = new byte[w * ht * 4];
            Marshal.Copy(bits, buf, 0, buf.Length);
            var hist = new Dictionary<int, int>();
            for (var i = 0; i + 3 < buf.Length; i += 4)
            {
                int key = (buf[i + 2] << 16) | (buf[i + 1] << 8) | buf[i];
                hist[key] = hist.GetValueOrDefault(key) + 1;
            }

            foreach (var kv in hist.OrderByDescending(k => k.Value).Take(6))
            {
                Log($"  pw color #{kv.Key:X6} count={kv.Value}");
            }
        }

        SelectObject(mem, old);
        DeleteObject(dib);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
    }

    private static void DumpTopColors(int x, int y, int w, int h)
    {
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };
        var dib = CreateDIBSection(mem, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(mem, dib);
        if (BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000))
        {
            var buf = new byte[w * h * 4];
            Marshal.Copy(bits, buf, 0, buf.Length);
            var hist = new Dictionary<int, int>();
            for (var i = 0; i + 3 < buf.Length; i += 4)
            {
                int key = (buf[i + 2] << 16) | (buf[i + 1] << 8) | buf[i];
                hist[key] = hist.GetValueOrDefault(key) + 1;
            }

            foreach (var kv in hist.OrderByDescending(k => k.Value).Take(10))
            {
                Log($"  color #{kv.Key:X6} count={kv.Value}");
            }
        }

        SelectObject(mem, old);
        DeleteObject(dib);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
    }

    private static void CaptureColors(int x, int y, int w, int h, out int green, out int red, out int total)
    {
        green = 0;
        red = 0;
        total = 0;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };
        var dib = CreateDIBSection(mem, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(mem, dib);
        var ok = BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000);
        if (ok)
        {
            var buf = new byte[w * h * 4];
            Marshal.Copy(bits, buf, 0, buf.Length);
            for (var i = 0; i + 3 < buf.Length; i += 4)
            {
                int b = buf[i], g = buf[i + 1], r = buf[i + 2];
                if (Math.Abs(r - 0x1E) <= 10 && Math.Abs(g - 0x8E) <= 10 && Math.Abs(b - 0x3E) <= 10)
                {
                    green++;
                }
                else if (Math.Abs(r - 0xC5) <= 10 && Math.Abs(g - 0x22) <= 10 && Math.Abs(b - 0x1F) <= 10)
                {
                    red++;
                }

                if (r + g + b > 30)
                {
                    total++;
                }
            }
        }

        SelectObject(mem, old);
        DeleteObject(dib);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
    }

    private static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero)
        {
            return "(null)";
        }

        GetWindowThreadProcessId(h, out var pid);
        var title = new StringBuilder(256);
        GetWindowText(h, title, title.Capacity);
        return $"class={ClassName(h)} pid={pid}({ProcessName(pid)}) title=\"{title}\"";
    }

    private static string ProcessName(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
    }

    private static string ClassName(IntPtr h)
    {
        var b = new StringBuilder(256);
        var n = GetClassName(h, b, b.Capacity);
        return n > 0 ? b.ToString(0, n) : string.Empty;
    }

    private static void TapKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
    }

    private static void Click(int x, int y)
    {
        if (!SetCursorPos(x, y)) Log($"SetCursorPos failed err={Marshal.GetLastWin32Error()}");
        Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        Log($"clicked {x},{y}");
    }

    private static void TrySetDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
    }

    private static void Log(string s)
    {
        Lines.Add(s);
        Console.WriteLine(s);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowBand(IntPtr hwnd, out uint band);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}