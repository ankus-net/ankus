using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ankus.PgConfig;

public sealed partial class ExtensionSchema
{
    /// <summary>
    /// Selects declarations by exact SQL name, managed name, signature or explicit dependency identifier.
    /// </summary>
    /// <param name="names">One or more names; ambiguous names require a qualified name or signature.</param>
    /// <param name="alterExtension">Whether to wrap creation and extension attachment in one transaction.</param>
    /// <returns>The selected SQL, ordered dependency closure and attachment diagnostics.</returns>
    /// <exception cref="InvalidOperationException">The library predates embedded dependency graphs, or selected SQL needs an installation schema absent from metadata.</exception>
    /// <exception cref="ArgumentException">A selection is empty, unknown or ambiguous.</exception>
    /// <remarks>
    /// The extension must already exist when attaching objects. The concrete library must be installed in PostgreSQL's library directory.
    /// Custom SQL without declared created objects remains in the script and produces a warning when attachment is requested.
    /// </remarks>
    public ExtensionSchemaSelection Select(IEnumerable<string> names, bool alterExtension = true)
        => SelectCore(names, alterExtension, null);

    /// <summary>
    /// Selects declarations for a known installation schema, resolving PostgreSQL's reserved @extschema@ token.
    /// </summary>
    /// <param name="names">One or more exact declaration names.</param>
    /// <param name="extensionSchema">The actual installation schema; it must agree with any fixed control schema.</param>
    /// <param name="alterExtension">Whether to wrap creation and extension attachment in one transaction.</param>
    /// <returns>Selected SQL with exact schema qualification and dependency closure.</returns>
    /// <exception cref="ArgumentNullException">Names or the installation schema is null.</exception>
    /// <exception cref="ArgumentException">The selection or schema is invalid, or the schema conflicts with fixed metadata.</exception>
    /// <exception cref="InvalidOperationException">The library predates embedded dependency graphs.</exception>
    public ExtensionSchemaSelection Select(IEnumerable<string> names, string extensionSchema, bool alterExtension = true)
    {
        ArgumentNullException.ThrowIfNull(extensionSchema);
        return SelectCore(names, alterExtension, extensionSchema);
    }

    /// <summary>
    /// Applies the selected installation schema without changing the published installation script.
    /// </summary>
    /// <param name="names">The requested declaration identities.</param>
    /// <param name="alterExtension">Whether to attach emitted objects.</param>
    /// <param name="extensionSchema">An explicit installation schema, or null to use fixed metadata.</param>
    /// <returns>The executable selected SQL and dependency closure.</returns>
    private ExtensionSchemaSelection SelectCore(IEnumerable<string> names, bool alterExtension, string? extensionSchema)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (extensionSchema is not null)
        {
            try
            {
                if (extensionSchema.Length == 0 || extensionSchema.Contains('\0') || s_utf8.GetByteCount(extensionSchema) > 63)
                {
                    throw new ArgumentException("The installation schema must be a nonempty PostgreSQL identifier of at most 63 UTF-8 bytes.", nameof(extensionSchema));
                }
            }
            catch (EncoderFallbackException error)
            {
                throw new ArgumentException("The installation schema must contain valid Unicode.", nameof(extensionSchema), error);
            }

            if (DefaultSchema is not null && !string.Equals(DefaultSchema, extensionSchema, StringComparison.Ordinal))
            {
                throw new ArgumentException("The installation schema must agree with the fixed control schema.", nameof(extensionSchema));
            }
        }

        ExtensionSchemaGraph graph = Graph ?? throw new InvalidOperationException(
            "This library does not contain a dependency graph. Rebuild it with a current Ankus SDK to select schema items.");
        Dictionary<string, ExtensionSchemaItem> items = graph.Items.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        ILookup<string, ExtensionSchemaItem> families = graph.Items.Where(static item => item.Owner.Length != 0)
            .ToLookup(static item => item.Owner, StringComparer.Ordinal);
        ILookup<string, ExtensionSchemaItem> matches = graph.Items.SelectMany(static item => item.Names.Select(name => (Name: name, Item: item)))
            .ToLookup(static entry => entry.Name, static entry => entry.Item, StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<ExtensionSchemaItem>();
        foreach (string name in names)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ExtensionSchemaItem[] candidates = [.. matches[name].Select(item => item.Owner.Length == 0 ? item : items[item.Owner])
                .DistinctBy(static item => item.Id)];
            if (candidates.Length != 1)
            {
                throw new ArgumentException(candidates.Length == 0 ? $"Unknown schema item '{name}'." :
                    $"Ambiguous schema item '{name}': {string.Join(", ", candidates.Select(static item => item.Id))}. Use a qualified name or signature.", nameof(names));
            }

            Add(candidates[0]);
        }

        if (pending.Count == 0)
        {
            throw new ArgumentException("Select at least one schema item.", nameof(names));
        }

        while (pending.TryDequeue(out ExtensionSchemaItem? item))
        {
            foreach (string dependency in item.Dependencies)
            {
                Add(items[dependency]);
            }

            if (item.Owner.Length != 0)
            {
                Add(items[item.Owner]);
            }

            foreach (ExtensionSchemaItem member in families[item.Id])
            {
                Add(member);
            }
        }

        ExtensionSchemaItem[] ordered = [.. graph.Items.Where(item => selected.Contains(item.Id))];
        var script = new StringBuilder(graph.Preamble);
        if (alterExtension)
        {
            script.Append("BEGIN;\n\n");
        }

        var warnings = new List<string>();
        var attached = new HashSet<string>(StringComparer.Ordinal);
        string extension = "\"" + Name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        string library = "'$libdir/" + Artifacts.Library.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal) + "'";
        string? schema = extensionSchema ?? DefaultSchema;
        string? identifier = schema is null ? null : "\"" + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        string prefix = identifier is null ? string.Empty : identifier + ".";
        foreach (ExtensionSchemaItem item in ordered)
        {
            string sql = RenderSelectionTemplate(item.SqlTemplate, prefix, identifier, library);
            script.Append(sql);
            if (sql.Length != 0 && sql[^1] != '\n')
            {
                script.Append('\n');
            }

            bool emits = item.Sql.Length != 0 || item.Owner.Length != 0 && items[item.Owner].Sql.Length != 0;
            if (!alterExtension || !emits)
            {
                continue;
            }

            foreach (string template in item.AttachmentTemplates)
            {
                string attachment = RenderSelectionTemplate(template, prefix, identifier, library);
                if (attached.Add(attachment))
                {
                    script.Append("ALTER EXTENSION ").Append(extension).Append(" ADD ").Append(attachment).Append(";\n");
                }
            }

            if (item.Kind == "sql" && item.Attachments.Count == 0)
            {
                string? location = SourceLocation(item.Sql);
                warnings.Add($"Custom SQL '{(item.Names.Count == 0 ? item.Id : item.Names[0])}'{(location is null ? "" : " at " + location)} " +
                    "has no declared created objects; attach them to the extension manually.");
            }
        }

        if (alterExtension)
        {
            script.Append("\nCOMMIT;\n");
        }

        return new ExtensionSchemaSelection(script.ToString(), ordered, [.. warnings]);

        void Add(ExtensionSchemaItem item)
        {
            if (selected.Add(item.Id))
            {
                pending.Enqueue(item);
            }
        }
    }

    /// <summary>
    /// Resolves reserved publication tokens in one pass without interpreting tokens inside inserted identifiers or library paths.
    /// </summary>
    /// <param name="template">The original graph SQL or attachment text.</param>
    /// <param name="prefix">The quoted installation-schema prefix, or empty text.</param>
    /// <param name="schema">The quoted bare installation schema, when known.</param>
    /// <param name="library">The quoted installed library path.</param>
    /// <returns>The resolved selected statement.</returns>
    private static string RenderSelectionTemplate(string template, string prefix, string? schema, string library)
    {
        var result = new StringBuilder(template.Length);
        for (int index = 0; index < template.Length; index++)
        {
            if (template[index] == '\0')
            {
                result.Append(prefix);
            }
            else if (template[index] == '@' && template.AsSpan(index).StartsWith("@extschema@", StringComparison.Ordinal))
            {
                if (schema is null)
                {
                    throw new InvalidOperationException("Selected SQL uses @extschema@; supply the extension's installation schema using the Select overload or --schema.");
                }

                result.Append(schema);
                index += "@extschema@".Length - 1;
            }
            else if (template[index] == '\'' && template.AsSpan(index).StartsWith("'MODULE_PATHNAME'", StringComparison.Ordinal))
            {
                result.Append(library);
                index += "'MODULE_PATHNAME'".Length - 1;
            }
            else
            {
                result.Append(template[index]);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Reads the declaration's source location from a generated block's provenance comment, such as
    /// <c>-- Sql/Setup.cs:12</c>, as pgrx's warning names the file and line of an <c>extension_sql!</c> block.
    /// </summary>
    private static string? SourceLocation(string sql)
    {
        const string Begin = "/* <begin connected objects> */\n-- ";
        if (!sql.StartsWith(Begin, StringComparison.Ordinal))
        {
            return null;
        }

        int end = sql.IndexOf('\n', Begin.Length);
        string comment = end < 0 ? sql[Begin.Length..] : sql[Begin.Length..end];
        int colon = comment.LastIndexOf(':');
        return colon > 0 && colon < comment.Length - 1 && comment.AsSpan(colon + 1).IndexOfAnyExceptInRange('0', '9') < 0
            ? comment
            : null;
    }
}
