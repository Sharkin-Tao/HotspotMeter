using System.Globalization;
using System.IO;
using HotspotMeter.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.App.Services;

/// <summary>某一天的网卡总流量。HasRecord=false 表示当天没有任何检查点，不能当成 0。</summary>
public sealed record DailyUsage(
    DateOnly Date,
    long DownloadBytes,
    long UploadBytes,
    long SessionCount,
    long ConnectedMilliseconds,
    bool HasRecord)
{
    public long TotalBytes => DownloadBytes + UploadBytes;
}

/// <summary>
/// 只读历史统计查询。数据来自本程序写入的 TrafficSnapshots / DailyTraffic，
/// 不读取网卡累计计数器，也不调用运营商接口。
/// </summary>
public sealed class TrafficStatsService
{
    private readonly string _databasePath;

    public TrafficStatsService(string? databasePath = null)
    {
        _databasePath = databasePath ?? DatabasePaths.CreateDefault().DatabaseFile;
    }

    public string DatabasePath => _databasePath;

    public bool DatabaseExists => File.Exists(_databasePath);

    /// <summary>按本地自然日读取区间；缺失日期保留 HasRecord=false。</summary>
    public async Task<IReadOnlyList<DailyUsage>> GetDailyAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var rows = new Dictionary<DateOnly, (long Down, long Up, long Count, long Ms)>();
        if (!DatabaseExists)
        {
            return [];
        }

        await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT LocalDate, SUM(DownloadBytes), SUM(UploadBytes), SUM(SessionCount), SUM(ConnectedMilliseconds)
            FROM DailyTraffic
            WHERE LocalDate >= $from AND LocalDate <= $to
            GROUP BY LocalDate;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!DateOnly.TryParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                continue;
            }

            rows[date] = (reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        }

        var result = new List<DailyUsage>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (rows.TryGetValue(date, out var row))
            {
                result.Add(new DailyUsage(date, row.Down, row.Up, row.Count, row.Ms, true));
            }
            else
            {
                result.Add(new DailyUsage(date, 0, 0, 0, 0, false));
            }
        }

        return result;
    }

    public async Task<DailyUsage> GetDayAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var rows = await GetDailyAsync(date, date, cancellationToken);
        return rows.Count == 0 ? new DailyUsage(date, 0, 0, 0, 0, false) : rows[0];
    }

    /// <summary>最近连续 7 天（含今天），按时间升序。</summary>
    public Task<IReadOnlyList<DailyUsage>> GetLastSevenDaysAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return GetDailyAsync(today.AddDays(-6), today, cancellationToken);
    }
}