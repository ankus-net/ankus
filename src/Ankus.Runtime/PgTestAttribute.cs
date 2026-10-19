namespace Ankus;

/// <summary>
/// Declares a synchronous, zero-SQL-argument test that runs inside PostgreSQL.
/// Native entry points are emitted only when AnkusIncludeTests is enabled.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PgTestAttribute : Attribute
{
    /// <summary>
    /// Gets or sets an ordered schema search path scoped to this test; null preserves the caller's path.
    /// Use <see cref="PgSearchPath.ExtensionSchema"/> for the schema selected during CREATE EXTENSION.
    /// </summary>
    public string[]? SearchPath
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the test function's volatility, as pgrx's <c>#[pg_test]</c> passes <c>#[pg_extern]</c> options
    /// through. The default is Volatile.
    /// </summary>
    public PgVolatility Volatility
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether PostgreSQL may run the test function in parallel queries. The default is Unsafe.
    /// </summary>
    public PgParallelSafety ParallelSafety
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether the test function runs with its owner's privileges; otherwise it uses the caller's.
    /// </summary>
    public bool SecurityDefiner
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the positive finite planner cost of the test function. The default is one.
    /// </summary>
    public double Cost
    {
        get;
        set;
    } = 1;

    /// <summary>
    /// Gets or sets the exact PostgreSQL primary error message expected from the test, or null for success.
    /// </summary>
    public string? ExpectedError
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets a reason for the host test framework to report this test as ignored.
    /// </summary>
    public string? IgnoreReason
    {
        get;
        set;
    }
}
