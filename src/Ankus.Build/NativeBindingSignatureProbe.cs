using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Verifies requested function prototypes and measures their native storage against selected headers.
/// </summary>
internal static class NativeBindingSignatureProbe
{
    /// <summary>
    /// Emits a probe which checks complete function-pointer types without calling backend functions.
    /// </summary>
    /// <param name="catalog">The selected major's named type declarations.</param>
    /// <param name="raw">The selected major's foreign declarations.</param>
    /// <param name="names">The exact native functions needed by a binding consumer.</param>
    /// <param name="headers">The selected major's include manifest.</param>
    /// <returns>C source which fails compilation on incompatible prototypes.</returns>
    internal static string GenerateSource(NativeBindingCatalog catalog, NativeBindingRawCatalog raw,
        IReadOnlyList<string> names, string headers)
    {
        ReadOnlyDictionary<string, NativeBindingFunction> functions = Select(catalog, raw, names);
        var source = new StringBuilder(NativeProbeHeaders.Wrap(headers));
        source.AppendLine();
        source.AppendLine("#include <stdint.h>");
        source.AppendLine("#include <limits.h>");
        source.AppendLine("#include <stdio.h>");
        source.AppendLine("#undef printf");
        source.AppendLine("#if defined(_MSC_VER)");
        source.AppendLine("#define ANKUS_SIGNATURE_ALIGNOF(type) __alignof(type)");
        source.AppendLine("#else");
        source.AppendLine("#define ANKUS_SIGNATURE_ALIGNOF(type) __alignof__(type)");
        source.AppendLine("#endif");
        NativeBindingTarget.Write(source);
        source.AppendLine(CultureInfo.InvariantCulture, $"#if PG_VERSION_NUM / 10000 != {catalog.PostgresMajor}");
        source.AppendLine("#error PostgreSQL headers do not match the requested signature major");
        source.AppendLine("#endif");
        foreach ((string name, NativeBindingFunction function) in functions)
        {
            string prefix = "ankus_signature_" + name;
            source.Append("typedef ").Append(NativeBindingCDeclaration.FunctionPointer(catalog, function, prefix)).AppendLine(";");
            string target = function.NativeSymbol == name + "__pgrx_cshim" ? name : function.NativeSymbol;
            // Validate linkage as an identifier before placing it in native source.
            _ = NativeBindingCDeclaration.Value(catalog, "u8", target);
            source.AppendLine(CultureInfo.InvariantCulture,
                $"_Static_assert(_Generic(&{target}, {prefix}: 1, default: 0), \"incompatible native signature: {name}\");");
            for (int index = 0; index < function.Parameters.Count; index++)
            {
                source.Append("typedef ").Append(NativeBindingCDeclaration.Value(catalog, function.Parameters[index].Representation,
                    prefix + "_arg" + index.ToString(CultureInfo.InvariantCulture))).AppendLine(";");
            }

            if (HasResult(function))
            {
                source.Append("typedef ").Append(NativeBindingCDeclaration.Value(catalog, function.ReturnType, prefix + "_result")).AppendLine(";");
            }
        }

        source.AppendLine("int main(void)");
        source.AppendLine("{");
        source.AppendLine("    unsigned int endian = 1;");
        source.AppendLine("    printf(\"signatures|1|%d|%zu|%d|%s-%s\\n\", PG_VERSION_NUM, sizeof(void*), *((unsigned char*)&endian) == 1, ANKUS_NATIVE_OS, ANKUS_NATIVE_ARCH);");
        foreach ((string name, NativeBindingFunction function) in functions)
        {
            for (int index = 0; index < function.Parameters.Count; index++)
            {
                WriteValue(name, index.ToString(CultureInfo.InvariantCulture));
            }

            if (HasResult(function))
            {
                WriteValue(name, "result");
            }
        }

        source.AppendLine("    return 0;");
        source.AppendLine("}");
        return source.ToString();

        void WriteValue(string name, string index)
        {
            string type = "ankus_signature_" + name + "_" + (index == "result" ? index : "arg" + index);
            source.AppendLine(CultureInfo.InvariantCulture,
                $"    printf(\"value|{name}|{index}|%zu|%zu\\n\", sizeof({type}), (size_t)ANKUS_SIGNATURE_ALIGNOF({type}));");
        }
    }

    /// <summary>
    /// Validates exact observations for the requested signatures before writing a measured call contract.
    /// </summary>
    /// <param name="catalog">The selected major's named types.</param>
    /// <param name="raw">The selected major's foreign declarations.</param>
    /// <param name="names">The exact requested function set.</param>
    /// <param name="output">A successful probe's complete standard output.</param>
    /// <returns>Complete native parameter and result storage measurements.</returns>
    internal static NativeBindingSignatures Read(NativeBindingCatalog catalog, NativeBindingRawCatalog raw,
        IReadOnlyList<string> names, string output)
    {
        ReadOnlyDictionary<string, NativeBindingFunction> functions = Select(catalog, raw, names);
        string[] lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string[] header = lines.Length == 0 ? [] : lines[0].Split('|');
        if (header.Length != 6 || header[0] != "signatures" || header[1] != "1" ||
            !int.TryParse(header[2], NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version / 10000 != catalog.PostgresMajor ||
            !int.TryParse(header[3], NumberStyles.None, CultureInfo.InvariantCulture, out int width) || header[4] is not ("0" or "1") ||
            !NativeBindingTarget.IsValid(header[5], width, header[4] == "1"))
        {
            throw new FormatException("Missing or incompatible native signature header.");
        }

        var observed = new Dictionary<(string Name, string Index), NativeBindingSignatureValue>();
        foreach (string[] fields in lines.Skip(1).Select(static line => line.Split('|')))
        {
            if (fields.Length != 5 || fields[0] != "value" || !functions.TryGetValue(fields[1], out NativeBindingFunction? function) ||
                !(fields[2] == "result" ? HasResult(function) : int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
                    fields[2] == index.ToString(CultureInfo.InvariantCulture) && index < function.Parameters.Count) ||
                !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out int size) || size <= 0 ||
                !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out int alignment) || alignment <= 0 ||
                (alignment & (alignment - 1)) != 0 || size % alignment != 0 ||
                !observed.TryAdd((fields[1], fields[2]), new(size, alignment)))
            {
                throw new FormatException("Invalid, unexpected or duplicate native signature observation.");
            }
        }

        var measured = new SortedDictionary<string, NativeBindingFunctionSignature>(StringComparer.Ordinal);
        foreach ((string name, NativeBindingFunction function) in functions)
        {
            NativeBindingSignatureValue[] parameters = [.. Enumerable.Range(0, function.Parameters.Count)
                .Select(index => Required(name, index.ToString(CultureInfo.InvariantCulture)))];
            measured.Add(name, new(Array.AsReadOnly(parameters), HasResult(function) ? Required(name, "result") : null,
                function.IsVariadic, NativeBindingSelection.ResolveAlias(catalog, function.ReturnType) == "!"));
        }

        return new(version, width, header[4] == "1", header[5], new ReadOnlyDictionary<string, NativeBindingFunctionSignature>(measured));

        NativeBindingSignatureValue Required(string name, string index)
            => observed.TryGetValue((name, index), out NativeBindingSignatureValue? value) ? value
                : throw new FormatException($"Missing native signature observation for {name} {index}.");
    }

    private static bool HasResult(NativeBindingFunction function)
        => NativeBindingParser.MaskTrivia(function.ReturnType).Trim() is not ("()" or "!");

    private static ReadOnlyDictionary<string, NativeBindingFunction> Select(NativeBindingCatalog catalog, NativeBindingRawCatalog raw,
        IReadOnlyList<string> names)
    {
        if (catalog.PostgresMajor != raw.PostgresMajor)
        {
            throw new FormatException("Native type and signature catalogs have different majors.");
        }

        var selected = new SortedDictionary<string, NativeBindingFunction>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!raw.Functions.TryGetValue(name, out NativeBindingFunction? function) || !selected.TryAdd(name, function))
            {
                throw new FormatException($"Unknown or duplicate native function '{name}'.");
            }
        }

        return new ReadOnlyDictionary<string, NativeBindingFunction>(selected);
    }
}

/// <summary>
/// Records function storage measured from a selected installation, without asserting call or error-boundary availability.
/// </summary>
/// <param name="PostgresVersion">The observed PG_VERSION_NUM.</param>
/// <param name="PointerSize">The native pointer width.</param>
/// <param name="IsLittleEndian">The native byte order.</param>
/// <param name="RuntimeIdentifier">The measured operating-system and processor ABI.</param>
/// <param name="Functions">The complete requested function set.</param>
internal sealed record NativeBindingSignatures(int PostgresVersion, int PointerSize, bool IsLittleEndian,
    string RuntimeIdentifier, IReadOnlyDictionary<string, NativeBindingFunctionSignature> Functions);

/// <summary>
/// Retains the selected header's argument and result storage for one verified C prototype.
/// </summary>
/// <param name="Parameters">Ordered fixed parameter storage.</param>
/// <param name="Result">Result storage, or null for void and non-returning functions.</param>
/// <param name="IsVariadic">Whether caller-specific promoted arguments remain necessary.</param>
/// <param name="DoesNotReturn">Whether the reference declaration is non-returning.</param>
internal sealed record NativeBindingFunctionSignature(IReadOnlyList<NativeBindingSignatureValue> Parameters,
    NativeBindingSignatureValue? Result, bool IsVariadic, bool DoesNotReturn);

/// <summary>
/// Describes storage for one C type; this is not a managed calling-convention classification.
/// </summary>
/// <param name="Size">The exact native sizeof value.</param>
/// <param name="Alignment">The native alignment requirement.</param>
internal sealed record NativeBindingSignatureValue(int Size, int Alignment);
