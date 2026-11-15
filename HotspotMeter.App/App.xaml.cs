using System.Windows;
using HotspotMeter.App.Localization;
using HotspotMeter.App.Models;
using HotspotMeter.App.Services;
using HotspotMeter.App.ViewModels;
using HotspotMeter.App.Views;
using HotspotMeter.Core.Localization;
using HotspotMeter.Infrastructure.SingleInstance;

namespace HotspotMeter.App;

/// <summary>
/// 组合根：创建窗口、托盘与监控服务，并实现设置窗口需要的宿主能力。
/// 生命周期约定：胶囊/设置窗口的显示状态不影响监控；只有"退出程序"才停止引擎。
/// </summary>
public partial class App : Application, ISettingsHost
{
    private readonly WindowStateStore _stateStore = new();
    private readonly StartupRegistrationService _startupRegistration = new();
    private SingleInstanceGuard? _singleInstance;
    private CapsuleViewModel _capsuleViewModel = null!;
    private CapsuleWindow _capsule = null!;
    private SettingsWindow? _settingsWindow;
    private StatsWindow? _statsWindow;
    private readonly TrafficStatsService _statsService = new();
    private readonly AttributedUsageReader _usageReader = new();
    private TrayIconService? _tray;
    private MonitorSessionService? _monitor;
    private bool _exiting;
    private StartupRegistration? _lastStartupWrite;

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

        // 胶囊/设置窗口关闭后不得退出程序；退出只由托盘或胶囊菜单触发。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 语言必须先于单实例提示和任何窗口创建确定，否则第二个实例会先用旧语言弹提示。
        var saved = _stateStore.Load();
        var hasLanguage = LanguageCatalog.TryNormalize(saved?.Language, out var savedLanguage);
        LocalizationService.Current.SetLanguage(hasLanguage ? savedLanguage : LanguageCatalog.ChineseSimplified);

        _singleInstance = SingleInstanceGuard.TryAcquire();
        if (_singleInstance is null)
        {
            MessageBox.Show(
                LocalizationService.T("App_AlreadyRunning"),
                "HotspotMeter",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (!hasLanguage)
        {
            var picker = new LanguageWindow();
            if (picker.ShowDialog() != true || string.IsNullOrWhiteSpace(picker.SelectedLanguage))
            {
                // 用户直接关闭选择窗口：视为放弃启动，不写入任何语言值。
                Shutdown();
                return;
            }

            _stateStore.Update(state => state.Language = picker.SelectedLanguage);
            LocalizationService.Current.SetLanguage(picker.SelectedLanguage);
            saved = _stateStore.Load();
            AppLog.Info($"已保存首次启动语言：{picker.SelectedLanguage}");
        }

        var startSize = CapsuleSize.FromPersisted(saved?.Width, saved?.Height);
        var showCapsule = saved?.ShowCapsuleOnStartup ?? true;

        _capsuleViewModel = new CapsuleViewModel();
        _capsuleViewModel.SetSize(startSize);

        _capsule = new CapsuleWindow(_stateStore, OpenSettings, OpenStats)
        {
            DataContext = _capsuleViewModel,
        };
        MainWindow = _capsule;

        _tray = new TrayIconService(
            openStats: OpenStats,
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
        try
        {
            await _monitor.InitializeAsync(_capsuleViewModel);
        }
        catch (Exception ex)
        {
            // 监控启动失败不应把整个程序带走：保留胶囊与统计窗口，明确告知采集不可用。
            AppLog.Info($"监控初始化失败：{ex.Message}");
            CrashLog.Write(ex, "MonitorInitialize");
        }

        if (_exiting)
        {
            return;
        }

        // 开发期自检入口：默认关闭，只有显式设置环境变量才运行。
        var selfTestPath = Environment.GetEnvironmentVariable("HOTSPOTMETER_UI_TEST");
        if (!string.IsNullOrWhiteSpace(selfTestPath))
        {
            _ = RunUiSelfTestAsync(selfTestPath);
        }

        // 开发期统计窗口抓取入口，同样只在显式设置环境变量时启用。
        var statsDumpPath = Environment.GetEnvironmentVariable("HOTSPOTMETER_UI_STATS");
        if (!string.IsNullOrWhiteSpace(statsDumpPath))
        {
            _ = RunStatsDumpAsync(statsDumpPath);
        }

        // 开发期设置窗口抓取入口，用于验证英文布局与实际渲染。
        var settingsDumpPath = Environment.GetEnvironmentVariable("HOTSPOTMETER_UI_SETTINGS");
        if (!string.IsNullOrWhiteSpace(settingsDumpPath))
        {
            _ = RunSettingsDumpAsync(settingsDumpPath);
        }

        // 阶段 5 运行时验收：会真实写 HKCU Run 并恢复原状态，只在显式请求时执行。
        var stage5Path = Environment.GetEnvironmentVariable("HOTSPOTMETER_STAGE5_TEST");
        if (!string.IsNullOrWhiteSpace(stage5Path))
        {
            _ = RunStage5SelfTestAsync(stage5Path);
        }
    }

    private void OpenStats()
    {
        if (_statsWindow is { } existing)
        {
            if (!existing.IsVisible)
            {
                existing.Show();
            }

            existing.Activate();
            return;
        }

        var viewModel = new StatsViewModel(
            _statsService,
            _usageReader,
            () => (_monitor?.TargetInterfaceGuid, _monitor?.TargetSsid));

        _statsWindow = new StatsWindow(viewModel)
        {
            ShowInTaskbar = ShowStatsInTaskbar,
        };
        _statsWindow.Closed += (_, _) => _statsWindow = null;
        _statsWindow.Show();
        _statsWindow.Activate();
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

    public bool AutoStartEnabled => _startupRegistration.Read().IsEnabledForCurrentPath;

    public string StartupStatusText
    {
        get
        {
            // 写注册表失败时，单纯回读只会显示“未开启”，用户不知道失败原因。
            if (_lastStartupWrite is { State: StartupRegistrationState.Unknown } failed)
            {
                return string.Format(LocalizationService.Current.Culture, LocalizationService.T("Startup_WriteFailed"), failed.Error);
            }

            var registration = _startupRegistration.Read();
            return registration.State switch
            {
                StartupRegistrationState.RegisteredForCurrentPath => LocalizationService.T("Startup_Registered"),
                StartupRegistrationState.NotRegistered => LocalizationService.T("Startup_NotRegistered"),
                StartupRegistrationState.RegisteredForOtherPath =>
                    string.Format(LocalizationService.Current.Culture, LocalizationService.T("Startup_OtherPath"), registration.Command),
                _ => string.Format(LocalizationService.Current.Culture, LocalizationService.T("Startup_ReadFailed"), registration.Error),
            };
        }
    }

    /// <summary>旧配置没有该字段时默认 true；显式保存的 false 必须保留。</summary>
    public bool ShowStatsInTaskbar => _stateStore.GetOrCreate().ShowStatsInTaskbar ?? true;

    public string CurrentLanguage => LocalizationService.Current.Language;

    public void SetLanguage(string language)
    {
        if (!LocalizationService.Current.SetLanguage(language))
        {
            return;
        }

        _stateStore.Update(state => state.Language = LocalizationService.Current.Language);
        AppLog.Info($"界面语言切换为 {LocalizationService.Current.Language}");
    }

    public void SetAutoStart(bool enabled)
    {
        var result = enabled ? _startupRegistration.Enable() : _startupRegistration.Disable();
        _lastStartupWrite = result.State == StartupRegistrationState.Unknown ? result : null;
        AppLog.Info($"开机自启动切换为 {enabled}：state={result.State} command={result.Command} error={result.Error}");
    }

    public void SetShowStatsInTaskbar(bool show)
    {
        _stateStore.Update(state => state.ShowStatsInTaskbar = show);
        if (_statsWindow is { } window)
        {
            window.ShowInTaskbar = show;
        }
    }

    public void ResetPosition() => _capsule.ResetToDefaultPosition();

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _tray?.Dispose();
        _tray = null;

        if (_monitor is not null)
        {
            try
            {
                // DisposeAsync 的续体需要 UI 线程；OnExit 正在 UI 线程等待。
                // 在线程池上执行整个释放链，避免 3 秒固定超时与“释放只做了一半”。
                var dispose = Task.Run(() => _monitor.DisposeAsync().AsTask());
                if (!dispose.Wait(TimeSpan.FromSeconds(3)))
                {
                    AppLog.Info("退出时监控释放超时：最后检查点可能未完成。");
                }
            }
            catch (Exception ex)
            {
                AppLog.Info($"退出时监控释放失败：{ex.Message}");
            }

            _monitor = null;
        }

        _singleInstance?.Dispose();
        _singleInstance = null;

        base.OnExit(e);
    }
}