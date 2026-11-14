using System.Runtime.InteropServices;
using System.Text;

namespace HotspotMeter.App.Services;

/// <summary>
/// 监听前台窗口变化，每次变化后通知胶囊重新声明置顶。
///
/// 为什么不再只匹配任务栏类名：点击任务栏图标后，前台会变成被激活的应用；
/// 按下 Win 键后，前台是 SearchHost 的 CoreWindow。这两种情况下任务栏已经把
/// 自己抬到胶囊之上，但旧实现因为类名不在白名单里而完全不处理，胶囊就被盖住。
///
/// 为什么不轮询：无条件定时 SetWindowPos 会持续干扰 Z 序并增加输入延迟。
/// 前台切换是事件驱动的，频率与用户操作一致，不需要定时器。
///
/// 限制：开始菜单/搜索属于 Shell 的高优先级窗口 band（诊断读数 band 6），
/// 普通 WIN32 窗口的 HWND_TOPMOST 只能在自己的 band 内排序，无法在开始菜单
/// 展开期间盖住它；这里不尝试未公开的 band 接口。开始菜单关闭后前台会再次
/// 变化，届时本监听会把胶囊恢复到任务栏之上。
/// </summary>
internal sealed class ShellForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private const int ObjIdWindow = 0;

    private readonly WinEventDelegate _callback;
    private IntPtr _hook;

    public ShellForegroundWatcher()
    {
        // 委托必须由字段持有：传给原生 API 后若被 GC 回收，回调会直接崩溃。
        _callback = OnForegroundChanged;
    }

    /// <summary>任意前台窗口变化；参数包含新前台窗口的类名。</summary>
    public event EventHandler<ShellForegroundChangedEventArgs>? ForegroundChanged;

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

    /// <summary>开发期自检：不依赖真实点击即可走同一条重断言路径。</summary>
    internal void RaiseForTest(string className = "Shell_TrayWnd") =>
        ForegroundChanged?.Invoke(this, new ShellForegroundChangedEventArgs(className));

    private void OnForegroundChanged(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType != EventSystemForeground || hwnd == IntPtr.Zero || idObject != ObjIdWindow || idChild != 0)
        {
            return;
        }

        var className = GetClassName(hwnd);
        if (className.Length == 0)
        {
            return;
        }

        ForegroundChanged?.Invoke(this, new ShellForegroundChangedEventArgs(className));
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

/// <summary>新前台窗口的类名；只用于日志和自检，不参与过滤。</summary>
internal sealed class ShellForegroundChangedEventArgs(string className) : EventArgs
{
    public string ClassName { get; } = className;
}