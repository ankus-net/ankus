using System;

namespace Ankus;

/// <summary>
/// Names the output columns of an IEnumerable return, producing RETURNS TABLE.
/// A single name also supports a one-column table from scalar elements.
/// </summary>
/// <param name="names">The SQL column names, in result order.</param>
[AttributeUsage(AttributeTargets.ReturnValue)]
public sealed class PgColumnNamesAttribute(params string[] names) : Attribute
{
    /// <summary>
    /// Gets the SQL column names in result order.
    /// </summary>
    public string[] Names { get; } = names;
}
