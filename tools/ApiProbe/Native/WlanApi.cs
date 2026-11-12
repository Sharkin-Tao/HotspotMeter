using System.Runtime.InteropServices;
using System.Text;
using HotspotMeter.ApiProbe.Model;
using HotspotMeter.ApiProbe.Support;

namespace HotspotMeter.ApiProbe.Native;

/// <summary>
/// wlanapi.dll 的最小 P/Invoke 封装。字段顺序与 <c>wlanapi.h</c> 一致，
/// 结构体大小在运行时用 <see cref="Marshal.SizeOf{T}()"/> 自检（见 <see cref="LayoutCheck"/>）。
/// </summary>
internal static class WlanNative
{
    private const string Dll = "wlanapi.dll";

    internal const uint ErrorSuccess = 0;
    internal const uint ErrorInvalidState = 5023;

    /// <summary>WLAN_API_VERSION_2_0，Windows Vista 之后的客户端都应使用 2。</summary>
    internal const uint ClientVersion = 2;

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanFreeMemory(IntPtr pMemory);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanQueryInterface(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        int opCode,
        IntPtr pReserved,
        out uint pdwDataSize,
        out IntPtr ppData,
        out int pWlanOpcodeValueType);

    /// <summary>WLAN_INTF_OPCODE 中本阶段用到的两项（枚举从 0 开始，已与头文件核对）。</summary>
    internal const int OpcodeInterfaceState = 6;

    internal const int OpcodeCurrentConnection = 7;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Dot11Ssid
{
    public uint uSSIDLength;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] ucSSID;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WlanAssociationAttributes
{
    public Dot11Ssid dot11Ssid;
    public int dot11BssType;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
    public byte[] dot11Bssid;

    public int dot11PhyType;
    public uint uDot11PhyIndex;
    public uint wlanSignalQuality;
    public uint ulRxRate;
    public uint ulTxRate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WlanSecurityAttributes
{
    [MarshalAs(UnmanagedType.Bool)]
    public bool bSecurityEnabled;

    [MarshalAs(UnmanagedType.Bool)]
    public bool bOneXEnabled;

    public int dot11AuthAlgorithm;
    public int dot11CipherAlgorithm;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WlanConnectionAttributes
{
    /// <summary>WLAN_INTERFACE_STATE。</summary>
    public int isState;

    /// <summary>WLAN_CONNECTION_MODE。</summary>
    public int wlanConnectionMode;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string strProfileName;

    public WlanAssociationAttributes wlanAssociationAttributes;
    public WlanSecurityAttributes wlanSecurityAttributes;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WlanInterfaceInfo
{
    public Guid InterfaceGuid;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string strInterfaceDescription;

    /// <summary>WLAN_INTERFACE_STATE。</summary>
    public int isState;
}

/// <summary>WLAN_INTERFACE_INFO_LIST 的头部；数组元素按 SizeOf&lt;WlanInterfaceInfo&gt; 步进读取。</summary>
internal static class WlanInterop
{
    internal const int InterfaceInfoListHeaderSize = 8; // dwNumberOfItems + dwIndex
}

/// <summary>WlanOpenHandle 的托管封装，保证句柄与 WlanFreeMemory 成对释放。</summary>
internal sealed class WlanClient : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private IntPtr _handle;
    private bool _disposed;

    private WlanClient(IntPtr handle, uint negotiatedVersion)
    {
        _handle = handle;
        NegotiatedVersion = negotiatedVersion;
    }

    public uint NegotiatedVersion { get; }

    public static WlanClient Open()
    {
        var rc = WlanNative.WlanOpenHandle(WlanNative.ClientVersion, IntPtr.Zero, out var negotiated, out var handle);
        if (rc != WlanNative.ErrorSuccess || handle == IntPtr.Zero)
        {
            throw new ProbeException($"WlanOpenHandle 失败，返回码 {rc}（{NativeErrors.Describe(rc)}）。请确认 WLAN AutoConfig 服务正在运行。");
        }

        return new WlanClient(handle, negotiated);
    }

    /// <summary>枚举所有无线接口，并对已连接的接口补上当前连接属性。</summary>
    public IReadOnlyList<WlanSnapshot> Enumerate()
    {
        ThrowIfDisposed();

        var rc = WlanNative.WlanEnumInterfaces(_handle, IntPtr.Zero, out var listPtr);
        if (rc != WlanNative.ErrorSuccess || listPtr == IntPtr.Zero)
        {
            throw new ProbeException($"WlanEnumInterfaces 失败，返回码 {rc}（{NativeErrors.Describe(rc)}）。");
        }

        try
        {
            var count = Marshal.ReadInt32(listPtr);
            var stride = Marshal.SizeOf<WlanInterfaceInfo>();
            var result = new List<WlanSnapshot>(count);

            for (var i = 0; i < count; i++)
            {
                var itemPtr = IntPtr.Add(listPtr, WlanInterop.InterfaceInfoListHeaderSize + (i * stride));
                var info = Marshal.PtrToStructure<WlanInterfaceInfo>(itemPtr);
                var connected = info.isState == (int)WlanInterfaceState.Connected;

                WlanConnectionAttributes? attrs = null;
                if (connected && TryQueryConnection(info.InterfaceGuid, out var queried))
                {
                    attrs = queried;
                }

                result.Add(new WlanSnapshot(
                    info.InterfaceGuid,
                    info.strInterfaceDescription ?? string.Empty,
                    info.isState,
                    connected,
                    attrs is { } a ? DecodeSsid(a.wlanAssociationAttributes.dot11Ssid) : null,
                    attrs is { } b ? FormatBssid(b.wlanAssociationAttributes.dot11Bssid) : null,
                    attrs is { } c ? (c.strProfileName ?? string.Empty) : null,
                    attrs is { } d ? (int)d.wlanAssociationAttributes.wlanSignalQuality : 0,
                    attrs is { } e ? e.wlanAssociationAttributes.ulRxRate : 0,
                    attrs is { } f ? f.wlanAssociationAttributes.ulTxRate : 0,
                    attrs is { } g && g.wlanSecurityAttributes.bSecurityEnabled,
                    attrs is { } h ? h.wlanConnectionMode : -1,
                    attrs is { } k ? k.wlanAssociationAttributes.dot11PhyType : -1,
                    attrs is { } m ? m.wlanSecurityAttributes.dot11AuthAlgorithm : -1,
                    attrs is { } n ? n.wlanSecurityAttributes.dot11CipherAlgorithm : -1));
            }

            return result;
        }
        finally
        {
            WlanNative.WlanFreeMemory(listPtr);
        }
    }

    /// <summary>
    /// 查询 wlan_intf_opcode_current_connection。
    /// 未连接时返回 ERROR_INVALID_STATE（5023），属正常情况，不抛异常。
    /// </summary>
    public bool TryQueryConnection(Guid interfaceGuid, out WlanConnectionAttributes attributes)
    {
        ThrowIfDisposed();
        attributes = default;

        var guid = interfaceGuid;
        var rc = WlanNative.WlanQueryInterface(
            _handle,
            ref guid,
            WlanNative.OpcodeCurrentConnection,
            IntPtr.Zero,
            out var size,
            out var data,
            out _);

        if (rc == WlanNative.ErrorInvalidState)
        {
            return false;
        }

        if (rc != WlanNative.ErrorSuccess || data == IntPtr.Zero)
        {
            throw new ProbeException($"WlanQueryInterface(current_connection) 失败，返回码 {rc}（{NativeErrors.Describe(rc)}）。");
        }

        try
        {
            var expected = Marshal.SizeOf<WlanConnectionAttributes>();
            if (size != expected)
            {
                throw new ProbeException(
                    $"WLAN_CONNECTION_ATTRIBUTES 结构体大小不匹配：API 报告 {size} 字节，托管定义 {expected} 字节。"
                    + "这说明 P/Invoke 布局有误，后续数据不可信，必须先修正结构体再继续。");
            }

            attributes = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
            return true;
        }
        finally
        {
            WlanNative.WlanFreeMemory(data);
        }
    }

    /// <summary>按 uSSIDLength 截断后解码。SSID 是原始字节，不保证 UTF-8，非法字节会显示为替换字符。</summary>
    private static string DecodeSsid(Dot11Ssid ssid)
    {
        var length = (int)Math.Min(ssid.uSSIDLength, 32u);
        if (length <= 0 || ssid.ucSSID is null)
        {
            return string.Empty;
        }

        return Utf8.GetString(ssid.ucSSID, 0, length);
    }

    private static string FormatBssid(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 6)
        {
            return string.Empty;
        }

        return string.Join(':', bytes.Take(6).Select(b => b.ToString("X2")));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            WlanNative.WlanCloseHandle(_handle, IntPtr.Zero);
            _handle = IntPtr.Zero;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>WLAN_INTERFACE_STATE，值来自 wlanapi.h。</summary>
internal enum WlanInterfaceState
{
    NotReady = 0,
    Connected = 1,
    AdHocNetworkFormed = 2,
    Disconnecting = 3,
    Disconnected = 4,
    Associating = 5,
    Discovering = 6,
    Authenticating = 7,
}