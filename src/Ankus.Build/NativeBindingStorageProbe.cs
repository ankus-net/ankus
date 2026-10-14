using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ankus.Build;

/// <summary>
/// Measures native value storage from collected header types without guessing a managed calling convention.
/// </summary>
internal static class NativeBindingStorageProbe
{
    /// <summary>
    /// Emits a native probe for every fixed parameter, non-void result and requested global.
    /// </summary>
    internal static string GenerateSource(NativeHeaderCatalog catalog, string headers)
    {
        Dictionary<(string Name, string Slot), NativeHeaderType> values = Select(catalog);
        var source = new StringBuilder(NativeBindingHeaderTarget.GenerateSource(headers, catalog.Target.PostgresVersion / 10000));
        source.AppendLine("#if !defined(__clang__)");
        source.AppendLine("#error Native storage measurement requires the selected Clang frontend");
        source.AppendLine("#endif");
        source.Append(NativeBindingHeaderParser.GenerateChecks("", catalog.Symbols));
        foreach (((string name, string slot), NativeHeaderType type) in values)
        {
            source.Append("typedef ").Append(type.Declare(Alias(name, slot))).AppendLine(";");
        }

        foreach (((string name, string slot), NativeHeaderType type) in values)
        {
            string alias = Alias(name, slot);
            NativeHeaderType canonical = Canonical(type);
            bool array = canonical is NativeHeaderArray;
            bool opaque = IsOpaque(canonical);
            bool incomplete = opaque || canonical is NativeHeaderArray { Count: null };
            bool integer = !opaque && IsInteger(canonical);
            source.AppendLine("enum {");
            source.Append(alias).Append("_size = ").Append(incomplete ? "-1" : $"sizeof({alias})").AppendLine(",");
            source.Append(alias).Append("_alignment = ").Append(opaque ? "-1" : $"_Alignof({alias})").AppendLine(",");
            source.Append(alias).Append("_element = ").Append(array ? $"sizeof((*({alias}*)0)[0])" : "-1").AppendLine(",");
            source.Append(alias).Append("_signed = ").Append(integer ? $"(({alias})-1 < ({alias})0)" : "-1").AppendLine();
            source.AppendLine("};");
        }

        return source.ToString();
    }

    /// <summary>
    /// Reads compiler-evaluated integer constants without executing code for the selected target.
    /// </summary>
    internal static string ReadObservations(NativeHeaderCatalog catalog, JsonElement root)
    {
        Dictionary<(string Name, string Slot), NativeHeaderType> values = Select(catalog);
        NativeHeaderTarget target = NativeBindingHeaderTarget.Read(root, catalog.Target.PostgresVersion / 10000);
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (JsonElement node in root.GetProperty("inner").EnumerateArray())
            {
                if (node.GetProperty("kind").GetString() != "EnumDecl" || !node.TryGetProperty("inner", out JsonElement members))
                {
                    continue;
                }

                foreach (JsonElement member in members.EnumerateArray())
                {
                    if (!member.TryGetProperty("name", out JsonElement nameProperty))
                    {
                        continue;
                    }

                    string name = nameProperty.GetString() ?? throw new FormatException("Missing native storage constant name.");
                    if (!name.StartsWith("ankus_storage_", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (member.GetProperty("kind").GetString() != "EnumConstantDecl")
                    {
                        throw new FormatException("Invalid native storage constant declaration.");
                    }

                    JsonElement expression = member;
                    while (expression.GetProperty("kind").GetString() != "ConstantExpr")
                    {
                        JsonElement inner = expression.GetProperty("inner");
                        if (inner.GetArrayLength() != 1)
                        {
                            throw new FormatException("Missing native storage constant expression.");
                        }

                        expression = inner[0];
                    }

                    string value = expression.GetProperty("value").GetString() ?? throw new FormatException("Missing native storage constant value.");
                    if (!constants.TryAdd(name, value == "-1" ? "-" : value))
                    {
                        throw new FormatException("Duplicate native storage constant.");
                    }
                }
            }

            var output = new StringBuilder();
            output.AppendLine(CultureInfo.InvariantCulture,
                $"storage|2|{target.PostgresVersion}|{target.PointerSize}|{(target.IsLittleEndian ? 1 : 0)}|{target.RuntimeIdentifier}|{target.ClangMajor}|{NativeBindingNumericModel.Encode(target.Numeric)}");
            foreach ((string name, string slot) in values.Keys)
            {
                string alias = Alias(name, slot);
                output.Append("value|").Append(name).Append('|').Append(slot);
                foreach (string suffix in new[] { "_size", "_alignment", "_element", "_signed" })
                {
                    if (!constants.Remove(alias + suffix, out string? value))
                    {
                        throw new FormatException("Missing native storage constant.");
                    }

                    output.Append('|').Append(value);
                }

                output.AppendLine();
            }

            if (constants.Count != 0)
            {
                throw new FormatException("Unexpected native storage constant.");
            }

            return output.ToString();
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("Malformed native storage constant observation.", exception);
        }
    }

    /// <summary>
    /// Requires complete, consistent storage observations from the same header and compiler target.
    /// </summary>
    internal static NativeHeaderStorage Read(NativeHeaderCatalog catalog, string output)
    {
        Dictionary<(string Name, string Slot), NativeHeaderType> values = Select(catalog);
        string[] lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string[] header = lines.Length == 0 ? [] : lines[0].Split('|');
        if (header.Length != 8 || header[0] != "storage" || header[1] != "2" ||
            !int.TryParse(header[2], NumberStyles.None, CultureInfo.InvariantCulture, out int version) ||
            !int.TryParse(header[3], NumberStyles.None, CultureInfo.InvariantCulture, out int width) ||
            header[4] is not ("0" or "1") ||
            !int.TryParse(header[6], NumberStyles.None, CultureInfo.InvariantCulture, out int compiler) ||
            new NativeHeaderTarget(version, header[5], width, header[4] == "1", compiler, catalog.Target.Numeric) != catalog.Target ||
            header[7] != NativeBindingNumericModel.Encode(catalog.Target.Numeric))
        {
            throw new FormatException("Native storage observations do not match the collected header target.");
        }

        var observed = new Dictionary<(string Name, string Slot), NativeHeaderValueStorage>();
        foreach (string[] fields in lines.Skip(1).Select(static line => line.Split('|')))
        {
            if (fields.Length != 7 || fields[0] != "value" ||
                !values.TryGetValue((fields[1], fields[2]), out NativeHeaderType? type) ||
                observed.ContainsKey((fields[1], fields[2])))
            {
                throw new FormatException("Unexpected or duplicate native storage observation.");
            }

            NativeHeaderType canonical = Canonical(type);
            NativeHeaderArray? array = canonical as NativeHeaderArray;
            bool opaque = IsOpaque(canonical);
            ulong? size = Number(fields[3]);
            ulong? alignment = Number(fields[4]);
            ulong? elementSize = Number(fields[5]);
            bool? isSigned = fields[6] switch
            {
                "-" => null,
                "0" => false,
                "1" => true,
                _ => throw new FormatException("Invalid native integer signedness."),
            };
            // Opaque types have no layout, and only an incomplete array has no size.
            bool alignmentValid = alignment is not ulong aligned || aligned != 0 && (aligned & (aligned - 1)) == 0;
            bool storageValid = (alignment is null) == opaque && alignmentValid &&
                (size is null) == (opaque || array is { Count: null }) && (size != 0 || AllowsEmpty(canonical));
            bool elementValid = (elementSize is not null) == (array is not null) &&
                (elementSize != 0 || array is null || AllowsEmpty(Canonical(array.Element)));
            bool signValid = isSigned.HasValue == (!opaque && IsInteger(canonical)) &&
                (KnownSignedness(canonical) is not bool expectedSign || isSigned == expectedSign) &&
                (canonical is not NativeHeaderScalar { Name: "char" } || isSigned == catalog.Target.Numeric.CharIsSigned);
            bool pointerValid = canonical is not NativeHeaderPointer || size == (ulong)catalog.Target.PointerSize;
            if (!storageValid || !elementValid || !signValid || !pointerValid)
            {
                throw new FormatException("Inconsistent native value storage.");
            }

            if (array?.Count is ulong count &&
                (count != 0 && elementSize > ulong.MaxValue / count || size != count * elementSize))
            {
                throw new FormatException("Native array extent and element stride disagree.");
            }

            observed.Add((fields[1], fields[2]), new(size, alignment, elementSize, isSigned));
        }

        if (observed.Count != values.Count)
        {
            throw new FormatException("Missing native storage observations.");
        }

        var symbols = new SortedDictionary<string, NativeHeaderSymbolStorage>(StringComparer.Ordinal);
        foreach ((string name, NativeHeaderSymbol symbol) in catalog.Symbols)
        {
            if (symbol.IsFunction)
            {
                var function = (NativeHeaderFunction)Canonical(symbol.Type);
                NativeHeaderValueStorage[] parameters = [.. Enumerable.Range(0, function.Parameters.Count)
                    .Select(index => observed[(name, index.ToString(CultureInfo.InvariantCulture))])];
                symbols.Add(name, new(Array.AsReadOnly(parameters),
                    IsVoid(function.Result) ? null : observed[(name, "result")], null));
            }
            else
            {
                symbols.Add(name, new([], null, observed[(name, "global")]));
            }
        }

        return new(catalog, new ReadOnlyDictionary<string, NativeHeaderSymbolStorage>(symbols));
    }

    private static Dictionary<(string Name, string Slot), NativeHeaderType> Select(NativeHeaderCatalog catalog)
    {
        NativeHeaderTarget target = catalog.Target;
        if (target.PostgresVersion / 10000 is < 13 or > 19 || target.ClangMajor <= 0 ||
            !NativeBindingTarget.IsValid(target.RuntimeIdentifier, target.PointerSize, target.IsLittleEndian))
        {
            throw new FormatException("Invalid collected native target.");
        }

        var values = new Dictionary<(string Name, string Slot), NativeHeaderType>();
        foreach ((string name, NativeHeaderSymbol symbol) in catalog.Symbols.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            NativeBindingCDeclaration.ValidateName(name);
            NativeBindingCDeclaration.ValidateName(symbol.NativeName);
            if (symbol.IsFunction)
            {
                if (Canonical(symbol.Type) is not NativeHeaderFunction function)
                {
                    throw new FormatException("A native function requires its collected function type.");
                }

                for (int index = 0; index < function.Parameters.Count; index++)
                {
                    Add(name, index.ToString(CultureInfo.InvariantCulture), function.Parameters[index]);
                }

                if (!IsVoid(function.Result))
                {
                    Add(name, "result", function.Result);
                }
            }
            else
            {
                Add(name, "global", symbol.Type);
            }
        }

        return values;

        void Add(string name, string slot, NativeHeaderType type)
        {
            if (IsVoid(type) || Canonical(type) is NativeHeaderFunction)
            {
                throw new FormatException("Native storage requires an object type.");
            }

            values.Add((name, slot), type);
        }
    }

    private static NativeHeaderType Canonical(NativeHeaderType type)
    {
        while (true)
        {
            switch (type)
            {
                case NativeHeaderAlias alias:
                    type = alias.Underlying;
                    break;
                case NativeHeaderQualified qualified:
                    type = qualified.Underlying;
                    break;
                case NativeHeaderAdjusted adjusted:
                    type = adjusted.Adjusted;
                    break;
                default:
                    return type;
            }
        }
    }

    private static bool IsVoid(NativeHeaderType type) => Canonical(type) is NativeHeaderScalar { Name: "void" };

    private static bool IsOpaque(NativeHeaderType type)
        => type is NativeHeaderRecord { IsComplete: false } or NativeHeaderEnum { IsComplete: false };

    private static bool AllowsEmpty(NativeHeaderType type)
        => type is NativeHeaderRecord or NativeHeaderArray { Count: 0 };

    private static bool IsInteger(NativeHeaderType type)
        => type is NativeHeaderEnum || type is NativeHeaderScalar scalar &&
            scalar.Name is not ("void" or "float" or "double" or "long double" or "_Float16" or "__bf16" or "__float128" or "__fp16");

    private static bool? KnownSignedness(NativeHeaderType type)
        => type is not NativeHeaderScalar scalar || !IsInteger(type) || scalar.Name == "char"
            ? null : scalar.Name is not ("bool" or "_Bool") && !scalar.Name.StartsWith("unsigned", StringComparison.Ordinal);

    private static ulong? Number(string value)
        => value == "-" ? null : ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number)
            ? number : throw new FormatException("Invalid native storage extent.");

    private static string Alias(string name, string slot) => "ankus_storage_" + name + "_" + slot;
}

/// <summary>
/// Couples authoritative native types with storage measurements from the same selected target.
/// </summary>
/// <param name="Headers">The exact collected header types and compiler identity.</param>
/// <param name="Symbols">Complete measurements for the requested functions and globals.</param>
internal sealed record NativeHeaderStorage(NativeHeaderCatalog Headers, IReadOnlyDictionary<string, NativeHeaderSymbolStorage> Symbols);

/// <summary>
/// Distinguishes ordered function values from a global object's storage.
/// </summary>
/// <param name="Parameters">The fixed, C-adjusted function parameters; empty for globals.</param>
/// <param name="Result">The non-void result, or null.</param>
/// <param name="Global">The global object's storage, or null for functions.</param>
internal sealed record NativeHeaderSymbolStorage(IReadOnlyList<NativeHeaderValueStorage> Parameters,
    NativeHeaderValueStorage? Result, NativeHeaderValueStorage? Global);

/// <summary>
/// Retains native object size, alignment, array stride and integer signedness without managed ABI classification.
/// </summary>
/// <param name="Size">The native byte size, or null for an incomplete outer array or opaque tag.</param>
/// <param name="Alignment">The compiler's alignment requirement, which can exceed a typedef's size, or null for an opaque tag.</param>
/// <param name="ElementSize">Array element stride, or null for a non-array.</param>
/// <param name="IsSigned">Integer or enum signedness, or null for other representations.</param>
internal sealed record NativeHeaderValueStorage(ulong? Size, ulong? Alignment, ulong? ElementSize, bool? IsSigned);
