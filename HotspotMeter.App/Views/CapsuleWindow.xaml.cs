using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HotspotMeter.App.Models;
using HotspotMeter.App.Services;
using HotspotMeter.App.ViewModels;
using HotspotMeter.Core.Layout;

namespace HotspotMeter.App.Views;

/// <summary>
/// 悬浮胶囊窗口。
/// 只负责"显示 + 位置 + 尺寸"：数据来自 <see cref="CapsuleViewModel"/>，
/// 拖动/固定/记忆位置属于窗口自身职责，不进入业务层。
/// <para>
/// 4C-8 的自动适配只计算尺寸，不计算位置：用户可以把胶囊拖到任务栏内部，
/// 窗口只保证尺寸贴合任务栏高度、位置不越出显示器。
/// </para>
/// </summary>
public partial class CapsuleWindow : Window
{
    private const double ScreenMargin = 8;
    private const double DefaultMargin = 24;
    private const uint MonitorDefaultToNearest = 2;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private readonly WindowStateStore _stateStore;
    private readonly Action _openSettings;
    private readonly TaskbarDetector _taskbarDetector = new();
    private readonly ShellForegroundWatcher _foregroundWatcher = new();
    private CapsuleViewModel? _viewModel;
    private bool _positionRestored;
    private bool _autoFit;
    private HwndSource? _hwndSource;
    private DispatcherTimer? _taskbarDebounce;
    private TaskbarDetection? _lastDetection;
    private TaskbarCapsuleFit? _lastFit;
    private uint _taskbarCreatedMessage;
    private string? _lastAnchorNote;
    private int _topmostReassertCount;
    private DispatcherTimer? _foregroundReassertTimer;
    private string _pendingForegroundClass = string.Empty;
    private int _foregroundLogCount;

    public CapsuleWindow(WindowStateStore stateStore, Action openSettings)
    {
        _stateStore = stateStore;
        _openSettings = openSettings;

        InitializeComponent();
        Icon = AppIcons.WindowIcon;

        DataContextChanged += OnDataContextChanged;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Closing += (_, _) => PersistState();

        _foregroundWatcher.ForegroundChanged += (_, e) =>
        {
            // 前台变化覆盖两种遮挡路径：
            // 1) 点击任务栏图标：前台变成目标应用，任务栏把自己抬到胶囊之上；
            // 2) 开始菜单关闭：Shell 的窗口 band 回落后，需要一次重断言把胶囊放回任务栏之上。
            // 不能在前台事件里立刻 SetWindowPos：任务栏/开始菜单的抬升动作通常发生在这条
            // 事件之后，立即置顶会被随后到达的 Shell Z 序操作覆盖。这里只安排一次 250 ms
            // 后的事件驱动重断言，不做轮询；开始菜单仍展开时该调用无效（band 6），
            // 关闭后下一次前台事件会再次安排。
            if (!_autoFit || !IsVisible)
            {
                return;
            }

            _pendingForegroundClass = e.ClassName;
            _foregroundReassertTimer ??= CreateForegroundReassertTimer();
            _foregroundReassertTimer.Stop();
            _foregroundReassertTimer.Start();
        };
    }

    public bool IsCapsuleVisible => IsVisible;

    /// <summary>自动适配是否开启；设置窗口与菜单都只读这一份状态。</summary>
    public bool IsAutoFitTaskbar => _autoFit;

    /// <summary>最近一次自动适配的结果，供自检/设置窗口显示。</summary>
    internal TaskbarCapsuleFit? LastFit => _lastFit;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 句柄已创建：此时可以按显示器工作区计算位置，即使窗口本次不显示也能定位。
        _autoFit = _stateStore.GetOrCreate().AutoFitTaskbar ?? false;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            _hwndSource = HwndSource.FromHwnd(handle);
            // 只挂一次钩子：任务栏移动/自动隐藏/分辨率/DPI 变化都通过窗口消息通知，
            // 不额外注册 SystemEvents，避免重复监听导致多次重排。
            _hwndSource?.AddHook(WndProc);
        }

        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        // 自动适配时先把"最终尺寸"算出来，再恢复位置。
        // 否则 RestorePosition 会拿旧的手动尺寸（可能是 220×71.5）做边界夹取，
        // 把保存的 (120,1020.7) 误夹成 995.3——尺寸随后变小，但位置已经回不来了。
        if (_autoFit)
        {
            ApplyAutoFitSizeOnly();
        }

        RestorePosition();
        _positionRestored = true;

        if (_autoFit)
        {
            ApplyAutoFitSize();
            _foregroundWatcher.Start();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 首次显示后再校正一次：显示器/DPI 可能在启动过程中发生变化。
        ClampCurrentPosition();
        EnsureTopmost();
        UiDump.DumpIfRequested(this);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _viewModel = e.NewValue as CapsuleViewModel;

        // ContextMenu 不在可视化树里，不会继承窗口 DataContext；必须显式赋值，
        // 否则"固定位置/解除固定位置"的绑定文本为空。
        if (ContextMenu is not null)
        {
            ContextMenu.DataContext = _viewModel;
        }

        if (_viewModel is not null)
        {
            // 窗口尺寸在代码里设置而不是绑定：Window.Width/Height 是"尺寸到内容"语义的
            // 依赖属性，绑定到 VM 时会被窗口自身的布局流程覆盖（实测高度变成内容期望高度）。
            Width = _viewModel.Size.Width;
            Height = _viewModel.Size.Height;
        }

        _ = sender;
        _ = e;
    }

    /// <summary>
    /// 应用普通尺寸（四档/自定义）。手动改尺寸代表用户明确接管外观，
    /// 自动适配随之关闭，否则下一次任务栏变化会覆盖用户刚选的尺寸。
    /// </summary>
    public void ApplySize(CapsuleSize size)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_autoFit)
        {
            _autoFit = false;
            _stateStore.Update(state => state.AutoFitTaskbar = false);
        }

        _viewModel.SetSize(size);
        Width = size.Width;
        Height = size.Height;

        // 绑定生效后 ActualWidth 才会更新，交给布局完成后再夹取位置。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            ClampCurrentPosition();
            PersistState();
        }));
    }

    /// <summary>
    /// 开启/关闭"自动适配任务栏"。
    /// 开启时备份当前手动尺寸与位置，只调整尺寸；关闭时恢复备份的手动尺寸。
    /// 自动适配期间绝不写 Width/Height，用户的手动配置不会被自动结果污染。
    /// <para>
    /// 首次开启（以及关闭后再次开启）会执行一次自动定位；后续的任务栏变化、跨屏、
    /// 重启都只重算尺寸，不再移动窗口——用户拖动过就以用户位置为准。
    /// </para>
    /// </summary>
    public void SetAutoFitTaskbar(bool enabled)
    {
        if (_autoFit == enabled)
        {
            return;
        }

        _autoFit = enabled;
        if (enabled)
        {
            var size = _viewModel?.Size ?? CapsuleSize.Default;
            var hasPosition = _positionRestored && !double.IsNaN(Left) && !double.IsNaN(Top);
            _stateStore.Update(state =>
            {
                state.AutoFitTaskbar = true;
                state.ManualWidth = size.Width;
                state.ManualHeight = size.Height;
                if (hasPosition)
                {
                    state.ManualLeft = Left;
                    state.ManualTop = Top;
                }
            });

            ApplyAutoFitSize(positionAtAnchor: true);
            EnsureTopmost();
            _foregroundWatcher.Start();
        }
        else
        {
            _stateStore.Update(state => state.AutoFitTaskbar = false);
            RestoreManualSize();
            _foregroundWatcher.Stop();
        }
    }

    /// <summary>
    /// 检测任务栏并调整尺寸。默认不动位置；只有用户首次开启自动适配时由调用方
    /// 传入 <paramref name="positionAtAnchor"/> = true 执行一次初始定位。
    /// </summary>
    public void ApplyAutoFitSize(bool positionAtAnchor = false)
    {
        if (_viewModel is null || !_autoFit)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var detection = _taskbarDetector.Detect(handle);
        _lastDetection = detection;
        var fit = TaskbarSizingCalculator.Compute(new TaskbarSizingInput(
            detection.Input.MonitorBounds,
            detection.Input.WorkArea,
            detection.Input.TaskbarRect,
            detection.Input.Edge,
            detection.Input.DpiScale));

        ApplyFit(fit);

        if (positionAtAnchor)
        {
            PositionAtAnchor(detection.Input, fit);
        }
    }

    /// <summary>
    /// 只计算并应用自动尺寸，不夹取、不持久化位置。
    /// 启动恢复位置前必须先确定最终尺寸，否则会用旧尺寸的边界把用户保存的位置夹高。
    /// </summary>
    private void ApplyAutoFitSizeOnly()
    {
        if (_viewModel is null || !_autoFit)
        {
            return;
        }

        var detection = _taskbarDetector.Detect(new WindowInteropHelper(this).Handle);
        _lastDetection = detection;
        var fit = TaskbarSizingCalculator.Compute(new TaskbarSizingInput(
            detection.Input.MonitorBounds,
            detection.Input.WorkArea,
            detection.Input.TaskbarRect,
            detection.Input.Edge,
            detection.Input.DpiScale));

        _lastFit = fit;
        _viewModel.ApplyAutoFit(fit);
        Width = fit.Width;
        Height = fit.Height;
    }

    /// <summary>
    /// 把胶囊放到"底部任务栏内部偏左"。定位只用显示器/任务栏矩形与 DPI 计算，
    /// 不枚举任务栏按钮、不寻找 UIA 空隙；顶部/左侧/右侧任务栏不做猜测。
    /// 固定位置期间不移动窗口，避免破坏用户明确锁定的摆放。
    /// </summary>
    private void PositionAtAnchor(TaskbarLayoutInput input, TaskbarCapsuleFit fit)
    {
        if (_viewModel?.IsPinned == true)
        {
            _lastAnchorNote = "位置已固定，自动定位未执行。";
            AppLog.Info("自动定位跳过：位置已固定。");
            return;
        }

        var anchor = TaskbarAnchorCalculator.Compute(input, fit.Width, fit.Height);
        _lastAnchorNote = anchor.Explanation;
        AppLog.Info(
            $"自动定位几何：monitor={input.MonitorBounds} taskbar={input.TaskbarRect} " +
            $"dpi={input.DpiScale:0.##} 胶囊={fit.Width:0.#}x{fit.Height:0.#} → {anchor.Left:0.#},{anchor.Top:0.#}");

        if (!anchor.Supported)
        {
            AppLog.Info($"自动定位未执行：{anchor.Explanation}");
            return;
        }

        Left = anchor.Left;
        Top = anchor.Top;
        ClampCurrentPosition();
        PersistState();
        EnsureTopmost();
        AppLog.Info($"自动定位：{anchor.Explanation} 位置=({Left:0.#},{Top:0.#})，尺寸={fit.Width:0.#}x{fit.Height:0.#}。");
    }

    /// <summary>最近一次自动定位的说明，供设置窗口/自检显示“为什么没定位”。</summary>
    public string? LastAnchorNote => _lastAnchorNote;

    /// <summary>前台切到任务栏后重新声明置顶的次数，供自检确认事件路径真的执行。</summary>
    internal int TopmostReassertCount => _topmostReassertCount;

    internal bool IsForegroundWatcherRunning => _foregroundWatcher.IsRunning;

    internal void SimulateForegroundChangedForTest(string className = "Shell_TrayWnd") =>
        _foregroundWatcher.RaiseForTest(className);

    /// <summary>最近一次任务栏检测输入（物理像素），供自检断言位置确实落在任务栏内。</summary>
    internal TaskbarLayoutInput? LastDetectionInput => _lastDetection?.Input;

    private void ApplyFit(TaskbarCapsuleFit fit)
    {
        if (_viewModel is null)
        {
            return;
        }

        _lastFit = fit;
        _viewModel.ApplyAutoFit(fit);
        Width = fit.Width;
        Height = fit.Height;

        // 只夹取位置，不改变用户的相对摆放；自动模式允许压到任务栏矩形内，
        // 因此边界用整个显示器，而不是排除任务栏后的工作区。
        ClampCurrentPosition();
        PersistState();
    }

    /// <summary>供设置窗口显示"检测到了什么、算出了多大"；每次打开设置都重新检测。</summary>
    public string DescribeTaskbar()
    {
        var detection = _taskbarDetector.Detect(new WindowInteropHelper(this).Handle);
        _lastDetection = detection;
        var fit = TaskbarSizingCalculator.Compute(new TaskbarSizingInput(
            detection.Input.MonitorBounds,
            detection.Input.WorkArea,
            detection.Input.TaskbarRect,
            detection.Input.Edge,
            detection.Input.DpiScale));
        var anchorNote = string.IsNullOrWhiteSpace(_lastAnchorNote) ? string.Empty : $" · {_lastAnchorNote}";
        return $"{detection.Description} · {fit.Explanation}{anchorNote}";
    }

    /// <summary>开发期自检：用合成任务栏几何验证窗口尺寸会被自动调整，而不依赖真实任务栏。</summary>
    internal void ApplyAutoFitForTest(TaskbarLayoutInput input, bool positionAtAnchor = false)
    {
        _lastDetection = new TaskbarDetection(input, TaskbarDetector.Describe(input, "自检合成"), "自检合成", null);
        var fit = TaskbarSizingCalculator.Compute(new TaskbarSizingInput(
            input.MonitorBounds,
            input.WorkArea,
            input.TaskbarRect,
            input.Edge,
            input.DpiScale));
        ApplyFit(fit);

        if (positionAtAnchor)
        {
            PositionAtAnchor(input, fit);
        }
    }

    /// <summary>开发期自检：模拟一次拖动结束后应走的完整路径（尺寸重算/夹取/置顶/持久化）。</summary>
    internal void DragEndForTest() => OnDragFinished();

    /// <summary>开发期自检：投递真实的 TaskbarCreated 动态消息，验证 Explorer 重启后的重建处理。</summary>
    internal void NotifyTaskbarCreatedForTest()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && _taskbarCreatedMessage != 0)
        {
            PostMessage(handle, _taskbarCreatedMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>开发期自检：投递真实的 WM_SETTINGCHANGE(SPI_SETWORKAREA)，走完整消息钩子与防抖路径。</summary>
    internal void NotifyTaskbarChangedForTest()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            PostMessage(handle, 0x001A, (IntPtr)0x002F, IntPtr.Zero);
        }
    }

    /// <summary>关闭自动适配：恢复开启前备份的手动尺寸，位置保持用户当前摆放。</summary>
    private void RestoreManualSize()
    {
        var state = _stateStore.GetOrCreate();
        var size = CapsuleSize.FromPersisted(state.ManualWidth, state.ManualHeight);
        _viewModel?.SetSize(size);
        Width = size.Width;
        Height = size.Height;

        ClampCurrentPosition();
        PersistState();
    }

    /// <summary>任务栏/分辨率变化会连续发多条消息，防抖后再重算，避免窗口被反复改变尺寸。</summary>
    private void ScheduleAutoFit()
    {
        if (!_autoFit)
        {
            return;
        }

        _taskbarDebounce ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _taskbarDebounce.Tick -= OnDebounceTick;
        _taskbarDebounce.Tick += OnDebounceTick;
        _taskbarDebounce.Stop();
        _taskbarDebounce.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _taskbarDebounce?.Stop();
        ApplyAutoFitSize();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == unchecked((int)_taskbarCreatedMessage))
        {
            // Explorer 重启后旧任务栏句柄失效，置顶顺序会丢；先重排尺寸再重新声明置顶。
            AppLog.Info("收到 TaskbarCreated：任务栏已重建，重新检测并恢复置顶。");
            ScheduleAutoFit();
            EnsureTopmost();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            // 工作区变化：任务栏移动/改尺寸/自动隐藏切换。
            case 0x001A when wParam == (IntPtr)0x002F:
            // 分辨率或显示器拓扑变化。
            case 0x007E:
            // 窗口跨到不同 DPI 的显示器。
            case 0x02E0:
                ScheduleAutoFit();
                break;
        }

        return IntPtr.Zero;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _taskbarDebounce?.Stop();
        _taskbarDebounce = null;
        _foregroundReassertTimer?.Stop();
        _foregroundReassertTimer = null;
        _foregroundWatcher.Dispose();

        // 必须解钩：窗口重建或退出后继续响应消息会访问已释放的窗口。
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    public void SetPinned(bool pinned)
    {
        if (_viewModel is null)
        {
            return;
        }

        // 固定只锁定位置；自动适配只改尺寸，因此固定期间仍可继续适配任务栏高度。
        _viewModel.IsPinned = pinned;
        PersistState();
    }

    /// <summary>回到主屏/当前屏右下角（任务栏上方），保留当前尺寸与固定状态。</summary>
    public void ResetToDefaultPosition()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var workArea = GetWorkAreaInDips(dpi.DpiScaleX, dpi.DpiScaleY);
        var size = _viewModel?.Size ?? CapsuleSize.Default;

        Left = workArea.Left + workArea.Width - size.Width - DefaultMargin;
        Top = workArea.Top + workArea.Height - size.Height - DefaultMargin;
        ClampCurrentPosition();
        PersistState();
    }

    public void ShowCapsule()
    {
        if (!IsVisible)
        {
            Show();
        }

        // 隐藏期间任务栏可能已经变了；重新显示时按最新任务栏高度校正尺寸。
        if (_autoFit)
        {
            ApplyAutoFitSize();
        }

        // 不切换 Topmost 再打开（那样会闪一下并抢焦点），直接以
        // SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE) 复位 Z 序。
        EnsureTopmost();
        Activate();
    }

    /// <summary>
    /// 以 SWP_NOACTIVATE 重新声明置顶：任务栏/其他置顶窗口抢占 Z 序后，
    /// 胶囊仍能回到最上层，而且不会把焦点从用户正在用的程序抢走。
    /// 只在显示、尺寸变化、Explorer 重启等事件调用，不做定时器轮询。
    /// </summary>
    private DispatcherTimer CreateForegroundReassertTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_autoFit || !IsVisible)
            {
                return;
            }

            EnsureTopmost();
            _topmostReassertCount++;

            // 只留少量样本：既能量化触发，又不会长期写盘（上限 12 条）。
            if (_foregroundLogCount < 12)
            {
                _foregroundLogCount++;
                AppLog.Info($"前台变化（{_pendingForegroundClass}）后重新声明胶囊置顶。");
            }
        };
        return timer;
    }

    private void EnsureTopmost()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public void HideCapsule() => Hide();

    private void RestorePosition()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var size = _viewModel?.Size ?? CapsuleSize.Default;
        var workArea = GetWorkAreaInDips(dpi.DpiScaleX, dpi.DpiScaleY);
        AppLog.Info($"恢复位置：auto={_autoFit} vm={size.Width:0.#}x{size.Height:0.#} work={workArea}");

        var saved = _stateStore.Load();
        double left;
        double top;
        if (saved is null)
        {
            // 首次运行：当前显示器工作区右下角，避开任务栏与开始按钮。
            left = workArea.Left + workArea.Width - size.Width - DefaultMargin;
            top = workArea.Top + workArea.Height - size.Height - DefaultMargin;
        }
        else
        {
            left = saved.Left;
            top = saved.Top;
            if (_viewModel is not null)
            {
                _viewModel.IsPinned = saved.IsPinned;
            }
        }

        (Left, Top) = ClampToScreen(left, top, size.Width, size.Height, dpi.DpiScaleX, dpi.DpiScaleY, _autoFit ? 0 : ScreenMargin);
    }

    /// <summary>开发期自检用：模拟一次"位置在屏幕外"后的校正。</summary>
    internal void ClampForTest(double left, double top)
    {
        Left = left;
        Top = top;
        ClampCurrentPosition();
    }

    /// <summary>开发期自检用：把窗口放在一个确定的安全位置，便于断言"自动适配不改位置"。</summary>
    internal void MoveForTest(double left, double top)
    {
        Left = left;
        Top = top;
    }

    private void ClampCurrentPosition()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var size = _viewModel?.Size ?? CapsuleSize.Default;

        // 自动适配模式下允许胶囊进入任务栏矩形（用户手动拖进去），边界取整个显示器，
        // 且不预留底边距——否则窗口会在任务栏上方 8 DIP 处被"弹回"，永远进不去任务栏。
        // 普通模式仍限制在工作区内，避免任务栏激活时盖住胶囊。
        var bounds = _autoFit
            ? GetMonitorBoundsInDips(dpi.DpiScaleX, dpi.DpiScaleY)
            : GetWorkAreaInDips(dpi.DpiScaleX, dpi.DpiScaleY);
        var margin = _autoFit ? 0 : ScreenMargin;

        var minLeft = bounds.Left + margin;
        var minTop = bounds.Top + margin;
        var maxLeft = Math.Max(minLeft, bounds.Left + bounds.Width - size.Width - margin);
        var maxTop = Math.Max(minTop, bounds.Top + bounds.Height - size.Height - margin);

        Left = Math.Clamp(Left, minLeft, maxLeft);
        Top = Math.Clamp(Top, minTop, maxTop);
    }

    /// <summary>
    /// 多显示器/分辨率/DPI 变化后仍把窗口限制在虚拟桌面内，避免"记忆到屏幕外"。
    /// Win32 返回物理像素，必须先按当前 DPI 缩放换算成 WPF 的 DIP。
    /// </summary>
    private static (double Left, double Top) ClampToScreen(
        double left, double top, double width, double height, double scaleX, double scaleY, double margin = ScreenMargin)
    {
        var minLeft = GetSystemMetrics(SmXVirtualScreen) / scaleX + margin;
        var minTop = GetSystemMetrics(SmYVirtualScreen) / scaleY + margin;
        var maxLeft = (GetSystemMetrics(SmXVirtualScreen) + GetSystemMetrics(SmCxVirtualScreen)) / scaleX - width - margin;
        var maxTop = (GetSystemMetrics(SmYVirtualScreen) + GetSystemMetrics(SmCyVirtualScreen)) / scaleY - height - margin;

        if (maxLeft < minLeft)
        {
            maxLeft = minLeft;
        }

        if (maxTop < minTop)
        {
            maxTop = minTop;
        }

        return (Math.Clamp(left, minLeft, maxLeft), Math.Clamp(top, minTop, maxTop));
    }

    private (double Left, double Top, double Width, double Height) GetWorkAreaInDips(double scaleX, double scaleY)
    {
        if (TryGetMonitorInfo(out var info))
        {
            return (
                info.Work.Left / scaleX,
                info.Work.Top / scaleY,
                (info.Work.Right - info.Work.Left) / scaleX,
                (info.Work.Bottom - info.Work.Top) / scaleY);
        }

        var fallback = SystemParameters.WorkArea;
        return (fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }

    /// <summary>整个显示器的 DIP 矩形；自动模式允许胶囊落在任务栏上，用它做边界。</summary>
    private (double Left, double Top, double Width, double Height) GetMonitorBoundsInDips(double scaleX, double scaleY)
    {
        if (TryGetMonitorInfo(out var info))
        {
            return (
                info.Monitor.Left / scaleX,
                info.Monitor.Top / scaleY,
                (info.Monitor.Right - info.Monitor.Left) / scaleX,
                (info.Monitor.Bottom - info.Monitor.Top) / scaleY);
        }

        var fallback = SystemParameters.WorkArea;
        return (fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }

    private bool TryGetMonitorInfo(out MonitorInfo info)
    {
        info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        return GetMonitorInfo(monitor, ref info);
    }

    private void OnSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 固定后禁止拖动；未固定时按住空白区域移动窗口。
        if (_viewModel?.IsPinned == true)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已释放时 DragMove 会抛异常，忽略即可。
        }

        // 拖动结束：按新显示器重算尺寸，保存实际位置；绝不把窗口挪回自动定位点，
        // 用户的摆放优先。自动模式允许停在任务栏内部。
        OnDragFinished();
    }

    private void OnDragFinished()
    {
        if (_autoFit)
        {
            ApplyAutoFitSize();
        }

        ClampCurrentPosition();
        EnsureTopmost();
        PersistState();
    }

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.IsPinned = !_viewModel.IsPinned;
        PersistState();
    }

    private void OnOpenSizeSettings(object sender, RoutedEventArgs e) => _openSettings();

    /// <summary>
    /// 持久化位置/尺寸/固定状态。设置窗口与胶囊共用同一个 store 实例，
    /// 因此这里只更新自己负责的字段，不会覆盖"启动时显示"等设置。
    /// 胶囊可能从未显示（启动时隐藏），此时没有有效坐标，只保存尺寸与固定状态。
    /// </summary>
    private void PersistState()
    {
        if (_viewModel is null)
        {
            return;
        }

        var size = _viewModel.Size;
        var pinned = _viewModel.IsPinned;
        var hasPosition = _positionRestored && !double.IsNaN(Left) && !double.IsNaN(Top);
        _stateStore.Update(state =>
        {
            state.IsPinned = pinned;
            state.AutoFitTaskbar = _autoFit;

            // 自动适配期间保留用户原本的手动尺寸；否则关闭自动适配后，
            // 用户会发现自己的尺寸设置被"上一次自动结果"覆盖了。
            if (!_autoFit)
            {
                state.Width = size.Width;
                state.Height = size.Height;
            }

            if (hasPosition)
            {
                state.Left = Left;
                state.Top = Top;
            }
        });
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
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
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
}