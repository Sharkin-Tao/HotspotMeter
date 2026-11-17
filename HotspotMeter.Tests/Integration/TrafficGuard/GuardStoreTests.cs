using HotspotMeter.Core.TrafficGuard;
using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Infrastructure.TrafficGuard;
using HotspotMeter.Tests.TestSupport;

namespace HotspotMeter.Tests.Integration.TrafficGuard;

public sealed class GuardStoreTests : IAsyncLifetime
{
    private const string ProfileKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly TempDirectory _directory = new("guard-store");
    private SqliteConnectionFactory? _factory;
    private GuardStore? _store;

    public Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(_directory.File("hotspotmeter.db"));
        using (var connection = _factory.Create())
        {
            new SchemaMigrator().Migrate(connection);
        }

        _store = new GuardStore(_factory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        _directory.Dispose();
    }

    [Fact]
    public async Task Write_InsertsRuleAndSample()
    {
        await _store!.WriteAsync(new[] { Sample(600, 50) });

        await using var connection = _factory!.Create();
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM GuardAppRules;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM GuardTrafficSamples;"));
        Assert.Equal(600L, Scalar(connection, "SELECT DownloadBytes FROM GuardTrafficSamples;"));
        Assert.Equal(50L, Scalar(connection, "SELECT UploadBytes FROM GuardTrafficSamples;"));
    }

    [Fact]
    public async Task DuplicateSample_IsIgnored_NotDoubleCounted()
    {
        var sample = Sample(600, 50);
        await _store!.WriteAsync(new[] { sample });
        await _store.WriteAsync(new[] { sample });

        await using var connection = _factory!.Create();
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM GuardTrafficSamples;"));
        Assert.Equal(600L, Scalar(connection, "SELECT SUM(DownloadBytes) FROM GuardTrafficSamples;"));
    }

    [Fact]
    public async Task Rule_IsUpdatedWithLatestDisplayNameAndLastSeen()
    {
        await _store!.WriteAsync(new[] { Sample(10, 1) with { DisplayName = "old.exe", ToUtc = Base.AddSeconds(10) } });
        await _store.WriteAsync(new[]
        {
            Sample(20, 2) with
            {
                DisplayName = "new.exe",
                FromUtc = Base.AddSeconds(10),
                ToUtc = Base.AddSeconds(20),
            },
        });

        await using var connection = _factory!.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DisplayName FROM GuardAppRules WHERE AppKey = $key;";
        command.Parameters.AddWithValue("$key", AppKey);
        Assert.Equal("new.exe", (string?)command.ExecuteScalar());
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM GuardTrafficSamples;"));
    }

    [Fact]
    public async Task Settings_RoundTrip()
    {
        var loaded = await _store!.LoadSettingsAsync();
        Assert.True(loaded.Enabled);
        Assert.Equal(30, loaded.SamplingIntervalSeconds);

        await _store.SaveSettingsAsync(new GuardSettingsSnapshot(false, 60, 7, Base));
        var saved = await _store.LoadSettingsAsync();

        Assert.False(saved.Enabled);
        Assert.Equal(60, saved.SamplingIntervalSeconds);
        Assert.Equal(7, saved.DataRetentionDays);
    }

    [Fact]
    public async Task Write_RejectsInvalidProfileKey_AndWritesNothing()
    {
        var bad = Sample(10, 1) with { ProfileKey = "NOT-HEX" };

        await Assert.ThrowsAsync<ArgumentException>(() => _store!.WriteAsync(new[] { bad }));

        await using var connection = _factory!.Create();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM GuardTrafficSamples;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM GuardAppRules;"));
    }

    [Fact]
    public async Task AlertSettings_RoundTrip()
    {
        var defaults = await _store!.LoadAlertSettingsAsync();
        Assert.True(defaults.AlertsEnabled);
        Assert.Equal(GuardPeriod.Today, defaults.Period);
        Assert.Equal(GuardMetric.Total, defaults.Metric);

        var custom = new GuardAlertSettings(
            false,
            200 * 1024 * 1024,
            GuardPeriod.Rolling24Hours,
            GuardMetric.Upload,
            false,
            TimeSpan.FromMinutes(15),
            false,
            10 * 1024 * 1024);
        await _store.SaveAlertSettingsAsync(custom);

        var loaded = await _store.LoadAlertSettingsAsync();
        Assert.Equal(custom, loaded);
    }

    [Fact]
    public async Task AppRule_RoundTrip()
    {
        var rule = new GuardAppRule(AppKey, GuardRuleMode.Ignored, 42, Base.AddHours(1));
        await _store!.SaveAppRuleAsync(rule);

        var loaded = Assert.Single(await _store.LoadAppRulesAsync());
        Assert.Equal(rule, loaded);
    }

    [Fact]
    public async Task Aggregates_SumOnlyRequestedProfileAndPeriod()
    {
        await _store!.WriteAsync(new[]
        {
            Sample(100, 10) with { FromUtc = Base, ToUtc = Base.AddSeconds(30) },
            Sample(200, 20) with { FromUtc = Base.AddSeconds(30), ToUtc = Base.AddSeconds(60) },
            Sample(300, 30) with
            {
                ProfileKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                FromUtc = Base.AddSeconds(60),
                ToUtc = Base.AddSeconds(90),
            },
        });

        var aggregates = await _store.ReadAggregatesAsync(ProfileKey, Base.AddSeconds(31));

        var aggregate = Assert.Single(aggregates);
        Assert.Equal(200, aggregate.DownloadBytes);
        Assert.Equal(20, aggregate.UploadBytes);
        Assert.Equal(1, aggregate.SampleCount);
    }

    [Fact]
    public async Task AlertHistory_IsInsertedAndNotificationStatusUpdated()
    {
        await _store!.WriteAsync(new[] { Sample(100, 10) });
        var record = new GuardAlertRecord(
            0,
            AppKey,
            ProfileKey,
            GuardPeriod.Today,
            Base,
            GuardAlertKind.Threshold,
            100,
            110,
            100,
            10,
            Base.AddMinutes(1),
            GuardNotificationStatus.Pending);

        var id = await _store.InsertAlertAsync(record);
        Assert.True(id > 0);

        await _store.UpdateNotificationAsync(id, GuardNotificationStatus.Failed, "denied");
        await using var connection = _factory!.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NotificationStatus, NotificationError FROM GuardAlertHistory WHERE AlertId = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.Equal("denied", reader.GetString(1));
    }

    [Fact]
    public async Task ActiveSessionStart_IsReadForProfile()
    {
        await using (var connection = _factory!.Create())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO HotspotProfiles
                    (ProfileKey, DisplayName, InterfaceGuid, Ssid, CreatedAtUtc, LastSeenUtc)
                VALUES
                    ($key, 'TestHotspot', '11111111-1111-1111-1111-111111111111', 'TestHotspot',
                     $now, $now);
                INSERT INTO ConnectionSessions
                    (ProfileId, SessionStatus, InterfaceGuid, Ssid, StartedAtUtc, CreatedAtUtc)
                VALUES
                    (last_insert_rowid(), 0, '11111111-1111-1111-1111-111111111111', 'TestHotspot',
                     $started, $now);
                """;
            command.Parameters.AddWithValue("$key", ProfileKey);
            command.Parameters.AddWithValue("$now", Base.ToString("O"));
            command.Parameters.AddWithValue("$started", Base.AddMinutes(-5).ToString("O"));
            command.ExecuteNonQuery();
        }

        Assert.Equal(Base.AddMinutes(-5), await _store!.GetActiveSessionStartAsync(ProfileKey));
    }

    private static readonly DateTimeOffset Base = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private static readonly string AppKey = @"exe:c:\apps\demo.exe";

    private static GuardTrafficSample Sample(long download, long upload) => new(
        AppKey,
        "demo.exe",
        AppIdentityKind.DesktopExecutable,
        true,
        ProfileKey,
        Base,
        Base.AddSeconds(30),
        download,
        upload,
        GuardActivityState.Background,
        1,
        GuardQualityFlags.None);

    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}