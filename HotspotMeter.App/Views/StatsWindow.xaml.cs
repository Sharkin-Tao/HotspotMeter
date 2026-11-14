using System.Windows;
using HotspotMeter.App.Services;
using HotspotMeter.App.ViewModels;

namespace HotspotMeter.App.Views;

/// <summary>
/// 独立统计窗口。普通桌面窗口（可最小化/最大化/关闭）；
/// 关闭它只释放窗口，不触碰托盘与后台监控。
/// </summary>
public partial class StatsWindow : Window
{
    private readonly StatsViewModel _viewModel;

    public StatsWindow(StatsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        // 关闭窗口时取消仍在进行的查询，避免旧结果回到已关闭的 ViewModel。
        Closed += (_, _) => _viewModel.Cancel();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.LoadAsync();
    }
}