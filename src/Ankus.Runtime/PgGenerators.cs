using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ankus;

/// <summary>
/// Creates the generators that <see cref="PgPropertyRunner"/> draws inputs from, like proptest's built-in strategies.
/// </summary>
/// <remarks>
/// Integers sometimes take their range's limits so edge cases appear early, and they shrink toward zero, or toward the
/// limit nearest zero when the range excludes it, preferring positive values. Text holds Unicode scalar values other
/// than NUL, which PostgreSQL text cannot store, and favors ASCII letters and digits.
/// </remarks>
public static class PgGenerators
{
    /// <summary>
    /// How rarely a number takes one of its range's limits; other draws are uniform.
    /// </summary>
    private const uint LimitOneIn = 16;

    /// <summary>
    /// The simplest characters first, so text shrinks toward letters and digits.
    /// </summary>
    private const string SimpleCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    /// <summary>
    /// The number of Unicode scalar values other than NUL.
    /// </summary>
    private const uint ScalarCount = 0x10_FFFF - 0x800;

    /// <summary>
    /// Always produces the same value.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The generator.</returns>
    public static PgGenerator<T> Constant<T>(T value) => new(_ => value);

    /// <summary>
    /// Produces <see langword="false"/> or <see langword="true"/>, shrinking toward <see langword="false"/>.
    /// </summary>
    /// <returns>The generator.</returns>
    public static PgGenerator<bool> Boolean() => new(static source => source.Choose(1) != 0);

    /// <summary>
    /// Produces any value of an integer type, such as <see cref="int"/>, <see cref="long"/> or <see cref="byte"/>.
    /// </summary>
    /// <typeparam name="T">The integer type, at most 64 bits wide.</typeparam>
    /// <returns>The generator.</returns>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is wider than 64 bits.</exception>
    public static PgGenerator<T> Number<T>() where T : IBinaryInteger<T>, IMinMaxValue<T> => Number(T.MinValue, T.MaxValue);

    /// <summary>
    /// Produces integers in an inclusive range.
    /// </summary>
    /// <typeparam name="T">The integer type.</typeparam>
    /// <param name="minimum">The smallest value.</param>
    /// <param name="maximum">The largest value.</param>
    /// <returns>The generator.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximum"/> is less than <paramref name="minimum"/>.</exception>
    /// <exception cref="NotSupportedException">The range holds more than 2<sup>64</sup> values.</exception>
    public static PgGenerator<T> Number<T>(T minimum, T maximum) where T : IBinaryInteger<T>
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, minimum);
        // Only UInt128 holds values above Int128.MaxValue, and such a range always exceeds 2^64 values.
        if ((!T.IsNegative(maximum) && UInt128.CreateChecked(maximum) > (UInt128)Int128.MaxValue) ||
            (UInt128)(Int128.CreateChecked(maximum) - Int128.CreateChecked(minimum)) > ulong.MaxValue)
        {
            throw new NotSupportedException("Ranges of more than 2^64 values are not supported.");
        }

        Int128 low = Int128.CreateChecked(minimum);
        Int128 high = Int128.CreateChecked(maximum);
        return new(source => T.CreateChecked(Integer(source, low, high)));
    }

    /// <summary>
    /// Produces any value of a binary floating-point type, such as <see cref="double"/> or <see cref="float"/>,
    /// including infinities, NaN, negative zero and subnormal values, shrinking toward small whole numbers.
    /// </summary>
    /// <typeparam name="T"><see cref="double"/>, <see cref="float"/> or <see cref="Half"/>.</typeparam>
    /// <returns>The generator.</returns>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is another floating-point type.</exception>
    public static PgGenerator<T> FloatingPoint<T>() where T : IBinaryFloatingPointIeee754<T>, IMinMaxValue<T>
    {
        if (typeof(T) != typeof(double) && typeof(T) != typeof(float) && typeof(T) != typeof(Half))
        {
            throw new NotSupportedException($"'{typeof(T)}' is not a supported floating-point type.");
        }

        return new(static source => source.ChooseRarely(8, LimitOneIn) switch
        {
            1 => T.NaN,
            2 => T.PositiveInfinity,
            3 => T.NegativeInfinity,
            4 => T.NegativeZero,
            5 => T.Epsilon,
            6 => T.MaxValue,
            7 => T.MinValue,
            8 => FromBits<T>(source.Choose(ulong.MaxValue)),
            _ => T.CreateSaturating((double)Integer(source, -(1 << 20), 1 << 20, limits: false) +
                (source.Choose((1UL << 20) - 1) / (double)(1UL << 20))),
        });
    }

    /// <summary>
    /// Produces text of Unicode scalar values other than NUL.
    /// </summary>
    /// <param name="maxLength">The most scalar values; text is at most twice as many UTF-16 characters.</param>
    /// <returns>The generator, which shrinks toward shorter text and simpler characters.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is negative.</exception>
    public static PgGenerator<string> Text(int maxLength = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        return new(source =>
        {
            var text = new System.Text.StringBuilder();
            for (int count = 0; count < maxLength && source.Continue(8); count++)
            {
                text.Append(Character(source));
            }

            return text.ToString();
        });
    }

    /// <summary>
    /// Produces arrays of generated elements.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="element">The element generator.</param>
    /// <param name="maxLength">The most elements.</param>
    /// <returns>The generator, which shrinks toward fewer and simpler elements.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is negative.</exception>
    public static PgGenerator<T[]> Array<T>(PgGenerator<T> element, int maxLength = 32)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        return new(source =>
        {
            var values = new List<T>();
            while (values.Count < maxLength && source.Continue(8))
            {
                values.Add(element.Generate(source));
            }

            return [.. values];
        });
    }

    /// <summary>
    /// Produces one of the supplied values, shrinking toward the first.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="values">The candidates.</param>
    /// <returns>The generator.</returns>
    /// <exception cref="ArgumentException">No values are supplied.</exception>
    public static PgGenerator<T> Element<T>(params T[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException("Supply at least one value.", nameof(values));
        }

        T[] copy = [.. values];
        return new(source => copy[(int)source.Choose((ulong)copy.Length - 1)]);
    }

    /// <summary>
    /// Produces a value from one of the supplied generators, shrinking toward the first.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="generators">The alternatives.</param>
    /// <returns>The generator.</returns>
    /// <exception cref="ArgumentException">No generators are supplied, or one is null.</exception>
    public static PgGenerator<T> OneOf<T>(params PgGenerator<T>[] generators)
    {
        ArgumentNullException.ThrowIfNull(generators);
        if (generators.Length == 0 || System.Array.IndexOf(generators, null) >= 0)
        {
            throw new ArgumentException("Supply at least one generator and no null generators.", nameof(generators));
        }

        PgGenerator<T>[] copy = [.. generators];
        return new(source => copy[(int)source.Choose((ulong)copy.Length - 1)].Generate(source));
    }

    /// <summary>
    /// Produces SQL NULL as <see langword="null"/> for one value in eight, otherwise a generated value.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value generator.</param>
    /// <returns>The generator.</returns>
    public static PgGenerator<T?> Nullable<T>(PgGenerator<T> value) where T : struct
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(source => source.ChooseRarely(1, 8) == 0 ? value.Generate(source) : null);
    }

    /// <summary>
    /// Produces SQL NULL as <see langword="null"/> for one value in eight, otherwise a generated reference.
    /// </summary>
    /// <typeparam name="T">The reference type.</typeparam>
    /// <param name="value">The value generator.</param>
    /// <returns>The generator.</returns>
    public static PgGenerator<T?> NullableReference<T>(PgGenerator<T> value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(source => source.ChooseRarely(1, 8) == 0 ? value.Generate(source) : null);
    }

    /// <summary>
    /// Draws an integer in a range as a sign and a magnitude, or as an offset from the limit nearest zero when the
    /// range excludes zero, so smaller choices are values closer to zero and positive values come first.
    /// </summary>
    /// <param name="source">The choice source.</param>
    /// <param name="minimum">The smallest value.</param>
    /// <param name="maximum">The largest value.</param>
    /// <param name="limits">Whether random draws sometimes take a limit, which still shrinks like any other value.</param>
    private static Int128 Integer(PgPropertySource source, Int128 minimum, Int128 maximum, bool limits = true)
    {
        // 0 draws uniformly, 1 takes the minimum and 2 the maximum.
        ulong limit = limits && source.Draw(LimitOneIn - 1) == 0 ? 1 + source.Draw(1) : 0;
        if (minimum > 0 || maximum < 0)
        {
            ulong span = (ulong)(maximum - minimum);
            bool upward = minimum > 0;
            ulong offset = source.Choose(span, limit switch
            {
                1 => upward ? 0 : span,
                2 => upward ? span : 0,
                _ => source.Draw(span),
            });
            return upward ? minimum + offset : maximum - offset;
        }

        ulong positive = (ulong)maximum;
        ulong negative = (ulong)-minimum;
        bool negate = positive != 0 && negative != 0
            ? source.Choose(1, limit == 1 ? 1UL : limit == 2 ? 0 : source.Draw(1)) != 0
            : positive == 0;
        ulong largest = negate ? negative : positive;
        ulong magnitude = source.Choose(largest, limit == 0 ? source.Draw(largest) : largest);
        return negate ? -(Int128)magnitude : magnitude;
    }

    /// <summary>
    /// Reinterprets the low bits of a choice as a floating-point value.
    /// </summary>
    private static T FromBits<T>(ulong bits) where T : IBinaryFloatingPointIeee754<T>
        => typeof(T) == typeof(double) ? GenericValue.As<T>(BitConverter.UInt64BitsToDouble(bits))
            : typeof(T) == typeof(float) ? GenericValue.As<T>(BitConverter.UInt32BitsToSingle((uint)bits))
            : GenericValue.As<T>(BitConverter.UInt16BitsToHalf((ushort)bits));

    /// <summary>
    /// Draws a Unicode scalar value other than NUL, from ASCII letters and digits half the time.
    /// </summary>
    private static string Character(PgPropertySource source)
    {
        if (source.Choose(1) == 0)
        {
            return SimpleCharacters[(int)source.Choose((ulong)SimpleCharacters.Length - 1)].ToString();
        }

        // Code points 1 through 0x10FFFF without the 0x800 surrogates.
        uint value = 1 + (uint)source.Choose(ScalarCount - 1);
        return char.ConvertFromUtf32((int)(value < 0xD800 ? value : value + 0x800));
    }
}
