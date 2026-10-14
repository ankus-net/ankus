namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies PostgreSQL interval ordering without backend access or component normalization.
/// </summary>
[TestClass]
public sealed class PgIntervalOrderingTests
{
    /// <summary>
    /// Finite comparisons use PostgreSQL's duration approximation, including mixed signs and wide intermediate values.
    /// </summary>
    /// <param name="leftMonths">The left calendar months.</param>
    /// <param name="leftDays">The left calendar days.</param>
    /// <param name="leftMicros">The left elapsed microseconds.</param>
    /// <param name="rightMonths">The right calendar months.</param>
    /// <param name="rightDays">The right calendar days.</param>
    /// <param name="rightMicros">The right elapsed microseconds.</param>
    /// <param name="expectedComparison">The independently specified comparison sign.</param>
    [TestMethod]
    [DataRow(0, 0, 0L, 0, 0, 0L, 0)]
    [DataRow(0, 0, -1L, 0, 0, 0L, -1)]
    [DataRow(0, 0, 1L, 0, 0, 0L, 1)]
    [DataRow(1, 0, 0L, 0, 30, 0L, 0)]
    [DataRow(-1, 0, 0L, 0, -30, 0L, 0)]
    [DataRow(0, 1, 0L, 0, 0, 86_400_000_000L, 0)]
    [DataRow(1, -30, -1L, 0, 0, 0L, -1)]
    [DataRow(1, -30, 0L, 0, 0, 0L, 0)]
    [DataRow(1, -30, 1L, 0, 0, 0L, 1)]
    [DataRow(1, -31, 86_400_000_001L, 0, 0, 0L, 1)]
    [DataRow(-1, 31, -86_400_000_001L, 0, 0, 0L, -1)]
    [DataRow(int.MaxValue, int.MaxValue, long.MaxValue, 0, 0, long.MaxValue, 1)]
    [DataRow(int.MinValue, int.MinValue, long.MinValue, 0, 0, long.MinValue, -1)]
    [DataRow(int.MaxValue, int.MinValue, long.MinValue, 0, 0, long.MaxValue, 1)]
    [DataRow(int.MinValue, int.MaxValue, long.MaxValue, 0, 0, long.MinValue, -1)]
    [DataRow(int.MaxValue, 0, long.MinValue, int.MaxValue, 0, long.MaxValue, -1)]
    public void FiniteOrderingUsesPostgresComparisonConvention(int leftMonths, int leftDays, long leftMicros,
        int rightMonths, int rightDays, long rightMicros, int expectedComparison)
    {
        var left = new PgInterval(leftMonths, leftDays, leftMicros);
        var right = new PgInterval(rightMonths, rightDays, rightMicros);
        AssertOrdering(left, right, expectedComparison);
        AssertOrdering(right, left, -expectedComparison);
    }

    /// <summary>
    /// Both explicit infinities bound even finite values containing all signed endpoint components.
    /// </summary>
    [TestMethod]
    public void InfinityOrderingBoundsEveryFiniteRepresentation()
    {
        PgInterval[] finite = [default, new(int.MinValue, int.MinValue, long.MinValue),
            new(int.MaxValue, int.MaxValue, long.MaxValue)];
        foreach (PgInterval value in finite)
        {
            AssertOrdering(PgInterval.NegativeInfinity, value, -1);
            AssertOrdering(value, PgInterval.NegativeInfinity, 1);
            AssertOrdering(PgInterval.PositiveInfinity, value, 1);
            AssertOrdering(value, PgInterval.PositiveInfinity, -1);
        }

        AssertOrdering(PgInterval.NegativeInfinity, PgInterval.PositiveInfinity, -1);
        AssertOrdering(PgInterval.PositiveInfinity, PgInterval.NegativeInfinity, 1);
        AssertOrdering(PgInterval.NegativeInfinity, PgInterval.NegativeInfinity, 0);
        AssertOrdering(PgInterval.PositiveInfinity, PgInterval.PositiveInfinity, 0);
    }

    /// <summary>
    /// The default generic comparer sorts finite, mixed-sign and infinite intervals without backend calls.
    /// </summary>
    [TestMethod]
    public void IntervalsSortOutsideTheBackend()
    {
        PgInterval[] values = [PgInterval.PositiveInfinity, new(1, -30, 1), new(1, -30, -1),
            new(int.MaxValue, int.MaxValue, long.MaxValue), default, PgInterval.NegativeInfinity,
            new(int.MinValue, int.MinValue, long.MinValue)];
        Array.Sort(values);
        Assert.AreSequenceEqual([PgInterval.NegativeInfinity, new(int.MinValue, int.MinValue, long.MinValue),
            new(1, -30, -1), default, new(1, -30, 1), new(int.MaxValue, int.MaxValue, long.MaxValue),
            PgInterval.PositiveInfinity], values);
    }

    /// <summary>
    /// Equivalent intervals share equality and collection membership while retaining independent stored components.
    /// </summary>
    [TestMethod]
    public void ComparisonEqualityPreservesComponentsAndCollectionMembership()
    {
        var month = new PgInterval(1, 0, 0);
        var days = new PgInterval(0, 30, 0);
        var elapsed = new PgInterval(0, 0, 2_592_000_000_000);
        AssertOrdering(month, days, 0);
        AssertOrdering(days, elapsed, 0);
        Assert.AreEqual(month, days);
        Assert.AreEqual(month, elapsed);
        Assert.AreEqual(days, elapsed);
        Assert.AreEqual((1, 0, 0L), (month.Months, month.Days, month.Microseconds));
        Assert.AreEqual((0, 30, 0L), (days.Months, days.Days, days.Microseconds));
        Assert.AreEqual((0, 0, 2_592_000_000_000L), (elapsed.Months, elapsed.Days, elapsed.Microseconds));
        HashSet<PgInterval> identities = [month, days, elapsed];
        SortedSet<PgInterval> ordered = [month, days, elapsed];
        Assert.HasCount(1, identities);
        Assert.HasCount(1, ordered);
        Assert.Contains(elapsed, identities);
        Assert.Contains(elapsed, ordered);
        Assert.DoesNotContain(new PgInterval(1, 0, 1), identities);
        Assert.DoesNotContain(new PgInterval(1, 0, 1), ordered);
    }

    /// <summary>
    /// Absolute values follow Sign and PostgreSQL's comparison, negating every component of a negative interval together.
    /// </summary>
    /// <param name="months">The calendar months.</param>
    /// <param name="days">The calendar days.</param>
    /// <param name="micros">The elapsed microseconds.</param>
    /// <param name="sign">The independently specified comparison sign.</param>
    /// <param name="absoluteMonths">The expected absolute value's months.</param>
    /// <param name="absoluteDays">The expected absolute value's days.</param>
    /// <param name="absoluteMicros">The expected absolute value's microseconds.</param>
    [TestMethod]
    [DataRow(0, 0, 0L, 0, 0, 0, 0L)]
    [DataRow(1, -30, 0L, 0, 1, -30, 0L)]
    [DataRow(-1, 30, 0L, 0, -1, 30, 0L)]
    [DataRow(1, -31, 0L, -1, -1, 31, 0L)]
    [DataRow(-1, 31, 0L, 1, -1, 31, 0L)]
    [DataRow(-1, 2, -3L, -1, 1, -2, 3L)]
    [DataRow(0, -1, 86_400_000_001L, 1, 0, -1, 86_400_000_001L)]
    [DataRow(0, 1, -86_400_000_001L, -1, 0, -1, 86_400_000_001L)]
    [DataRow(1, -30, -1L, -1, -1, 30, 1L)]
    [DataRow(int.MaxValue, 0, long.MinValue, 1, int.MaxValue, 0, long.MinValue)]
    [DataRow(int.MinValue + 1, int.MaxValue, long.MaxValue, -1, int.MaxValue, -int.MaxValue, -long.MaxValue)]
    [DataRow(-int.MaxValue, -int.MaxValue, -long.MaxValue, -1, int.MaxValue, int.MaxValue, long.MaxValue)]
    public void AbsoluteValueFollowsComparisonSign(int months, int days, long micros, int sign,
        int absoluteMonths, int absoluteDays, long absoluteMicros)
    {
        var value = new PgInterval(months, days, micros);
        PgInterval absolute = value.Abs();
        Assert.AreEqual(sign, value.Sign);
        Assert.AreEqual((absoluteMonths, absoluteDays, absoluteMicros), (absolute.Months, absolute.Days, absolute.Microseconds));
        Assert.AreEqual(Math.Abs(sign), absolute.Sign);
        Assert.IsTrue(absolute >= value);
        Assert.IsTrue(absolute >= default(PgInterval));
        Assert.AreEqual(sign == 0, absolute == default(PgInterval));
        Assert.AreEqual(sign >= 0, absolute.Equals(value));
        if (months != int.MinValue && days != int.MinValue && micros != long.MinValue)
        {
            var negated = new PgInterval(-months, -days, -micros);
            Assert.AreEqual(-sign, negated.Sign);
            Assert.IsTrue(absolute >= negated);
            Assert.AreEqual(sign <= 0, absolute.Equals(negated));
            Assert.AreEqual(absolute, negated.Abs());
        }
    }

    /// <summary>
    /// Equivalent representations have equivalent absolute values and hashes while each keeps its own components.
    /// </summary>
    [TestMethod]
    public void EquivalentIntervalsHaveEquivalentAbsoluteValues()
    {
        PgInterval[] negative = [PgInterval.FromMonths(-1), PgInterval.FromDays(-30), PgInterval.FromMicroseconds(-2_592_000_000_000),
            new(-2, 30, 0), new(0, -31, 86_400_000_000)];
        PgInterval[] expected = [PgInterval.FromMonths(1), PgInterval.FromDays(30), PgInterval.FromMicroseconds(2_592_000_000_000),
            new(2, -30, 0), new(0, 31, -86_400_000_000)];
        HashSet<PgInterval> absolutes = [];
        for (int index = 0; index < negative.Length; index++)
        {
            PgInterval absolute = negative[index].Abs();
            Assert.AreEqual((expected[index].Months, expected[index].Days, expected[index].Microseconds),
                (absolute.Months, absolute.Days, absolute.Microseconds));
            Assert.AreEqual(PgInterval.FromMonths(1), absolute);
            Assert.AreEqual(PgInterval.FromMonths(1).GetHashCode(), absolute.GetHashCode());
            Assert.AreEqual(negative[0], negative[index]);
            absolutes.Add(absolute);
        }

        Assert.HasCount(1, absolutes);
    }

    /// <summary>
    /// Only negative intervals are negated, so signed minimum components overflow only when negation is required.
    /// </summary>
    [TestMethod]
    public void AbsoluteValueOverflowsOnlyWhenNegationIsRequired()
    {
        Assert.ThrowsExactly<OverflowException>(() => new PgInterval(int.MinValue, 0, 0).Abs());
        Assert.ThrowsExactly<OverflowException>(() => new PgInterval(0, int.MinValue, 0).Abs());
        Assert.ThrowsExactly<OverflowException>(() => new PgInterval(0, 0, long.MinValue).Abs());
        Assert.ThrowsExactly<OverflowException>(() => new PgInterval(1, 0, long.MinValue).Abs());
        Assert.ThrowsExactly<OverflowException>(() => new PgInterval(int.MinValue, int.MaxValue, long.MaxValue).Abs());
        Assert.ThrowsExactly<OverflowException>(() => new PgInterval(int.MinValue, int.MinValue, long.MinValue).Abs());
        PgInterval positive = new PgInterval(int.MaxValue, int.MinValue, long.MinValue).Abs();
        Assert.AreEqual((int.MaxValue, int.MinValue, long.MinValue), (positive.Months, positive.Days, positive.Microseconds));
        Assert.ThrowsExactly<OverflowException>(() => new PgInterval(1, int.MinValue, 0).Abs());
        PgInterval days = new PgInterval(-1, int.MinValue + 1, 0).Abs();
        Assert.AreEqual((1, int.MaxValue, 0L), (days.Months, days.Days, days.Microseconds));
        Assert.AreEqual(PgInterval.PositiveInfinity, PgInterval.PositiveInfinity.Abs());
        Assert.AreEqual(PgInterval.PositiveInfinity, PgInterval.NegativeInfinity.Abs());
        Assert.IsFalse(PgInterval.NegativeInfinity.Abs().IsFinite);
    }

    /// <summary>
    /// Checks comparison and every ordering operator against an independently specified sign.
    /// </summary>
    /// <param name="left">The left interval.</param>
    /// <param name="right">The right interval.</param>
    /// <param name="expectedComparison">The expected comparison sign.</param>
    private static void AssertOrdering(PgInterval left, PgInterval right, int expectedComparison)
    {
        Assert.AreEqual(expectedComparison, left.CompareTo(right));
        bool[] comparisons = [left < right, left > right, left <= right, left >= right];
        Assert.AreSequenceEqual([expectedComparison < 0, expectedComparison > 0,
            expectedComparison <= 0, expectedComparison >= 0], comparisons);
        Assert.AreEqual(expectedComparison == 0, left.Equals(right));
        Assert.AreEqual(expectedComparison == 0, left.Equals((object)right));
        Assert.AreEqual(expectedComparison == 0, left == right);
        Assert.AreEqual(expectedComparison != 0, left != right);
        object? missing = null;
        Assert.IsFalse(left.Equals(missing));
        Assert.IsFalse(left.Equals("interval"));
        HashSet<PgInterval> hashed = [left, right];
        SortedSet<PgInterval> ordered = [left, right];
        Assert.HasCount(expectedComparison == 0 ? 1 : 2, hashed);
        Assert.HasCount(expectedComparison == 0 ? 1 : 2, ordered);
        if (expectedComparison == 0)
        {
            Assert.AreEqual(left.GetHashCode(), right.GetHashCode());
        }
    }
}
