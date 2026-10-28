using System;

namespace Ankus;

/// <summary>
/// Contains PostgreSQL's table rewrite reason bits. Multiple reasons and future nonnegative bits are preserved.
/// </summary>
[Flags]
public enum PgTableRewriteReason
{
    /// <summary>
    /// Contains no reason bits.
    /// </summary>
    None = 0,

    /// <summary>
    /// Changes the table's persistence.
    /// </summary>
    AlterPersistence = 1,

    /// <summary>
    /// Adds or changes a value that requires rewriting existing rows.
    /// </summary>
    DefaultValue = 2,

    /// <summary>
    /// Rewrites a column's stored representation.
    /// </summary>
    ColumnRewrite = 4,

    /// <summary>
    /// Changes the table's access method.
    /// </summary>
    AccessMethod = 8,
}
