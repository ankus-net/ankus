using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using Ankus.Serialization;

namespace Ankus;

/// <summary>
/// Owns a full-range PostgreSQL numeric value, including display scale, NaN, and infinities.
/// The default value is zero. Equality ignores trailing fractional zeroes and treats NaN as equal to NaN.
/// </summary>
[JsonConverter(typeof(PgNumericConverter))]
public readonly partial record struct PgNumeric : IComparable<PgNumeric>,
    IAdditionOperators<PgNumeric, PgNumeric, PgNumeric>, ISubtractionOperators<PgNumeric, PgNumeric, PgNumeric>,
    IMultiplyOperators<PgNumeric, PgNumeric, PgNumeric>, IDivisionOperators<PgNumeric, PgNumeric, PgNumeric>,
    IModulusOperators<PgNumeric, PgNumeric, PgNumeric>, IUnaryNegationOperators<PgNumeric, PgNumeric>,
    IUnaryPlusOperators<PgNumeric, PgNumeric>, IComparisonOperators<PgNumeric, PgNumeric, bool>,
    IAdditiveIdentity<PgNumeric, PgNumeric>, IMultiplicativeIdentity<PgNumeric, PgNumeric>
{
    /// <summary>
    /// Shares the immutable portable representation of scale-zero zero.
    /// </summary>
    private static readonly PgNumericStorage s_zero = PgNumericStorage.FromCanonicalText("0");

    /// <summary>
    /// Shares the immutable portable representation of one.
    /// </summary>
    private static readonly PgNumericStorage s_one = PgNumericStorage.FromCanonicalText("1");

    /// <summary>
    /// Shares PostgreSQL's portable not-a-number representation.
    /// </summary>
    private static readonly PgNumericStorage s_nan = PgNumericStorage.FromCanonicalText("NaN");

    /// <summary>
    /// Shares the portable positive-infinity representation.
    /// </summary>
    private static readonly PgNumericStorage s_positiveInfinity = PgNumericStorage.FromCanonicalText("Infinity");

    /// <summary>
    /// Shares the portable negative-infinity representation.
    /// </summary>
    private static readonly PgNumericStorage s_negativeInfinity = PgNumericStorage.FromCanonicalText("-Infinity");

    /// <summary>
    /// Owns binary numeric storage independently of PostgreSQL memory contexts.
    /// </summary>
    private readonly PgNumericStorage? _storage;

    /// <summary>
    /// Encodes managed-origin canonical text into portable binary digits.
    /// </summary>
    private PgNumeric(string text) => _storage = PgNumericStorage.FromCanonicalText(text);

    /// <summary>
    /// Wraps immutable owned binary storage.
    /// </summary>
    private PgNumeric(PgNumericStorage storage) => _storage = storage;

    /// <summary>
    /// Gets binary storage, treating the default struct as scale-zero zero.
    /// </summary>
    internal PgNumericStorage Storage => _storage ?? s_zero;

    /// <summary>
    /// Gets the owned, culture-independent PostgreSQL output text, retaining display scale.
    /// </summary>
    public string Text => Storage.Text;

    /// <summary>
    /// Gets zero with scale zero, without backend access.
    /// </summary>
    public static PgNumeric Zero => default;

    /// <summary>
    /// Gets one with scale zero, without backend access.
    /// </summary>
    public static PgNumeric One => new(s_one);

    /// <summary>
    /// Gets the additive identity for generic arithmetic.
    /// </summary>
    public static PgNumeric AdditiveIdentity => Zero;

    /// <summary>
    /// Gets the multiplicative identity for generic arithmetic.
    /// </summary>
    public static PgNumeric MultiplicativeIdentity => One;

    /// <summary>
    /// Gets PostgreSQL's not-a-number value, which sorts above all other numeric values.
    /// </summary>
    public static PgNumeric NaN => new(s_nan);

    /// <summary>
    /// Gets positive infinity. Native numeric infinities require PostgreSQL 14 or later.
    /// </summary>
    public static PgNumeric PositiveInfinity => new(s_positiveInfinity);

    /// <summary>
    /// Gets negative infinity. Native numeric infinities require PostgreSQL 14 or later.
    /// </summary>
    public static PgNumeric NegativeInfinity => new(s_negativeInfinity);

    /// <summary>
    /// Gets whether the value is finite.
    /// </summary>
    public bool IsFinite => Kind == 1;

    /// <summary>
    /// Gets whether the value is PostgreSQL NaN.
    /// </summary>
    public bool IsNaN => Kind == 3;

    /// <summary>
    /// Gets the display scale, or null for NaN and infinities.
    /// </summary>
    public int? Scale => Storage.Scale;

    /// <summary>
    /// Gets -1, 0, or 1 for negative, zero, or positive values, or null for NaN.
    /// </summary>
    public int? Sign => Storage.Sign;

    private int Kind => Storage.Kind;

    private ReadOnlySpan<char> NormalizedText
    {
        get
        {
            ReadOnlySpan<char> text = Text;
            if (text.Contains('.'))
            {
                text = text.TrimEnd('0').TrimEnd('.');
            }

            return text.SequenceEqual("-0") ? "0" : text;
        }
    }

    /// <summary>
    /// Encodes canonical text for managed-only value creation without a backend.
    /// </summary>
    internal static PgNumeric FromCanonicalText(string text) => new(text);

    /// <summary>
    /// Copies borrowed opaque datum and portable numeric_send bytes into an owned numeric.
    /// </summary>
    internal static PgNumeric FromBinary(ReadOnlySpan<byte> buffer, int rawLength)
        => new(PgNumericStorage.Copy(buffer, rawLength));

    /// <summary>
    /// Parses PostgreSQL numeric syntax on the active backend thread, including exponents and special values.
    /// </summary>
    /// <param name="text">The PostgreSQL numeric input.</param>
    /// <returns>The parsed value with PostgreSQL's display scale.</returns>
    public static PgNumeric Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return NativeBackend.Numeric<PgNumeric>(NumericOperation.Parse, [SpiParameter.Create(text)]);
    }

    /// <summary>
    /// Tries to parse numeric input on the active backend thread.
    /// </summary>
    /// <param name="text">The candidate input.</param>
    /// <param name="value">The parsed numeric, or zero on invalid input.</param>
    /// <returns>Whether the input was valid. Backend-access and operational errors still throw.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgNumeric value)
    {
        value = default;
        if (text is null || text.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            value = NativeInputRecovery.Run(() => Parse(text));
            return true;
        }
        catch (PgException error) when ((error.NativeFlags & NativeErrorFlags.Unrecovered) == 0 && (error.SqlState is "22P02" or "22003"))
        {
            return false;
        }
        catch (System.Text.EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Converts a decimal exactly without requiring an active backend.
    /// </summary>
    /// <param name="value">The decimal, including its stored scale.</param>
    /// <returns>The exact numeric value.</returns>
    public static PgNumeric FromDecimal(decimal value) => new(PgNumericStorage.FromDecimal(value));

    /// <summary>
    /// Converts an arbitrary-precision integer within PostgreSQL's 131072-digit limit.
    /// </summary>
    /// <param name="value">The integer.</param>
    /// <returns>The numeric value without a fractional part.</returns>
    public static PgNumeric FromBigInteger(BigInteger value)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Length - (value.Sign < 0 ? 1 : 0) > 131072)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The integer exceeds PostgreSQL's numeric range.");
        }

        return new(text);
    }

    /// <summary>
    /// Converts any binary integer exactly without requiring a backend, including Int128 and UInt128.
    /// </summary>
    /// <typeparam name="TInteger">The integer type.</typeparam>
    /// <param name="value">The integer value.</param>
    /// <returns>The exact numeric with scale zero.</returns>
    public static PgNumeric FromInteger<TInteger>(TInteger value) where TInteger : IBinaryInteger<TInteger>
        => FromBigInteger(BigInteger.CreateChecked(value));

    /// <summary>
    /// Converts exactly to an integer, rejecting fractional, nonfinite, and out-of-range values.
    /// </summary>
    /// <typeparam name="TInteger">The requested integer type.</typeparam>
    /// <returns>The exact integer. This conversion does not require a backend.</returns>
    /// <exception cref="OverflowException">The value cannot be represented exactly in the requested integer type.</exception>
    public TInteger ToInteger<TInteger>() where TInteger : IBinaryInteger<TInteger>
        => TInteger.CreateChecked(ToBigInteger());

    /// <summary>
    /// Converts a single-precision value with PostgreSQL's float4-to-numeric precision rules.
    /// </summary>
    /// <param name="value">The floating-point value.</param>
    /// <returns>The PostgreSQL numeric approximation.</returns>
    public static PgNumeric FromSingle(float value)
        => NativeBackend.Numeric<PgNumeric>(NumericOperation.FromSingle, [SpiParameter.Create(value)]);

    /// <summary>
    /// Converts a floating-point value using PostgreSQL's float8-to-numeric conversion.
    /// </summary>
    /// <param name="value">The floating-point value.</param>
    /// <returns>The numeric value, with the server's conversion precision.</returns>
    public static PgNumeric FromDouble(double value)
        => NativeBackend.Numeric<PgNumeric>(NumericOperation.FromDouble, [SpiParameter.Create(value)]);

    /// <summary>
    /// Converts exactly to decimal, retaining the display scale and rejecting any value or scale change.
    /// </summary>
    /// <returns>The decimal with this value and this display scale, so FromDecimal restores the same numeric.</returns>
    /// <remarks>
    /// Like SqlDecimal, the conversion fails when decimal cannot carry the display scale, including trailing
    /// fractional zeros beyond 28 digits or beyond its 96-bit coefficient. Round, Truncate, Rescale, or a SQL cast
    /// to a smaller numeric scale reduces the scale explicitly first.
    /// </remarks>
    /// <exception cref="OverflowException">The value or its display scale cannot be represented exactly as a decimal.</exception>
    public decimal ToDecimal()
    {
        if (!IsFinite || !decimal.TryParse(Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out decimal value) ||
            !string.Equals(Text, PgNumericStorage.FromDecimal(value).Text, StringComparison.Ordinal))
        {
            throw new OverflowException("The PostgreSQL numeric value or display scale cannot be represented exactly as a decimal.");
        }

        return value;
    }

    /// <summary>
    /// Converts a finite integer exactly, rejecting fractional values and nonfinite values.
    /// </summary>
    /// <returns>The arbitrary-precision integer.</returns>
    public BigInteger ToBigInteger()
    {
        if (!IsFinite || NormalizedText.Contains('.'))
        {
            throw new OverflowException("The numeric value is not a finite integer.");
        }

        return BigInteger.Parse(NormalizedText, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Converts to double using PostgreSQL's precision and range rules.
    /// </summary>
    /// <returns>The floating-point approximation.</returns>
    public double ToDouble() => NativeBackend.Numeric<double>(NumericOperation.ToDouble, [SpiParameter.Create(this)]);

    /// <summary>
    /// Converts to single precision using PostgreSQL's rounding, overflow, and underflow rules.
    /// </summary>
    /// <returns>The floating-point approximation.</returns>
    public float ToSingle() => NativeBackend.Numeric<float>(NumericOperation.ToSingle, [SpiParameter.Create(this)]);

    /// <summary>
    /// Casts to PostgreSQL smallint, rounding ties away from zero and reporting native range errors.
    /// </summary>
    /// <returns>The server-rounded integer. Use ToInteger for an exact, backend-independent conversion.</returns>
    public short ToInt16() => NativeBackend.Numeric<short>(NumericOperation.ToInt16, [SpiParameter.Create(this)]);

    /// <summary>
    /// Casts to PostgreSQL integer, rounding ties away from zero and reporting native range errors.
    /// </summary>
    /// <returns>The server-rounded integer. Use ToInteger for an exact, backend-independent conversion.</returns>
    public int ToInt32() => NativeBackend.Numeric<int>(NumericOperation.ToInt32, [SpiParameter.Create(this)]);

    /// <summary>
    /// Casts to PostgreSQL bigint, rounding ties away from zero and reporting native range errors.
    /// </summary>
    /// <returns>The server-rounded integer. Use ToInteger for an exact, backend-independent conversion.</returns>
    public long ToInt64() => NativeBackend.Numeric<long>(NumericOperation.ToInt64, [SpiParameter.Create(this)]);

    /// <summary>
    /// Sums values in enumeration order with PostgreSQL arithmetic; an empty sequence yields zero.
    /// </summary>
    /// <param name="values">The sequence, enumerated once and disposed even when an operation fails.</param>
    /// <returns>The sum, retaining PostgreSQL result scale and special-value semantics.</returns>
    public static PgNumeric Sum(IEnumerable<PgNumeric> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        using IEnumerator<PgNumeric> enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return Zero;
        }

        PgNumeric result = enumerator.Current;
        while (enumerator.MoveNext())
        {
            result += enumerator.Current;
        }

        return result;
    }

    /// <summary>
    /// Rounds to a declared precision and scale with PostgreSQL's numeric typmod rules.
    /// </summary>
    /// <param name="precision">The maximum significant digits, one through 1000.</param>
    /// <param name="scale">The declared scale. Negative scales and scales above precision require PostgreSQL 15 or later.</param>
    /// <returns>The rescaled numeric; a range error is raised if it does not fit.</returns>
    public PgNumeric Rescale(int precision, int scale)
        => NativeBackend.Numeric<PgNumeric>(NumericOperation.Rescale,
            [SpiParameter.Create(this), SpiParameter.Create(precision), SpiParameter.Create(scale)]);

    /// <summary>
    /// Rounds to a fractional scale, breaking ties away from zero as PostgreSQL does.
    /// </summary>
    /// <param name="scale">The fractional scale; negative values round to the left of the decimal point.</param>
    /// <returns>The rounded numeric.</returns>
    public PgNumeric Round(int scale = 0) => WithScale(NumericOperation.Round, scale);

    /// <summary>
    /// Truncates toward zero to a fractional scale.
    /// </summary>
    /// <param name="scale">The fractional scale; negative values truncate to the left of the decimal point.</param>
    /// <returns>The truncated numeric.</returns>
    public PgNumeric Truncate(int scale = 0) => WithScale(NumericOperation.Truncate, scale);

    /// <summary>
    /// Gets the absolute value using PostgreSQL numeric semantics.
    /// </summary>
    /// <returns>The absolute value.</returns>
    public PgNumeric Abs() => Unary(NumericOperation.Abs);

    /// <summary>
    /// Rounds toward positive infinity.
    /// </summary>
    /// <returns>The least integer greater than or equal to this value.</returns>
    public PgNumeric Ceiling() => Unary(NumericOperation.Ceiling);

    /// <summary>
    /// Rounds toward negative infinity.
    /// </summary>
    /// <returns>The greatest integer less than or equal to this value.</returns>
    public PgNumeric Floor() => Unary(NumericOperation.Floor);

    /// <summary>
    /// Computes the PostgreSQL numeric square root.
    /// </summary>
    /// <returns>The square root.</returns>
    public PgNumeric Sqrt() => Unary(NumericOperation.Sqrt);

    /// <summary>
    /// Computes e raised to this value using PostgreSQL numeric precision.
    /// </summary>
    /// <returns>The exponential.</returns>
    public PgNumeric Exp() => Unary(NumericOperation.Exp);

    /// <summary>
    /// Computes the natural logarithm.
    /// </summary>
    /// <returns>The natural logarithm.</returns>
    public PgNumeric Log() => Unary(NumericOperation.Log);

    /// <summary>
    /// Computes the logarithm in the specified base.
    /// </summary>
    /// <param name="base">The logarithm base.</param>
    /// <returns>The logarithm.</returns>
    public PgNumeric Log(PgNumeric @base) => Binary(NumericOperation.LogBase, @base, this);

    /// <summary>
    /// Raises this value to a numeric power.
    /// </summary>
    /// <param name="exponent">The exponent.</param>
    /// <returns>The power.</returns>
    public PgNumeric Pow(PgNumeric exponent) => Binary(NumericOperation.Power, this, exponent);

    /// <summary>
    /// Computes the greatest common divisor with PostgreSQL numeric semantics.
    /// </summary>
    /// <param name="other">The other value.</param>
    /// <returns>The greatest common divisor.</returns>
    public PgNumeric GreatestCommonDivisor(PgNumeric other) => Binary(NumericOperation.Gcd, this, other);

    /// <summary>
    /// Computes the least common multiple with PostgreSQL numeric semantics.
    /// </summary>
    /// <param name="other">The other value.</param>
    /// <returns>The least common multiple.</returns>
    public PgNumeric LeastCommonMultiple(PgNumeric other) => Binary(NumericOperation.Lcm, this, other);

    /// <summary>
    /// Returns culture-independent text without insignificant fractional zeroes.
    /// </summary>
    /// <returns>The normalized numeric text.</returns>
    public string ToNormalizedString() => NormalizedText.ToString();

    /// <summary>
    /// Returns the owned PostgreSQL text, retaining display scale.
    /// </summary>
    /// <returns>The numeric text.</returns>
    public override string ToString() => Text;

    /// <summary>
    /// Compares numeric values independently of display scale and backend access.
    /// </summary>
    /// <param name="other">The other value.</param>
    /// <returns>Whether the values compare equal, including NaN compared with NaN.</returns>
    public bool Equals(PgNumeric other) => NormalizedText.SequenceEqual(other.NormalizedText);

    /// <summary>
    /// Gets a hash consistent with scale-independent numeric equality.
    /// </summary>
    /// <returns>The numeric hash.</returns>
    public override int GetHashCode() => string.GetHashCode(NormalizedText, StringComparison.Ordinal);

    /// <summary>
    /// Compares with PostgreSQL's ordering: negative infinity, finite values, positive infinity, then NaN.
    /// </summary>
    /// <param name="other">The other value.</param>
    /// <returns>A negative value, zero, or a positive value for lesser, equal, or greater values.</returns>
    public int CompareTo(PgNumeric other)
    {
        int order = Kind.CompareTo(other.Kind);
        if (order != 0 || !IsFinite)
        {
            return order;
        }

        ReadOnlySpan<char> left = NormalizedText;
        ReadOnlySpan<char> right = other.NormalizedText;
        bool negative = left[0] == '-';
        if (negative != (right[0] == '-'))
        {
            return negative ? -1 : 1;
        }

        if (negative)
        {
            left = left[1..];
            right = right[1..];
        }

        int leftPoint = left.IndexOf('.');
        int rightPoint = right.IndexOf('.');
        order = (leftPoint < 0 ? left.Length : leftPoint).CompareTo(rightPoint < 0 ? right.Length : rightPoint);
        if (order == 0)
        {
            order = left.SequenceCompareTo(right);
        }

        return negative ? -order : order;
    }

    /// <summary>
    /// Adds numeric values in PostgreSQL.
    /// </summary>
    public static PgNumeric operator +(PgNumeric left, PgNumeric right)
    {
        return Binary(NumericOperation.Add, left, right);
    }

    /// <summary>
    /// Subtracts numeric values in PostgreSQL.
    /// </summary>
    public static PgNumeric operator -(PgNumeric left, PgNumeric right)
    {
        return Binary(NumericOperation.Subtract, left, right);
    }

    /// <summary>
    /// Multiplies numeric values in PostgreSQL.
    /// </summary>
    public static PgNumeric operator *(PgNumeric left, PgNumeric right)
    {
        return Binary(NumericOperation.Multiply, left, right);
    }

    /// <summary>
    /// Divides numeric values using PostgreSQL's result-scale rules.
    /// </summary>
    public static PgNumeric operator /(PgNumeric left, PgNumeric right)
    {
        return Binary(NumericOperation.Divide, left, right);
    }

    /// <summary>
    /// Computes PostgreSQL's numeric remainder.
    /// </summary>
    public static PgNumeric operator %(PgNumeric left, PgNumeric right)
    {
        return Binary(NumericOperation.Remainder, left, right);
    }

    /// <summary>
    /// Negates a numeric value in PostgreSQL.
    /// </summary>
    public static PgNumeric operator -(PgNumeric value)
    {
        return value.Unary(NumericOperation.Negate);
    }

    /// <summary>
    /// Returns the value unchanged, retaining display scale.
    /// </summary>
    public static PgNumeric operator +(PgNumeric value)
    {
        return value;
    }

    /// <summary>
    /// Tests whether the left numeric is less than the right numeric.
    /// </summary>
    public static bool operator <(PgNumeric left, PgNumeric right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Tests whether the left numeric is greater than the right numeric.
    /// </summary>
    public static bool operator >(PgNumeric left, PgNumeric right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Tests whether the left numeric is less than or equal to the right numeric.
    /// </summary>
    public static bool operator <=(PgNumeric left, PgNumeric right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Tests whether the left numeric is greater than or equal to the right numeric.
    /// </summary>
    public static bool operator >=(PgNumeric left, PgNumeric right)
    {
        return left.CompareTo(right) >= 0;
    }

    /// <summary>
    /// Converts a signed byte exactly without backend access.
    /// </summary>
    public static implicit operator PgNumeric(sbyte value)
    {
        return FromInteger(value);
    }

    /// <summary>
    /// Converts an unsigned byte exactly without backend access.
    /// </summary>
    public static implicit operator PgNumeric(byte value)
    {
        return FromInteger(value);
    }

    /// <summary>
    /// Converts a signed 16-bit integer exactly without backend access.
    /// </summary>
    public static implicit operator PgNumeric(short value)
    {
        return FromInteger(value);
    }

    /// <summary>
    /// Converts an unsigned 16-bit integer exactly without backend access.
    /// </summary>
    public static implicit operator PgNumeric(ushort value)
    {
        return FromInteger(value);
    }

    /// <summary>
    /// Converts a signed 32-bit integer exactly without backend access.
    /// </summary>
    public static implicit operator PgNumeric(int value)
    {
        return FromInteger(value);
    }

    /// <summary>
    /// Converts an unsigned 32-bit integer exactly without backend access.
    /// </summary>
    public static implicit operator PgNumeric(uint value)
    {
        return FromInteger(value);
    }

    /// <summary>
    /// Converts a signed 64-bit integer exactly without backend access.
    /// </summary>
    public static implicit operator PgNumeric(long value)
    {
        return FromInteger(value);
    }

    /// <summary>
    /// Converts an unsigned 64-bit integer exactly without backend access.
    /// </summary>
    public static implicit operator PgNumeric(ulong value)
    {
        return FromInteger(value);
    }

    /// <summary>
    /// Converts a native-sized signed integer exactly without backend access.
    /// </summary>
    public static implicit operator PgNumeric(nint value)
    {
        return FromInteger(value);
    }

    /// <summary>
    /// Converts a native-sized unsigned integer exactly without backend access.
    /// </summary>
    public static implicit operator PgNumeric(nuint value)
    {
        return FromInteger(value);
    }

    /// <summary>
    /// Converts a signed 128-bit integer exactly without backend access.
    /// </summary>
    public static implicit operator PgNumeric(Int128 value)
    {
        return FromInteger(value);
    }

    /// <summary>
    /// Converts an unsigned 128-bit integer exactly without backend access.
    /// </summary>
    public static implicit operator PgNumeric(UInt128 value)
    {
        return FromInteger(value);
    }

    /// <summary>
    /// Converts a decimal exactly, preserving scale without backend access.
    /// </summary>
    public static implicit operator PgNumeric(decimal value)
    {
        return FromDecimal(value);
    }

    /// <summary>
    /// Converts an arbitrary integer within PostgreSQL's numeric range without backend access.
    /// </summary>
    public static implicit operator PgNumeric(BigInteger value)
    {
        return FromBigInteger(value);
    }

    /// <summary>
    /// Converts from single precision using PostgreSQL's float4-to-numeric rules.
    /// </summary>
    public static explicit operator PgNumeric(float value)
    {
        return FromSingle(value);
    }

    /// <summary>
    /// Converts from double precision using PostgreSQL's float8-to-numeric rules.
    /// </summary>
    public static explicit operator PgNumeric(double value)
    {
        return FromDouble(value);
    }

    /// <summary>
    /// Converts exactly to decimal, rejecting rounding, overflow, nonfinite values, and display-scale loss.
    /// </summary>
    public static explicit operator decimal(PgNumeric value)
    {
        return value.ToDecimal();
    }

    /// <summary>
    /// Converts exactly to an arbitrary integer, rejecting fractional and nonfinite values.
    /// </summary>
    public static explicit operator BigInteger(PgNumeric value)
    {
        return value.ToBigInteger();
    }

    /// <summary>
    /// Converts to single precision using PostgreSQL's precision and range rules.
    /// </summary>
    public static explicit operator float(PgNumeric value)
    {
        return value.ToSingle();
    }

    /// <summary>
    /// Converts to double precision using PostgreSQL's precision and range rules.
    /// </summary>
    public static explicit operator double(PgNumeric value)
    {
        return value.ToDouble();
    }

    private PgNumeric Unary(NumericOperation operation) => NativeBackend.Numeric<PgNumeric>(operation, [SpiParameter.Create(this)]);
    private PgNumeric WithScale(NumericOperation operation, int scale)
        => NativeBackend.Numeric<PgNumeric>(operation, [SpiParameter.Create(this), SpiParameter.Create(scale)]);
    private static PgNumeric Binary(NumericOperation operation, PgNumeric left, PgNumeric right)
        => NativeBackend.Numeric<PgNumeric>(operation, [SpiParameter.Create(left), SpiParameter.Create(right)]);
}
