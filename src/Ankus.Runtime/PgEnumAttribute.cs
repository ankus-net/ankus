using System;

namespace Ankus;

/// <summary>
/// Generates a PostgreSQL enum type and Native AOT conversions for a C# enum.
/// Labels retain member names by default; PostgreSQL ordering follows declaration order and ignores numeric values.
/// </summary>
[AttributeUsage(AttributeTargets.Enum, Inherited = false)]
public sealed class PgEnumAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the SQL type identifier; the default is the enum name in snake_case.
    /// </summary>
    public string? Name
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets a fixed schema. Otherwise the nearest PgSchema or extension installation schema applies.
    /// </summary>
    public string? Schema
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets this type's identifier in the installation dependency graph.
    /// </summary>
    public string? Id
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets installation entities that must precede this type.
    /// </summary>
    public string[] Requires
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Gets or sets whether installation SQL is emitted for the enum's CREATE TYPE declaration.
    /// The default is true. False retains the managed enum mapping, native conversions and dependency identifier.
    /// Cannot be false when Sql contains a replacement, including an empty string.
    /// </summary>
    public bool GenerateSql
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Gets or sets literal installation SQL replacing this enum's CREATE TYPE declaration.
    /// Null preserves generated SQL; empty text emits no statements for this declaration.
    /// </summary>
    /// <remarks>
    /// @MODULE_PATHNAME@ becomes MODULE_PATHNAME. Keep the declared type name, schema and labels compatible
    /// with the managed enum mapping. Consuming functions and operators remain generated.
    /// </remarks>
    public string? Sql
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether the literal Sql replacement permits moving the extension to another schema.
    /// The default is false. This option applies only to non-null Sql; fixed schemas and other
    /// non-relocatable declarations can still prevent relocation.
    /// </summary>
    public bool SqlRelocatable
    {
        get;
        set;
    }
}
