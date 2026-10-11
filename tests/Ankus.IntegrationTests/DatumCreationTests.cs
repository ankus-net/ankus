using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Creates raw datums from managed values, resolves unmapped enum labels and observes pending interrupts in PostgreSQL.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class DatumCreationTests(TestContext context)
{
    /// <summary>
    /// Managed values become datums of their PostgreSQL types, typed NULL keeps its type, and a domain's check applies.
    /// </summary>
    [TestMethod]
    public async Task ManagedValuesBecomeTypedDatums()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("héllo|1.50|{1,2}|True|25|5|True|héllo|23514|5",
            await Scalar<string>(connection, "SELECT datum_creation.create_values()"));
    }

    /// <summary>
    /// A label of an enum without a C# mapping resolves to its pg_enum row, and an unknown label fails as PostgreSQL's
    /// enum input does.
    /// </summary>
    [TestMethod]
    public async Task UnmappedEnumLabelsResolveToTheirRows()
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, "CREATE TYPE pg_temp.creation_mood AS ENUM ('sad', 'happy')");
        Assert.IsTrue(await Scalar<bool>(connection, """
            SELECT datum_creation.enum_value_oid('pg_temp.creation_mood', 'happy') =
                (SELECT oid FROM pg_enum WHERE enumtypid = 'pg_temp.creation_mood'::regtype AND enumlabel = 'happy')
            """));
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            Scalar<uint>(connection, "SELECT datum_creation.enum_value_oid('pg_temp.creation_mood', 'angry')"));
        Assert.AreEqual(PostgresErrorCodes.InvalidTextRepresentation, error.SqlState);
    }

    /// <summary>
    /// A statement timeout is visible as a pending interrupt before it is processed, and processing it still cancels the
    /// statement.
    /// </summary>
    [TestMethod]
    public async Task PendingInterruptsAreVisibleBeforeProcessing()
    {
        await using NpgsqlConnection connection = await Open();
        var notices = new List<string>();
        connection.Notice += (_, arguments) => notices.Add(arguments.Notice.MessageText);
        Assert.AreEqual("False|False", await Scalar<string>(connection, "SELECT datum_creation.interrupt_pending(false)"));
        await Execute(connection, "SET statement_timeout = 100");
        PostgresException canceled = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            Scalar<string>(connection, "SELECT datum_creation.interrupt_pending(true)"));
        await Execute(connection, "RESET statement_timeout");
        Assert.AreEqual(PostgresErrorCodes.QueryCanceled, canceled.SqlState);
        Assert.Contains("pending False True", notices);
        Assert.AreEqual(1, await Scalar<int>(connection, "SELECT 1"));
    }

    /// <summary>
    /// Opens a fresh PostgreSQL backend.
    /// </summary>
    private Task<NpgsqlConnection> Open() => PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);

    /// <summary>
    /// Executes a scalar query.
    /// </summary>
    private async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        object? value = await command.ExecuteScalarAsync(context.CancellationToken);
        return (T)value!;
    }

    /// <summary>
    /// Executes statements and consumes every result.
    /// </summary>
    private async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }
}
