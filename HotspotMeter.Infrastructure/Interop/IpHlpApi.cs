using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HotspotMeter.Infrastructure.Interop;

/// <summary>
/// MIB_IF_ROW2 的托管投影。字段顺序严格对应 netioapi.h；
/// <c>PhysicalAddressLength + PhysicalAddress[32] + PermanentPhysicalAddress[32]</c> 是三个独立字段，
/// 阶段 1 已在真机验证 x64 大小必须为 1352 字节。
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

/// <summary>
/// GetIfTable2 / FreeMibTable 封装。每次读取都去系统拿新表，
/// 不缓存任何行——接口可能随时消失或重建。
/// </summary>
internal static class IpHelper
{
    private const string Dll = "iphlpapi.dll";
    private const int TableOffset = 8;
    private const int ExpectedRowSize = 1352;

    private static readonly int RowSize = Marshal.SizeOf<MibIfRow2>();

    [DllImport(Dll, ExactSpelling = true)]
    private static extern uint GetIfTable2(out IntPtr ppIfTable);

    [DllImport(Dll, ExactSpelling = true)]
    private static extern void FreeMibTable(IntPtr pMemory);

    /// <summary>读取接口表全量行；调用失败抛出，由引擎转成异常事件。</summary>
    public static IReadOnlyList<MibIfRow2> ReadRows()
    {
        if (RowSize != ExpectedRowSize)
        {
            throw new InvalidOperationException(
                $"MIB_IF_ROW2 托管大小 {RowSize} 字节，与真机验证值 {ExpectedRowSize} 不一致，P/Invoke 布局有误。");
        }

        var rc = GetIfTable2(out var table);
        if (rc != 0 || table == IntPtr.Zero)
        {
            throw new InvalidOperationException($"GetIfTable2 失败，返回码 {rc}（{new Win32Exception((int)rc).Message}）。");
        }

        try
        {
            var count = Marshal.ReadInt32(table);
            var rows = new List<MibIfRow2>(Math.Max(count, 0));
            var arrayStart = IntPtr.Add(table, TableOffset);

            for (var i = 0; i < count; i++)
            {
                rows.Add(Marshal.PtrToStructure<MibIfRow2>(IntPtr.Add(arrayStart, i * RowSize)));
            }

            return rows;
        }
        finally
        {
            FreeMibTable(table);
        }
    }
}