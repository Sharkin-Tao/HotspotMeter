using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HotspotMeter.Core.TrafficGuard;

namespace HotspotMeter.Infrastructure.TrafficGuard;

/// <summary>
/// 一次枚举所有进程与顶层窗口，供 Guard 判定前后台。
///
/// 只在每个 30 秒采样周期调用一次，不做定时轮询；所有句柄都在本次调用内释放。
/// 拿不到路径/包族名时保留进程记录并让上层降级为“不确定身份”，绝不丢弃或猜测。
/// </summary>
public sealed class ProcessInspector : IProcessInspector
{
    private readonly DevicePathMapper _pathMapper;

    public ProcessInspector(DevicePathMapper? pathMapper = null)
    {
        _pathMapper = pathMapper ?? new DevicePathMapper();
    }

    public IReadOnlyList<ProcessObservation> Capture()
    {
        var visibleProcessIds = CaptureVisibleWindowProcessIds();
        var foregroundPid = GetForegroundProcessId();
        var result = new List<ProcessObservation>();

        foreach (var entry in EnumerateProcesses())
        {
            if (entry.ProcessId is 0u or 4u)
            {
                continue;
            }

            var path = TryGetImagePath(entry.ProcessId);
            if (!string.IsNullOrWhiteSpace(path))
            {
                path = _pathMapper.MapToDosPath(path);
            }

            var packageFamily = TryGetPackageFamilyName(entry.ProcessId);
            var hasVisibleWindow = visibleProcessIds.Contains(entry.ProcessId);
            var isForeground = foregroundPid == entry.ProcessId;
            var startTicks = TryGetStartTimeUtcTicks(entry.ProcessId);

            result.Add(new ProcessObservation(
                entry.ProcessId > int.MaxValue ? int.MaxValue : (int)entry.ProcessId,
                entry.Name,
                string.IsNullOrWhiteSpace(path) ? null : path,
                string.IsNullOrWhiteSpace(packageFamily) ? null : packageFamily,
                hasVisibleWindow,
                isForeground,
                startTicks));
        }

        return result;
    }

    private static HashSet<uint> CaptureVisibleWindowProcessIds()
    {
        var visible = new HashSet<uint>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            // 被虚拟桌面 cloaking 的窗口不应算作“可见主窗口”。
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            var exStyle = GetWindowLong(hwnd, GwlExStyle);
            if ((exStyle & WsExToolWindow) != 0)
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return true;
            }

            visible.Add(pid);
            return true;
        }, IntPtr.Zero);

        return visible;
    }

    private static uint GetForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        var root = GetAncestor(hwnd, GaRoot);
        if (root != IntPtr.Zero)
        {
            hwnd = root;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private static IEnumerable<ProcessEntry> EnumerateProcesses()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            yield break;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                yield break;
            }

            do
            {
                yield return new ProcessEntry(entry.ProcessId, entry.ExecutableFile);

                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static string? TryGetImagePath(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                return null;
            }

            return buffer.ToString(0, (int)size);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? TryGetPackageFamilyName(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            uint length = 0;
            var probe = GetPackageFamilyName(handle, ref length, null);
            if (probe != ErrorInsufficientBuffer || length == 0)
            {
                return null;
            }

            var buffer = new StringBuilder((int)length);
            return GetPackageFamilyName(handle, ref length, buffer) == 0
                ? buffer.ToString()
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static long TryGetStartTimeUtcTicks(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return GetProcessTimes(handle, out var creation, out _, out _, out _)
                ? DateTime.FromFileTimeUtc(((long)creation.HighDateTime << 32) | creation.LowDateTime).Ticks
                : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private readonly record struct ProcessEntry(uint ProcessId, string Name);

    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const uint GaRoot = 2;
    private const uint DwmwaCloaked = 14;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        uint attribute,
        out int value,
        int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr process,
        uint flags,
        StringBuilder name,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr process,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetPackageFamilyName(
        IntPtr process,
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}