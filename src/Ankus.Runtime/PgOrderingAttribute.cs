using System;

namespace Ankus;

/// <summary>
/// Generates PostgreSQL relational operators and a default B-tree operator class from IComparable of the declared type.
/// Enum declarations use their underlying numeric order.
/// </summary>
/// <remarks>
/// Requires PgType, PgEnum, or a readable PgDatumType mapping and a same-type equality operator,
/// generated with PgEquality or declared with PgOperator. A mapped writer is not required.
/// Non-enum types must also implement IEquatable of the declared type. Comparison must define an immutable,
/// parallel-safe total order whose zero results agree with equality. Generated functions are strict.
/// On PgEnum this explicit opt-in selects numeric ordering; without it, PostgreSQL orders labels by declaration.
/// Changing ordering after data is indexed requires rebuilding dependent indexes.
/// Mapped readers share the immutable, parallel-safe contract. Generated objects use the mapped SQL type's schema;
/// a fixed schema prevents extension relocation. PostgreSQL checks existing default classes and permissions.
/// PostgreSQL resolves a domain's default index class through its base type.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, Inherited = false)]
public sealed class PgOrderingAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the installation dependency identifier for the completed B-tree operator family and class.
    /// </summary>
    public string? Id
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets installation entities that must precede the generated ordering declarations.
    /// </summary>
    public string[] Requires
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Gets or sets whether installation SQL is emitted for the B-tree operator family and class.
    /// The default is true. False retains the comparison functions, relational operators and dependency identifier.
    /// Cannot be false when Sql contains a replacement, including an empty string.
    /// </summary>
    public bool GenerateSql
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Gets or sets literal installation SQL replacing the B-tree operator family and class.
    /// Null preserves generated SQL; empty text emits no statements for this declaration.
    /// </summary>
    /// <remarks>
    /// @COMPARISON_FUNCTION_SQL@ becomes the retained comparison helper's quoted SQL identifier,
    /// qualified when a fixed schema is declared, without an argument list. Do not add string quotes around this token.
    /// @MODULE_PATHNAME@ becomes MODULE_PATHNAME. Comparison functions and relational operators remain generated.
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
