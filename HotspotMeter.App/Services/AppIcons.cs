using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace HotspotMeter.App.Services;

/// <summary>
/// 应用图标（托盘 + 窗口）的唯一加载入口。
/// 图标以 Resource 编译进程序集，不依赖运行目录下的散落文件。
/// </summary>
internal static class AppIcons
{
    private static Drawing.Icon? _trayIcon;
    private static ImageSource? _windowIcon;

    public static Drawing.Icon TrayIcon => _trayIcon ??= LoadTrayIcon();

    public static ImageSource WindowIcon => _windowIcon ??= LoadWindowIcon();

    private static Stream OpenIconStream()
    {
        var uri = new Uri("pack://application:,,,/Assets/HotspotMeter.ico", UriKind.Absolute);
        var resource = Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException("找不到内嵌图标 Assets/HotspotMeter.ico。");
        return resource.Stream;
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        using var stream = OpenIconStream();
        // 克隆后不再依赖流生命周期；NotifyIcon 会在系统小图标尺寸下自行缩放。
        using var icon = new Drawing.Icon(stream);
        return (Drawing.Icon)icon.Clone();
    }

    private static ImageSource LoadWindowIcon()
    {
        using var stream = OpenIconStream();
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
    }
}