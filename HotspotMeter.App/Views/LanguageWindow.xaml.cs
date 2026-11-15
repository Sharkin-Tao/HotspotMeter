using System.Windows;
using HotspotMeter.App.Services;
using HotspotMeter.Core.Localization;

namespace HotspotMeter.App.Views;

/// <summary>
/// 首次启动的语言选择。只在配置里没有有效语言时显示；不预选任何语言，
/// 用户关闭窗口即退出程序，不会悄悄写入一个默认值。
/// </summary>
public partial class LanguageWindow : Window
{
    public LanguageWindow()
    {
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    public string SelectedLanguage { get; private set; } = string.Empty;

    private void OnSelectChinese(object sender, RoutedEventArgs e) => Complete(LanguageCatalog.ChineseSimplified);

    private void OnSelectEnglish(object sender, RoutedEventArgs e) => Complete(LanguageCatalog.English);

    private void Complete(string language)
    {
        SelectedLanguage = language;
        DialogResult = true;
    }
}