using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Compares newly inventoried native calls with independent SQL operations in PostgreSQL.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class NativeInventoryTests(TestContext context)
{
    /// <summary>
    /// Newly inventoried global storage reads the same value PostgreSQL exposes through its setting system.
    /// </summary>
    [TestMethod]
    public async Task NativePreparedTransactionGlobalMatchesPostgresSetting()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT native_inventory.max_prepared_transactions(),
                current_setting('max_prepared_transactions')::integer
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(context.CancellationToken));
        Assert.AreEqual(reader.GetInt32(1), reader.GetInt32(0));
        Assert.IsFalse(await reader.ReadAsync(context.CancellationToken));
    }

    /// <summary>
    /// A C long parameter takes a <c>long</c> on every platform, and <c>FETCH_ALL</c> has the platform's C long width:
    /// 32 bits on Windows and 64 elsewhere.
    /// </summary>
    [TestMethod]
    public async Task CLongArgumentsCompileAndKeepThePlatformWidth()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT native_inventory.c_long_arguments()", connection);
        long fetchAll = Assert.IsInstanceOfType<long>(await command.ExecuteScalarAsync(context.CancellationToken));
        Assert.AreEqual(OperatingSystem.IsWindows() ? int.MaxValue : long.MaxValue, fetchAll);
    }

    /// <summary>
    /// Native unsigned helper calls preserve signed SQL inputs and full-width hash results.
    /// </summary>
    /// <param name="value">The integer bit pattern.</param>
    /// <param name="seed">The extended hash's seed.</param>
    [TestMethod]
    [DataRow(0, 0L)]
    [DataRow(1, 1L)]
    [DataRow(-1, -1L)]
    [DataRow(int.MinValue, long.MinValue)]
    [DataRow(int.MaxValue, long.MaxValue)]
    [DataRow(-123456789, 0x123456789ABCDEFL)]
    public async Task NativeHashHelpersMatchPostgresSql(int value, long seed)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT native_inventory.hash($1), pg_catalog.hashint4($1),
                native_inventory.extended_hash($1, $2), pg_catalog.hashint4extended($1, $2)
            """, connection);
        command.Parameters.AddWithValue(value);
        command.Parameters.AddWithValue(seed);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(context.CancellationToken));
        Assert.AreEqual(reader.GetInt32(1), reader.GetInt32(0));
        Assert.AreEqual(reader.GetInt64(3), reader.GetInt64(2));
        Assert.IsFalse(await reader.ReadAsync(context.CancellationToken));
    }

    /// <summary>
    /// Inline overflow checks distinguish boundary sums from overflow without depending on unspecified overflow output bits.
    /// </summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <param name="expected">The exact sum or SQL NULL for overflow.</param>
    [TestMethod]
    [DataRow(0, 0, 0)]
    [DataRow(19, 23, 42)]
    [DataRow(-19, -23, -42)]
    [DataRow(int.MaxValue - 1, 1, int.MaxValue)]
    [DataRow(int.MaxValue, 1, null)]
    [DataRow(int.MinValue + 1, -1, int.MinValue)]
    [DataRow(int.MinValue, -1, null)]
    [DataRow(int.MinValue, int.MaxValue, -1)]
    public async Task NativeInlineAdditionPreservesBoundaries(int left, int right, int? expected)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT native_inventory.add($1, $2)", connection);
        command.Parameters.AddWithValue(left);
        command.Parameters.AddWithValue(right);
        object? actual = await command.ExecuteScalarAsync(context.CancellationToken);
        if (expected is int sum)
        {
            Assert.AreEqual(sum, Assert.IsInstanceOfType<int>(actual));
        }
        else
        {
            Assert.AreSame(DBNull.Value, actual);
        }

        command.Parameters[0].Value = 19;
        command.Parameters[1].Value = 23;
        Assert.AreEqual(42, Assert.IsInstanceOfType<int>(await command.ExecuteScalarAsync(context.CancellationToken)));
    }
}
