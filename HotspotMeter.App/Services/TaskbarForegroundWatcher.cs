using System.Runtime.InteropServices;
using System.Text;

namespace HotspotMeter.App.Services;

/// <summary>
/// 监听前台窗口变化，只在“前台变成任务栏”时通知胶囊重新声明置顶。
///
/// 为什么不能用定时器轮询：无条件高频 SetWindowPos 会持续干扰 Z 序、增加
/// 输入延迟，也违背“不通过不断强制置顶影响用户操作”的约束。任务栏只有在
/// 用户点击它时才会抢到前台，因此 <c>EVENT_SYSTEM_FOREGROUND</c> 已经覆盖
/// 需要恢复层级的那一个时刻。
///
/// 限制：HWND_TOPMOST 只能保证位于普通窗口之上，无法覆盖开始菜单、安全
/// 桌面、UAC 等 Shell 特殊界面；这里不尝试突破系统边界。
/// </summary>
internal sealed class TaskbarForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private const int ObjIdWindow = 0;

    private readonly WinEventDelegate _callback;
    private IntPtr _hook;

    public TaskbarForegroundWatcher()
    {
        // 委托必须由字段持有：传给原生 API 后若被 GC 回收，回调会直接崩溃。
        _callback = OnForegroundChanged;
    }

    /// <summary>前台切换到任务栏（主/副任务栏或通知区域）时触发。</summary>
    public event EventHandler? TaskbarActivated;

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventOutofcontext | WineventSkipownprocess);

        if (_hook == IntPtr.Zero)
        {
            AppLog.Info($"前台监听安装失败：Win32 错误 {Marshal.GetLastWin32Error()}；胶囊仍保持普通置顶。");
        }
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    public void Dispose() => Stop();

    /// <summary>开发期自检：不依赖真实点击任务栏即可走同一条重断言路径。</summary>
    internal void RaiseForTest() => TaskbarActivated?.Invoke(this, EventArgs.Empty);

    private void OnForegroundChanged(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType != EventSystemForeground || hwnd == IntPtr.Zero || idObject != ObjIdWindow || idChild != 0)
        {
            return;
        }

        var className = GetClassName(hwnd);
        if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "TrayNotifyWnd")
        {
            AppLog.Info($"前台切换到任务栏（{className}），重新声明胶囊置顶。");
            TaskbarActivated?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassNameW(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    private delegate void WinEventDelegate(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);
}