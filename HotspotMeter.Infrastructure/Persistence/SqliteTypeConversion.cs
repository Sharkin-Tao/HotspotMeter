using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// Core 的字节计数是 <see cref="ulong"/>，SQLite 只有有符号 64 位 INTEGER。
/// 这个类是所有跨越该边界的唯一转换入口，避免各处自行 <c>(long)</c> 强转
/// 造成“超范围变成负数”的静默错误。
///
/// 规则：
/// 1. 只有 <c>0..long.MaxValue</c> 可以写入；超过即抛 <see cref="OverflowException"/>，
///    绝不截断、绝不写成 REAL。
/// 2. 读取时只接受 SQLite 的 INTEGER（<see cref="long"/>）；负数视为数据损坏，
///    同样抛 <see cref="OverflowException"/>，不静默修正。
/// 3. <c>null</c> 与 <c>DBNull</c> 映射为 <c>null</c>，用于可空列。
/// 4. 失败发生在参数绑定之前，因此不会留下“部分写入”的副作用。
/// </summary>
public static class SqliteTypeConversion
{
    /// <summary>SQLite INTEGER 能表示的最大非负值。</summary>
    public const ulong MaxRepresentableOctets = long.MaxValue;

    /// <summary>把无符号字节数转换为可写入 SQLite INTEGER 的值。</summary>
    /// <exception cref="OverflowException">值超过 <see cref="long.MaxValue"/>。</exception>
    public static long ToInteger(ulong value)
    {
        if (value > MaxRepresentableOctets)
        {
            throw new OverflowException(
                $"字节数 {value} 超过 SQLite INTEGER 上限 {MaxRepresentableOctets}，拒绝写入以避免溢出为负数。");
        }

        return (long)value;
    }

    /// <summary>把 SQLite INTEGER 读回无符号字节数。</summary>
    /// <exception cref="OverflowException">值为负，说明数据已损坏。</exception>
    public static ulong ToOctets(long value)
    {
        if (value < 0)
        {
            throw new OverflowException($"读到的字节数 {value} 为负数，数据库内容已损坏。");
        }

        return (ulong)value;
    }

    /// <summary>可空写入：<c>null</c> → <see cref="DBNull.Value"/>。</summary>
    public static object ToDbValue(ulong? value) =>
        value is null ? DBNull.Value : ToInteger(value.Value);

    /// <summary>可空读取：只接受 INTEGER/NULL，其余类型直接拒绝。</summary>
    public static ulong? FromDbValue(object? value) => value switch
    {
        null or DBNull => null,
        long integer => ToOctets(integer),
        int integer => ToOctets(integer),
        _ => throw new InvalidCastException(
            $"期望 SQLite INTEGER，实际为 {value.GetType().Name}（值 '{value}'）；不会把 REAL/TEXT 当作字节数。"),
    };

    /// <summary>读取并用实际存储类型校验，供集成测试与诊断使用。</summary>
    public static ulong? ReadIntegerColumn(SqliteDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var value = reader.GetValue(ordinal);
        if (value is DBNull)
        {
            return null;
        }

        if (value is long integer)
        {
            return ToOctets(integer);
        }

        throw new InvalidCastException(
            $"列 {ordinal} 的实际存储类型是 {value.GetType().Name}，不是 INTEGER。");
    }

    /// <summary>诊断用：返回 SQLite 对某个值的存储类型（integer/real/text/null）。</summary>
    public static string GetStorageType(SqliteConnection connection, object value)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT typeof($value);";
        command.Parameters.AddWithValue("$value", value);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "null";
    }
}