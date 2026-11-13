using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HotspotMeter.Infrastructure.Persistence;

/// <summary>
/// 操作摘要编码。
///
/// 为什么不用拼接字符串：
/// 1. 字段里可能出现分隔符（时区 ID 虽然不会出现 <c>|</c>，但 OperationId 与未来字段没有这个保证），
///    拼接后 <c>a|bc</c> 与 <c>ab|c</c> 可能撞成同一个输入。二进制编码用"长度前缀 + 原始字节"消除歧义。
/// 2. 数字用定长大端字节，避免文化/格式差异（<c>1,000</c>、科学计数、负零）。
/// 3. 字段顺序固定并带版本前缀；未来加字段必须换版本，不得静默改变旧摘要的含义。
///
/// 两个摘要的分工：
/// - RequestDigest 只描述"请求内容"，用于判断"这次重试是不是同一个请求"。
/// - PayloadDigest 描述"实际写进数据库的全部字段"（含 OperationId/序列/采集时间/分段），
///   用于判断"同一 OperationId 的已有行是否与本请求逐字段一致"。
/// 两者都排除 <c>AppliedAtUtc</c>：它是写入时刻，不是请求内容。
/// </summary>
public static class OperationContentDigest
{
    private const byte EncoderVersion = 1;
    private const string RequestDomain = "HMOPS-REQ";
    private const string PayloadDomain = "HMOPS-PAY";

    /// <summary>计算请求摘要（小写十六进制 SHA-256）。</summary>
    public static string ComputeRequest(SnapshotWriteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();

        var buffer = new ArrayBufferWriter<byte>(256);
        WriteDomain(buffer, RequestDomain);

        WriteString(buffer, command.TimeZoneId);
        WriteByte(buffer, (byte)command.Kind);
        WriteInt64(buffer, command.DbSessionId);
        WriteInt64(buffer, command.ProfileId);
        WriteInt64(buffer, command.FromUtc.UtcTicks);
        WriteInt64(buffer, command.ToUtc.UtcTicks);
        WriteInt64(buffer, command.DownloadBytesAfter);
        WriteInt64(buffer, command.UploadBytesAfter);
        WriteInt64(buffer, command.ConnectedMilliseconds);
        WriteInt32(buffer, (int)command.QualityFlags);

        return Hash(buffer.WrittenSpan);
    }

    /// <summary>计算负载摘要（小写十六进制 SHA-256）。</summary>
    public static string ComputePayload(SnapshotWriteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();

        var buffer = new ArrayBufferWriter<byte>(512);
        WriteDomain(buffer, PayloadDomain);

        WriteString(buffer, command.OperationId);
        WriteInt64(buffer, command.OperationSequence);
        WriteByte(buffer, (byte)command.Kind);
        WriteInt64(buffer, command.DbSessionId);
        WriteInt64(buffer, command.ProfileId);
        WriteInt64(buffer, command.FromUtc.UtcTicks);
        WriteInt64(buffer, command.ToUtc.UtcTicks);
        WriteInt64(buffer, command.CapturedAtUtc.UtcTicks);
        WriteInt64(buffer, command.DownloadBytesAfter);
        WriteInt64(buffer, command.UploadBytesAfter);
        WriteInt64(buffer, command.ConnectedMilliseconds);
        WriteString(buffer, command.TimeZoneId);
        WriteInt32(buffer, (int)command.QualityFlags);

        WriteInt32(buffer, command.Segments.Count);
        foreach (var segment in command.Segments)
        {
            WriteInt32(buffer, segment.Index);
            WriteString(buffer, segment.LocalDate);
            WriteInt64(buffer, segment.FromUtc.UtcTicks);
            WriteInt64(buffer, segment.ToUtc.UtcTicks);
            WriteInt64(buffer, segment.DownloadBytes);
            WriteInt64(buffer, segment.UploadBytes);
            WriteInt64(buffer, segment.ConnectedMilliseconds);
        }

        return Hash(buffer.WrittenSpan);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void WriteDomain(IBufferWriter<byte> writer, string domain)
    {
        WriteByte(writer, EncoderVersion);
        WriteString(writer, domain);
    }

    private static void WriteByte(IBufferWriter<byte> writer, byte value)
    {
        var span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }

    private static void WriteInt32(IBufferWriter<byte> writer, int value)
    {
        var span = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(span, value);
        writer.Advance(sizeof(int));
    }

    private static void WriteInt64(IBufferWriter<byte> writer, long value)
    {
        var span = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64BigEndian(span, value);
        writer.Advance(sizeof(long));
    }

    /// <summary>长度前缀（int32 大端）+ UTF-8 原始字节；不做任何规范化或转义。</summary>
    private static void WriteString(IBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteInt32(writer, byteCount);
        var span = writer.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(value, span);
        writer.Advance(byteCount);
    }
}