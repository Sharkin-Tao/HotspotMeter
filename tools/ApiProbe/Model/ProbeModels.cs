namespace HotspotMeter.ApiProbe.Model;

/// <summary>一个无线接口在某一时刻的 WLAN 侧快照。</summary>
internal sealed record WlanSnapshot(
    Guid InterfaceGuid,
    string Description,
    int State,
    bool IsConnected,
    string? Ssid,
    string? Bssid,
    string? ProfileName,
    int SignalQuality,
    uint RxRateKbps,
    uint TxRateKbps,
    bool SecurityEnabled,
    int ConnectionMode,
    int PhyType,
    int AuthAlgorithm,
    int CipherAlgorithm);

/// <summary>一行 MIB_IF_ROW2 的投影（只取本阶段用得到的字段）。</summary>
internal sealed record IfRow(
    Guid InterfaceGuid,
    uint InterfaceIndex,
    ulong InterfaceLuid,
    string Alias,
    string Description,
    uint Type,
    uint OperStatus,
    uint AdminStatus,
    uint MediaConnectState,
    ulong InOctets,
    ulong OutOctets,
    ulong InUcastPkts,
    ulong OutUcastPkts,
    string PhysicalAddress,
    uint Mtu,
    ulong TransmitLinkSpeed,
    ulong ReceiveLinkSpeed);

/// <summary>一次采样的目标行读数；RowPresent=false 表示该 GUID 当前不在接口表里。</summary>
internal sealed record CounterSample(
    DateTimeOffset TimestampUtc,
    bool RowPresent,
    Guid InterfaceGuid,
    string? Alias,
    uint InterfaceIndex,
    ulong InOctets,
    ulong OutOctets);

/// <summary>GUID 与 IP Helper 行的映射结果，P1-2 的判定依据。</summary>
internal sealed record GuidMappingResult(
    Guid InterfaceGuid,
    string WlanDescription,
    int MatchCount,
    bool UniqueMatch,
    bool TypeIsIeee80211,
    IfRow? MatchedRow,
    IReadOnlyList<IfRow> AllMatches);