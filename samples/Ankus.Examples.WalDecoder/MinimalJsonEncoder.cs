using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;

namespace Ankus.Examples.WalDecoder;

/// <summary>
/// Escapes only JSON syntax and ASCII control characters and writes every other character as it is.
/// </summary>
internal sealed class MinimalJsonEncoder : JavaScriptEncoder
{
    /// <summary>
    /// Gets the immutable JSON text encoder shared by the sample's writers.
    /// </summary>
    internal static MinimalJsonEncoder Instance { get; } = new();

    /// <summary>
    /// Gets the largest ASCII control escape, including its reverse solidus and four hexadecimal digits.
    /// </summary>
    public override int MaxOutputCharactersPerInputCharacter => 6;

    /// <summary>
    /// Checks the exact escape set used by serde_json's compact formatter.
    /// </summary>
    /// <param name="unicodeScalar">The scalar to inspect.</param>
    /// <returns>Whether JSON requires that scalar to be escaped.</returns>
    public override bool WillEncode(int unicodeScalar)
        => !Rune.IsValid(unicodeScalar) || unicodeScalar is >= 0 and < 0x20 or '"' or '\\';

    /// <summary>
    /// Finds the first JSON escape or malformed UTF-16 sequence.
    /// </summary>
    /// <param name="text">The UTF-16 source buffer.</param>
    /// <param name="textLength">The number of source characters.</param>
    /// <returns>The first required escape offset, or minus one.</returns>
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
    /// Writes a JSON escape with the upstream formatter's lowercase hexadecimal digits.
    /// </summary>
    /// <param name="unicodeScalar">The scalar to encode.</param>
    /// <param name="buffer">The destination buffer.</param>
    /// <param name="bufferLength">The destination capacity.</param>
    /// <param name="numberOfCharactersWritten">The completed escape length.</param>
    /// <returns>Whether the complete scalar or escape fits.</returns>
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

        char escape = unicodeScalar switch
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
        int length = escape == '\0' ? 6 : 2;
        if (destination.Length < length)
        {
            return false;
        }

        destination[0] = '\\';
        if (length == 2)
        {
            destination[1] = escape;
        }
        else
        {
            destination[1] = 'u';
            destination[2] = '0';
            destination[3] = '0';
            destination[4] = "0123456789abcdef"[unicodeScalar >> 4];
            destination[5] = "0123456789abcdef"[unicodeScalar & 0xF];
        }

        numberOfCharactersWritten = length;
        return true;
    }
}
