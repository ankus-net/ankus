using System;

namespace Ankus;

/// <summary>
/// Binds a raw PgDatum parameter or result to a PostgreSQL type.
/// </summary>
/// <remarks>
/// Names identify existing types; use PgSql dependencies to create extension-defined types first.
/// Ankus validates the returned datum's exact type and storage lifetime.
/// </remarks>
/// <param name="name">The case-sensitive type name without SQL quoting or a schema prefix.</param>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = true)]
public sealed class PgSqlTypeAttribute(string name) : Attribute
{
    /// <summary>
    /// Gets the PostgreSQL type identifier.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets or sets a fixed schema, or null to resolve the type through the installation search path.
    /// </summary>
    public string? Schema
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether the datum represents an array of the named type.
    /// </summary>
    public bool IsArray
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the SQL TABLE output column to bind when the method returns several raw values.
    /// </summary>
    public string? Column
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the exact C# tuple element to bind within a typed aggregate argument group.
    /// Omit for a scalar parameter or return value; Column selects TABLE outputs separately.
    /// </summary>
    public string? Element
    {
        get;
        set;
    }
}
