using System.Globalization;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Emits selected native global accesses beneath the same frame contract as guarded function calls.
/// </summary>
internal static class NativeBindingGlobalSource
{
    /// <summary>
    /// Emits a complete translation unit for independently compiling and executing global accesses.
    /// </summary>
    /// <param name="records">The complete measured declaration contract.</param>
    /// <param name="headers">The selected target's original native headers.</param>
    /// <param name="accesses">Unique global operations to emit.</param>
    /// <returns>Native bodies that still require the caller's PostgreSQL error guard.</returns>
    internal static string Generate(NativeHeaderRecords records, string headers, IReadOnlyList<NativeBindingGlobalAccess> accesses)
        => NativeBindingCallSource.Generate(records, headers, []) + Bodies(records, accesses);

    /// <summary>
    /// Appends global bodies after the common header, target checks and native frame declarations.
    /// </summary>
    /// <param name="records">The complete measured declaration contract.</param>
    /// <param name="accesses">Unique global operations to emit.</param>
    /// <returns>Verified native declarations and deterministic operation bodies.</returns>
    internal static string Bodies(NativeHeaderRecords records, IReadOnlyList<NativeBindingGlobalAccess> accesses)
    {
        ArgumentNullException.ThrowIfNull(accesses);
        IReadOnlyList<NativeBindingGlobalContract> selected = NativeBindingGlobalModel.Select(records,
            [.. accesses.Select(static access => access.Name).Distinct(StringComparer.Ordinal)]);
        Dictionary<string, NativeBindingGlobalContract> globals = selected.ToDictionary(static value => value.Name, StringComparer.Ordinal);
        var unique = new HashSet<NativeBindingGlobalAccess>();
        var source = new StringBuilder(NativeBindingHeaderParser.GenerateChecks("",
            selected.ToDictionary(static value => value.Name, static value => value.Symbol, StringComparer.Ordinal)));
        foreach (NativeBindingGlobalContract global in selected.Where(static value => value.Symbol.Linkage == NativeHeaderLinkage.ImportSlot))
        {
            // The import library defines only this address slot for a variable exported as data.
            source.AppendLine(CultureInfo.InvariantCulture,
                $"extern __typeof__({global.Symbol.NativeName}) *{NativeBindingWindowsImports.SlotIdentifier(global.Symbol.NativeName, records.Graph.Target)};");
        }

        foreach (NativeBindingGlobalAccess access in accesses.OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ThenBy(static value => value.Operation))
        {
            if (!unique.Add(access) || !Enum.IsDefined(access.Operation))
            {
                throw new FormatException("Native global accesses must have unique names and valid operations.");
            }

            NativeBindingGlobalContract global = globals[access.Name];
            bool address = access.Operation == NativeBindingGlobalOperation.Address;
            bool write = access.Operation == NativeBindingGlobalOperation.Write;
            if (!address && !global.IsComplete || write && !global.CanWrite || global.Symbol.Linkage == NativeHeaderLinkage.Unavailable)
            {
                throw new FormatException($"Native global {global.Name} does not permit {access.Operation} access.");
            }

            string reference = global.Symbol.Linkage == NativeHeaderLinkage.ImportSlot
                ? "(*" + NativeBindingWindowsImports.SlotIdentifier(global.Symbol.NativeName, records.Graph.Target) + ")"
                : NativeBindingCompilerShims.Reference(global.Symbol);
            if (!address)
            {
                NativeRecordType storage = records.Graph.Types[global.StorageType];
                source.AppendLine(CultureInfo.InvariantCulture,
                    $"_Static_assert(sizeof({reference}) == {storage.Size!.Value} && _Alignof(__typeof__({reference})) == {storage.Alignment!.Value}, \"Native global storage changed: {global.Name}\");");
            }

            source.AppendLine("#if !defined(_WIN32)\n__attribute__((visibility(\"hidden\")))\n#endif");
            source.Append("int ").Append(BodyName(access)).AppendLine("(const AnkusNativeCallArgument *arguments, size_t count, void *result, size_t result_size)");
            source.AppendLine("{");
            source.AppendLine(write ? "    if (count != 1) return ANKUS_CALL_COUNT;" : "    if (count != 0) return ANKUS_CALL_COUNT;");
            if (write)
            {
                source.AppendLine("    if (arguments == NULL) return ANKUS_CALL_ARGUMENTS;");
                source.AppendLine("    if (result != NULL || result_size != 0) return ANKUS_CALL_RESULT;");
                source.AppendLine(CultureInfo.InvariantCulture, $"    if (arguments[0].data == NULL || arguments[0].size != sizeof({reference})) return ANKUS_CALL_STORAGE;");
                source.AppendLine(CultureInfo.InvariantCulture, $"    if ((uintptr_t)arguments[0].data % _Alignof(__typeof__({reference})) != 0) return ANKUS_CALL_ALIGNMENT;");
            }
            else
            {
                source.AppendLine("    (void)arguments;");
                source.AppendLine(CultureInfo.InvariantCulture, $"    if (result == NULL || result_size != sizeof({(address ? "uintptr_t" : reference)})) return ANKUS_CALL_RESULT;");
            }

            if (address)
            {
                source.AppendLine(CultureInfo.InvariantCulture, $"    uintptr_t value = (uintptr_t)&{reference};");
                source.AppendLine("    memcpy(result, &value, sizeof(value));");
            }
            else if (global.RequiresTypedAccess)
            {
                TypedCopy(source, records.Graph, global.StorageType, reference, "0", write, 0);
            }
            else
            {
                source.AppendLine(write ? $"    memcpy(&{reference}, arguments[0].data, sizeof({reference}));"
                    : $"    memcpy(result, &{reference}, sizeof({reference}));");
            }

            source.AppendLine("    return ANKUS_CALL_OK;\n}");
        }

        return source.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>
    /// Names a body in a separate namespace from fixed native function bodies.
    /// </summary>
    internal static string BodyName(NativeBindingGlobalAccess access)
        => "ankus_native_global_" + OperationName(access.Operation) + "_" + access.Name;

    /// <summary>
    /// Supplies the stable operation component used in native body and pure accessor names.
    /// </summary>
    internal static string OperationName(NativeBindingGlobalOperation operation) => operation switch
    {
        NativeBindingGlobalOperation.Read => "read",
        NativeBindingGlobalOperation.Write => "write",
        NativeBindingGlobalOperation.Address => "address",
        _ => throw new FormatException("Invalid native global access operation."),
    };

    /// <summary>
    /// Lets C perform qualified value loads and stores, then transports their local object bytes without discarding qualifiers.
    /// </summary>
    private static void TypedCopy(StringBuilder source, NativeRecordGraph graph, int index, string expression,
        string offset, bool write, int depth)
    {
        if (depth >= 128)
        {
            throw new FormatException("Native global array nesting exceeds the supported limit.");
        }

        NativeRecordType type = graph.Types[graph.Types[index].Canonical];
        string indent = new(' ', (depth + 1) * 4);
        if (type.Kind == "array")
        {
            string iterator = "index" + depth.ToString(CultureInfo.InvariantCulture);
            string element = expression + "[" + iterator + "]";
            source.AppendLine(CultureInfo.InvariantCulture, $"{indent}for (size_t {iterator} = 0; {iterator} < {type.Count!.Value}; ++{iterator})");
            source.AppendLine(indent + "{");
            TypedCopy(source, graph, type.Element!.Value, element, offset + " + " + iterator + " * sizeof(" + element + ")", write, depth + 1);
            source.AppendLine(indent + "}");
            return;
        }

        source.AppendLine(write ? $"{indent}__typeof__({expression}) value;" : $"{indent}__typeof__({expression}) value = {expression};");
        source.AppendLine(CultureInfo.InvariantCulture, $"{indent}for (size_t position = 0; position < sizeof(value); ++position)");
        source.AppendLine(indent + "{");
        source.AppendLine(write
            ? $"{indent}    ((volatile unsigned char *)&value)[position] = ((const unsigned char *)arguments[0].data)[{offset} + position];"
            : $"{indent}    ((unsigned char *)result)[{offset} + position] = ((const volatile unsigned char *)&value)[position];");
        source.AppendLine(indent + "}");
        if (write)
        {
            source.AppendLine(CultureInfo.InvariantCulture, $"{indent}{expression} = value;");
        }
    }
}

/// <summary>
/// Selects one operation on an observed native object declaration.
/// </summary>
/// <param name="Name">The public inventory name.</param>
/// <param name="Operation">The required native operation.</param>
internal sealed record NativeBindingGlobalAccess(string Name, NativeBindingGlobalOperation Operation);

/// <summary>
/// Distinguishes global value transport from explicitly dangerous native address access.
/// </summary>
internal enum NativeBindingGlobalOperation
{
    /// <summary>
    /// Reads one complete native object value.
    /// </summary>
    Read,
    /// <summary>
    /// Writes one complete mutable native object value.
    /// </summary>
    Write,
    /// <summary>
    /// Obtains the original object address without inventing its extent or lifetime.
    /// </summary>
    Address,
}
