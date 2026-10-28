using System;
using System.Collections.Generic;

namespace Ankus.PgConfig;

/// <summary>
/// Describes a dependency-complete subset of an extension's installation declarations.
/// </summary>
public sealed class ExtensionSchemaSelection
{
    /// <summary>
    /// Creates an owned selection and its rendering diagnostics.
    /// </summary>
    /// <param name="sql">The directly executable SQL script.</param>
    /// <param name="items">Selected declarations in installation order.</param>
    /// <param name="warnings">Declarations whose custom SQL has no attachment inventory.</param>
    internal ExtensionSchemaSelection(string sql, ExtensionSchemaItem[] items, string[] warnings)
    {
        Sql = sql;
        Items = Array.AsReadOnly(items);
        Warnings = Array.AsReadOnly(warnings);
    }

    /// <summary>
    /// Gets the selected SQL with a concrete library path and optional transactional extension attachments.
    /// </summary>
    public string Sql { get; }

    /// <summary>
    /// Gets the selected declarations, including prerequisites and attached declaration families.
    /// </summary>
    public IReadOnlyList<ExtensionSchemaItem> Items { get; }

    /// <summary>
    /// Gets warnings about custom SQL whose created objects cannot be attached automatically.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }
}
