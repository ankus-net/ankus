using System.Globalization;
using System.Numerics;
using System.Text;

namespace Ankus.Examples.PglzInspect;

/// <summary>
/// Reproduces the Rust formatting used in pgrx's messages, so recommendations and errors keep their exact text.
/// </summary>
internal static class RustFormat
{
    /// <summary>
    /// Formats a finite number like Rust's <c>{:.N}</c>: the exact binary value rounded half to even.
    /// </summary>
    /// <param name="value">The finite value.</param>
    /// <param name="decimals">The number of fractional digits.</param>
    /// <returns>The fixed-point text with an invariant decimal point.</returns>
    /// <remarks>
    /// .NET's fixed-point format rounds an exact tie, such as 0.125 to two digits, away from zero; Rust prints 0.12.
    /// </remarks>
    internal static string Fixed(double value, int decimals)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Only finite values are formatted.");
        }

        long bits = BitConverter.DoubleToInt64Bits(value);
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xF_FFFF_FFFF_FFFFL;
        if (exponent == 0)
        {
            exponent = 1;
        }
        else
        {
            mantissa |= 1L << 52;
        }

        // The value is mantissa * 2^(exponent - 1075); scale it by 10^decimals and divide exactly.
        BigInteger scaled = mantissa * BigInteger.Pow(10, decimals);
        int shift = exponent - 1075;
        BigInteger quotient;
        if (shift >= 0)
        {
            quotient = scaled << shift;
        }
        else
        {
            BigInteger denominator = BigInteger.One << -shift;
            quotient = BigInteger.DivRem(scaled, denominator, out BigInteger remainder);
            BigInteger twice = remainder << 1;
            if (twice > denominator || (twice == denominator && !quotient.IsEven))
            {
                quotient++;
            }
        }

        string digits = quotient.ToString(CultureInfo.InvariantCulture).PadLeft(decimals + 1, '0');
        string text = decimals == 0 ? digits : digits[..^decimals] + "." + digits[^decimals..];
        return bits < 0 ? "-" + text : text;
    }

    /// <summary>
    /// Quotes text like Rust's <c>{:?}</c> for strings, escaping quotes, backslashes and nonprinting characters.
    /// </summary>
    /// <param name="value">The text to quote.</param>
    /// <returns>The quoted text.</returns>
    /// <remarks>
    /// Control, format, separator, private-use, unassigned and combining characters use <c>\u{hex}</c>, following
    /// Rust's printable-character rules for those categories. Other characters, including non-ASCII letters, are kept.
    /// </remarks>
    internal static string Debug(string value)
    {
        StringBuilder text = new StringBuilder(value.Length + 2).Append('"');
        foreach (Rune rune in value.EnumerateRunes())
        {
            _ = rune.Value switch
            {
                '"' => text.Append("\\\""),
                '\\' => text.Append("\\\\"),
                '\n' => text.Append("\\n"),
                '\r' => text.Append("\\r"),
                '\t' => text.Append("\\t"),
                '\0' => text.Append("\\0"),
                _ when IsPrintable(rune) => text.Append(rune),
                _ => text.Append("\\u{").Append(rune.Value.ToString("x", CultureInfo.InvariantCulture)).Append('}'),
            };
        }

        return text.Append('"').ToString();
    }

    /// <summary>
    /// Reports whether Rust prints a character unescaped inside a debug-formatted string.
    /// </summary>
    private static bool IsPrintable(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or
            UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark => false,
        UnicodeCategory.SpaceSeparator => rune.Value == ' ',
        _ => true,
    };
}
