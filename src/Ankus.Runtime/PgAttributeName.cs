using System;
using System.Buffers;
using System.Text;

namespace Ankus;

/// <summary>
/// Converts attribute names between the native transport and managed strings. A SQL_ASCII database stores name bytes
/// PostgreSQL never checks; each byte of a name that is not valid UTF-8 reads as an unpaired low surrogate from
/// U+DC80 to U+DCFF, so the name reads and writes back exactly.
/// </summary>
internal static class PgAttributeName
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes a transported name, restoring each escaped byte as its surrogate.
    /// </summary>
    /// <param name="bytes">The name as UTF-8, with escaped bytes in their three-byte form.</param>
    /// <returns>The managed name.</returns>
    internal static string Decode(ReadOnlySpan<byte> bytes)
    {
        int index = FindEscape(bytes, 0);
        if (index < 0)
        {
            return s_utf8.GetString(bytes);
        }

        var builder = new StringBuilder(bytes.Length);
        int start = 0;
        while (index >= 0)
        {
            builder.Append(s_utf8.GetString(bytes[start..index]));
            int original = (bytes[index + 1] == 0xB2 ? 0x80 : 0xC0) | (bytes[index + 2] & 0x3F);
            builder.Append((char)(0xDC00 | original));
            start = index + 3;
            index = FindEscape(bytes, start);
        }

        builder.Append(s_utf8.GetString(bytes[start..]));
        return builder.ToString();
    }

    /// <summary>
    /// Encodes a managed name for the native transport, writing each surrogate that stands for a byte in its
    /// three-byte form.
    /// </summary>
    /// <param name="name">The managed name.</param>
    /// <returns>The transported bytes.</returns>
    /// <exception cref="EncoderFallbackException">The name holds other text that is not valid UTF-16.</exception>
    internal static byte[] Encode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int index = FindByte(name, 0);
        if (index < 0)
        {
            return s_utf8.GetBytes(name);
        }

        var buffer = new ArrayBufferWriter<byte>(name.Length * 3);
        int start = 0;
        while (index >= 0)
        {
            Append(buffer, name.AsSpan(start, index - start));
            int original = name[index] & 0xFF;
            Span<byte> escape = buffer.GetSpan(3);
            escape[0] = 0xED;
            escape[1] = original < 0xC0 ? (byte)0xB2 : (byte)0xB3;
            escape[2] = (byte)(0x80 | (original & 0x3F));
            buffer.Advance(3);
            start = index + 1;
            index = FindByte(name, start);
        }

        Append(buffer, name.AsSpan(start));
        return buffer.WrittenSpan.ToArray();
    }

    private static int FindEscape(ReadOnlySpan<byte> bytes, int start)
    {
        // Valid UTF-8 never holds 0xED followed by 0xA0 to 0xBF, and a lead byte never follows inside a character.
        for (int index = start; index + 2 < bytes.Length; index++)
        {
            if (bytes[index] == 0xED && bytes[index + 1] is 0xB2 or 0xB3 && (bytes[index + 2] & 0xC0) == 0x80)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindByte(string name, int start)
    {
        for (int index = start; index < name.Length; index++)
        {
            if (name[index] is >= '\uDC80' and <= '\uDCFF' && (index == 0 || !char.IsHighSurrogate(name[index - 1])))
            {
                return index;
            }
        }

        return -1;
    }

    private static void Append(ArrayBufferWriter<byte> buffer, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        int length = s_utf8.GetByteCount(text);
        buffer.Advance(s_utf8.GetBytes(text, buffer.GetSpan(length)));
    }
}
