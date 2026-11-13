using System.Globalization;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 数据库中 UTC 时间文本的统一格式：固定 33 个字符、
/// <c>yyyy-MM-dd'T'HH:mm:ss.fffffff+00:00</c>（100 纳秒精度、固定零偏移）。
///
/// 为什么不用 <c>"O"</c>：
/// 1. <c>"O"</c> 会保留原偏移（<c>+08:00</c>），同一时刻可能写出多种文本，
///    破坏"同一时刻只有一种持久化表示"的前提，摘要与比较都会变得不可靠。
/// 2. <c>"O"</c> 的小数位数随值变化（0/3/7 位），而 SQLite 的 CHECK 需要固定长度才能用 GLOB 精确校验。
/// 3. 固定零偏移与固定 7 位小数后，文本的字典序与时间先后一致，跨日与区间比较可以直接用 SQL 字符串比较。
///
/// 该类只做"格式化/解析/校验"，不读时钟、不做时区换算之外的业务判断。
/// </summary>
public static class UtcTimestamp
{
    /// <summary>与 <see cref="DateTimeOffset.ToString(string)"/> 配合的格式串。</summary>
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'";

    /// <summary>固定文本长度。SQLite CHECK 依赖这个常量。</summary>
    public const int Length = 33;

    private const string ParseFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";

    /// <summary>
    /// 把任意 <see cref="DateTimeOffset"/> 规范化为 UTC 文本。
    /// 输入不是 UTC 时会先换算，而不是拒绝——调用方无需先转换。
    /// </summary>
    public static string FromUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>
    /// 严格解析：必须是 33 字符、固定零偏移、7 位小数，且格式化回来与原文完全一致。
    /// 宽松解析会把 <c>24:00</c>、单数字月份等写法带进数据库，与 CHECK 冲突。
    /// </summary>
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (text is null || text.Length != Length)
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                text,
                ParseFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        if (parsed.Offset != TimeSpan.Zero)
        {
            return false;
        }

        // 往返校验：拒绝 24:00 这类"解析成功但格式不唯一"的写法。
        if (!string.Equals(FromUtc(parsed), text, StringComparison.Ordinal))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>解析失败时抛 <see cref="FormatException"/>，异常消息包含原始文本便于定位。</summary>
    public static DateTimeOffset Parse(string text)
    {
        if (!TryParse(text, out var value))
        {
            throw new FormatException($"不是合法的 UTC 持久化时间：'{text}'。");
        }

        return value;
    }

    /// <summary>仅校验格式，不产出值；用于参数校验与测试断言。</summary>
    public static bool IsWellFormed(string? text) => TryParse(text, out _);
}