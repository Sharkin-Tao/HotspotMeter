using System.Runtime.InteropServices;
using HotspotMeter.ApiProbe.Native;

namespace HotspotMeter.ApiProbe.Support;

/// <summary>
/// 结构体布局自检。P/Invoke 最常见的错误就是结构体大小与 Windows 头文件不一致，
/// 一旦错位，读到的 SSID、字节数全是垃圾值，而且"看起来像"正常数字，极难发现。
/// 因此每个命令启动时先核对大小，不通过就直接失败，绝不在不可信的数据上继续。
/// </summary>
internal static class LayoutCheck
{
    /// <summary>x64 下 MIB_IF_ROW2 的期望大小（按 netioapi.h 字段顺序推算）。</summary>
    private const int ExpectedMibIfRow2 = 1352;

    /// <summary>x64 下 WLAN_CONNECTION_ATTRIBUTES 的期望大小（isState + mode + 256 WCHAR + assoc + security）。</summary>
    private const int ExpectedWlanConnectionAttributes = 604;

    /// <summary>x64 下 WLAN_INTERFACE_INFO 的期望大小（GUID + 256 WCHAR + state）。</summary>
    private const int ExpectedWlanInterfaceInfo = 532;

    public static bool Verify(out string report)
    {
        var ifRow = Marshal.SizeOf<MibIfRow2>();
        var connection = Marshal.SizeOf<WlanConnectionAttributes>();
        var interfaceInfo = Marshal.SizeOf<WlanInterfaceInfo>();
        var pointerSize = IntPtr.Size;

        var ok = ifRow == ExpectedMibIfRow2
                 && connection == ExpectedWlanConnectionAttributes
                 && interfaceInfo == ExpectedWlanInterfaceInfo;

        var lines = new List<string>
        {
            $"结构体自检（进程 {(pointerSize == 8 ? "x64" : "x86")}）",
            $"  MIB_IF_ROW2                 = {ifRow,5}  (期望 {ExpectedMibIfRow2})  {(ifRow == ExpectedMibIfRow2 ? "OK" : "MISMATCH")}",
            $"  WLAN_CONNECTION_ATTRIBUTES  = {connection,5}  (期望 {ExpectedWlanConnectionAttributes})  {(connection == ExpectedWlanConnectionAttributes ? "OK" : "MISMATCH")}",
            $"  WLAN_INTERFACE_INFO         = {interfaceInfo,5}  (期望 {ExpectedWlanInterfaceInfo})  {(interfaceInfo == ExpectedWlanInterfaceInfo ? "OK" : "MISMATCH")}",
        };

        if (pointerSize != 8)
        {
            lines.Add("  警告：当前不是 x64 进程，上述期望值不适用；请用 x64 运行，否则结论不可用。");
        }

        report = string.Join(Environment.NewLine, lines);
        return ok;
    }
}