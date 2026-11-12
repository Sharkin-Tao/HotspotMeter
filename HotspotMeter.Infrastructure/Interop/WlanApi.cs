using System.Runtime.InteropServices;
using System.Text;

namespace HotspotMeter.Infrastructure.Interop;

/// <summary>
/// wlanapi.dll 的最小 P/Invoke 封装。结构体布局与 <c>wlanapi.h</c> 一致，
/// 阶段 1 已在真机上用 <see cref="Marshal.SizeOf{T}()"/> 验证（WLAN_CONNECTION_ATTRIBUTES = 604 字节）。
/// </summary>
internal static class WlanNative
{
    private const string Dll = "wlanapi.dll";

    internal const uint ErrorSuccess = 0;

    /// <summary>接口未连接时 WlanQueryInterface 的返回码，属正常情况。</summary>
    internal const uint ErrorInvalidState = 5023;

    /// <summary>WLAN_API_VERSION_2_0。</summary>
    internal const uint ClientVersion = 2;

    internal const int OpcodeCurrentConnection = 7;

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
}

[StructLayout(LayoutKind.Sequential)]
internal struct Dot11Ssid
{
    public uint SsidLength;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] Ssid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WlanAssociationAttributes
{
    public Dot11Ssid Dot11Ssid;
    public int Dot11BssType;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
    public byte[] Dot11Bssid;

    public int Dot11PhyType;
    public uint Dot11PhyIndex;
    public uint WlanSignalQuality;
    public uint UlRxRate;
    public uint UlTxRate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WlanSecurityAttributes
{
    [MarshalAs(UnmanagedType.Bool)]
    public bool SecurityEnabled;

    [MarshalAs(UnmanagedType.Bool)]
    public bool OneXEnabled;

    public int Dot11AuthAlgorithm;
    public int Dot11CipherAlgorithm;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WlanConnectionAttributes
{
    /// <summary>WLAN_INTERFACE_STATE。</summary>
    public int IsState;

    /// <summary>WLAN_CONNECTION_MODE。</summary>
    public int WlanConnectionMode;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string ProfileName;

    public WlanAssociationAttributes Association;
    public WlanSecurityAttributes Security;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WlanInterfaceInfo
{
    public Guid InterfaceGuid;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string InterfaceDescription;

    /// <summary>WLAN_INTERFACE_STATE。</summary>
    public int IsState;
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

/// <summary>WLAN P/Invoke 调用失败。检测器只在"查询失败"时抛出，未连接不是错误。</summary>
internal sealed class WlanInteropException : Exception
{
    public WlanInteropException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// WlanOpenHandle 的托管封装。句柄生命周期与实例一致；WlanFreeMemory 由调用方保证。
/// </summary>
internal sealed class WlanClient : IDisposable
{
    private const int InterfaceInfoListHeaderSize = 8;
    private const int ExpectedConnectionAttributesSize = 604;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly int ConnectionAttributesSize = Marshal.SizeOf<WlanConnectionAttributes>();

    private IntPtr _handle;
    private bool _disposed;

    private WlanClient(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>打开 WLAN 客户端句柄；WLAN AutoConfig 未运行时抛出。</summary>
    public static WlanClient Open()
    {
        var rc = WlanNative.WlanOpenHandle(WlanNative.ClientVersion, IntPtr.Zero, out _, out var handle);
        if (rc != WlanNative.ErrorSuccess || handle == IntPtr.Zero)
        {
            throw new WlanInteropException(
                $"WlanOpenHandle 失败，返回码 {rc}（{NativeErrors.Describe(rc)}）。请确认 WLAN AutoConfig 服务正在运行。");
        }

        if (ConnectionAttributesSize != ExpectedConnectionAttributesSize)
        {
            WlanNative.WlanCloseHandle(handle, IntPtr.Zero);
            throw new WlanInteropException(
                $"WLAN_CONNECTION_ATTRIBUTES 托管大小 {ConnectionAttributesSize} 字节，与真机验证值 {ExpectedConnectionAttributesSize} 不一致，P/Invoke 布局有误。");
        }

        return new WlanClient(handle);
    }

    /// <summary>枚举所有无线接口。</summary>
    public IReadOnlyList<WlanInterfaceInfo> EnumerateInterfaces()
    {
        ThrowIfDisposed();

        var rc = WlanNative.WlanEnumInterfaces(_handle, IntPtr.Zero, out var listPtr);
        if (rc != WlanNative.ErrorSuccess || listPtr == IntPtr.Zero)
        {
            throw new WlanInteropException($"WlanEnumInterfaces 失败，返回码 {rc}（{NativeErrors.Describe(rc)}）。");
        }

        try
        {
            var count = Marshal.ReadInt32(listPtr);
            var stride = Marshal.SizeOf<WlanInterfaceInfo>();
            var result = new List<WlanInterfaceInfo>(Math.Max(count, 0));

            for (var i = 0; i < count; i++)
            {
                var itemPtr = IntPtr.Add(listPtr, InterfaceInfoListHeaderSize + (i * stride));
                result.Add(Marshal.PtrToStructure<WlanInterfaceInfo>(itemPtr));
            }

            return result;
        }
        finally
        {
            WlanNative.WlanFreeMemory(listPtr);
        }
    }

    /// <summary>
    /// 查询指定接口的当前连接。成功但未连接时返回 false（对应 ERROR_INVALID_STATE），
    /// 其它失败码抛出异常，由引擎按"暂时不可用"处理。
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
            throw new WlanInteropException($"WlanQueryInterface(current_connection) 失败，返回码 {rc}（{NativeErrors.Describe(rc)}）。");
        }

        try
        {
            if (size != ExpectedConnectionAttributesSize)
            {
                throw new WlanInteropException(
                    $"WLAN_CONNECTION_ATTRIBUTES 大小不匹配：API 报告 {size}，托管定义 {ExpectedConnectionAttributesSize}。");
            }

            attributes = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
            return true;
        }
        finally
        {
            WlanNative.WlanFreeMemory(data);
        }
    }

    /// <summary>SSID 是原始字节，按长度截断后解码；非法字节显示为替换字符而不是抛异常。</summary>
    public static string DecodeSsid(Dot11Ssid ssid)
    {
        var length = (int)Math.Min(ssid.SsidLength, 32u);
        if (length <= 0 || ssid.Ssid is null)
        {
            return string.Empty;
        }

        return Utf8.GetString(ssid.Ssid, 0, length);
    }

    /// <summary>把 6 字节 BSSID 格式化为大写冒号分隔文本；长度不足时返回空串。</summary>
    public static string FormatBssid(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 6)
        {
            return string.Empty;
        }

        return string.Join(':', bytes.Take(6).Select(b => b.ToString("X2")));
    }

    /// <summary>WLAN 报告的速率单位是 kbps，这里统一换算成 bps。</summary>
    public static ulong ToBitsPerSecond(uint kilobitsPerSecond)
        => (ulong)kilobitsPerSecond * 1000UL;

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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>把 Win32 返回码翻译成可读文本，避免日志里只有数字。</summary>
internal static class NativeErrors
{
    public static string Describe(uint code) => code switch
    {
        0 => "ERROR_SUCCESS",
        87 => "ERROR_INVALID_PARAMETER",
        1168 => "ERROR_NOT_FOUND（接口不存在）",
        5023 => "ERROR_INVALID_STATE（接口未连接，通常属正常）",
        _ => new System.ComponentModel.Win32Exception((int)code).Message,
    };
}