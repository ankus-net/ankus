using System.Globalization;
using System.Numerics;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies numeric ownership, value ordering, display scale, and checked .NET conversions.
/// </summary>
[TestClass]
public sealed class PgNumericTests
{
    /// <summary>
    /// Verifies decimal scale and extremes survive exact conversion without backend access.
    /// </summary>
    [TestMethod]
    public void DecimalConversionsPreserveValueAndScale()
    {
        PgNumeric scaled = PgNumeric.FromDecimal(123.4500m);
        Assert.AreEqual("123.4500", scaled.Text);
        Assert.AreEqual(4, scaled.Scale);
        Assert.AreEqual("123.45", scaled.ToNormalizedString());
        Assert.AreEqual(123.4500m, scaled.ToDecimal());
        Assert.AreSequenceEqual(decimal.GetBits(123.4500m), decimal.GetBits(scaled.ToDecimal()));
        Assert.AreEqual(decimal.MaxValue, PgNumeric.FromDecimal(decimal.MaxValue).ToDecimal());
        Assert.AreEqual(decimal.MinValue, PgNumeric.FromDecimal(decimal.MinValue).ToDecimal());
        Assert.AreEqual(0.0000000000000000000000000001m, PgNumeric.FromDecimal(0.0000000000000000000000000001m).ToDecimal());
    }

    /// <summary>
    /// Rejects the rounding and underflow which decimal parsing would otherwise perform silently.
    /// </summary>
    /// <param name="text">Canonical PostgreSQL output outside decimal's exact value set.</param>
    [TestMethod]
    [DataRow("0.00000000000000000000000000001")]
    [DataRow("8.0000000000000000000000000001")]
    [DataRow("79228162514264337593543950335.1")]
    [DataRow("79228162514264337593543950336")]
    [DataRow("-79228162514264337593543950336")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("-Infinity")]
    public void UnrepresentableDecimalsAreRejected(string text)
        => Assert.ThrowsExactly<OverflowException>(() => PgNumeric.FromCanonicalText(text).ToDecimal());

    /// <summary>
    /// Keeps the exact value and display scale, so converting back restores the identical numeric.
    /// </summary>
    /// <param name="text">Canonical PostgreSQL output representable by decimal with its display scale.</param>
    /// <param name="scale">The display scale that decimal must retain.</param>
    [TestMethod]
    [DataRow("1.0000000000000000000000000000", 28)]
    [DataRow("0.0000000000000000000000000000", 28)]
    [DataRow("-0.0000000000000000000000000001", 28)]
    [DataRow("7.9228162514264337593543950335", 28)]
    [DataRow("7922816251426433759354395033.5", 1)]
    [DataRow("79228162514264337593543950335", 0)]
    [DataRow("-79228162514264337593543950335", 0)]
    [DataRow("123.4500", 4)]
    [DataRow("0", 0)]
    public void DecimalConversionRetainsDisplayScale(string text, int scale)
    {
        PgNumeric numeric = PgNumeric.FromCanonicalText(text);
        decimal value = numeric.ToDecimal();
        Assert.AreEqual(scale, value.Scale);
        Assert.AreEqual(text, value.ToString(CultureInfo.InvariantCulture));
        Assert.AreEqual(text, PgNumeric.FromDecimal(value).Text);
        Assert.AreEqual(value, (decimal)numeric);
    }

    /// <summary>
    /// Rejects trailing fractional zeros that decimal cannot carry instead of silently reducing the display scale.
    /// </summary>
    /// <param name="text">A canonical PostgreSQL value whose numeric value fits decimal but whose display scale does not.</param>
    [TestMethod]
    [DataRow("1.00000000000000000000000000000")]
    [DataRow("0.00000000000000000000000000000")]
    [DataRow("0.10000000000000000000000000000")]
    [DataRow("-1.00000000000000000000000000000")]
    [DataRow("7922816251426433759354395033.50")]
    [DataRow("79228162514264337593543950335.0")]
    [DataRow("1.0000000000000000000000000000000000000000")]
    public void DecimalConversionRejectsDisplayScaleLoss(string text)
    {
        PgNumeric numeric = PgNumeric.FromCanonicalText(text);
        Assert.ThrowsExactly<OverflowException>(() => numeric.ToDecimal());
        Assert.ThrowsExactly<OverflowException>(() => (decimal)numeric);
        Assert.AreEqual(text, numeric.Text);
    }

    /// <summary>
    /// Checks equality/hash normalization, including NaN and differently scaled zeroes.
    /// </summary>
    /// <param name="left">The first canonical numeric.</param>
    /// <param name="right">The numerically equal canonical numeric.</param>
    [TestMethod]
    [DataRow("0", "0.0000")]
    [DataRow("123.4500", "123.45")]
    [DataRow("-123.4500", "-123.45")]
    [DataRow("NaN", "NaN")]
    [DataRow("Infinity", "Infinity")]
    [DataRow("-Infinity", "-Infinity")]
    public void EqualityAndHashesIgnoreDisplayScale(string left, string right)
    {
        PgNumeric first = PgNumeric.FromCanonicalText(left);
        PgNumeric second = PgNumeric.FromCanonicalText(right);
        Assert.AreEqual(first, second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
        Assert.AreEqual(0, first.CompareTo(second));
        Assert.IsTrue(first == second);
        Assert.IsTrue(first <= second);
        Assert.IsTrue(first >= second);
    }

    /// <summary>
    /// Checks sign, integer-digit width, fractional digits, and PostgreSQL special-value ordering.
    /// </summary>
    [TestMethod]
    public void OrderingMatchesNumericMagnitudeAndSpecialValues()
    {
        string[] ordered = ["-Infinity", "-100", "-1.01", "-1", "-0.0001", "0", "0.0001", "1", "1.01", "100", "Infinity", "NaN"];
        for (int index = 1; index < ordered.Length; index++)
        {
            PgNumeric left = PgNumeric.FromCanonicalText(ordered[index - 1]);
            PgNumeric right = PgNumeric.FromCanonicalText(ordered[index]);
            Assert.IsTrue(left < right, $"{left} should sort before {right}.");
            Assert.IsTrue(right > left, $"{right} should sort after {left}.");
            Assert.IsTrue(left != right);
        }

        Assert.AreEqual("0", default(PgNumeric).Text);
        Assert.AreEqual(PgNumeric.FromDecimal(0m), default);
        Assert.IsTrue(default(PgNumeric).IsFinite);
        Assert.AreEqual(0, default(PgNumeric).Sign);
        Assert.AreEqual(-1, PgNumeric.NegativeInfinity.Sign);
        Assert.AreEqual(1, PgNumeric.PositiveInfinity.Sign);
        Assert.IsNull(PgNumeric.NaN.Sign);
        Assert.IsNull(PgNumeric.NaN.Scale);
        Assert.IsNull(PgNumeric.PositiveInfinity.Scale);
        Assert.IsTrue(PgNumeric.NaN.IsNaN);
        Assert.IsFalse(PgNumeric.PositiveInfinity.IsFinite);
    }

    /// <summary>
    /// Checks integer conversions beyond decimal and refuses fractional/nonfinite input.
    /// </summary>
    [TestMethod]
    public void BigIntegersAndRedundantFractionalZerosRemainExact()
    {
        BigInteger value = BigInteger.Parse("12345678901234567890123456789012345678901234567890", CultureInfo.InvariantCulture);
        Assert.AreEqual(value, PgNumeric.FromBigInteger(value).ToBigInteger());
        Assert.AreEqual(-value, PgNumeric.FromBigInteger(-value).ToBigInteger());
        Assert.AreEqual(new BigInteger(1), PgNumeric.FromCanonicalText("1.00000000000000000000000000000000").ToBigInteger());
        Assert.ThrowsExactly<OverflowException>(() => PgNumeric.FromCanonicalText("1.00000000000000000000000000000000").ToDecimal());
        Assert.ThrowsExactly<OverflowException>(() => PgNumeric.FromDecimal(1.1m).ToBigInteger());
        Assert.ThrowsExactly<OverflowException>(() => PgNumeric.NaN.ToBigInteger());
        BigInteger limit = BigInteger.Pow(10, 131072);
        Assert.AreEqual(limit - 1, PgNumeric.FromBigInteger(limit - 1).ToBigInteger());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgNumeric.FromBigInteger(limit));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgNumeric.FromBigInteger(-limit));
    }

    /// <summary>
    /// Checks all binary integer widths, signs, exact narrowing, and independent overflow boundaries.
    /// </summary>
    [TestMethod]
    public void GenericIntegerConversionsAreExactAndChecked()
    {
        VerifyIntegerRange<sbyte>();
        VerifyIntegerRange<byte>();
        VerifyIntegerRange<short>();
        VerifyIntegerRange<ushort>();
        VerifyIntegerRange<int>();
        VerifyIntegerRange<uint>();
        VerifyIntegerRange<long>();
        VerifyIntegerRange<ulong>();
        VerifyIntegerRange<nint>();
        VerifyIntegerRange<nuint>();
        VerifyIntegerRange<Int128>();
        VerifyIntegerRange<UInt128>();
        BigInteger huge = BigInteger.Pow(10, 1000) - 1;
        Assert.AreEqual(huge.ToString(CultureInfo.InvariantCulture), PgNumeric.FromInteger(huge).Text);
        Assert.AreEqual(huge, PgNumeric.FromInteger(huge).ToInteger<BigInteger>());
    }

    /// <summary>
    /// Checks exact implicit and explicit conversions preserve primitive extremes and decimal scale.
    /// </summary>
    [TestMethod]
    public void PrimitiveConversionOperatorsPreserveValues()
    {
        PgNumeric[] values = [sbyte.MinValue, byte.MaxValue, short.MinValue, ushort.MaxValue, int.MinValue, uint.MaxValue,
            long.MinValue, ulong.MaxValue, (nint)(-42), (nuint)42, Int128.MinValue, UInt128.MaxValue, 1.2300m,
            BigInteger.Pow(10, 40)];
        string[] expected = ["-128", "255", "-32768", "65535", "-2147483648", "4294967295",
            "-9223372036854775808", "18446744073709551615", "-42", "42", "-170141183460469231731687303715884105728",
            "340282366920938463463374607431768211455", "1.2300", "10000000000000000000000000000000000000000"];
        Assert.AreSequenceEqual(expected, values.Select(static value => value.Text));
        Assert.AreEqual(1.2300m, (decimal)values[12]);
        Assert.AreEqual(BigInteger.Pow(10, 40), (BigInteger)values[13]);
        Assert.ThrowsExactly<OverflowException>(() => (decimal)PgNumeric.PositiveInfinity);
        Assert.ThrowsExactly<OverflowException>(() => (BigInteger)PgNumeric.FromDecimal(1.1m));
        Assert.AreEqual("1.2300", (+values[12]).Text);
        Assert.AreEqual("0", PgNumeric.Zero.Text);
        Assert.AreEqual("1", PgNumeric.One.Text);
    }

    /// <summary>
    /// Checks empty and singleton sums require no backend and always dispose their source enumerator.
    /// </summary>
    [TestMethod]
    public void SumPreservesSingletonScaleAndDisposesOnFailure()
    {
        Assert.AreEqual(PgNumeric.Zero, PgNumeric.Sum([]));
        Assert.AreEqual("1.2300", PgNumeric.Sum([PgNumeric.FromDecimal(1.2300m)]).Text);
        int finalized = 0;
        IEnumerable<PgNumeric> Source()
        {
            try
            {
                yield return PgNumeric.One;
                yield return PgNumeric.One;
            }
            finally
            {
                finalized++;
            }
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => PgNumeric.Sum(Source()));
        Assert.AreEqual(1, finalized);
        Assert.ThrowsExactly<ArgumentNullException>(() => PgNumeric.Sum(null!));
    }

    private static void VerifyIntegerRange<T>() where T : IBinaryInteger<T>, IMinMaxValue<T>
    {
        foreach (T value in new[] { T.MinValue, T.Zero, T.One, T.MaxValue })
        {
            Assert.AreEqual(value.ToString(null, CultureInfo.InvariantCulture), PgNumeric.FromInteger(value).Text);
            Assert.AreEqual(value, PgNumeric.FromInteger(value).ToInteger<T>());
        }

        Assert.ThrowsExactly<OverflowException>(() => PgNumeric.FromBigInteger(BigInteger.CreateChecked(T.MinValue) - 1).ToInteger<T>());
        Assert.ThrowsExactly<OverflowException>(() => PgNumeric.FromBigInteger(BigInteger.CreateChecked(T.MaxValue) + 1).ToInteger<T>());
        Assert.ThrowsExactly<OverflowException>(() => PgNumeric.FromDecimal(1.1m).ToInteger<T>());
        Assert.ThrowsExactly<OverflowException>(() => PgNumeric.NaN.ToInteger<T>());
        Assert.ThrowsExactly<OverflowException>(() => PgNumeric.PositiveInfinity.ToInteger<T>());
        Assert.AreEqual(T.One, PgNumeric.FromDecimal(1.000m).ToInteger<T>());
    }

    /// <summary>
    /// Checks numeric/decimal row conversions and independent cell type metadata.
    /// </summary>
    [TestMethod]
    public void RowConversionsUseExactNumericSemantics()
    {
        var row = new SpiRow([PgNumeric.FromDecimal(12.340m)], [new("value", 1700)]);
        Assert.AreEqual(12.340m, row.Get<decimal?>(0));
        row.Set(0, 9.870m);
        Assert.AreEqual(PgNumeric.FromDecimal(9.870m), row.Get<PgNumeric?>(0));
        Assert.AreEqual(1700u, row.GetTypeOid(0));
        row.Set(0, PgNumeric.PositiveInfinity);
        Assert.ThrowsExactly<OverflowException>(() => row.Get<decimal>(0));
        row.Set<decimal?>(0, null);
        Assert.IsNull(row.Get<PgNumeric?>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => row.Get<PgNumeric>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgNumeric.TryParse("1", out _));
        Assert.IsFalse(PgNumeric.TryParse(null, out PgNumeric parsed));
        Assert.AreEqual(default, parsed);
    }
}
