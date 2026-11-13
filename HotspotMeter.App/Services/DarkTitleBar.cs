using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HotspotMeter.App.Services;

/// <summary>
/// Win10 2004+ / Win11 的深色标题栏。
/// 这是纯外观增强：调用失败（旧系统、被策略禁用）时窗口仍然可用，
/// 因此所有返回值都忽略，不抛异常。
/// </summary>
internal static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            // Windows 10 1809–1909 使用属性 19。
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }

        // Win11 22H2 起可单独指定标题栏/边框/文字色；失败不影响深色模式本身。
        var caption = ToColorRef(0x0B, 0x0B, 0x0C);
        DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
        var border = ToColorRef(0x26, 0x26, 0x2A);
        DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
        var text = ToColorRef(0xF5, 0xF5, 0xF7);
        DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
    }

    /// <summary>Win32 COLORREF 是 0x00BBGGRR，与 WPF 的 RGB 顺序相反。</summary>
    private static int ToColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}