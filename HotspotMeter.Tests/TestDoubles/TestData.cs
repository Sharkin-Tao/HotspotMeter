using HotspotMeter.Core.Models;

namespace HotspotMeter.Tests.TestDoubles;

/// <summary>测试用的固定目标与帧构造器，避免每个用例重复样板。</summary>
internal static class TestData
{
    public static readonly Guid InterfaceGuid = Guid.Parse("2f96e651-0898-4f18-ab35-41096d91e1b5");

    public static readonly Guid OtherInterfaceGuid = Guid.Parse("9a7b2c31-1111-2222-3333-444455556666");

    public const ulong Luid = 0x0047008000000000;

    public static HotspotTarget Target { get; } = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        InterfaceGuid,
        "OPPO Find X9");

    public static HotspotConnection Connection(
        string? bssid = "1A:44:DD:0E:E5:B0",
        string ssid = "OPPO Find X9",
        ulong speedBps = 866_700_000,
        Guid? guid = null)
        => new(guid ?? InterfaceGuid, ssid, bssid, speedBps, speedBps);

    public static InterfaceReading Reading(ulong inOctets, ulong outOctets, ulong luid = Luid, Guid? guid = null)
        => new(new NetworkInterfaceKey(guid ?? InterfaceGuid, luid), new InterfaceCounters(inOctets, outOctets));

    public static MonitorFrame Frame(
        long monotonicMilliseconds,
        ulong inOctets,
        ulong outOctets,
        HotspotConnection? connection = null,
        InterfaceReading? reading = null,
        bool detectorFailed = false,
        long tickCount64 = 5_000_000)
    {
        var at = DateTimeOffset.UnixEpoch.AddMilliseconds(monotonicMilliseconds);
        return new MonitorFrame(
            at,
            monotonicMilliseconds,
            connection ?? Connection(),
            reading ?? Reading(inOctets, outOctets),
            tickCount64,
            detectorFailed);
    }

    public static MonitorFrame FrameWithoutReading(
        long monotonicMilliseconds,
        HotspotConnection? connection = null,
        bool detectorFailed = false)
    {
        var at = DateTimeOffset.UnixEpoch.AddMilliseconds(monotonicMilliseconds);
        return new MonitorFrame(at, monotonicMilliseconds, connection ?? Connection(), null, 5_000_000, detectorFailed);
    }
}