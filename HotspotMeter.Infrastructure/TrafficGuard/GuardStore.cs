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
public sealed class GuardStore : IGuardSampleSink, IGuardAlertRepository, IAsyncDisposable
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

    public async Task<GuardAlertSettings> LoadAlertSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await _writer.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT AlertsEnabled, DefaultThresholdBytes, PeriodKind, MetricKind,
                       BackgroundOnly, CooldownSeconds, SecondaryEnabled, SecondaryGrowthBytes
                FROM GuardSettings WHERE Id = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                throw new InvalidOperationException("GuardSettings 缺少 Id=1 的种子行。");
            }

            return new GuardAlertSettings(
                reader.GetInt64(0) != 0,
                reader.GetInt64(1),
                (GuardPeriod)reader.GetInt64(2),
                (GuardMetric)reader.GetInt64(3),
                reader.GetInt64(4) != 0,
                TimeSpan.FromSeconds(reader.GetInt64(5)),
                reader.GetInt64(6) != 0,
                reader.GetInt64(7));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAlertSettingsAsync(
        GuardAlertSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        await _writer.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE GuardSettings
                SET AlertsEnabled = $alerts,
                    DefaultThresholdBytes = $threshold,
                    PeriodKind = $period,
                    MetricKind = $metric,
                    BackgroundOnly = $background,
                    CooldownSeconds = $cooldown,
                    SecondaryEnabled = $secondary,
                    SecondaryGrowthBytes = $growth,
                    UpdatedAtUtc = $updated
                WHERE Id = 1;
                """;
            command.Parameters.AddWithValue("$alerts", settings.AlertsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$threshold", settings.DefaultThresholdBytes);
            command.Parameters.AddWithValue("$period", (int)settings.Period);
            command.Parameters.AddWithValue("$metric", (int)settings.Metric);
            command.Parameters.AddWithValue("$background", settings.BackgroundOnly ? 1 : 0);
            command.Parameters.AddWithValue("$cooldown", checked((long)settings.Cooldown.TotalSeconds));
            command.Parameters.AddWithValue("$secondary", settings.SecondaryEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$growth", settings.SecondaryGrowthBytes);
            command.Parameters.AddWithValue("$updated", UtcTimestamp.FromUtc(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GuardAppRule>> LoadAppRulesAsync(CancellationToken cancellationToken = default)
    {
        return await _writer.WriteAsync(async (connection, transaction, token) =>
        {
            var rules = new List<GuardAppRule>();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT AppKey, RuleMode, ThresholdOverrideBytes, IgnoreUntilUtc
                FROM GuardAppRules
                ORDER BY DisplayName COLLATE NOCASE, AppKey;
                """;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rules.Add(new GuardAppRule(
                    reader.GetString(0),
                    (GuardRuleMode)reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : UtcTimestamp.Parse(reader.GetString(3))));
            }

            return rules;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAppRuleAsync(GuardAppRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.AppKey.Length is < 8 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(rule), "AppKey 长度必须在 8–512 之间。");
        }

        if (rule.ThresholdOverrideBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rule), "单程序阈值必须为正数或 null。");
        }

        await _writer.WriteAsync(async (connection, transaction, token) =>
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE GuardAppRules
                SET RuleMode = $mode,
                    ThresholdOverrideBytes = $threshold,
                    IgnoreUntilUtc = $ignore
                WHERE AppKey = $key;
                """;
            update.Parameters.AddWithValue("$mode", (int)rule.Mode);
            update.Parameters.AddWithValue("$threshold", (object?)rule.ThresholdOverrideBytes ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "$ignore",
                rule.IgnoreUntilUtc is null ? DBNull.Value : UtcTimestamp.FromUtc(rule.IgnoreUntilUtc.Value));
            update.Parameters.AddWithValue("$key", rule.AppKey);
            var affected = await update.ExecuteNonQueryAsync(token);
            if (affected > 0)
            {
                return;
            }

            // 规则可以先于样本创建（例如用户手动信任某程序）。此时用 AppKey 作为占位名称，
            // 下一次采样 upsert 会补齐真实 DisplayName/IdentityKind。
            var now = UtcTimestamp.FromUtc(DateTimeOffset.UtcNow);
            var placeholderName = rule.AppKey.Length <= 256 ? rule.AppKey : rule.AppKey[..256];
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO GuardAppRules
                    (AppKey, DisplayName, IdentityKind, IdentityCertain, TrustState,
                     FirstSeenAtUtc, LastSeenAtUtc, RuleMode, ThresholdOverrideBytes, IgnoreUntilUtc)
                VALUES
                    ($key, $name, 3, 0, 0, $now, $now, $mode, $threshold, $ignore);
                """;
            insert.Parameters.AddWithValue("$key", rule.AppKey);
            insert.Parameters.AddWithValue("$name", placeholderName);
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$mode", (int)rule.Mode);
            insert.Parameters.AddWithValue("$threshold", (object?)rule.ThresholdOverrideBytes ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "$ignore",
                rule.IgnoreUntilUtc is null ? DBNull.Value : UtcTimestamp.FromUtc(rule.IgnoreUntilUtc.Value));
            await insert.ExecuteNonQueryAsync(token);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GuardAppAggregate>> ReadAggregatesAsync(
        string profileKey,
        DateTimeOffset fromUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        return await _writer.WriteAsync(async (connection, transaction, token) =>
        {
            var items = new List<GuardAppAggregate>();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT s.AppKey,
                       r.DisplayName,
                       SUM(s.DownloadBytes),
                       SUM(s.UploadBytes),
                       (SELECT s2.ActivityState
                        FROM GuardTrafficSamples s2
                        WHERE s2.AppKey = s.AppKey
                          AND lower(s2.ProfileKey) = lower($profile)
                          AND s2.ToUtc >= $from
                        ORDER BY s2.ToUtc DESC, s2.SampleId DESC
                        LIMIT 1),
                       MAX(s.ToUtc),
                       COUNT(*)
                FROM GuardTrafficSamples s
                JOIN GuardAppRules r ON r.AppKey = s.AppKey
                WHERE lower(s.ProfileKey) = lower($profile)
                  AND s.ToUtc >= $from
                GROUP BY s.AppKey, r.DisplayName
                ORDER BY SUM(s.DownloadBytes) + SUM(s.UploadBytes) DESC, s.AppKey;
                """;
            command.Parameters.AddWithValue("$profile", profileKey);
            command.Parameters.AddWithValue("$from", UtcTimestamp.FromUtc(fromUtc));
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                items.Add(new GuardAppAggregate(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? GuardActivityState.Idle : (GuardActivityState)reader.GetInt64(4),
                    UtcTimestamp.Parse(reader.GetString(5)),
                    checked((int)reader.GetInt64(6))));
            }

            return items;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, GuardAlertRecord>> LoadLatestAlertsAsync(
        GuardPeriod period,
        DateTimeOffset fromUtc,
        CancellationToken cancellationToken = default)
    {
        return await _writer.WriteAsync(async (connection, transaction, token) =>
        {
            var latest = new Dictionary<string, GuardAlertRecord>(StringComparer.Ordinal);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT AlertId, AppKey, ProfileKey, PeriodKind, PeriodStartUtc, AlertType,
                       ThresholdBytes, ActualBytes, DownloadBytes, UploadBytes,
                       CreatedAtUtc, NotificationStatus
                FROM GuardAlertHistory
                WHERE PeriodKind = $period
                  AND CreatedAtUtc >= $from
                  AND AppKey IS NOT NULL
                ORDER BY CreatedAtUtc, AlertId;
                """;
            command.Parameters.AddWithValue("$period", (int)period);
            command.Parameters.AddWithValue("$from", UtcTimestamp.FromUtc(fromUtc));
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var appKey = reader.GetString(1);
                latest[appKey] = new GuardAlertRecord(
                    reader.GetInt64(0),
                    appKey,
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    (GuardPeriod)reader.GetInt64(3),
                    reader.IsDBNull(4) ? fromUtc : UtcTimestamp.Parse(reader.GetString(4)),
                    (GuardAlertKind)reader.GetInt64(5),
                    reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                    reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                    UtcTimestamp.Parse(reader.GetString(10)),
                    (GuardNotificationStatus)reader.GetInt64(11));
            }

            return (IReadOnlyDictionary<string, GuardAlertRecord>)latest;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetActiveSessionStartAsync(
        string profileKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        return await _writer.WriteAsync<DateTimeOffset?>(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT s.StartedAtUtc
                FROM ConnectionSessions s
                JOIN HotspotProfiles p ON p.ProfileId = s.ProfileId
                WHERE lower(p.ProfileKey) = lower($profile)
                  AND s.SessionStatus = 0
                ORDER BY s.StartedAtUtc DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$profile", profileKey);
            var value = await command.ExecuteScalarAsync(token);
            return value is string text ? UtcTimestamp.Parse(text) : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> InsertAlertAsync(
        GuardAlertRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return await _writer.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO GuardAlertHistory
                    (AppKey, ProfileKey, AlertType, ThresholdBytes, ActualBytes, CreatedAtUtc,
                     IsDismissed, Details, PeriodKind, PeriodStartUtc, DownloadBytes, UploadBytes,
                     NotificationStatus, NotificationError, HandledAtUtc)
                VALUES
                    ($key, $profile, $type, $threshold, $actual, $created,
                     0, NULL, $period, $periodStart, $download, $upload,
                     0, NULL, NULL);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$key", record.AppKey);
            command.Parameters.AddWithValue(
                "$profile",
                string.IsNullOrWhiteSpace(record.ProfileKey) ? DBNull.Value : record.ProfileKey);
            command.Parameters.AddWithValue("$type", (int)record.Kind);
            command.Parameters.AddWithValue("$threshold", record.ThresholdBytes);
            command.Parameters.AddWithValue("$actual", record.ActualBytes);
            command.Parameters.AddWithValue("$created", UtcTimestamp.FromUtc(record.CreatedAtUtc));
            command.Parameters.AddWithValue("$period", (int)record.Period);
            command.Parameters.AddWithValue("$periodStart", UtcTimestamp.FromUtc(record.PeriodStartUtc));
            command.Parameters.AddWithValue("$download", record.DownloadBytes);
            command.Parameters.AddWithValue("$upload", record.UploadBytes);
            return Convert.ToInt64(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateNotificationAsync(
        long alertId,
        GuardNotificationStatus status,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await _writer.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE GuardAlertHistory
                SET NotificationStatus = $status,
                    NotificationError = $error
                WHERE AlertId = $id;
                """;
            command.Parameters.AddWithValue("$status", (int)status);
            command.Parameters.AddWithValue(
                "$error",
                string.IsNullOrEmpty(error)
                    ? DBNull.Value
                    : error.Length <= 512 ? error : error[..512]);
            command.Parameters.AddWithValue("$id", alertId);
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken).ConfigureAwait(false);
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