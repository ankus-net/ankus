using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Observes named native enums without assuming the pinned bindgen platform's integer model.
/// </summary>
internal static class NativeBindingEnums
{
    /// <summary>
    /// Replaces reference member inventories with the independently collected native enum values before probing.
    /// </summary>
    internal static NativeBindingCatalog SelectNative(NativeBindingCatalog catalog, NativeRecordGraph graph)
    {
        Dictionary<string, NativeBindingEnum> enums = catalog.Enums.ToDictionary(StringComparer.Ordinal);
        foreach ((string name, NativeBindingEnum reference) in Select(catalog))
        {
            int[] identities = [.. graph.Types.Where(type => type.Kind == "alias" && type.Name == name)
                .Select(type => graph.Types[type.Canonical].Declaration).OfType<int>().Distinct()];
            if (identities.Length != 1 || graph.Declarations[identities[0]].Kind != "enum")
            {
                throw new FormatException($"Native enum '{name}' requires a distinct measured enum identity.");
            }

            var values = new ReadOnlyDictionary<string, string>(graph.Declarations[identities[0]].EnumValues
                .ToDictionary(static value => value.Name, static value => value.Value, StringComparer.Ordinal));
            enums[name] = reference with { Values = values };
        }

        return catalog with { Enums = new ReadOnlyDictionary<string, NativeBindingEnum>(enums) };
    }

    /// <summary>
    /// Finds named enums embedded by value in selected nodes and their complete dependencies.
    /// </summary>
    /// <param name="catalog">The pinned native declarations.</param>
    /// <returns>The required enum declarations in deterministic order.</returns>
    internal static IReadOnlyDictionary<string, NativeBindingEnum> Select(NativeBindingCatalog catalog)
    {
        var selected = new SortedDictionary<string, NativeBindingEnum>(StringComparer.Ordinal);
        foreach (NativeBindingSelectionEntry entry in NativeBindingSelection.Create(catalog))
        {
            foreach (NativeBindingField field in entry.Type.Fields)
            {
                string representation = NativeBindingSelection.ResolveAlias(catalog, field.Representation);
                while (NativeBindingSelection.ArrayElement(representation) is string element)
                {
                    representation = NativeBindingSelection.ResolveAlias(catalog, element);
                }

                if (representation.EndsWith("::Type", StringComparison.Ordinal) &&
                    catalog.Enums.TryGetValue(representation[..^6], out NativeBindingEnum? declaration))
                {
                    selected.TryAdd(representation[..^6], declaration);
                }
            }
        }

        return new ReadOnlyDictionary<string, NativeBindingEnum>(selected);
    }

    /// <summary>
    /// Emits measured widths, signedness and native constants through the selected header declarations.
    /// </summary>
    /// <param name="source">The native probe under construction.</param>
    /// <param name="catalog">The expected declaration catalog.</param>
    internal static void Write(StringBuilder source, NativeBindingCatalog catalog)
    {
        foreach ((string name, NativeBindingEnum declaration) in Select(catalog))
        {
            source.AppendLine("    {");
            source.AppendLine(CultureInfo.InvariantCulture, $"        volatile {name} below_zero = ({name})-1;");
            source.AppendLine(CultureInfo.InvariantCulture, $"        volatile {name} zero = ({name})0;");
            source.AppendLine("        int is_signed = below_zero < zero;");
            source.AppendLine(CultureInfo.InvariantCulture,
                $"        printf(\"enum|{name}|%zu|%d\\n\", sizeof({name}), is_signed);");
            foreach (string member in declaration.Values.Keys)
            {
                source.AppendLine(CultureInfo.InvariantCulture,
                    $"        if (is_signed) printf(\"constant|{name}|{member}|%lld\\n\", (long long)({name}){member});");
                source.AppendLine(CultureInfo.InvariantCulture,
                    $"        else printf(\"constant|{name}|{member}|%llu\\n\", (unsigned long long)({name}){member});");
            }

            source.AppendLine("    }");
        }
    }

    /// <summary>
    /// Requires complete, exact native enum values and a valid measured integer representation.
    /// </summary>
    /// <param name="catalog">The expected enum declarations and constants.</param>
    /// <param name="lines">The native observation records.</param>
    /// <returns>Validated target enum storage keyed by native name.</returns>
    internal static IReadOnlyDictionary<string, NativeBindingEnumLayout> Read(NativeBindingCatalog catalog, string[] lines)
    {
        IReadOnlyDictionary<string, NativeBindingEnum> expected = Select(catalog);
        var layouts = new SortedDictionary<string, NativeBindingEnumLayout>(StringComparer.Ordinal);
        var constants = new HashSet<(string Enum, string Member)>();
        foreach (string line in lines)
        {
            string[] parts = line.Split('|');
            switch (parts[0])
            {
                case "enum":
                    if (parts.Length != 4 || !expected.ContainsKey(parts[1]) ||
                        !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int size) ||
                        size is not (1 or 2 or 4 or 8) || parts[3] is not ("0" or "1") ||
                        !layouts.TryAdd(parts[1], new(size, parts[3] == "1")))
                    {
                        throw new FormatException($"Invalid or duplicate native enum: {line}");
                    }

                    break;
                case "constant":
                    if (parts.Length != 4 || !layouts.TryGetValue(parts[1], out NativeBindingEnumLayout? layout) ||
                        !expected[parts[1]].Values.TryGetValue(parts[2], out string? expression) ||
                        !BigInteger.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger observed) ||
                        !Fits(observed, layout) || observed != NativeValue(expression, layout) || !constants.Add((parts[1], parts[2])))
                    {
                        throw new FormatException($"Mismatched or duplicate native enum constant: {line}");
                    }

                    break;
            }
        }

        if (layouts.Count != expected.Count || constants.Count != expected.Values.Sum(static declaration => declaration.Values.Count))
        {
            throw new FormatException("Native enum observations are incomplete.");
        }

        return new ReadOnlyDictionary<string, NativeBindingEnumLayout>(layouts);
    }

    /// <summary>
    /// Preserves every catalog constant bit while interpreting its native enum storage signedness.
    /// </summary>
    /// <param name="expression">The pinned constant's exact integer value.</param>
    /// <param name="layout">The selected compiler's integer representation.</param>
    /// <returns>The stored native value, rejecting constants that would require truncation.</returns>
    internal static BigInteger NativeValue(string expression, NativeBindingEnumLayout layout)
    {
        BigInteger modulus = BigInteger.One << (layout.Size * 8);
        if (!BigInteger.TryParse(expression, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger value) ||
            value < -(modulus >> 1) || value >= modulus)
        {
            throw new FormatException($"Native enum constant does not fit its measured width: {expression}.");
        }

        if (layout.IsSigned && value >= modulus >> 1)
        {
            return value - modulus;
        }

        if (!layout.IsSigned && value < 0)
        {
            return value + modulus;
        }

        return value;
    }

    private static bool Fits(BigInteger value, NativeBindingEnumLayout layout)
    {
        BigInteger upper = BigInteger.One << (layout.Size * 8 - (layout.IsSigned ? 1 : 0));
        return value >= (layout.IsSigned ? -upper : BigInteger.Zero) && value < upper;
    }
}
