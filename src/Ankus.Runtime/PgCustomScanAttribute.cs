namespace Ankus;

/// <summary>
/// Declares a custom scan provider from a class implementing <see cref="IPgCustomScan{TState}"/>.
/// </summary>
/// <remarks>
/// Ankus registers the provider when PostgreSQL loads the extension library and offers it every plain table the
/// planner scans in that backend. EXPLAIN shows the chosen scan as <c>Custom Scan (</c><i>name</i><c>)</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PgCustomScanAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the name PostgreSQL registers and EXPLAIN shows, defaulting to the class name in snake_case.
    /// </summary>
    /// <remarks>
    /// The name is case-sensitive, must be unique in the backend and must fit 63 UTF-8 bytes.
    /// </remarks>
    public string? Name
    {
        get;
        set;
    }
}
