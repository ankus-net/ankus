using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies that reading a raw datum argument does the work <c>PG_GETARG_DATUM</c> does in C, without a subtransaction.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed class ArgumentReadTests(TestContext context)
{
    /// <summary>
    /// Calls that read <c>name</c> and <c>text</c> arguments as raw datums start no subtransaction, which PostgreSQL
    /// reports at DEBUG5 for each one it starts. A savepoint in the same session shows that the reports arrive. A
    /// transaction that calls such a function many times after creating many relations otherwise slows quadratically,
    /// since each subtransaction's end walks state that grows with every relation. A first call warms up the session,
    /// since loading the library does work of its own once.
    /// </summary>
    [TestMethod]
    public Task RawDatumArgumentsStartNoSubtransaction()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(RawDatumArgumentsStartNoSubtransaction),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT argument_reads.takes_name('warm'), argument_reads.takes_text('warm');
                    SET LOCAL client_min_messages = debug5
                    """, connection, transaction);
                await command.ExecuteNonQueryAsync(token);
                var notices = new List<PostgresNotice>();
                connection.Notice += (_, args) => notices.Add(args.Notice);
                command.CommandText = """
                    SELECT bool_and(argument_reads.takes_name('pglogical')), min(argument_reads.takes_text(repeat('x', 3000)))
                    FROM generate_series(1, 100)
                    """;
                await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
                {
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.IsTrue(reader.GetBoolean(0));
                    Assert.AreEqual(3004, reader.GetInt32(1));
                }

                Assert.AreEqual(0, CountSubtransactionStarts(notices));
                command.CommandText = "SAVEPOINT counted; RELEASE SAVEPOINT counted";
                await command.ExecuteNonQueryAsync(token);
                Assert.AreNotEqual(0, CountSubtransactionStarts(notices));
            }, context.CancellationToken);

    /// <summary>
    /// A compressed table value reaches the function as an exact detoasted copy, still without a subtransaction, and the
    /// session continues normally after the call.
    /// </summary>
    [TestMethod]
    public Task ToastedRawDatumArgumentsAreCopiedWithoutASubtransaction()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ToastedRawDatumArgumentsAreCopiedWithoutASubtransaction),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    CREATE TEMP TABLE argument_read_values (value text);
                    INSERT INTO argument_read_values VALUES (repeat('pglogical', 20000));
                    SELECT argument_reads.takes_text('warm');
                    SET LOCAL client_min_messages = debug5
                    """, connection, transaction);
                await command.ExecuteNonQueryAsync(token);
                var notices = new List<PostgresNotice>();
                connection.Notice += (_, args) => notices.Add(args.Notice);
                command.CommandText = """
                    SELECT argument_reads.takes_text(value), octet_length(value) + 4, pg_column_size(value) < octet_length(value)
                    FROM argument_read_values
                    """;
                await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
                {
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.AreEqual(180_004, reader.GetInt32(0));
                    Assert.AreEqual(reader.GetInt32(1), reader.GetInt32(0));
                    Assert.IsTrue(reader.GetBoolean(2));
                }

                Assert.AreEqual(0, CountSubtransactionStarts(notices));
                command.CommandText = "SET LOCAL client_min_messages = notice; SELECT argument_reads.takes_name('after')";
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            }, context.CancellationToken);

    private static int CountSubtransactionStarts(List<PostgresNotice> notices)
        => notices.Count(static notice => notice.MessageText.StartsWith("StartSubTransaction", StringComparison.Ordinal));
}
