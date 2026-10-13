using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Checks that <c>PgErrorContext</c> lines reach PostgreSQL's context field for errors and messages, and that a
/// context a function forgets does not describe later errors.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
[TestClass]
public sealed class ErrorContextTests(TestContext context)
{
    /// <summary>
    /// An error PostgreSQL raises inside a backend call carries the context line along with PostgreSQL's own lines.
    /// </summary>
    [TestMethod]
    public async Task BackendErrorsCarryTheContext()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT datatype.error_context_divide(0)"));
        Assert.AreEqual(PostgresErrorCodes.DivisionByZero, error.SqlState);
        Assert.Contains("computing the ratio for 42", error.Where!);
        Assert.AreEqual(42, await ScalarAsync<int>(connection, "SELECT datatype.error_context_divide(1)"));
    }

    /// <summary>
    /// A managed exception thrown while contexts are active is reported with them, innermost first and with the
    /// current text.
    /// </summary>
    [TestMethod]
    public async Task ManagedExceptionsCarryTheContexts()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT datatype.error_context_managed()"));
        Assert.AreEqual(PostgresErrorCodes.ExternalRoutineException, error.SqlState);
        Assert.StartsWith("validating row 7\nimporting batch 3", error.Where!);
    }

    /// <summary>
    /// Messages carry the contexts active when they are logged: both, then only the outer one after the inner is
    /// removed.
    /// </summary>
    [TestMethod]
    public async Task MessagesCarryTheActiveContexts()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        List<PostgresNotice> notices = [];
        connection.Notice += (_, arguments) => notices.Add(arguments.Notice);
        await ExecuteAsync(connection, "SELECT datatype.error_context_notice()");
        Assert.HasCount(2, notices);
        Assert.AreEqual("first", notices[0].MessageText);
        Assert.StartsWith("inner step 2\nouter step", notices[0].Where!);
        Assert.AreEqual("second", notices[1].MessageText);
        Assert.StartsWith("outer step", notices[1].Where!);
        Assert.DoesNotContain("inner step", notices[1].Where!);
    }

    /// <summary>
    /// A context a function leaves open is removed when the function returns, so later errors in the session do not
    /// show it.
    /// </summary>
    [TestMethod]
    public async Task ForgottenContextsEndWithTheirFunction()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ExecuteAsync(connection, "SELECT datatype.error_context_forgotten()");
        PostgresException later = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT datatype.error_context_divide(0)"));
        Assert.DoesNotContain("forgotten context", later.Where!);
        PostgresException managed = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT datatype.error_context_managed()"));
        Assert.DoesNotContain("forgotten context", managed.Where!);
    }

    private async Task<NpgsqlConnection> OpenAsync() => await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);

    private async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(context.CancellationToken));
    }

    private async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }
}
