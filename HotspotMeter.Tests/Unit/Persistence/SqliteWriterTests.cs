using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Unit.Persistence;

public class SqliteWriterTests
{
    [Fact]
    public async Task WriteAsync_ExecutesWorkInTransactionAndCommits()
    {
        await using var context = await WriterTestContext.CreateAsync();

        var inserted = await context.Writer.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO probe (value) VALUES ($value); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$value", "hello");
            return Convert.ToInt64(await command.ExecuteScalarAsync(token));
        });

        Assert.Equal(1, inserted);
        Assert.Equal(1, ReadCount(context.Factory));
    }

    [Fact]
    public async Task WriteAsync_RollsBackAndPropagates_WhenWorkThrows()
    {
        await using var context = await WriterTestContext.CreateAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Writer.WriteAsync(async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO probe (value) VALUES ('will-rollback');";
                await command.ExecuteNonQueryAsync(token);
                throw new InvalidOperationException("模拟业务异常");
            }));

        Assert.Contains("模拟业务异常", exception.Message);
        Assert.Equal(0, ReadCount(context.Factory));

        // 事务失败后写入器必须继续可用。
        await context.Writer.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO probe (value) VALUES ('after-failure');";
            await command.ExecuteNonQueryAsync(token);
        });
        Assert.Equal(1, ReadCount(context.Factory));
    }

    [Fact]
    public async Task FlushAsync_WaitsForQueuedWrites()
    {
        await using var context = await WriterTestContext.CreateAsync();

        for (var i = 0; i < 5; i++)
        {
            var value = i;
            _ = context.Writer.WriteAsync(async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO probe (value) VALUES ($value);";
                command.Parameters.AddWithValue("$value", value.ToString());
                await command.ExecuteNonQueryAsync(token);
            });
        }

        await context.Writer.FlushAsync();

        Assert.Equal(5, ReadCount(context.Factory));
    }

    [Fact]
    public async Task StopAsync_DrainsPendingWorkBeforeDisposing()
    {
        await using var context = await WriterTestContext.CreateAsync();

        var pending = context.Writer.WriteAsync(async (connection, transaction, token) =>
        {
            await Task.Delay(100, token);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO probe (value) VALUES ('drained');";
            await command.ExecuteNonQueryAsync(token);
        });

        await context.Writer.StopAsync();
        await pending;

        Assert.Equal(1, ReadCount(context.Factory));
        Assert.False(context.Writer.IsRunning);
    }

    [Fact]
    public async Task StopAsync_IsIdempotent()
    {
        await using var context = await WriterTestContext.CreateAsync();

        await context.Writer.StopAsync();
        await context.Writer.StopAsync();

        Assert.False(context.Writer.IsRunning);
    }

    [Fact]
    public async Task WriteAsync_AfterStop_ThrowsObjectDisposed()
    {
        await using var context = await WriterTestContext.CreateAsync();
        await context.Writer.StopAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            context.Writer.WriteAsync(async (_, _, _) => await Task.CompletedTask));
    }

    [Fact]
    public async Task ParallelWrites_AreSerializedWithoutLockErrors()
    {
        await using var context = await WriterTestContext.CreateAsync();

        var writes = Enumerable.Range(0, 100).Select(i => context.Writer.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO probe (value) VALUES ($value);";
            command.Parameters.AddWithValue("$value", i.ToString());
            await command.ExecuteNonQueryAsync(token);
        }));

        await Task.WhenAll(writes);

        Assert.Equal(100, ReadCount(context.Factory));
    }

    [Fact]
    public async Task WriteAsync_WithCanceledToken_IsCanceledAndWriterStaysUsable()
    {
        await using var context = await WriterTestContext.CreateAsync();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Writer.WriteAsync(
                async (_, _, _) => await Task.CompletedTask,
                cancellation.Token));

        await context.Writer.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO probe (value) VALUES ('still-usable');";
            await command.ExecuteNonQueryAsync(token);
        });

        Assert.Equal(1, ReadCount(context.Factory));
    }

    [Fact]
    public async Task StopAsync_ReleasesDatabaseConnection()
    {
        var context = await WriterTestContext.CreateAsync();
        await context.Writer.StopAsync();

        try
        {
            // 连接与 WAL 都已关闭时，主库文件才能被独占打开。
            using var exclusive = new FileStream(
                context.Factory.DatabasePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            Assert.True(exclusive.Length > 0);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    private static long ReadCount(SqliteConnectionFactory factory)
    {
        using var connection = factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM probe;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private sealed class WriterTestContext : IAsyncDisposable
    {
        private readonly TempDirectory _temp;

        private WriterTestContext(TempDirectory temp, SqliteConnectionFactory factory, SqliteWriter writer)
        {
            _temp = temp;
            Factory = factory;
            Writer = writer;
        }

        public SqliteConnectionFactory Factory { get; }

        public SqliteWriter Writer { get; }

        public static Task<WriterTestContext> CreateAsync()
        {
            var temp = new TempDirectory();
            var factory = new SqliteConnectionFactory(temp.File("hotspotmeter.db"));
            using (var connection = factory.Create())
            {
                new SchemaMigrator().Migrate(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE probe (id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT NOT NULL);";
                command.ExecuteNonQuery();
            }

            return Task.FromResult(new WriterTestContext(temp, factory, new SqliteWriter(factory)));
        }

        public async ValueTask DisposeAsync()
        {
            await Writer.DisposeAsync();
            _temp.Dispose();
        }
    }
}