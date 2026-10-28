using System;
using System.ComponentModel;
using System.Linq;

namespace Ankus.CompilerServices;

/// <summary>
/// Holds a generated string setting's last conversion and the server-encoded bytes it came from.
/// </summary>
/// <remarks>
/// The conversion is held weakly, so a setting's value, which can be large, is collected once nothing else uses it;
/// reads until the next collection reuse it. A property reads its setting on the backend thread only, so the cache
/// needs no synchronization.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class NativeGucText
{
    private readonly WeakReference<Conversion?> _last = new(null);

    /// <summary>
    /// Returns the last conversion when it is still alive and came from the same bytes.
    /// </summary>
    internal bool TryGet(ReadOnlySpan<byte> current, out string? value)
    {
        if (_last.TryGetTarget(out Conversion? conversion) && conversion is not null && current.SequenceEqual(conversion.Bytes))
        {
            value = conversion.Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Records a conversion and a copy of the bytes it came from.
    /// </summary>
    internal void Set(ReadOnlySpan<byte> current, string? value) => _last.SetTarget(new Conversion(current.ToArray(), value));

    /// <summary>
    /// Pairs a converted value with its source bytes so that both are collected together.
    /// </summary>
    /// <param name="bytes">The server-encoded bytes.</param>
    /// <param name="value">The converted string.</param>
    private sealed class Conversion(byte[] bytes, string? value)
    {
        internal byte[] Bytes => bytes;

        internal string? Value => value;
    }
}
