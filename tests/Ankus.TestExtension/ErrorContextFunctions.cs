namespace Ankus.TestExtension;

/// <summary>
/// Adds context lines to errors and messages through <see cref="PgErrorContext"/>.
/// </summary>
public static class ErrorContextFunctions
{
    /// <summary>
    /// Divides by a value through SPI while a context describes the work, so a zero divisor fails inside PostgreSQL.
    /// </summary>
    /// <param name="divisor">The divisor.</param>
    /// <returns>The quotient.</returns>
    [PgFunction]
    public static int ErrorContextDivide(int divisor)
    {
        using PgErrorContext context = PgErrorContext.Push("computing the ratio for 42");
        return Spi.ExecuteScalar<int>("SELECT 42 / $1", SpiParameter.Create(divisor));
    }

    /// <summary>
    /// Throws a managed exception while two contexts are active.
    /// </summary>
    [PgFunction]
    public static void ErrorContextManaged()
    {
        using PgErrorContext outer = PgErrorContext.Push("importing batch 3");
        using PgErrorContext inner = PgErrorContext.Push("validating row 6");
        inner.Text = "validating row 7";
        throw new InvalidOperationException("bad row");
    }

    /// <summary>
    /// Logs a notice while nested contexts are active, after updating and then removing the inner one.
    /// </summary>
    [PgFunction]
    public static void ErrorContextNotice()
    {
        using PgErrorContext outer = PgErrorContext.Push("outer step");
        using (PgErrorContext inner = PgErrorContext.Push("inner step 1"))
        {
            inner.Text = "inner step 2";
            PgLog.Write(PgLogLevel.Notice, "first");
        }

        PgLog.Write(PgLogLevel.Notice, "second");
    }

    /// <summary>
    /// Inserts a row through SPI while a context describes the work, into a table whose C# row trigger can reject it,
    /// so the trigger's exception becomes the error of the function's backend call.
    /// </summary>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table's name.</param>
    [PgFunction]
    public static void ErrorContextNestedCallback(string schema, string table)
    {
        using PgErrorContext outer = PgErrorContext.Push("applying queued change 9");
        Spi.Execute($"INSERT INTO {Spi.QuoteQualifiedIdentifier(schema, table)} VALUES (1, 1, 'queued')");
    }

    /// <summary>
    /// Pushes a context and returns without removing it.
    /// </summary>
    [PgFunction]
    public static void ErrorContextForgotten() => _ = PgErrorContext.Push("forgotten context");
}
