using HotspotMeter.Core.TrafficGuard;
using HotspotMeter.Infrastructure.Persistence;

namespace HotspotMeter.Infrastructure.TrafficGuard;

/// <summary>Guard 配置的持久化形态。与 GuardSettings 表一一对应。</summary>
public sealed record GuardSettingsSnapshot(
    bool Enabled,
    int SamplingIntervalSeconds,
    int DataRetentionDays,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Traffic Guard 的 SQLite 写入层。
///
/// 一个事务内先 upsert 程序规则、再插样本：
/// - 规则表保存身份来源与信任状态，样本只引用 AppKey；
/// - 样本用 UNIQUE(AppKey,ProfileKey,FromUtc,ToUtc) + DO NOTHING 实现幂等；
/// - 只接受正增量，0 字节样本不会入库（由 CHECK 再兜底一次）。
///
/// 写入通过 <see cref="SqliteWriter"/> 单消费者串行执行，不阻塞采样循环。
/// </summary>
public sealed class GuardStore : IGuardSampleSink, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriter _writer;
    private readonly bool _ownsWriter;
    private bool _disposed;

    public GuardStore(SqliteConnectionFactory factory, SqliteWriter? writer = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _ownsWriter = writer is null;
        _writer = writer ?? new SqliteWriter(factory);
    }

    public async Task<GuardSettingsSnapshot> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await _writer.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT Enabled, SamplingIntervalSeconds, DataRetentionDays, UpdatedAtUtc
                FROM GuardSettings WHERE Id = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                throw new InvalidOperationException("GuardSettings 缺少 Id=1 的种子行。");
            }

            return new GuardSettingsSnapshot(
                reader.GetInt64(0) != 0,
                checked((int)reader.GetInt64(1)),
                checked((int)reader.GetInt64(2)),
                UtcTimestamp.Parse(reader.GetString(3)));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(GuardSettingsSnapshot settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SamplingIntervalSeconds is < 10 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "采样周期必须在 10–300 秒之间。");
        }

        if (settings.DataRetentionDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "保留天数必须在 1–3650 之间。");
        }

        await _writer.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE GuardSettings
                SET Enabled = $enabled,
                    SamplingIntervalSeconds = $interval,
                    DataRetentionDays = $retention,
                    UpdatedAtUtc = $updated
                WHERE Id = 1;
                """;
            command.Parameters.AddWithValue("$enabled", settings.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$interval", settings.SamplingIntervalSeconds);
            command.Parameters.AddWithValue("$retention", settings.DataRetentionDays);
            command.Parameters.AddWithValue("$updated", UtcTimestamp.FromUtc(settings.UpdatedAtUtc));
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteAsync(
        IReadOnlyList<GuardTrafficSample> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return;
        }

        var capturedAt = samples.Max(sample => sample.ToUtc);
        await _writer.WriteAsync(async (connection, transaction, token) =>
        {
            foreach (var sample in samples)
            {
                if (sample.DownloadBytes == 0 && sample.UploadBytes == 0)
                {
                    continue;
                }

                if (sample.ProfileKey.Length != 32 ||
                    sample.ProfileKey.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                {
                    throw new ArgumentException("ProfileKey 必须是 32 位小写十六进制。", nameof(samples));
                }

                await UpsertRuleAsync(connection, transaction, sample, token);
                await InsertSampleAsync(connection, transaction, sample, capturedAt, token);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertRuleAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        GuardTrafficSample sample,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO GuardAppRules
                (AppKey, DisplayName, IdentityKind, IdentityCertain, TrustState, FirstSeenAtUtc, LastSeenAtUtc)
            VALUES
                ($key, $name, $kind, $certain, 0, $first, $last)
            ON CONFLICT (AppKey) DO UPDATE SET
                DisplayName = excluded.DisplayName,
                IdentityKind = excluded.IdentityKind,
                IdentityCertain = excluded.IdentityCertain,
                LastSeenAtUtc = excluded.LastSeenAtUtc;
            """;
        command.Parameters.AddWithValue("$key", sample.AppKey);
        command.Parameters.AddWithValue("$name", sample.DisplayName);
        command.Parameters.AddWithValue("$kind", (int)sample.IdentityKind);
        command.Parameters.AddWithValue("$certain", sample.IdentityCertain ? 1 : 0);
        command.Parameters.AddWithValue("$first", UtcTimestamp.FromUtc(sample.ToUtc));
        command.Parameters.AddWithValue("$last", UtcTimestamp.FromUtc(sample.ToUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSampleAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        GuardTrafficSample sample,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO GuardTrafficSamples
                (AppKey, ProfileKey, FromUtc, ToUtc, CapturedAtUtc,
                 DownloadBytes, UploadBytes, ActivityState, ProcessCount, QualityFlags)
            VALUES
                ($key, $profile, $from, $to, $captured,
                 $download, $upload, $state, $processes, $flags)
            ON CONFLICT (AppKey, ProfileKey, FromUtc, ToUtc) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$key", sample.AppKey);
        command.Parameters.AddWithValue("$profile", sample.ProfileKey);
        command.Parameters.AddWithValue("$from", UtcTimestamp.FromUtc(sample.FromUtc));
        command.Parameters.AddWithValue("$to", UtcTimestamp.FromUtc(sample.ToUtc));
        command.Parameters.AddWithValue("$captured", UtcTimestamp.FromUtc(capturedAt));
        command.Parameters.AddWithValue("$download", sample.DownloadBytes);
        command.Parameters.AddWithValue("$upload", sample.UploadBytes);
        command.Parameters.AddWithValue("$state", (int)sample.ActivityState);
        command.Parameters.AddWithValue("$processes", sample.ProcessCount);
        command.Parameters.AddWithValue("$flags", (int)sample.QualityFlags);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsWriter)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
    }
}