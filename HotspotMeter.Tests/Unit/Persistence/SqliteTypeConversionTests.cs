using HotspotMeter.Infrastructure.Persistence;
using HotspotMeter.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace HotspotMeter.Tests.Unit.Persistence;

public class SqliteTypeConversionTests
{
    public static TheoryData<ulong> RoundTripValues => new()
    {
        0UL,
        1UL,
        (ulong)long.MaxValue,
    };

    [Theory]
    [MemberData(nameof(RoundTripValues))]
    public void ToInteger_ToOctets_RoundTrips(ulong value)
    {
        var integer = SqliteTypeConversion.ToInteger(value);

        Assert.True(integer >= 0);
        Assert.Equal(value, SqliteTypeConversion.ToOctets(integer));
    }

    [Theory]
    [InlineData((ulong)long.MaxValue + 1)]
    [InlineData(ulong.MaxValue)]
    public void AboveLongMax_IsRejected(ulong value)
    {
        var exception = Assert.Throws<OverflowException>(() => SqliteTypeConversion.ToInteger(value));

        Assert.Contains("拒绝写入", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeInteger_IsRejectedOnRead()
    {
        Assert.Throws<OverflowException>(() => SqliteTypeConversion.ToOctets(-1));
    }

    [Fact]
    public void NullableConversions_MapNullBothWays()
    {
        Assert.Same(DBNull.Value, SqliteTypeConversion.ToDbValue(null));
        Assert.Null(SqliteTypeConversion.FromDbValue(DBNull.Value));
        Assert.Null(SqliteTypeConversion.FromDbValue(null));
        Assert.Equal(0UL, SqliteTypeConversion.FromDbValue(0L));
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData("123")]
    public void FromDbValue_RejectsNonIntegerStorage(object value)
    {
        Assert.Throws<InvalidCastException>(() => SqliteTypeConversion.FromDbValue(value));
    }

    [Fact]
    public void StoredValue_UsesIntegerStorageClass()
    {
        using var context = new PersistenceContext();
        using var connection = context.Factory.Create();
        Execute(connection, "CREATE TABLE t (v INTEGER NOT NULL);");
        Execute(connection, "INSERT INTO t (v) VALUES ($v);", ("$v", SqliteTypeConversion.ToInteger((ulong)long.MaxValue)));

        Assert.Equal("integer", context.Scalar<string>("SELECT typeof(v) FROM t;"));

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT v FROM t;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal((ulong)long.MaxValue, SqliteTypeConversion.ReadIntegerColumn(reader, 0));
    }

    [Fact]
    public void Overflow_FailsBeforeAnyRowIsWritten()
    {
        using var context = new PersistenceContext();
        using var connection = context.Factory.Create();
        Execute(connection, "CREATE TABLE t (v INTEGER NOT NULL);");

        Assert.Throws<OverflowException>(() =>
        {
            var value = SqliteTypeConversion.ToInteger(ulong.MaxValue);
            Execute(connection, "INSERT INTO t (v) VALUES ($v);", ("$v", value));
        });

        Assert.Equal(0, context.Scalar<int>("SELECT COUNT(*) FROM t;"));
    }

    [Fact]
    public void ReadIntegerColumn_RejectsRealStoredInIntegerColumn()
    {
        using var context = new PersistenceContext();
        using var connection = context.Factory.Create();
        Execute(connection, "CREATE TABLE t (v INTEGER NOT NULL);");
        Execute(connection, "INSERT INTO t (v) VALUES (1.5);");

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT v FROM t;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Throws<InvalidCastException>(() => SqliteTypeConversion.ReadIntegerColumn(reader, 0));
    }

    [Fact]
    public void GetStorageType_DistinguishesIntegerAndReal()
    {
        using var context = new PersistenceContext();
        using var connection = context.Factory.Create();

        Assert.Equal("integer", SqliteTypeConversion.GetStorageType(connection, 1L));
        Assert.Equal("real", SqliteTypeConversion.GetStorageType(connection, 1.5));
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}