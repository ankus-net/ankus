using System;

namespace Ankus;

/// <summary>
/// Generates PostgreSQL equality and inequality operators from IEquatable of the declared type.
/// Enum declarations use their underlying numeric equality.
/// </summary>
/// <remarks>
/// Requires PgType, PgEnum, or a PgDatumType mapping with a reader. A mapped writer is not required.
/// Equality and any mapped reader must be immutable and parallel safe; equality must be an equivalence relation.
/// Generated functions are strict and never pass SQL NULL to managed comparisons.
/// Add PgOrdering or PgHashing to generate compatible default index operator classes.
/// Generated objects use the mapped SQL type's schema; fixed-schema objects prevent extension relocation.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, Inherited = false)]
public sealed class PgEqualityAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the installation dependency identifier for the generated equality operators.
    /// </summary>
    public string? Id
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets installation entities that must precede the generated equality operators.
    /// </summary>
    public string[] Requires
    {
        get;
        set;
    } = [];
}
