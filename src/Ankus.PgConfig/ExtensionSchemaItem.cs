using System;
using System.Collections.Generic;
using System.Linq;

namespace Ankus.PgConfig;

/// <summary>
/// Describes one generated SQL declaration and the prerequisites retained in its native library.
/// </summary>
public sealed class ExtensionSchemaItem
{
    /// <summary>
    /// Captures a validated declaration using private arrays exposed only through read-only views.
    /// </summary>
    /// <param name="id">The stable graph identifier.</param>
    /// <param name="kind">The declaration kind.</param>
    /// <param name="sql">The exact installation fragment.</param>
    /// <param name="owner">The optional declaration-family owner.</param>
    /// <param name="names">The distinct selection names.</param>
    /// <param name="dependencies">The preceding prerequisite identifiers.</param>
    /// <param name="attachments">The generated PostgreSQL object identities.</param>
    internal ExtensionSchemaItem(string id, string kind, string sql, string owner, string[] names,
        string[] dependencies, string[] attachments)
    {
        Id = id;
        Kind = kind;
        SqlTemplate = sql;
        Sql = sql.Replace("\0", string.Empty, StringComparison.Ordinal);
        Owner = owner;
        Names = Array.AsReadOnly(names);
        Dependencies = Array.AsReadOnly(dependencies);
        AttachmentTemplates = Array.AsReadOnly(attachments);
        Attachments = Array.AsReadOnly(attachments.Select(static value => value.Replace("\0", string.Empty, StringComparison.Ordinal)).ToArray());
    }

    /// <summary>
    /// Gets compiler-only schema insertion points alongside the SQL text.
    /// </summary>
    internal string SqlTemplate { get; }

    /// <summary>
    /// Gets compiler-only schema insertion points in object identities.
    /// </summary>
    internal IReadOnlyList<string> AttachmentTemplates { get; }

    /// <summary>
    /// Gets the stable declaration identifier used by dependency edges.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the declaration kind, such as function, type, schema, operator, aggregate or sql.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Gets this declaration's exact installation SQL, including intentional empty output.
    /// </summary>
    public string Sql { get; }

    /// <summary>
    /// Gets the containing declaration family's identifier, or empty text for an independent declaration or family root.
    /// </summary>
    public string Owner { get; }

    /// <summary>
    /// Gets exact SQL names, managed declaration names and explicit dependency aliases used for item selection.
    /// </summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>
    /// Gets the identifiers of declarations that must precede this item.
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; }

    /// <summary>
    /// Gets generated PostgreSQL object identities for ALTER EXTENSION ADD clauses.
    /// </summary>
    public IReadOnlyList<string> Attachments { get; }
}
