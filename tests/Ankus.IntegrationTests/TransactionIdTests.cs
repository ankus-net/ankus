using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies transaction ID datum identity and wrap-aware expansion in a live PostgreSQL backend.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed class TransactionIdTests(TestContext context)
{
    /// <summary>
    /// Preserves xid values and NULL through every managed ownership path.
    /// </summary>
    /// <param name="mode">The direct, SPI, plan, session, cursor, retained-plan, or edited-row path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public Task TransactionIdsRemainDistinctFromOidsAcrossOwners(int mode)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TransactionIdsRemainDistinctFromOidsAcrossOwners),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT datatype.exchange_transaction_id('3'::xid, $1)::text,
                           datatype.exchange_transaction_id('4294967295'::xid, $1)::text,
                           datatype.exchange_transaction_id(NULL::xid, $1) IS NULL,
                           pg_typeof(datatype.exchange_transaction_id('3'::xid, $1))::text
                    """, connection, transaction);
                command.Parameters.AddWithValue(mode);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("3", reader.GetString(0));
                Assert.AreEqual("4294967295", reader.GetString(1));
                Assert.IsTrue(reader.GetBoolean(2));
                Assert.AreEqual("xid", reader.GetString(3));
            }, context.CancellationToken);

    /// <summary>
    /// Maps PostgreSQL's invalid xid to SQL NULL on generated and SPI output boundaries.
    /// </summary>
    [TestMethod]
    public Task InvalidTransactionIdIsSqlNull()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(InvalidTransactionIdIsSqlNull),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT datatype.invalid_transaction_id() IS NULL,
                           datatype.exchange_transaction_id('0'::xid, 0) IS NULL,
                           datatype.exchange_transaction_id('0'::xid, 1) IS NULL
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.IsTrue(reader.GetBoolean(0));
                Assert.IsTrue(reader.GetBoolean(1));
                Assert.IsTrue(reader.GetBoolean(2));
            }, context.CancellationToken);

    /// <summary>
    /// Expands the current xid to the same unsigned 64-bit value reported by PostgreSQL.
    /// </summary>
    [TestMethod]
    public Task FullTransactionIdUsesTheCurrentPostgresEpoch()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(FullTransactionIdUsesTheCurrentPostgresEpoch),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    WITH current_id AS (SELECT pg_current_xact_id()::text AS value)
                    SELECT value, datatype.transaction_id_to_full(value::xid),
                           datatype.transaction_id_to_full('1'::xid)
                    FROM current_id
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(reader.GetString(0), reader.GetString(1));
                Assert.AreEqual("1", reader.GetString(2));
            }, context.CancellationToken);
}
