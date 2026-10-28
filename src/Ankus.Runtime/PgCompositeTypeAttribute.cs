using System;

namespace Ankus;

/// <summary>
/// Binds a PgHeapTuple value or array to an existing PostgreSQL composite type.
/// Without this attribute, the SQL type is record or record[].
/// </summary>
/// <param name="name">The exact composite type identifier, without SQL quoting.</param>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = true)]
public sealed class PgCompositeTypeAttribute(string name) : Attribute
{
    /// <summary>
    /// Gets the exact PostgreSQL composite type identifier.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets or sets the schema identifier. An omitted schema leaves the SQL type name unqualified.
    /// </summary>
    public string? Schema
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the exact SQL TABLE column name to bind when a result contains multiple composite columns.
    /// A single composite output is selected automatically when this value is omitted.
    /// </summary>
    public string? Column
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the exact C# tuple element to bind within a typed aggregate argument group.
    /// Omit for a scalar parameter or return value.
    /// </summary>
    public string? Element
    {
        get;
        set;
    }
}
