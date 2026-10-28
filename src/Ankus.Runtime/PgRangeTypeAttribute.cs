using System;

namespace Ankus;

/// <summary>
/// Associates a mapped scalar bound with an existing PostgreSQL range type.
/// </summary>
/// <remarks>
/// Apply this attribute to a value type carrying PgDatumType. The scalar reader and writer convert
/// finite bounds of PgRange&lt;T&gt;; PostgreSQL validates and canonicalizes the range itself.
/// This declaration registers conversion and SQL metadata; it does not create a SQL range type.
/// An extension-owned range requires a PgSqlTypeProvider for its exact PgRange&lt;T&gt; managed identity,
/// independently of the scalar's provider. External ranges require an explicit schema.
/// Generic default declarations apply to finite selected scalar constructions. An explicit managed-type
/// declaration selects one closed scalar construction and takes precedence over its optional default.
/// Multidimensional arrays use one PgArray&lt;PgRange&lt;T&gt;&gt; container with the declared range identity.
/// </remarks>
/// <param name="name">The exact unquoted PostgreSQL range type identifier.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, AllowMultiple = true, Inherited = false)]
public sealed class PgRangeTypeAttribute(string name) : Attribute
{
    /// <summary>
    /// Associates one exact closed construction of the annotated scalar type with a SQL range type.
    /// </summary>
    /// <param name="managedType">The closed scalar bound type whose definition carries this attribute.</param>
    /// <param name="name">The exact unquoted PostgreSQL range type identifier.</param>
    public PgRangeTypeAttribute(Type managedType, string name) : this(name) => ManagedType = managedType;

    /// <summary>
    /// Gets the exact closed scalar bound identity, or null for the annotated type's default range mapping.
    /// </summary>
    public Type? ManagedType { get; }

    /// <summary>
    /// Gets the exact catalog range type identifier.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets or sets the range's fixed schema. External ranges require an explicit schema.
    /// </summary>
    public string? Schema
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether this extension supplies the range or an external schema already contains it.
    /// </summary>
    public PgTypeOrigin Origin
    {
        get;
        set;
    }
}
