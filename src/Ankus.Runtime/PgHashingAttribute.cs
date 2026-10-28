using System;

namespace Ankus;

/// <summary>
/// Generates a PostgreSQL hash support function and default hash operator class from IPgHashable.
/// Enum declarations hash their numeric value as eight little-endian bytes with PgHash.
/// </summary>
/// <remarks>
/// Requires PgType, PgEnum, or a readable PgDatumType mapping and a same-type equality operator,
/// generated with PgEquality or declared with PgOperator. A mapped writer is not required.
/// Non-enum types must implement IEquatable of the declared type and IPgHashable. Hashes must agree with equality
/// and remain stable across processes, platforms and extension versions. Generated functions are strict,
/// immutable and parallel safe. Changing the hashing contract requires rebuilding dependent indexes.
/// Mapped readers must also provide stable, immutable and parallel-safe logical keys. Generated objects use the
/// mapped SQL type's schema; a fixed schema prevents extension relocation. PostgreSQL validates existing default
/// classes and permissions, and resolves a domain's default index class through its base type.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, Inherited = false)]
public sealed class PgHashingAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the installation dependency identifier for the completed hash operator family and class.
    /// </summary>
    public string? Id
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets installation entities that must precede the generated hashing declarations.
    /// </summary>
    public string[] Requires
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Gets or sets whether installation SQL is emitted for the hash operator family and class.
    /// The default is true. False retains the hash support function, equality prerequisite and dependency identifier.
    /// Cannot be false when Sql contains a replacement, including an empty string.
    /// </summary>
    public bool GenerateSql
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Gets or sets literal installation SQL replacing the hash operator family and class.
    /// Null preserves generated SQL; empty text emits no statements for this declaration.
    /// </summary>
    /// <remarks>
    /// @HASH_FUNCTION_SQL@ becomes the retained hash helper's quoted SQL identifier, qualified when a fixed
    /// schema is declared, without an argument list. Do not add string quotes around this token.
    /// @MODULE_PATHNAME@ becomes MODULE_PATHNAME. The hash support function remains generated.
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
