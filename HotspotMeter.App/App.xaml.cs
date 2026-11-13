using System.Windows;
using HotspotMeter.App.Models;
using HotspotMeter.App.Services;
using HotspotMeter.App.ViewModels;
using HotspotMeter.App.Views;
using HotspotMeter.Infrastructure.SingleInstance;

namespace HotspotMeter.App;

/// <summary>
/// 组合根：创建窗口、托盘与监控服务，并实现设置窗口需要的宿主能力。
/// 生命周期约定：胶囊/设置窗口的显示状态不影响监控；只有"退出程序"才停止引擎。
/// </summary>
public partial class App : Application, ISettingsHost
{
    private readonly WindowStateStore _stateStore = new();
    private SingleInstanceGuard? _singleInstance;
    private CapsuleViewModel _capsuleViewModel = null!;
    private CapsuleWindow _capsule = null!;
    private SettingsWindow? _settingsWindow;
    private TrayIconService? _tray;
    private MonitorSessionService? _monitor;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 诊断：记录真实崩溃堆栈，但不把它标记为已处理，行为与未捕获一致。
        DispatcherUnhandledException += (_, args) =>
            CrashLog.Write(args.Exception, "DispatcherUnhandledException");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, args) =>
            CrashLog.Write(args.Exception, "UnobservedTaskException");

        _singleInstance = SingleInstanceGuard.TryAcquire();
        if (_singleInstance is null)
        {
            MessageBox.Show(
                "HotspotMeter 已在运行。请从系统托盘图标打开设置或显示悬浮胶囊。",
                "HotspotMeter",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 胶囊/设置窗口关闭后不得退出程序；退出只由托盘或胶囊菜单触发。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var saved = _stateStore.Load();
        var startSize = CapsuleSize.FromPersisted(saved?.Width, saved?.Height);
        var showCapsule = saved?.ShowCapsuleOnStartup ?? true;

        _capsuleViewModel = new CapsuleViewModel();
        _capsuleViewModel.SetSize(startSize);

        _capsule = new CapsuleWindow(_stateStore, OpenSettings)
        {
            DataContext = _capsuleViewModel,
        };
        MainWindow = _capsule;

        _tray = new TrayIconService(
            openSettings: OpenSettings,
            showCapsule: () => _capsule.ShowCapsule(),
            hideCapsule: () => _capsule.HideCapsule(),
            exit: Shutdown,
            isCapsuleVisible: () => _capsule.IsCapsuleVisible);

        if (showCapsule)
        {
            _capsule.Show();
        }

        // 监控与窗口可见性解耦：即使启动时不显示胶囊，引擎仍照常采样。
        _monitor = new MonitorSessionService(Dispatcher);
        await _monitor.InitializeAsync(_capsuleViewModel);

        // 开发期自检入口：默认关闭，只有显式设置环境变量才运行。
        var selfTestPath = Environment.GetEnvironmentVariable("HOTSPOTMETER_UI_TEST");
        if (!string.IsNullOrWhiteSpace(selfTestPath))
        {
            _ = RunUiSelfTestAsync(selfTestPath);
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { } existing)
        {
            if (!existing.IsVisible)
            {
                existing.Show();
            }

            existing.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(this);
        _settingsWindow = new SettingsWindow(viewModel, _stateStore);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ------- ISettingsHost：所有写入都落到唯一真实来源 -------

    public CapsuleSize CurrentSize => _capsuleViewModel.Size;

    public bool IsPinned => _capsuleViewModel.IsPinned;

    public bool ShowCapsuleOnStartup => _stateStore.GetOrCreate().ShowCapsuleOnStartup ?? true;

    public bool AutoFitTaskbar => _capsule.IsAutoFitTaskbar;

    public string TaskbarStatusText => _capsule.DescribeTaskbar();

    public (double Left, double Top) CapsulePosition => (_capsule.Left, _capsule.Top);

    public void ApplySize(CapsuleSize size) => _capsule.ApplySize(size);

    public void SetAutoFitTaskbar(bool enabled) => _capsule.SetAutoFitTaskbar(enabled);

    public void SetPinned(bool pinned) => _capsule.SetPinned(pinned);

    public void SetShowCapsuleOnStartup(bool show) =>
        _stateStore.Update(state => state.ShowCapsuleOnStartup = show);

    public void ResetPosition() => _capsule.ResetToDefaultPosition();

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _tray = null;

        if (_monitor is not null)
        {
            try
            {
                // 退出时给引擎最多 3 秒完成最后一次快照与停止，避免无限等待。
                _monitor.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
                // 退出阶段的异常不再影响用户体验。
            }

            _monitor = null;
        }

        _singleInstance?.Dispose();
        _singleInstance = null;

        base.OnExit(e);
    }
}