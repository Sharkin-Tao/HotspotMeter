using System.Globalization;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 一次日汇总增量。只能通过 <see cref="Create"/> 构造，避免出现“SessionCount 与
/// First/LastSessionAtUtc 不自洽”的中间状态。
///
/// 语义边界：
/// 1. <see cref="SessionCount"/> 表示“在这个自然日**开始**的会话数”，因此一个
///    23:50 → 次日 00:20 的会话只在第一天 +1。
/// 2. <see cref="FirstSessionAtUtc"/> / <see cref="LastSessionAtUtc"/> 是当天开始
///    会话的最早/最晚开始时间，不是流量片段的起止时间。
/// 3. 当前阶段只支持单次会话开始（0 或 1）；批量补记需要放宽约束并重新设计。
/// </summary>
public readonly record struct DailyTrafficDelta
{
    private DailyTrafficDelta(
        long profileId,
        string localDate,
        long downloadBytes,
        long uploadBytes,
        int sessionCount,
        long connectedMilliseconds,
        DateTimeOffset? firstSessionAtUtc,
        DateTimeOffset? lastSessionAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        ProfileId = profileId;
        LocalDate = localDate;
        DownloadBytes = downloadBytes;
        UploadBytes = uploadBytes;
        SessionCount = sessionCount;
        ConnectedMilliseconds = connectedMilliseconds;
        FirstSessionAtUtc = firstSessionAtUtc;
        LastSessionAtUtc = lastSessionAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public long ProfileId { get; }

    public string LocalDate { get; }

    public long DownloadBytes { get; }

    public long UploadBytes { get; }

    public int SessionCount { get; }

    public long ConnectedMilliseconds { get; }

    public DateTimeOffset? FirstSessionAtUtc { get; }

    public DateTimeOffset? LastSessionAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>创建并校验增量；所有业务规则都在这里失败，便于上游尽早发现问题。</summary>
    public static DailyTrafficDelta Create(
        long profileId,
        string localDate,
        long downloadBytes,
        long uploadBytes,
        int sessionCount,
        long connectedMilliseconds,
        DateTimeOffset? firstSessionAtUtc,
        DateTimeOffset? lastSessionAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (profileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "ProfileId 必须大于 0。");
        }

        if (localDate is null)
        {
            throw new ArgumentNullException(nameof(localDate));
        }

        if (!IsValidLocalDate(localDate))
        {
            throw new ArgumentException($"日期必须是合法的 yyyy-MM-dd：{localDate}", nameof(localDate));
        }

        if (downloadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(downloadBytes), downloadBytes, "下载字节不能为负。");
        }

        if (uploadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(uploadBytes), uploadBytes, "上传字节不能为负。");
        }

        if (connectedMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectedMilliseconds), connectedMilliseconds, "连接毫秒不能为负。");
        }

        if (sessionCount is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionCount), sessionCount, "当前阶段 SessionCount 只能是 0 或 1。");
        }

        if (sessionCount == 0 && (firstSessionAtUtc is not null || lastSessionAtUtc is not null))
        {
            throw new ArgumentException("SessionCount 为 0 时，First/LastSessionAtUtc 必须同时为 null。");
        }

        if (sessionCount == 1 && (firstSessionAtUtc is null || lastSessionAtUtc is null))
        {
            throw new ArgumentException("SessionCount 为 1 时，First/LastSessionAtUtc 必须同时非 null。");
        }

        if (firstSessionAtUtc is { } first && lastSessionAtUtc is { } last && first > last)
        {
            throw new ArgumentException("FirstSessionAtUtc 不能晚于 LastSessionAtUtc。");
        }

        return new DailyTrafficDelta(
            profileId,
            localDate,
            downloadBytes,
            uploadBytes,
            sessionCount,
            connectedMilliseconds,
            firstSessionAtUtc?.ToUniversalTime(),
            lastSessionAtUtc?.ToUniversalTime(),
            updatedAtUtc.ToUniversalTime());
    }

    /// <summary>
    /// 仓储入口的防御性校验：不假设所有调用方都走 <see cref="Create"/>，
    /// 但只重复关键不变量，不重新定义业务语义。
    /// </summary>
    internal static void Validate(in DailyTrafficDelta delta)
    {
        if (delta.ProfileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta.ProfileId, "ProfileId 必须大于 0。");
        }

        if (delta.LocalDate is null || !IsValidLocalDate(delta.LocalDate))
        {
            throw new ArgumentException("LocalDate 必须是合法的 yyyy-MM-dd。", nameof(delta));
        }

        if (delta.DownloadBytes < 0 || delta.UploadBytes < 0 || delta.ConnectedMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "字节与连接毫秒不能为负。");
        }

        if (delta.SessionCount is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta.SessionCount, "SessionCount 只能是 0 或 1。");
        }

        if (delta.SessionCount == 0)
        {
            if (delta.FirstSessionAtUtc is not null || delta.LastSessionAtUtc is not null)
            {
                throw new ArgumentException("SessionCount 为 0 时，First/LastSessionAtUtc 必须同时为 null。", nameof(delta));
            }
        }
        else
        {
            if (delta.FirstSessionAtUtc is null || delta.LastSessionAtUtc is null)
            {
                throw new ArgumentException("SessionCount 为 1 时，First/LastSessionAtUtc 必须同时非 null。", nameof(delta));
            }

            if (delta.FirstSessionAtUtc > delta.LastSessionAtUtc)
            {
                throw new ArgumentException("FirstSessionAtUtc 不能晚于 LastSessionAtUtc。", nameof(delta));
            }
        }
    }

    /// <summary>严格校验 yyyy-MM-dd，并拒绝 Sqlite 会宽松接受的写法（如 2026-2-30）。</summary>
    internal static bool IsValidLocalDate(string value)
    {
        if (value.Length != 10)
        {
            return false;
        }

        return DateOnly.TryParseExact(
                   value,
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var parsed)
               && parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == value;
    }
}