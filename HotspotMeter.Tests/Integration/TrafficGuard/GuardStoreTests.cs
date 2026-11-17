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