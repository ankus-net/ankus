using System;
using System.Globalization;
using System.Linq;

namespace Ankus;

public readonly partial record struct PgInterval
{
    /// <summary>
    /// Contains the microseconds in one hour.
    /// </summary>
    private const long MicrosecondsPerHour = 3_600_000_000;

    /// <summary>
    /// Contains the microseconds in one minute.
    /// </summary>
    private const long MicrosecondsPerMinute = 60_000_000;

    /// <summary>
    /// Contains the microseconds in one second.
    /// </summary>
    private const long MicrosecondsPerSecond = 1_000_000;

    /// <summary>
    /// Formats the interval as PostgreSQL's ISO 8601 duration, independently of the session's IntervalStyle.
    /// No backend is required.
    /// </summary>
    /// <returns>
    /// The text PostgreSQL produces with IntervalStyle iso_8601, such as P1Y2M-3DT4H5M6.5S or PT0S,
    /// or infinity and -infinity for the PostgreSQL 17 infinities.
    /// </returns>
    /// <remarks>
    /// Years and months share the sign of the month component, and hours, minutes, and seconds share the sign
    /// of the microsecond component, so mixed signs and every component remain exact. PostgreSQL interval input
    /// reads this form identically under every IntervalStyle. PostgreSQL 13 and 14 reject ISO 8601 hour fields
    /// beyond the signed 32-bit range; Ankus JSON reads this form without the backend.
    /// </remarks>
    public string ToIsoString()
    {
        if (!IsFinite)
        {
            return _infinity > 0 ? "infinity" : "-infinity";
        }

        if (Months == 0 && Days == 0 && Microseconds == 0)
        {
            return "PT0S";
        }

        long hours = Math.DivRem(Microseconds, MicrosecondsPerHour, out long remainder);
        long minutes = Math.DivRem(remainder, MicrosecondsPerMinute, out remainder);
        long seconds = Math.DivRem(remainder, MicrosecondsPerSecond, out long fraction);
        Span<char> text = stackalloc char[64];
        text[0] = 'P';
        int length = 1;
        AppendIsoPart(text, ref length, Months / 12, 'Y');
        AppendIsoPart(text, ref length, Months % 12, 'M');
        AppendIsoPart(text, ref length, Days, 'D');
        if (Microseconds != 0)
        {
            text[length++] = 'T';
            AppendIsoPart(text, ref length, hours, 'H');
            AppendIsoPart(text, ref length, minutes, 'M');
            if (seconds != 0 || fraction != 0)
            {
                if (seconds < 0 || fraction < 0)
                {
                    text[length++] = '-';
                }

                length += Format(Math.Abs(seconds), text[length..], default);
                if (fraction != 0)
                {
                    text[length++] = '.';
                    int digits = Format(Math.Abs(fraction), text[length..], "D6");
                    length += text.Slice(length, digits).TrimEnd('0').Length;
                }

                text[length++] = 'S';
            }
        }

        return new string(text[..length]);
    }

    /// <summary>
    /// Reads exactly the finite text produced by ToIsoString without backend access.
    /// </summary>
    /// <param name="text">The candidate interval text.</param>
    /// <param name="value">The exact interval, or the default value when the text is not in that form.</param>
    /// <returns>
    /// Whether the text is that canonical form. Other PostgreSQL syntax, infinities, and component triples
    /// matching PostgreSQL 17 infinity sentinels return false so the backend applies its own input rules.
    /// </returns>
    internal static bool TryParseIsoString(ReadOnlySpan<char> text, out PgInterval value)
    {
        value = default;
        if (text.Length < 3 || text.Length > 64 || text[0] != 'P')
        {
            return false;
        }

        Int128 months = 0;
        Int128 days = 0;
        Int128 microseconds = 0;
        bool timePart = false;
        int position = 1;
        while (position < text.Length)
        {
            if (text[position] == 'T')
            {
                if (timePart)
                {
                    return false;
                }

                timePart = true;
                position++;
                continue;
            }

            bool negative = text[position] == '-';
            if (negative)
            {
                position++;
            }

            if (!TryReadDigits(text, ref position, 19, out ulong whole))
            {
                return false;
            }

            long fraction = 0;
            bool hasFraction = position < text.Length && text[position] == '.';
            if (hasFraction)
            {
                position++;
                int start = position;
                if (!TryReadDigits(text, ref position, 6, out ulong digits))
                {
                    return false;
                }

                fraction = (long)digits;
                for (int scale = position - start; scale < 6; scale++)
                {
                    fraction *= 10;
                }
            }

            if (position == text.Length)
            {
                return false;
            }

            char unit = text[position++];
            Int128 signedMagnitude = negative ? -(Int128)whole : whole;
            Int128 signedFraction = negative ? -fraction : fraction;
            switch (timePart, unit, hasFraction)
            {
                case (false, 'Y', false):
                    months += signedMagnitude * 12;
                    break;
                case (false, 'M', false):
                    months += signedMagnitude;
                    break;
                case (false, 'D', false):
                    days += signedMagnitude;
                    break;
                case (true, 'H', false):
                    microseconds += signedMagnitude * MicrosecondsPerHour;
                    break;
                case (true, 'M', false):
                    microseconds += signedMagnitude * MicrosecondsPerMinute;
                    break;
                case (true, 'S', _):
                    microseconds += signedMagnitude * MicrosecondsPerSecond + signedFraction;
                    break;
                default:
                    return false;
            }
        }

        if (months < int.MinValue || months > int.MaxValue || days < int.MinValue || days > int.MaxValue ||
            microseconds < long.MinValue || microseconds > long.MaxValue)
        {
            return false;
        }

        var candidate = new PgInterval((int)months, (int)days, (long)microseconds);
        if (candidate is { Months: int.MaxValue, Days: int.MaxValue, Microseconds: long.MaxValue } or
            { Months: int.MinValue, Days: int.MinValue, Microseconds: long.MinValue } ||
            !text.SequenceEqual(candidate.ToIsoString()))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    /// <summary>
    /// Appends one nonzero ISO 8601 field with its own sign, as PostgreSQL's interval output does.
    /// </summary>
    /// <param name="text">The output buffer.</param>
    /// <param name="length">The current output length, advanced past the field.</param>
    /// <param name="number">The signed field value; zero writes nothing.</param>
    /// <param name="unit">The field designator.</param>
    private static void AppendIsoPart(Span<char> text, ref int length, long number, char unit)
    {
        if (number == 0)
        {
            return;
        }

        length += Format(number, text[length..], default);
        text[length++] = unit;
    }

    /// <summary>
    /// Formats an integer with invariant digits and sign into a buffer sized for every interval field.
    /// </summary>
    /// <param name="number">The integer.</param>
    /// <param name="destination">The remaining output buffer.</param>
    /// <param name="format">The numeric format.</param>
    /// <returns>The number of characters written.</returns>
    private static int Format(long number, Span<char> destination, ReadOnlySpan<char> format)
    {
        if (!number.TryFormat(destination, out int written, format, CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("The interval text buffer is too small.");
        }

        return written;
    }

    /// <summary>
    /// Reads one to the requested number of ASCII digits without accepting signs, separators, or exponents.
    /// </summary>
    /// <param name="text">The input text.</param>
    /// <param name="position">The first digit position, advanced past the digits.</param>
    /// <param name="maximumDigits">The largest accepted digit count.</param>
    /// <param name="number">The unsigned value of the digits.</param>
    /// <returns>Whether one to the maximum number of digits were present.</returns>
    private static bool TryReadDigits(ReadOnlySpan<char> text, ref int position, int maximumDigits, out ulong number)
    {
        number = 0;
        int start = position;
        while (position < text.Length && char.IsAsciiDigit(text[position]))
        {
            if (position - start == maximumDigits)
            {
                return false;
            }

            number = number * 10 + (ulong)(text[position] - '0');
            position++;
        }

        return position > start;
    }
}
