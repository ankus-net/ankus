using System;
using System.Buffers;
using System.Text;

namespace Ankus;

/// <summary>
/// Converts PostgreSQL character strings to <see cref="Rune"/>, the .NET counterpart of Rust's <c>char</c>.
/// </summary>
/// <remarks>
/// pgrx reads a Rust <c>char</c> from <c>varchar</c> by taking the first character, so empty text becomes NULL and
/// extra characters are dropped. A <see cref="Rune"/> requires exactly one Unicode scalar value instead, and other
/// text fails, so no character is lost silently.
/// </remarks>
internal static class PgRuneText
{
    /// <summary>
    /// Reads text that holds exactly one Unicode scalar value.
    /// </summary>
    /// <param name="text">The PostgreSQL text.</param>
    /// <returns>The scalar value.</returns>
    /// <exception cref="InvalidCastException">The text is empty, holds more than one scalar value, or begins with an unpaired surrogate.</exception>
    internal static Rune Parse(string text)
    {
        OperationStatus status = Rune.DecodeFromUtf16(text, out Rune rune, out int consumed);
        if (status == OperationStatus.Done && consumed == text.Length)
        {
            return rune;
        }

        throw new InvalidCastException(text.Length == 0 ? "Empty text has no character to read as a Rune." :
            status == OperationStatus.Done ? "A Rune holds exactly one Unicode scalar value, but the text has more than one." :
            "The text begins with an unpaired surrogate, which is not a Unicode scalar value.");
    }
}
