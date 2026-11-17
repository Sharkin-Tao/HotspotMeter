using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HotspotMeter.App.Converters;

/// <summary>true → Collapsed，false → Visible。用于“无数据提示”等反向可见性。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}