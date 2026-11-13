using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>DailyTraffic 的一行快照，用于读取与断言。</summary>
public sealed record DailyTrafficRow(
    string LocalDate,
    long ProfileId,
    long DownloadBytes,
    long UploadBytes,
    int SessionCount,
    long ConnectedMilliseconds,
    DateTimeOffset? FirstSessionAtUtc,
    DateTimeOffset? LastSessionAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// DailyTraffic 的增量写入与读取。
///
/// 事务边界：<see cref="ApplyAsync"/> **不**开启事务，调用方必须传入连接与事务，
/// 以便把“插快照 + 累加日汇总”放进同一个原子操作。仓储自身不做重试、不做幂等判断：
/// 同一增量应用两次就会累加两次，幂等必须由写入管线的 OperationId 去重负责。
/// </summary>
public sealed class DailyTrafficRepository
{
    private const string ApplySql = """
        INSERT INTO DailyTraffic
          (LocalDate, ProfileId, DownloadBytes, UploadBytes, SessionCount,
           ConnectedMilliseconds, FirstSessionAtUtc, LastSessionAtUtc, UpdatedAtUtc)
        VALUES
          ($localDate, $profileId, $downloadBytes, $uploadBytes, $sessionCount,
           $connectedMilliseconds, $firstSessionAtUtc, $lastSessionAtUtc, $updatedAtUtc)
        ON CONFLICT(LocalDate, ProfileId) DO UPDATE SET
          DownloadBytes = DownloadBytes + excluded.DownloadBytes,
          UploadBytes = UploadBytes + excluded.UploadBytes,
          SessionCount = SessionCount + excluded.SessionCount,
          ConnectedMilliseconds = ConnectedMilliseconds + excluded.ConnectedMilliseconds,
          FirstSessionAtUtc = CASE
            WHEN excluded.FirstSessionAtUtc IS NULL THEN FirstSessionAtUtc
            WHEN FirstSessionAtUtc IS NULL THEN excluded.FirstSessionAtUtc
            WHEN excluded.FirstSessionAtUtc < FirstSessionAtUtc THEN excluded.FirstSessionAtUtc
            ELSE FirstSessionAtUtc END,
          LastSessionAtUtc = CASE
            WHEN excluded.LastSessionAtUtc IS NULL THEN LastSessionAtUtc
            WHEN LastSessionAtUtc IS NULL THEN excluded.LastSessionAtUtc
            WHEN excluded.LastSessionAtUtc > LastSessionAtUtc THEN excluded.LastSessionAtUtc
            ELSE LastSessionAtUtc END,
          UpdatedAtUtc = excluded.UpdatedAtUtc;
        """;

    private const string GetSql = """
        SELECT LocalDate, ProfileId, DownloadBytes, UploadBytes, SessionCount,
               ConnectedMilliseconds, FirstSessionAtUtc, LastSessionAtUtc, UpdatedAtUtc
        FROM DailyTraffic
        WHERE LocalDate = $localDate AND ProfileId = $profileId;
        """;

    /// <summary>在调用方事务内累加一条增量。</summary>
    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DailyTrafficDelta delta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        DailyTrafficDelta.Validate(delta);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ApplySql;
        command.Parameters.AddWithValue("$localDate", delta.LocalDate);
        command.Parameters.AddWithValue("$profileId", delta.ProfileId);
        command.Parameters.AddWithValue("$downloadBytes", delta.DownloadBytes);
        command.Parameters.AddWithValue("$uploadBytes", delta.UploadBytes);
        command.Parameters.AddWithValue("$sessionCount", delta.SessionCount);
        command.Parameters.AddWithValue("$connectedMilliseconds", delta.ConnectedMilliseconds);
        command.Parameters.AddWithValue(
            "$firstSessionAtUtc",
            delta.FirstSessionAtUtc is { } first ? FormatUtc(first) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$lastSessionAtUtc",
            delta.LastSessionAtUtc is { } last ? FormatUtc(last) : DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUtc", FormatUtc(delta.UpdatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取指定日期与热点的一行；不存在返回 null。</summary>
    public async Task<DailyTrafficRow?> GetAsync(
        SqliteConnection connection,
        string localDate,
        long profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!DailyTrafficDelta.IsValidLocalDate(localDate))
        {
            throw new ArgumentException($"日期必须是合法的 yyyy-MM-dd：{localDate}", nameof(localDate));
        }

        if (profileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "ProfileId 必须大于 0。");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = GetSql;
        command.Parameters.AddWithValue("$localDate", localDate);
        command.Parameters.AddWithValue("$profileId", profileId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DailyTrafficRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt32(4),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
            ParseUtc(reader.GetString(8)));
    }

    private static string FormatUtc(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}