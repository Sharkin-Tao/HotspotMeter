using HotspotMeter.Core.Models;

namespace HotspotMeter.Tests.TestDoubles;

/// <summary>测试用的固定目标与帧构造器，避免每个用例重复样板。</summary>
internal static class TestData
{
    public static readonly Guid InterfaceGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid OtherInterfaceGuid = Guid.Parse("9a7b2c31-1111-2222-3333-444455556666");

    public const ulong Luid = 0x0047008000000000;

    public static HotspotTarget Target { get; } = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        InterfaceGuid,
        "TestHotspot");

    public static HotspotConnection Connection(
        string? bssid = "02:00:00:00:00:01",
        string ssid = "TestHotspot",
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