using System.Globalization;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Selects statically generated managed callback wrappers from actual Native AOT imports.
/// </summary>
internal static class NativeBindingCallbackImports
{
    /// <summary>
    /// Identifies static callback registration accessors independently of indirect invocation bodies.
    /// </summary>
    internal const string Prefix = "ankus_native_callback_";

    /// <summary>
    /// Reads canonical native signatures and deterministic managed target identities without guessing malformed names.
    /// </summary>
    internal static IReadOnlyList<NativeBindingCallbackImport> Select(ReadOnlySpan<byte> image)
    {
        NativeObjectImports imports = NativeObjectSymbols.Read(image, Prefix);
        var callbacks = new List<NativeBindingCallbackImport>(imports.Symbols.Count);
        foreach (string suffix in imports.Symbols.Select(static symbol => symbol[Prefix.Length..]))
        {
            int separator = suffix.IndexOf('_');
            if (separator <= 0 || !int.TryParse(suffix.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out int signature) ||
                signature.ToString(CultureInfo.InvariantCulture) != suffix[..separator] || !ValidIdentity(suffix[(separator + 1)..]))
            {
                throw new FormatException("A native callback import requires a canonical signature and managed target identity.");
            }

            callbacks.Add(new(signature, suffix[(separator + 1)..]));
        }

        return callbacks.OrderBy(static callback => callback.Signature).ThenBy(static callback => callback.Identity, StringComparer.Ordinal).ToList().AsReadOnly();
    }

    /// <summary>
    /// Appends only selected static native wrappers after the common frame declarations and original headers.
    /// </summary>
    internal static void Append(StringBuilder source, NativeHeaderRecords records, IReadOnlyList<NativeBindingCallbackImport> callbacks)
        => source.Append(NativeBindingCallbackSource.Generate(records, callbacks));

    /// <summary>
    /// Restricts linker identities to the deterministic 128-bit lowercase hash emitted by the source generator.
    /// </summary>
    internal static bool ValidIdentity(string identity)
        => identity is { Length: 32 } && identity.All(static value => value is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Associates one module-owned managed target with its exact canonical native prototype.
/// </summary>
/// <param name="Signature">The canonical native function type index.</param>
/// <param name="Identity">The deterministic managed handler identity.</param>
internal sealed record NativeBindingCallbackImport(int Signature, string Identity);
