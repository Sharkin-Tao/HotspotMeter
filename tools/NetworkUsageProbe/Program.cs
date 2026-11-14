using System.Text;
using Windows.Networking.Connectivity;

// 最小化探针：验证 API 在当前非打包 WPF 进程模型下是否可用，以及历史数据范围。
Console.OutputEncoding = Encoding.UTF8;

var end = DateTimeOffset.Now;
var start = end.AddDays(-7);
Console.WriteLine($"range: {start:O} .. {end:O}");
Console.WriteLine($"identity: {Environment.UserName}, packaged={IsPackaged()}");
Console.WriteLine($"granularity: {string.Join(",", Enum.GetNames<DataUsageGranularity>())}");

var states = new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare };
var profiles = NetworkInformation.GetConnectionProfiles();
Console.WriteLine($"profiles: {profiles.Count}");

foreach (var profile in profiles)
{
    Console.WriteLine($"\n== {profile.ProfileName} ==");
    Console.WriteLine($"adapter={profile.NetworkAdapter?.NetworkAdapterId} cost={profile.GetConnectionCost()?.NetworkCostType}");

    try
    {
        var usage = await profile.GetNetworkUsageAsync(start, end, DataUsageGranularity.Total, states);
        Console.WriteLine($"GetNetworkUsageAsync OK: {usage.Count} rows sent={usage.Sum(u => (long)u.BytesSent)} recv={usage.Sum(u => (long)u.BytesReceived)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"GetNetworkUsageAsync FAILED: {Describe(ex)}");
    }

    try
    {
        var attributed = await profile.GetAttributedNetworkUsageAsync(start, end, states);
        Console.WriteLine($"GetAttributedNetworkUsageAsync OK: {attributed.Count} rows");
        foreach (var row in attributed.OrderByDescending(a => a.BytesReceived + a.BytesSent).Take(10))
        {
            Console.WriteLine($"  id={row.AttributionId} name={row.AttributionName} sent={row.BytesSent} recv={row.BytesReceived}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"GetAttributedNetworkUsageAsync FAILED: {Describe(ex)}");
    }

    // 历史可用性：按日粒度 + 逐应用查询"昨天/3 天前/7 天前"。
    foreach (var dayOffset in new[] { 1, 3, 7 })
    {
        var dayStart = end.Date.AddDays(-dayOffset);
        var dayEnd = dayStart.AddDays(1);
        try
        {
            var daily = await profile.GetNetworkUsageAsync(dayStart, dayEnd, DataUsageGranularity.PerDay, states);
            Console.WriteLine($"daily offset=-{dayOffset}: rows={daily.Count} sent={daily.Sum(d => (long)d.BytesSent)} recv={daily.Sum(d => (long)d.BytesReceived)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"daily offset=-{dayOffset} FAILED: {Describe(ex)}");
        }

        try
        {
            var hist = await profile.GetAttributedNetworkUsageAsync(dayStart, dayEnd, states);
            Console.WriteLine($"attributed offset=-{dayOffset}: rows={hist.Count} sent={hist.Sum(a => (long)a.BytesSent)} recv={hist.Sum(a => (long)a.BytesReceived)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"attributed offset=-{dayOffset} FAILED: {Describe(ex)}");
        }
    }
}

static string Describe(Exception ex)
{
    var hr = ex.HResult.ToString("X8");
    return $"{ex.GetType().Name} hr=0x{hr} msg={ex.Message}";
}

static bool IsPackaged()
{
    try
    {
        _ = Windows.ApplicationModel.Package.Current;
        return true;
    }
    catch
    {
        return false;
    }
}