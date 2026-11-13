using System.Globalization;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>与 Schema v2 的 <c>TrafficSnapshots.Kind</c> 一一对应的快照类型。</summary>
public enum SnapshotKind
{
    /// <summary>普通检查点，可被后续检查点覆盖/合并。</summary>
    Checkpoint = 1,

    /// <summary>会话最终结算，必须 <c>IsFinal=1</c>。</summary>
    Settlement = 2,

    /// <summary>恢复合并写回原会话。</summary>
    RecoveryMerge = 3,
}

/// <summary>
/// 操作标识的组成字段。保留结构化形式，便于持久化层在写入前
/// 用同一组输入重建标识并识别“同一 OperationId、不同内容”的冲突。
/// </summary>
public readonly record struct OperationKey(
    long DbSessionId,
    SnapshotKind Kind,
    long FromUtcTicks,
    long ToUtcTicks)
{
    /// <summary>规范化为 <c>op:{session}:{kind}:{fromTicks}:{toTicks}</c>。</summary>
    public string ToOperationId() => OperationIdFactory.Format(this);
}

/// <summary>
/// 生成确定性的快照操作标识。
///
/// 规则（与已批准设计一致）：
/// 1. 格式固定为 <c>op:{dbSessionId}:{kind}:{fromUtc.UtcTicks}:{toUtc.UtcTicks}</c>；
///    长度远小于 Schema v2 的 200 字符上限。
/// 2. 时间先统一换算为 UTC，再取 <see cref="DateTimeOffset.UtcTicks"/>；
///    同一瞬间用不同时区偏移表示，得到的 Ticks 完全相同，因此标识一致。
/// 3. 区间允许零长度（<c>FromUtc == ToUtc</c>），用于瞬时标记/边界结算；
///    <c>FromUtc &gt; ToUtc</c> 为非法区间，直接拒绝。这与
///    <c>TrafficSnapshots CHECK (FromUtc &lt;= ToUtc)</c> 保持一致。
/// 4. 不允许随机数、当前时间或本地时区参与计算；相同输入必然得到相同标识。
/// 5. 标识本身不承诺“崩溃后仍能重建内容”，它只承诺同一输入稳定；
///    内容冲突的识别由持久化层比较字段完成（见 <see cref="OperationKey"/>）。
/// </summary>
public static class OperationIdFactory
{
    /// <summary>Schema v2 对 OperationId 的最大长度约束。</summary>
    public const int MaxLength = 200;

    /// <summary>生成 OperationId。</summary>
    /// <exception cref="ArgumentOutOfRangeException">会话 Id 或 Kind 非法。</exception>
    /// <exception cref="ArgumentException">区间倒置（FromUtc &gt; ToUtc）。</exception>
    public static string Create(
        long dbSessionId,
        SnapshotKind kind,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        var key = CreateKey(dbSessionId, kind, fromUtc, toUtc);
        return Format(key);
    }

    /// <summary>生成结构化键；持久化层可保留它用于冲突检测。</summary>
    public static OperationKey CreateKey(
        long dbSessionId,
        SnapshotKind kind,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        if (dbSessionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dbSessionId), "数据库会话 Id 必须为正数。");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), $"未定义的快照类型：{(int)kind}。");
        }

        var fromTicks = fromUtc.ToUniversalTime().UtcTicks;
        var toTicks = toUtc.ToUniversalTime().UtcTicks;

        if (fromTicks > toTicks)
        {
            throw new ArgumentException(
                $"时间区间倒置：FromUtc({fromUtc:O}) 晚于 ToUtc({toUtc:O})。",
                nameof(fromUtc));
        }

        return new OperationKey(dbSessionId, kind, fromTicks, toTicks);
    }

    /// <summary>把结构化键格式化为字符串。</summary>
    public static string Format(OperationKey key)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"op:{key.DbSessionId}:{(int)key.Kind}:{key.FromUtcTicks}:{key.ToUtcTicks}");
        if (text.Length > MaxLength)
        {
            throw new InvalidOperationException($"OperationId 长度 {text.Length} 超过上限 {MaxLength}。");
        }

        return text;
    }

    /// <summary>严格解析；格式不符、Kind 非法或区间倒置时返回 false。</summary>
    public static bool TryParse(string? operationId, out OperationKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(operationId))
        {
            return false;
        }

        var parts = operationId.Split(':');
        if (parts.Length != 5 || !string.Equals(parts[0], "op", StringComparison.Ordinal))
        {
            return false;
        }

        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var session)
            || session <= 0)
        {
            return false;
        }

        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var kindValue)
            || !Enum.IsDefined(typeof(SnapshotKind), kindValue))
        {
            return false;
        }

        if (!long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var fromTicks)
            || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var toTicks)
            || fromTicks > toTicks)
        {
            return false;
        }

        key = new OperationKey(session, (SnapshotKind)kindValue, fromTicks, toTicks);
        return true;
    }
}