using System.Text;
using System.Text.RegularExpressions;

namespace Ankus.Build;

/// <summary>
/// Locates native node values and their embedded dependencies through real C field expressions.
/// </summary>
internal static partial class NativeBindingSelection
{
    /// <summary>
    /// Selects every node and recursively includes complete by-value dependencies without following pointers.
    /// </summary>
    /// <param name="catalog">The pinned declaration catalog.</param>
    /// <returns>Native values addressable through named root types and their embedded fields.</returns>
    internal static IReadOnlyList<NativeBindingSelectionEntry> Create(NativeBindingCatalog catalog)
    {
        var selected = new SortedDictionary<string, NativeBindingSelectionEntry>(StringComparer.Ordinal);
        var pending = new Queue<NativeBindingSelectionEntry>();
        foreach (NativeBindingType type in catalog.Types.Values.Where(static type => type.IsNode))
        {
            Add(type, type.IsUnion ? "union " + type.Name : type.Name, string.Empty);
        }

        while (pending.TryDequeue(out NativeBindingSelectionEntry? entry))
        {
            foreach (NativeBindingField field in entry.Type.Fields)
            {
                string path = entry.Path.Length == 0 ? field.NativeName : entry.Path + "." + field.NativeName;
                IncludeValue(field.Representation, entry.Root, path);
            }
        }

        return Array.AsReadOnly(selected.Values.ToArray());

        void IncludeValue(string representation, string root, string path)
        {
            string resolved = ResolveAlias(catalog, representation);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var indexed = new StringBuilder(path);
            while (ArrayElement(resolved) is string element)
            {
                if (!visited.Add(resolved))
                {
                    throw new FormatException($"Cyclic native array representation at {root}.{indexed}.");
                }

                resolved = ResolveAlias(catalog, element);
                indexed.Append("[0]");
            }

            path = indexed.ToString();

            if (catalog.Types.TryGetValue(resolved, out NativeBindingType? type))
            {
                Add(type, root, path);
            }
            // pgrx replaces these C scalar typedefs with hand-written Rust wrappers outside the bindgen files.
            else if (!IsIndirect(resolved) && !Scalar().IsMatch(resolved) &&
                resolved is not ("NodeTag" or "Oid" or "Datum" or "TransactionId" or "MultiXactId") &&
                !(resolved.EndsWith("::Type", StringComparison.Ordinal) && catalog.Enums.ContainsKey(resolved[..^6])))
            {
                throw new FormatException($"Unknown native value representation '{representation}' at {root}.{path}.");
            }
        }

        void Add(NativeBindingType type, string root, string path)
        {
            if (selected.ContainsKey(type.Name))
            {
                return;
            }

            var entry = new NativeBindingSelectionEntry(type, root, path);
            selected.Add(type.Name, entry);
            pending.Enqueue(entry);
        }
    }

    /// <summary>
    /// Resolves typedefs while retaining pointer, array, scalar and embedded-value distinctions.
    /// </summary>
    /// <param name="catalog">The source declarations.</param>
    /// <param name="representation">The complete bindgen field expression.</param>
    /// <returns>The normalized expression after following typedefs.</returns>
    internal static string ResolveAlias(NativeBindingCatalog catalog, string representation)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string resolved = Normalize(representation);
        while (catalog.Aliases.TryGetValue(resolved, out string? target))
        {
            if (!visited.Add(resolved))
            {
                throw new FormatException($"Cyclic native typedef at {resolved}.");
            }

            resolved = Normalize(target);
        }

        return resolved;
    }

    /// <summary>
    /// Gets a fixed or flexible array's element representation, or null for other fields.
    /// </summary>
    /// <param name="representation">A normalized expression with typedefs resolved.</param>
    /// <returns>The array element expression when present.</returns>
    internal static string? ArrayElement(string representation)
    {
        Match array = ArrayPattern().Match(representation);
        if (array.Success)
        {
            return array.Groups["element"].Value.Trim();
        }

        const string Prefix = "__IncompleteArrayField<";
        return representation.StartsWith(Prefix, StringComparison.Ordinal) && representation.EndsWith('>')
            ? representation[Prefix.Length..^1].Trim() : null;
    }

    private static string Normalize(string representation)
        => Whitespace().Replace(NativeBindingParser.MaskTrivia(representation), " ").Trim();

    private static bool IsIndirect(string representation)
        => representation.StartsWith("*mut ", StringComparison.Ordinal) || representation.StartsWith("*const ", StringComparison.Ordinal) ||
            representation.StartsWith("::core::option::Option<", StringComparison.Ordinal) && representation.Contains("fn(", StringComparison.Ordinal);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\[(?<element>.+);\s*\d+(?:usize)?\]$")]
    private static partial Regex ArrayPattern();

    [GeneratedRegex(@"^(?:bool|[iu](?:8|16|32|64|128|size)|f(?:32|64)|::core::ffi::c_(?:char|schar|uchar|short|ushort|int|uint|long|ulong|longlong|ulonglong|float|double))$")]
    private static partial Regex Scalar();
}

/// <summary>
/// Addresses one bindgen value through an actual C root and optional embedded member path.
/// </summary>
/// <param name="Type">The complete source declaration.</param>
/// <param name="Root">A C type name, including the union keyword when required.</param>
/// <param name="Path">An embedded member path, or empty for the root itself.</param>
internal sealed record NativeBindingSelectionEntry(NativeBindingType Type, string Root, string Path)
{
    /// <summary>
    /// Gets an unevaluated C lvalue identifying this native value.
    /// </summary>
    internal string Expression => Path.Length == 0 ? $"*(({Root}*)0)" : $"(({Root}*)0)->{Path}";

}
