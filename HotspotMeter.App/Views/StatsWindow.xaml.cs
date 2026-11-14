using System.Windows;
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
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.LoadAsync();
    }
}