namespace Ankus.Build;

/// <summary>
/// Requires independently collected header signatures and measured storage types to describe the same native calls.
/// </summary>
internal static class NativeBindingSignatureValidation
{
    /// <summary>
    /// Checks every root, including unselected functions and globals, in both its written and canonical forms.
    /// </summary>
    /// <param name="records">The complete selected-header contract.</param>
    internal static void Validate(NativeHeaderRecords records)
    {
        ArgumentNullException.ThrowIfNull(records);
        NativeBindingRecordValidation.Validate(records.Graph, records.Headers.Target, records.Headers.Symbols.Keys);
        var comparer = new Comparer(records.Graph);
        foreach ((string name, NativeHeaderSymbol symbol) in records.Headers.Symbols.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            NativeBindingCDeclaration.ValidateName(symbol.NativeName);
            int root = records.Graph.Roots[name];
            comparer.Compare(symbol.Type, root, name, canonical: false);
            if (symbol.IsFunction != (records.Graph.Types[records.Graph.Types[root].Canonical].Kind == "function"))
            {
                throw new FormatException($"Native signature '{name}' disagrees with its declaration kind.");
            }
        }
    }

    /// <summary>
    /// Tracks anonymous declarations across independently written aliases while comparing both compiler observations.
    /// </summary>
    private sealed class Comparer(NativeRecordGraph graph)
    {
        /// <summary>
        /// Binds each observed anonymous typedef to one declaration throughout the complete selection.
        /// </summary>
        private readonly Dictionary<string, int> _anonymousAliases = new(StringComparer.Ordinal);

        /// <summary>
        /// Compares native type identity, parameter adjustment and qualification without equating equally sized storage.
        /// </summary>
        internal void Compare(NativeHeaderType header, int index, string path, bool canonical, bool parameter = false,
            int depth = 0, NativeHeaderQualifiers headerQualifiers = NativeHeaderQualifiers.None,
            NativeHeaderQualifiers graphQualifiers = NativeHeaderQualifiers.None)
        {
            if (depth >= 128)
            {
                throw Invalid(path, "type nesting or alias cycle");
            }

            if (!canonical)
            {
                Compare(header, index, path, canonical: true, parameter, depth, headerQualifiers, graphQualifiers);
            }

            NativeRecordType type = graph.Types[canonical ? graph.Types[index].Canonical : index];
            var aliases = new List<string>();
            while (true)
            {
                if (depth++ >= 128)
                {
                    throw Invalid(path, "type nesting or alias cycle");
                }

                if (header is NativeHeaderQualified qualified)
                {
                    headerQualifiers |= qualified.Modifiers;
                    header = qualified.Underlying;
                    continue;
                }

                if (type.Kind == "typeof")
                {
                    // libclang exposes only the canonical resolution of typeof. The JSON AST
                    // retains written typedefs, so compare their exact resolved native contract here.
                    Compare(header, type.Element!.Value, path, canonical: true, parameter: false, depth,
                        headerQualifiers, graphQualifiers | type.Qualifiers);
                    return;
                }

                if (type.Kind is "elaborated" or "attributed")
                {
                    graphQualifiers |= type.Qualifiers;
                    type = graph.Types[type.Element!.Value];
                    continue;
                }

                if (header is NativeHeaderAdjusted adjusted)
                {
                    // A written parameter edge can retain an array/function even after canonicalization;
                    // the canonical function prototype instead points to its adjusted pointer.
                    header = graph.Types[type.Canonical].Kind is "array" or "function" ? adjusted.Written : adjusted.Adjusted;
                    continue;
                }

                if (header is NativeHeaderAlias alias)
                {
                    NativeBindingCDeclaration.ValidateName(alias.Name);
                    aliases.Add(alias.Name);
                    if (!canonical)
                    {
                        if (type.Kind != "alias" || type.Name != alias.Name)
                        {
                            throw Invalid(path, "typedef identity");
                        }

                        Compare(header, type.Canonical, path, canonical: true, parameter, depth, headerQualifiers, graphQualifiers);
                        graphQualifiers |= type.Qualifiers;
                        type = graph.Types[type.Element!.Value];
                    }

                    header = alias.Underlying;
                    continue;
                }

                break;
            }

            graphQualifiers |= type.Qualifiers;
            if ((!canonical || !parameter) && type.Kind != "array" && headerQualifiers != graphQualifiers)
            {
                throw Invalid(path, "type qualifiers");
            }

            switch (header)
            {
                case NativeHeaderScalar scalar when type.Kind == "scalar":
                    if (Scalar(scalar.Name) != Scalar(type.Name))
                    {
                        throw Invalid(path, "scalar identity");
                    }

                    break;
                case NativeHeaderRecord record when type.Kind == "record":
                    Tag(record.Name, record.IsUnion ? "union" : "struct", record.IsComplete);
                    break;
                case NativeHeaderEnum enumeration when type.Kind == "enum":
                    Tag(enumeration.Name, "enum", enumeration.IsComplete);
                    break;
                case NativeHeaderPointer pointer when type.Kind == "pointer":
                    Compare(pointer.Element, type.Element!.Value, path + ".element", canonical, depth: depth);
                    break;
                case NativeHeaderArray array when type.Kind == "array":
                    if (array.Count != (ulong?)type.Count)
                    {
                        throw Invalid(path, "array extent");
                    }

                    // libclang can place an element qualifier on the array; propagate it through every dimension.
                    Compare(array.Element, type.Element!.Value, path + ".element", canonical, depth: depth,
                        headerQualifiers: headerQualifiers, graphQualifiers: graphQualifiers);
                    break;
                case NativeHeaderFunction function when type.Function is NativeRecordFunction shape:
                    // libclang marks an unspecified parameter list variadic; the structured AST keeps that flag false.
                    if (function.HasPrototype != shape.HasPrototype ||
                        function.HasPrototype && function.IsVariadic != shape.IsVariadic || function.CallingConvention != shape.CallingConvention)
                    {
                        throw Invalid(path, "function prototype or calling convention");
                    }

                    if (function.Parameters.Count != shape.Parameters.Count)
                    {
                        throw Invalid(path, "parameter count");
                    }

                    Compare(function.Result, shape.Result, path + ".result", canonical, depth: depth);
                    for (int argument = 0; argument < function.Parameters.Count; argument++)
                    {
                        Compare(function.Parameters[argument], shape.Parameters[argument],
                            path + ".argument" + argument, canonical, parameter: true, depth: depth);
                    }

                    break;
                default:
                    throw Invalid(path, "type shape");
            }

            void Tag(string name, string kind, bool? complete)
            {
                int tagged = type.Declaration!.Value;
                NativeRecordDeclaration declaration = graph.Declarations[tagged];
                if (name != declaration.Name || kind != declaration.Kind || complete is bool known && known != declaration.IsComplete)
                {
                    throw Invalid(path, "tag identity or completeness");
                }

                if (name.Length != 0)
                {
                    return;
                }

                foreach (string alias in aliases)
                {
                    if (_anonymousAliases.TryGetValue(alias, out int previous) && previous != tagged)
                    {
                        throw Invalid(path, "anonymous typedef identity");
                    }

                    _anonymousAliases[alias] = tagged;
                }
            }
        }

        /// <summary>
        /// Normalizes the two compiler spellings of the same C Boolean type.
        /// </summary>
        private static string Scalar(string name) => name == "bool" ? "_Bool" : name;

        /// <summary>
        /// Identifies the first incompatible signature path without emitting a partial call body.
        /// </summary>
        private static FormatException Invalid(string path, string reason)
            => new($"Native signature '{path}' disagrees with its measured {reason}.");
    }
}
