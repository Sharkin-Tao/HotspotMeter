using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using HotspotMeter.App.Localization;
using HotspotMeter.App.Models;
using HotspotMeter.App.Services;
using HotspotMeter.Core.Localization;

namespace HotspotMeter.App.ViewModels;

/// <summary>
/// 设置窗口的视图模型。只做"读数 → 校验 → 交回宿主"，
/// 不直接修改窗口、不访问数据库，也不保存任何第二份状态。
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private static readonly Brush PresetNormal = Freeze(Color.FromRgb(0x1F, 0x1F, 0x23));
    private static readonly Brush PresetSelected = Freeze(Color.FromRgb(0x2C, 0x2C, 0x31));

    private readonly ISettingsHost _host;

    private string _widthText = string.Empty;
    private string _heightText = string.Empty;
    private string? _validationMessage;
    private string _currentSizeText = string.Empty;
    private string _aspectRatioText = string.Empty;
    private double _previewWidth;
    private double _previewHeight;
    private double _previewCornerRadius;
    private string _pinButtonText = string.Empty;
    private string _pinStatusText = string.Empty;
    private string _positionText = string.Empty;
    private bool _startupVisible;
    private bool _autoFitTaskbar;
    private string _taskbarStatusText = string.Empty;
    private string _autoFitHint = string.Empty;
    private bool _autoStart;
    private bool _showStatsInTaskbar;
    private string _startupStatusText = string.Empty;

    public SettingsViewModel(ISettingsHost host)
    {
        _host = host;

        ApplyCustomCommand = new RelayCommand(ApplyCustom);
        UseMiniCommand = new RelayCommand(() => ApplyPreset(CapsuleSize.Mini));
        UseSmallCommand = new RelayCommand(() => ApplyPreset(CapsuleSize.Small));
        UseStandardCommand = new RelayCommand(() => ApplyPreset(CapsuleSize.Default));
        UseLargeCommand = new RelayCommand(() => ApplyPreset(CapsuleSize.Large));
        TogglePinCommand = new RelayCommand(TogglePin);
        ResetPositionCommand = new RelayCommand(() =>
        {
            _host.ResetPosition();
            Refresh();
        });

        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand ApplyCustomCommand { get; }

    public ICommand UseMiniCommand { get; }

    public ICommand UseSmallCommand { get; }

    public ICommand UseStandardCommand { get; }

    public ICommand UseLargeCommand { get; }

    public ICommand TogglePinCommand { get; }

    public ICommand ResetPositionCommand { get; }

    public string WidthText
    {
        get => _widthText;
        set => Set(ref _widthText, value);
    }

    public string HeightText
    {
        get => _heightText;
        set => Set(ref _heightText, value);
    }

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (Set(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);

    public string CurrentSizeText
    {
        get => _currentSizeText;
        private set => Set(ref _currentSizeText, value);
    }

    public string AspectRatioText
    {
        get => _aspectRatioText;
        private set => Set(ref _aspectRatioText, value);
    }

    public string PinButtonText
    {
        get => _pinButtonText;
        private set => Set(ref _pinButtonText, value);
    }

    public string PinStatusText
    {
        get => _pinStatusText;
        private set => Set(ref _pinStatusText, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => Set(ref _positionText, value);
    }

    public bool StartupVisible
    {
        get => _startupVisible;
        set
        {
            if (!Set(ref _startupVisible, value))
            {
                return;
            }

            _host.SetShowCapsuleOnStartup(value);
        }
    }

    public double PreviewWidth
    {
        get => _previewWidth;
        private set => Set(ref _previewWidth, value);
    }

    public double PreviewHeight
    {
        get => _previewHeight;
        private set => Set(ref _previewHeight, value);
    }

    public double PreviewCornerRadius
    {
        get => _previewCornerRadius;
        private set => Set(ref _previewCornerRadius, value);
    }

    /// <summary>
    /// 自动适配开关。写入后立即让宿主重排一次窗口，因此设置窗口里能立刻看到效果。
    /// </summary>
    public bool AutoFitTaskbar
    {
        get => _autoFitTaskbar;
        set
        {
            if (!Set(ref _autoFitTaskbar, value))
            {
                return;
            }

            _host.SetAutoFitTaskbar(value);

            // 重排会改变尺寸/位置，必须把只读文本刷新到最新，否则界面显示旧值。
            Refresh();
        }
    }

    public string TaskbarStatusText
    {
        get => _taskbarStatusText;
        private set => Set(ref _taskbarStatusText, value);
    }

    public string AutoFitHint
    {
        get => _autoFitHint;
        private set => Set(ref _autoFitHint, value);
    }

    /// <summary>
    /// 开机自启动。setter 直接写注册表并回读；失败时把真实原因放进
    /// <see cref="StartupStatusText"/>，开关回到实际状态而不是“看起来已开启”。
    /// </summary>
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!Set(ref _autoStart, value))
            {
                return;
            }

            _host.SetAutoStart(value);
            RefreshStartupState();
        }
    }

    /// <summary>统计窗口是否出现在任务栏；与“自动适配任务栏”完全独立。</summary>
    public bool ShowStatsInTaskbar
    {
        get => _showStatsInTaskbar;
        set
        {
            if (!Set(ref _showStatsInTaskbar, value))
            {
                return;
            }

            _host.SetShowStatsInTaskbar(value);
        }
    }

    public string StartupStatusText
    {
        get => _startupStatusText;
        private set => Set(ref _startupStatusText, value);
    }

    /// <summary>
    /// 语言单选组。两个属性互斥，setter 只在“被选中”时写宿主，
    /// 避免取消选中时把语言又改回去。
    /// </summary>
    public bool IsChinese
    {
        get => string.Equals(_host.CurrentLanguage, LanguageCatalog.ChineseSimplified, StringComparison.Ordinal);
        set
        {
            if (value && !IsChinese)
            {
                _host.SetLanguage(LanguageCatalog.ChineseSimplified);
                Refresh();
            }
        }
    }

    public bool IsEnglish
    {
        get => string.Equals(_host.CurrentLanguage, LanguageCatalog.English, StringComparison.Ordinal);
        set
        {
            if (value && !IsEnglish)
            {
                _host.SetLanguage(LanguageCatalog.English);
                Refresh();
            }
        }
    }

    public Brush MiniPresetBackground => SelectedBackground(CapsuleSize.Mini);

    public Brush SmallPresetBackground => SelectedBackground(CapsuleSize.Small);

    public Brush StandardPresetBackground => SelectedBackground(CapsuleSize.Default);

    public Brush LargePresetBackground => SelectedBackground(CapsuleSize.Large);

    /// <summary>
    /// 从宿主重新读数。胶囊右键菜单也能改固定状态，窗口激活时必须同步，
    /// 否则设置窗口会显示过期状态。
    /// </summary>
    public void Refresh(bool keepSizeInputs = false)
    {
        var size = _host.CurrentSize;
        if (!keepSizeInputs)
        {
            WidthText = size.Width.ToString("0.#", CultureInfo.CurrentCulture);
            HeightText = size.Height.ToString("0.#", CultureInfo.CurrentCulture);
        }

        CurrentSizeText = $"{size.Width:0.#} × {size.Height:0.#} DIP";
        AspectRatioText = LocalizationService.F("Settings_AspectRatio", size.AspectRatioValue);

        // 预览矩形按比例塞进 236×72 的槽位，保持真实胶囊的观感。
        var scale = Math.Min(236 / size.Width, 72 / size.Height);
        PreviewWidth = Math.Round(size.Width * scale, 1);
        PreviewHeight = Math.Round(size.Height * scale, 1);
        PreviewCornerRadius = Math.Round(20 * scale, 1);

        PinStatusText = LocalizationService.T(_host.IsPinned ? "Settings_PinStatusPinned" : "Settings_PinStatusUnpinned");
        PinButtonText = LocalizationService.T(_host.IsPinned ? "Settings_PinButtonUnpin" : "Settings_PinButtonPin");
        _startupVisible = _host.ShowCapsuleOnStartup;
        OnPropertyChanged(nameof(StartupVisible));

        _autoFitTaskbar = _host.AutoFitTaskbar;
        OnPropertyChanged(nameof(AutoFitTaskbar));
        _showStatsInTaskbar = _host.ShowStatsInTaskbar;
        OnPropertyChanged(nameof(ShowStatsInTaskbar));
        OnPropertyChanged(nameof(IsChinese));
        OnPropertyChanged(nameof(IsEnglish));
        RefreshStartupState();
        TaskbarStatusText = _host.TaskbarStatusText;
        AutoFitHint = LocalizationService.T(
            _host.IsPinned && _host.AutoFitTaskbar ? "Settings_TaskbarHintPinned" : "Settings_TaskbarHintUnpinned");

        var (left, top) = _host.CapsulePosition;
        PositionText = double.IsNaN(left) || double.IsNaN(top)
            ? LocalizationService.T("Settings_PositionUnknown")
            : LocalizationService.F("Settings_Position", left, top);

        OnPropertyChanged(nameof(MiniPresetBackground));
        OnPropertyChanged(nameof(SmallPresetBackground));
        OnPropertyChanged(nameof(StandardPresetBackground));
        OnPropertyChanged(nameof(LargePresetBackground));
    }

    private void RefreshStartupState()
    {
        _autoStart = _host.AutoStartEnabled;
        OnPropertyChanged(nameof(AutoStart));
        StartupStatusText = _host.StartupStatusText;
    }

    private void ApplyPreset(CapsuleSize size)
    {
        ValidationMessage = null;
        _host.ApplySize(size);
        Refresh();
    }

    private void ApplyCustom()
    {
        if (!TryParse(WidthText, out var width))
        {
            ValidationMessage = LocalizationService.T("Settings_ErrorWidthNumber");
            return;
        }

        if (!TryParse(HeightText, out var height))
        {
            ValidationMessage = LocalizationService.T("Settings_ErrorHeightNumber");
            return;
        }

        // 0.1 DIP 精度足够；范围与比例先在这里校验，以便错误信息跟随界面语言。
        width = Math.Round(width, 1);
        height = Math.Round(height, 1);

        if (double.IsNaN(width) || double.IsInfinity(width) || width < CapsuleSize.MinWidth || width > CapsuleSize.MaxWidth)
        {
            ValidationMessage = LocalizationService.F("Settings_ErrorWidthRange", CapsuleSize.MinWidth, CapsuleSize.MaxWidth);
            return;
        }

        if (double.IsNaN(height) || double.IsInfinity(height) || height < CapsuleSize.MinHeight || height > CapsuleSize.MaxHeight)
        {
            ValidationMessage = LocalizationService.F(
                "Settings_ErrorHeightRange",
                CapsuleSize.MinHeight,
                CapsuleSize.MaxHeight,
                width,
                width * CapsuleSize.DefaultHeight / CapsuleSize.DefaultWidth);
            return;
        }

        var ratio = width / height;
        if (Math.Abs(ratio - CapsuleSize.AspectRatio) / CapsuleSize.AspectRatio > CapsuleSize.AspectTolerance)
        {
            ValidationMessage = LocalizationService.F(
                "Settings_ErrorAspect",
                width * CapsuleSize.DefaultHeight / CapsuleSize.DefaultWidth,
                height * CapsuleSize.DefaultWidth / CapsuleSize.DefaultHeight);
            return;
        }

        if (!CapsuleSize.TryCreate(width, height, out var size, out _))
        {
            ValidationMessage = LocalizationService.T("Settings_ErrorAspect");
            return;
        }

        ValidationMessage = null;
        _host.ApplySize(size);
        Refresh();
    }

    private void TogglePin()
    {
        _host.SetPinned(!_host.IsPinned);
        Refresh();
    }

    private Brush SelectedBackground(CapsuleSize preset) =>
        _host.CurrentSize.IsNearly(preset) ? PresetSelected : PresetNormal;

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}