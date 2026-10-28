using System;

namespace Ankus;

/// <summary>
/// Places a class's generated functions in a fixed PostgreSQL schema, creating an extension-owned schema by default.
/// Nested classes inherit the nearest declaration; a function's Schema option overrides it.
/// </summary>
/// <remarks>
/// Declares the schema's exact identifier, quoted by the SQL generator.
/// </remarks>
/// <param name="name">The nonempty schema name, at most 63 UTF-8 bytes.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PgSchemaAttribute(string name) : Attribute
{
    /// <summary>
    /// Gets the SQL schema identifier.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets or sets a dependency identifier for this schema declaration.
    /// Multiple classes describing the same schema may expose different identifiers for that single schema.
    /// </summary>
    public string? Id
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets identifiers of SQL blocks or generated declarations that must precede this schema.
    /// </summary>
    public string[] Requires
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Gets or sets whether installation creates and owns the schema. Set false to require an existing schema without adopting it.
    /// </summary>
    public bool Create
    {
        get;
        set;
    } = true;
}
