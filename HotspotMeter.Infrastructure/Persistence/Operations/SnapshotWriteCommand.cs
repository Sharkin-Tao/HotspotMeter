namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 快照质量位。当前只定义一位：逐日数据是否为比例分摊的估算值。
///
/// 为什么"跨日即估算"而不是用 <c>connected == interval</c> 豁免：
/// 字节在区间内是突发分布的（例如午夜前 2 秒一次性下载 100 MB），
/// 只要输入是"总量 + 一个区间"，跨日拆分就只能是按时间权重的近似。
/// 用数值巧合判断"精确"会把估算值伪装成精确值，界面与报表也就无法如实提示用户。
/// </summary>
[Flags]
public enum SnapshotQualityFlags
{
    /// <summary>无特殊标记。</summary>
    None = 0,

    /// <summary>逐日字节/连接时长来自按 UTC 区间长度的比例分摊，属于估算值。</summary>
    DailySplitEstimated = 1 << 0,
}

/// <summary>
/// 拆分后的一段逐日数据，对应 <c>TrafficSnapshots</c> 的一行。
/// </summary>
/// <param name="Index">段序号，从 0 开始，与 <c>SegmentIndex</c> 一致。</param>
/// <param name="LocalDate">该段所属本地自然日，格式 <c>yyyy-MM-dd</c>。</param>
/// <param name="FromUtc">段起点（含）。</param>
/// <param name="ToUtc">段终点（不含）。</param>
/// <param name="DownloadBytes">该段下载字节，非负。</param>
/// <param name="UploadBytes">该段上传字节，非负。</param>
/// <param name="ConnectedMilliseconds">该段连接毫秒，非负。</param>
public readonly record struct SegmentInput(
    int Index,
    string LocalDate,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long DownloadBytes,
    long UploadBytes,
    long ConnectedMilliseconds);

/// <summary>
/// 一次快照写入请求的持久化输入。
///
/// 这里的字段是"写入数据库所需的全部事实"，刻意不包含运行时对象（连接、事务、日志器），
/// 这样摘要可以只依赖值，3D 的审计与重算也可以在数据库行上复现同一份摘要。
/// </summary>
public sealed record SnapshotWriteCommand(
    string OperationId,
    long OperationSequence,
    SnapshotKind Kind,
    long DbSessionId,
    long ProfileId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset CapturedAtUtc,
    long DownloadBytesAfter,
    long UploadBytesAfter,
    long ConnectedMilliseconds,
    string TimeZoneId,
    SnapshotQualityFlags QualityFlags,
    IReadOnlyList<SegmentInput> Segments)
{
    /// <summary>
    /// 校验值的合法性。只检查"值本身是否自洽"，不查数据库、不判断会话状态。
    /// 数据库层的 CHECK 是最后一道防线，不是唯一一道。
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OperationId) || OperationId.Length is < 8 or > 200)
        {
            throw new ArgumentException("OperationId 长度必须在 8..200 之间。", nameof(OperationId));
        }

        if (OperationSequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(OperationSequence), "操作序号必须大于等于 1。");
        }

        if (DbSessionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DbSessionId), "数据库会话 ID 必须为正数。");
        }

        if (ProfileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ProfileId), "配置档 ID 必须为正数。");
        }

        if (Kind is not (SnapshotKind.Checkpoint or SnapshotKind.Settlement or SnapshotKind.RecoveryMerge))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind), $"未知的快照类型：{(int)Kind}。");
        }

        if (FromUtc > ToUtc)
        {
            throw new ArgumentException("区间起点不能晚于终点。", nameof(FromUtc));
        }

        if (CapturedAtUtc < ToUtc)
        {
            throw new ArgumentException("采集时间不能早于区间终点。", nameof(CapturedAtUtc));
        }

        if (DownloadBytesAfter < 0 || UploadBytesAfter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DownloadBytesAfter), "字节总量不能为负。");
        }

        if (ConnectedMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectedMilliseconds), "连接毫秒不能为负。");
        }

        if (string.IsNullOrWhiteSpace(TimeZoneId) || TimeZoneId.Length > 128)
        {
            throw new ArgumentException("TimeZoneId 不能为空且不得超过 128 字符。", nameof(TimeZoneId));
        }

        var segments = Segments ?? throw new ArgumentNullException(nameof(Segments));
        if (segments.Count is < 1 or > 400)
        {
            throw new ArgumentOutOfRangeException(nameof(Segments), "分段数必须在 1..400 之间。");
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.Index != i)
            {
                throw new ArgumentException($"分段序号必须从 0 连续递增：位置 {i} 实际为 {segment.Index}。", nameof(Segments));
            }

            if (string.IsNullOrEmpty(segment.LocalDate) || segment.LocalDate.Length != 10)
            {
                throw new ArgumentException("分段 LocalDate 必须是 yyyy-MM-dd。", nameof(Segments));
            }

            if (segment.FromUtc > segment.ToUtc)
            {
                throw new ArgumentException("分段起点不能晚于终点。", nameof(Segments));
            }

            if (segment.DownloadBytes < 0 || segment.UploadBytes < 0 || segment.ConnectedMilliseconds < 0)
            {
                throw new ArgumentException("分段字节与连接毫秒不能为负。", nameof(Segments));
            }
        }
    }
}