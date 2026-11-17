using System.Windows;
using HotspotMeter.App.Localization;
using HotspotMeter.App.Services;
using HotspotMeter.App.ViewModels;

namespace HotspotMeter.App.Views;

/// <summary>
/// 流量守护窗口。窗口只负责展示与交互；规则、历史与进程操作都通过
/// <see cref="GuardViewModel"/> 走数据库/系统服务，避免 UI 成为第二份状态。
/// </summary>
public partial class GuardWindow : Window
{
    private readonly GuardViewModel _viewModel;

    public GuardWindow(GuardViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += async (_, _) => await _viewModel.LoadAsync();
        LocalizationService.Current.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => _viewModel.RefreshLanguage();

    private async void Terminate_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedProcess is not { CanTerminate: true } process)
        {
            return;
        }

        var confirm = new GuardConfirmWindow(process.Candidate) { Owner = this };
        if (confirm.ShowDialog() == true)
        {
            await _viewModel.TerminateSelectedAsync();
        }
    }
}