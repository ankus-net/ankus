using System;
using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;

namespace Ankus;

/// <summary>
/// Escapes generated custom-type JSON text like pgrx's serde_json compact formatter.
/// </summary>
/// <remarks>
/// Only quotation marks, reverse solidus and C0 control characters are escaped. Other scalars, including non-ASCII
/// text, HTML-sensitive punctuation and DEL, are written as UTF-8. Controls use serde_json's short escapes where JSON
/// defines one and otherwise lowercase <c>\u00xx</c> escapes. Callers reject unpaired surrogates before writing.
/// </remarks>
internal sealed class PgJsonTextEncoder : JavaScriptEncoder
{
    private const string HexDigits = "0123456789abcdef";

    /// <summary>
    /// Gets the immutable encoder shared by generated JSON writers.
    /// </summary>
    internal static PgJsonTextEncoder Instance { get; } = new();

    /// <summary>
    /// Gets the longest escape, a reverse solidus, <c>u</c> and four hexadecimal digits.
    /// </summary>
    public override int MaxOutputCharactersPerInputCharacter => 6;

    /// <summary>
    /// Selects the scalars that JSON syntax requires to be escaped.
    /// </summary>
    /// <param name="unicodeScalar">The scalar to inspect.</param>
    /// <returns>Whether the scalar is escaped.</returns>
    public override bool WillEncode(int unicodeScalar)
        => !Rune.IsValid(unicodeScalar) || unicodeScalar is >= 0 and < 0x20 or '"' or '\\';

    /// <summary>
    /// Finds the first scalar that needs an escape, or a malformed UTF-16 sequence.
    /// </summary>
    /// <param name="text">The UTF-16 source.</param>
    /// <param name="textLength">The number of source characters.</param>
    /// <returns>The first offset to encode, or minus one.</returns>
    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        ReadOnlySpan<char> source = new(text, textLength);
        int index = 0;
        while (index < source.Length)
        {
            if (Rune.DecodeFromUtf16(source[index..], out Rune scalar, out int consumed) != OperationStatus.Done || WillEncode(scalar.Value))
            {
                return index;
            }

            index += consumed;
        }

        return -1;
    }

    /// <summary>
    /// Writes a scalar unchanged or as serde_json's escape.
    /// </summary>
    /// <param name="unicodeScalar">The scalar to write.</param>
    /// <param name="buffer">The destination.</param>
    /// <param name="bufferLength">The destination capacity.</param>
    /// <param name="numberOfCharactersWritten">The number of characters written.</param>
    /// <returns>Whether the scalar or its escape fits.</returns>
    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        numberOfCharactersWritten = 0;
        Span<char> destination = new(buffer, bufferLength);
        if (!Rune.TryCreate(unicodeScalar, out Rune scalar))
        {
            return false;
        }

        if (!WillEncode(unicodeScalar))
        {
            return scalar.TryEncodeToUtf16(destination, out numberOfCharactersWritten);
        }

        char shortEscape = unicodeScalar switch
        {
            '"' => '"',
            '\\' => '\\',
            '\b' => 'b',
            '\t' => 't',
            '\n' => 'n',
            '\f' => 'f',
            '\r' => 'r',
            _ => '\0',
        };
        int length = shortEscape == '\0' ? 6 : 2;
        if (destination.Length < length)
        {
            return false;
        }

        destination[0] = '\\';
        if (length == 2)
        {
            destination[1] = shortEscape;
        }
        else
        {
            destination[1] = 'u';
            destination[2] = '0';
            destination[3] = '0';
            destination[4] = HexDigits[unicodeScalar >> 4];
            destination[5] = HexDigits[unicodeScalar & 0xF];
        }

        numberOfCharactersWritten = length;
        return true;
    }
}
