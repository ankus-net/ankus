using System;
using System.Buffers.Binary;
using System.Text;

namespace Ankus;

/// <summary>
/// Computes stable PostgreSQL hash support values from an explicitly chosen value representation.
/// </summary>
/// <remarks>
/// Uses the SeaHash v4 byte-buffer algorithm and pgrx's frozen seeds, returning the signed low
/// 32 bits of the finished hash. The algorithm, seeds and overload encodings are stable across
/// processes and architectures. They do not reproduce Rust's type-specific Hash value encoding.
/// Callers must normalize values that compare equal to identical input, including case, Unicode,
/// floating-point zero or other distinctions ignored by their equality contract. Persist that
/// normalization contract; changing hash results requires rebuilding dependent PostgreSQL indexes.
/// </remarks>
public static class PgHash
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Hashes the exact bytes using SeaHash v4 with fixed seeds and little-endian block interpretation.
    /// </summary>
    /// <param name="value">The caller's equality-normalized byte representation, including any zero bytes.</param>
    /// <returns>The signed low 32 bits of the finished SeaHash value.</returns>
    public static int Compute(ReadOnlySpan<byte> value)
    {
        ulong a = 0x16f11fe89b0d677c;
        ulong b = 0xb480a793d8e6c86c;
        ulong c = 0x6fe2e5aaf078ebc9;
        ulong d = 0x14f994a4c5259381;
        int length = value.Length;
        while (value.Length >= sizeof(ulong))
        {
            ulong mixed = Diffuse(a ^ BinaryPrimitives.ReadUInt64LittleEndian(value));
            a = b;
            b = c;
            c = d;
            d = mixed;
            value = value[sizeof(ulong)..];
        }

        if (!value.IsEmpty)
        {
            ulong tail = 0;
            for (int index = 0; index < value.Length; index++)
            {
                tail |= (ulong)value[index] << (index * 8);
            }

            ulong mixed = Diffuse(a ^ tail);
            a = b;
            b = c;
            c = d;
            d = mixed;
        }

        return unchecked((int)Diffuse(a ^ b ^ c ^ d ^ (ulong)length));
    }

    /// <summary>
    /// Hashes strict UTF-8 bytes without adding a byte-order mark or normalizing the text.
    /// </summary>
    /// <param name="value">The caller's equality-normalized text; embedded zero characters are retained.</param>
    /// <returns>The signed low 32 bits of the finished SeaHash value.</returns>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="EncoderFallbackException">The value contains an unpaired UTF-16 surrogate.</exception>
    public static int Compute(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Compute(s_utf8.GetBytes(value));
    }

    /// <summary>
    /// Hashes an unsigned integer encoded as exactly eight little-endian bytes on every architecture.
    /// </summary>
    /// <param name="value">The caller's equality-normalized unsigned value.</param>
    /// <returns>The signed low 32 bits of the finished SeaHash value.</returns>
    public static int Compute(ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return Compute(bytes);
    }

    /// <summary>
    /// Applies SeaHash's reversible diffusion with explicitly wrapping 64-bit multiplication.
    /// </summary>
    private static ulong Diffuse(ulong value)
    {
        value = unchecked(value * 0x6eed0e9da4d94a4f);
        value ^= value >> 32 >> (int)(value >> 60);
        return unchecked(value * 0x6eed0e9da4d94a4f);
    }
}
