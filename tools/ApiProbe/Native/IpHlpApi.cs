using System.ComponentModel;
using System.Runtime.InteropServices;
using HotspotMeter.ApiProbe.Model;
using HotspotMeter.ApiProbe.Support;

namespace HotspotMeter.ApiProbe.Native;

/// <summary>
/// MIB_IF_ROW2 的托管投影。字段顺序严格对应 Windows SDK 的 netioapi.h，
/// 其中 PhysicalAddress 的实际布局是
/// <c>ULONG PhysicalAddressLength; UCHAR PhysicalAddress[32]; UCHAR PermanentPhysicalAddress[32];</c>，
/// 不是单个结构体。x64 下总大小应为 1352 字节，运行时自检。
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MibIfRow2
{
    public ulong InterfaceLuid;
    public uint InterfaceIndex;
    public Guid InterfaceGuid;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
    public string Alias;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
    public string Description;

    public uint PhysicalAddressLength;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] PhysicalAddress;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] PermanentPhysicalAddress;

    public uint Mtu;
    public uint Type;
    public uint TunnelType;
    public uint MediaType;
    public uint PhysicalMediumType;
    public uint AccessType;
    public uint DirectionType;
    public byte InterfaceAndOperStatusFlags;
    public uint OperStatus;
    public uint AdminStatus;
    public uint MediaConnectState;
    public Guid NetworkGuid;
    public uint ConnectionType;
    public ulong TransmitLinkSpeed;
    public ulong ReceiveLinkSpeed;
    public ulong InOctets;
    public ulong InUcastPkts;
    public ulong InNUcastPkts;
    public ulong InDiscards;
    public ulong InErrors;
    public ulong InUnknownProtos;
    public ulong InUcastOctets;
    public ulong InMulticastOctets;
    public ulong InBroadcastOctets;
    public ulong OutOctets;
    public ulong OutUcastPkts;
    public ulong OutNUcastPkts;
    public ulong OutDiscards;
    public ulong OutErrors;
    public ulong OutUcastOctets;
    public ulong OutMulticastOctets;
    public ulong OutBroadcastOctets;
    public ulong OutQLen;
}

/// <summary>GetIfTable2 / FreeMibTable 的最小封装。</summary>
internal static class IpHelper
{
    private const string Dll = "iphlpapi.dll";

    /// <summary>IF_TYPE_IEEE80211，WLAN 接口的 IANA 类型。</summary>
    internal const uint IfTypeIeee80211 = 71;

    /// <summary>MIB_IF_TABLE2 中 Table 数组相对结构体起始的偏移（NumEntries 之后按 8 字节对齐）。</summary>
    private const int TableOffset = 8;

    [DllImport(Dll, ExactSpelling = true)]
    private static extern uint GetIfTable2(out IntPtr ppIfTable);

    [DllImport(Dll, ExactSpelling = true)]
    private static extern void FreeMibTable(IntPtr pMemory);

    public static IReadOnlyList<IfRow> ReadRows()
    {
        var rc = GetIfTable2(out var table);
        if (rc != 0 || table == IntPtr.Zero)
        {
            throw new ProbeException($"GetIfTable2 失败，返回码 {rc}（{new Win32Exception((int)rc).Message}）。");
        }

        try
        {
            var count = Marshal.ReadInt32(table);
            var stride = Marshal.SizeOf<MibIfRow2>();
            var rows = new List<IfRow>(Math.Max(count, 0));
            var arrayStart = IntPtr.Add(table, TableOffset);

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibIfRow2>(IntPtr.Add(arrayStart, i * stride));
                rows.Add(new IfRow(
                    row.InterfaceGuid,
                    row.InterfaceIndex,
                    row.InterfaceLuid,
                    row.Alias ?? string.Empty,
                    row.Description ?? string.Empty,
                    row.Type,
                    row.OperStatus,
                    row.AdminStatus,
                    row.MediaConnectState,
                    row.InOctets,
                    row.OutOctets,
                    row.InUcastPkts,
                    row.OutUcastPkts,
                    FormatPhysicalAddress(row.PhysicalAddress, row.PhysicalAddressLength),
                    row.Mtu,
                    row.TransmitLinkSpeed,
                    row.ReceiveLinkSpeed));
            }

            return rows;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    private static string FormatPhysicalAddress(byte[]? address, uint length)
    {
        if (address is null || length == 0)
        {
            return string.Empty;
        }

        var take = (int)Math.Min(length, (uint)address.Length);
        return string.Join(':', address.Take(take).Select(b => b.ToString("X2")));
    }
}

/// <summary>把返回码翻译成可读文本，避免日志里只有数字。</summary>
internal static class NativeErrors
{
    public static string Describe(uint code) => code switch
    {
        0 => "ERROR_SUCCESS",
        87 => "ERROR_INVALID_PARAMETER",
        1168 => "ERROR_NOT_FOUND",
        5023 => "ERROR_INVALID_STATE（接口未连接时属正常）",
        _ => new Win32Exception((int)code).Message,
    };
}