using System;

namespace Ankus;

/// <summary>
/// Declares the SQL block that supplies a catalog type for raw or composite bindings, or an owned mapped scalar or range identity.
/// </summary>
/// <remarks>
/// Adds installation dependencies without parsing SQL or executing converters.
/// A managed-type provider selects its referenced datum mapping for static registration.
/// A PgRange&lt;T&gt; provider completes the declared range after its owned scalar bound provider.
/// The block may declare a shell type when its completion is ordered separately.
/// </remarks>
/// <param name="sqlId">The dependency identifier of a PgSql or PgSqlFile block.</param>
/// <param name="name">The exact, unquoted catalog type name, without a schema prefix.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class PgSqlTypeProviderAttribute(string sqlId, string name) : Attribute
{
    /// <summary>
    /// Declares the SQL provider for one extension-owned managed datum mapping.
    /// </summary>
    /// <param name="sqlId">The dependency identifier of a PgSql or PgSqlFile block.</param>
    /// <param name="managedType">The closed type carrying PgDatumType, or its PgRange&lt;T&gt; identity declared by PgRangeType.</param>
    public PgSqlTypeProviderAttribute(string sqlId, Type managedType) : this(sqlId, (string)null!) => ManagedType = managedType;

    /// <summary>
    /// Gets the dependency identifier of the supplying SQL block.
    /// </summary>
    public string SqlId { get; } = sqlId;

    /// <summary>
    /// Gets the exact catalog name of the supplied type or array element, or null for a managed-type provider.
    /// </summary>
    public string? Name { get; } = name;

    /// <summary>
    /// Gets the exact managed mapping identity, or null for a catalog-name provider.
    /// </summary>
    public Type? ManagedType { get; }

    /// <summary>
    /// Gets or sets the fixed schema for the catalog-name overload. Null matches only bindings that omit their schema.
    /// A managed-type provider uses its mapping's schema and rejects an explicitly assigned Schema.
    /// A fixed owned schema prevents extension schema relocation.
    /// </summary>
    public string? Schema
    {
        get;
        set;
    }
}
