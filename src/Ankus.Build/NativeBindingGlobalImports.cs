using System.Text;

namespace Ankus.Build;

/// <summary>
/// Selects global operations from actual native object imports without rooting unused global storage.
/// </summary>
internal static class NativeBindingGlobalImports
{
    /// <summary>
    /// Separates pure global-body accessors from ordinary native function accessors.
    /// </summary>
    internal const string Prefix = "ankus_native_global_body_";

    /// <summary>
    /// Reads selected global operations from a checked relocatable object image.
    /// </summary>
    /// <param name="image">The consuming Native AOT compiler's object image.</param>
    /// <returns>Ordinally ordered global accesses requested by its undefined imports.</returns>
    internal static IReadOnlyList<NativeBindingGlobalAccess> Select(ReadOnlySpan<byte> image)
    {
        NativeObjectImports imports = NativeObjectSymbols.Read(image, Prefix);
        var accesses = new List<NativeBindingGlobalAccess>(imports.Symbols.Count);
        foreach (string suffix in imports.Symbols.Select(static symbol => symbol[Prefix.Length..]))
        {
            int separator = suffix.IndexOf('_', StringComparison.Ordinal);
            if (separator < 0)
            {
                throw new FormatException("A native global accessor requires an operation and object name.");
            }

            NativeBindingGlobalOperation operation = suffix[..separator] switch
            {
                "read" => NativeBindingGlobalOperation.Read,
                "write" => NativeBindingGlobalOperation.Write,
                "address" => NativeBindingGlobalOperation.Address,
                _ => throw new FormatException("Unknown native global accessor operation."),
            };
            string name = suffix[(separator + 1)..];
            NativeBindingCDeclaration.ValidateName(name);
            accesses.Add(new(name, operation));
        }

        return Array.AsReadOnly(accesses.OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ThenBy(static value => value.Operation).ToArray());
    }

    /// <summary>
    /// Emits selected guarded bodies and their pure address accessors after the common native call declarations.
    /// </summary>
    /// <param name="source">The translation unit with its frame and body-pointer declarations already emitted.</param>
    /// <param name="records">The complete selected-header contract.</param>
    /// <param name="accesses">Validated object imports selecting the required global operations.</param>
    internal static void Append(StringBuilder source, NativeHeaderRecords records, IReadOnlyList<NativeBindingGlobalAccess> accesses)
    {
        source.Append(NativeBindingGlobalSource.Bodies(records, accesses));
        foreach (NativeBindingGlobalAccess access in accesses)
        {
            source.AppendLine("#if !defined(_WIN32)\n__attribute__((visibility(\"hidden\")))\n#endif");
            source.Append("AnkusNativeCallBody ").Append(Prefix).Append(NativeBindingGlobalSource.OperationName(access.Operation))
                .Append('_').Append(access.Name).AppendLine("(void)");
            source.Append("{ return ").Append(NativeBindingGlobalSource.BodyName(access)).AppendLine("; }");
        }
    }
}
