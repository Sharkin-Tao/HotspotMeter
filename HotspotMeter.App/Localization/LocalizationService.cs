using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using HotspotMeter.Core.Localization;

namespace HotspotMeter.App.Localization;

/// <summary>
/// 全局唯一的界面语言状态。所有窗口通过同一个实例读取文案，
/// 切换语言时只改这一处，再通知绑定和已打开的 ViewModel 刷新。
/// <para>
/// 只改变显示用的 <see cref="Culture"/>，不修改 <see cref="CultureInfo.CurrentCulture"/>：
/// 数据库时间戳、文件名和既有数值解析仍保持原来的区域性，避免切换语言影响持久化。
/// </para>
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private string _language = LanguageCatalog.ChineseSimplified;

    private LocalizationService()
    {
    }

    public static LocalizationService Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>语言确实发生变化后触发；ViewModel 用它重建已经生成的动态文案。</summary>
    public event Action? LanguageChanged;

    public string Language => _language;

    public CultureInfo Culture => CultureInfo.GetCultureInfo(_language);

    /// <summary>
    /// XAML 通过 <c>{loc:Tr Key}</c> 绑定到该索引器。WPF 对索引器绑定监听
    /// <c>Item[]</c>，切换语言时同时抛出空属性名，确保所有索引器绑定立即重读。
    /// </summary>
    public string this[string key] => LanguageCatalog.Get(_language, key);

    /// <summary>
    /// 设置语言；非法值回退简体中文。返回是否发生了实际变化，
    /// 便于调用方决定是否需要保存配置（首次选择时即使相同也要保存）。
    /// </summary>
    public bool SetLanguage(string? language)
    {
        var normalized = LanguageCatalog.Normalize(language);
        if (string.Equals(_language, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        _language = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        LanguageChanged?.Invoke();
        return true;
    }

    public static string T(string key) => Current[key];

    public static string F(string key, params object[] args) =>
        string.Format(Current.Culture, Current[key], args);
}

/// <summary>
/// 把 <c>{loc:Tr Some_Key}</c> 编译成对全局语言服务的索引器绑定。
/// 这样静态 XAML 文字也能随语言切换刷新，而不是在窗口构造时固化。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            return string.Empty;
        }

        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationService.Current,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}