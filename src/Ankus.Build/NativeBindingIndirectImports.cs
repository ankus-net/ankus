using System.Globalization;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Selects finite indirect-call bodies from the consuming Native AOT object's actual imports.
/// </summary>
internal static class NativeBindingIndirectImports
{
    /// <summary>
    /// Separates indirect body addresses from direct-function and native-global imports.
    /// </summary>
    internal const string Prefix = "ankus_native_indirect_body_";

    /// <summary>
    /// Reads canonical signature identifiers without accepting alternate spellings or narrowing oversized indices.
    /// </summary>
    internal static IReadOnlyList<int> Select(ReadOnlySpan<byte> image)
    {
        NativeObjectImports imports = NativeObjectSymbols.Read(image, Prefix);
        var signatures = new List<int>(imports.Symbols.Count);
        foreach (string suffix in imports.Symbols.Select(static symbol => symbol[Prefix.Length..]))
        {
            if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int signature) ||
                signature.ToString(CultureInfo.InvariantCulture) != suffix)
            {
                throw new FormatException("An indirect native import requires a canonical signature index.");
            }

            signatures.Add(signature);
        }

        signatures.Sort();
        return signatures.AsReadOnly();
    }

    /// <summary>
    /// Emits selected guarded bodies and pure address accessors after the common native frame declarations.
    /// </summary>
    internal static void Append(StringBuilder source, NativeHeaderRecords records, IReadOnlyList<int> signatures)
    {
        source.Append(NativeBindingIndirectSource.Bodies(records, signatures));
        foreach (int signature in signatures)
        {
            source.AppendLine("#if !defined(_WIN32)\n__attribute__((visibility(\"hidden\")))\n#endif");
            source.Append("AnkusNativeCallBody ").Append(Prefix).Append(signature.ToString(CultureInfo.InvariantCulture)).AppendLine("(void)");
            source.Append("{ return ").Append(NativeBindingIndirectSource.BodyName(signature)).AppendLine("; }");
        }
    }
}
