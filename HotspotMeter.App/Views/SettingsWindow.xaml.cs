using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HotspotMeter.App.Localization;
using HotspotMeter.App.Services;
using HotspotMeter.App.ViewModels;

namespace HotspotMeter.App.Views;

/// <summary>
/// 独立设置窗口。关闭它只销毁窗口，不影响后台监控；单实例由 <see cref="App"/> 统一管理。
/// 位置记忆在这里完成：写入 ui-state.json 的 SettingsLeft/SettingsTop，旧文件没有字段时保持居中。
/// </summary>
public partial class SettingsWindow : Window
{
    private const double ScreenMargin = 8;

    private readonly SettingsViewModel _viewModel;
    private readonly WindowStateStore _stateStore;

    public SettingsWindow(SettingsViewModel viewModel, WindowStateStore stateStore)
    {
        _viewModel = viewModel;
        _stateStore = stateStore;
        InitializeComponent();
        DataContext = viewModel;
        Icon = AppIcons.WindowIcon;

        SourceInitialized += (_, _) =>
        {
            DarkTitleBar.Apply(this);
            ClampWindowSize();
            RestorePosition();
        };
        Closed += (_, _) =>
        {
            LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
            PersistPosition();
        };

        LocalizationService.Current.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        // XAML 静态文字由 TrExtension 自行刷新；VM 生成的尺寸/状态文案需要重建。
        _viewModel.Refresh(keepSizeInputs: Keyboard.FocusedElement is TextBox);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // 胶囊右键菜单也能切换固定状态，激活时刷新，避免显示过期值。
        // 正在输入自定义宽高时不要用当前（旧）尺寸覆盖输入框。
        _viewModel.Refresh(keepSizeInputs: Keyboard.FocusedElement is TextBox);
    }

    /// <summary>工作区可能小于设计高度（例如 1080p@175%）；夹住窗口高度，底部按钮才可达。</summary>
    private void ClampWindowSize()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Max(480, Math.Min(Width, area.Width - ScreenMargin * 2));
        Height = Math.Max(360, Math.Min(Height, area.Height - ScreenMargin * 2));
    }

    private void RestorePosition()
    {
        var saved = _stateStore.Load();
        if (saved?.SettingsLeft is not { } left || saved.SettingsTop is not { } top)
        {
            return;
        }

        // 已记忆位置时不要再用 CenterScreen 覆盖。
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top) = Clamp(left, top);
    }

    private void PersistPosition()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        var (left, top) = Clamp(Left, Top);
        _stateStore.Update(state =>
        {
            state.SettingsLeft = left;
            state.SettingsTop = top;
        });
    }

    /// <summary>多显示器/分辨率变化后仍让设置窗口回到可见区域（WPF 的 SystemParameters 是 DIP）。</summary>
    private (double Left, double Top) Clamp(double left, double top)
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var minLeft = SystemParameters.VirtualScreenLeft + ScreenMargin;
        var minTop = SystemParameters.VirtualScreenTop + ScreenMargin;
        var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - width - ScreenMargin;
        var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - height - ScreenMargin;

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

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}