namespace Ankus;

/// <summary>
/// Owns the bounds of a built-in or explicitly mapped PostgreSQL range. A null reference is SQL NULL; a parameterless instance is empty.
/// Construction retains the requested bounds. PostgreSQL validates and canonicalizes them on datum conversion.
/// </summary>
/// <typeparam name="T">A supported built-in bound or a closed value type declaring both PgDatumType and PgRangeType.</typeparam>
public sealed class PgRange<T> : IEquatable<PgRange<T>>, IPgRange where T : struct
{
    /// <summary>
    /// Creates an empty range of the supported bound type.
    /// </summary>
    public PgRange()
    {
        SpiRange.Require(typeof(PgRange<T>));
        IsEmpty = true;
    }

    /// <summary>
    /// Creates a bounded or unbounded range without backend access. Null bounds mean unbounded ends.
    /// </summary>
    /// <param name="lower">The lower value, or null for an unbounded end.</param>
    /// <param name="upper">The upper value, or null for an unbounded end.</param>
    /// <param name="lowerInclusive">Whether the lower value is included; ignored when unbounded.</param>
    /// <param name="upperInclusive">Whether the upper value is included; ignored when unbounded.</param>
    public PgRange(T? lower, T? upper, bool lowerInclusive = true, bool upperInclusive = false)
    {
        SpiRange.Require(typeof(PgRange<T>));
        Lower = lower;
        Upper = upper;
        LowerInclusive = lower.HasValue && lowerInclusive;
        UpperInclusive = upper.HasValue && upperInclusive;
    }

    /// <summary>
    /// Gets whether this value represents an empty range, which has no bounds.
    /// </summary>
    public bool IsEmpty { get; }

    /// <summary>
    /// Gets the lower bound, or null for an empty range or unbounded lower end.
    /// </summary>
    public T? Lower { get; }

    /// <summary>
    /// Gets the upper bound, or null for an empty range or unbounded upper end.
    /// </summary>
    public T? Upper { get; }

    /// <summary>
    /// Gets whether the present lower bound is included, including an explicit subtype infinity value.
    /// </summary>
    public bool LowerInclusive { get; }

    /// <summary>
    /// Gets whether the present upper bound is included, including an explicit subtype infinity value.
    /// </summary>
    public bool UpperInclusive { get; }

    /// <summary>
    /// Gets whether the nonempty range has no lower or upper bound.
    /// </summary>
    public bool IsUnbounded => !IsEmpty && Lower is null && Upper is null;

    /// <summary>
    /// Validates and canonicalizes the bounds using PostgreSQL on the active backend.
    /// </summary>
    /// <returns>The canonical range, which can be empty even when this instance is not.</returns>
    public PgRange<T> Canonicalize() => NativeBackend.Range<PgRange<T>>(RangeOperation.Canonicalize, [SpiParameter.Create(this)]);

    /// <summary>
    /// Tests value containment using PostgreSQL's subtype ordering and canonical range semantics.
    /// </summary>
    /// <param name="value">The candidate value.</param>
    /// <returns>Whether the range contains the value.</returns>
    public bool Contains(T value) => NativeBackend.Range<bool>(RangeOperation.ContainsValue, [SpiParameter.Create(this), SpiParameter.Create(value)]);

    /// <summary>
    /// Tests whether this range contains another range on the active backend.
    /// </summary>
    /// <param name="other">The candidate range.</param>
    /// <returns>Whether it is contained.</returns>
    public bool Contains(PgRange<T> other) => Test(RangeOperation.ContainsRange, other);

    /// <summary>
    /// Tests whether two ranges overlap on the active backend.
    /// </summary>
    /// <param name="other">The other range.</param>
    /// <returns>Whether the ranges overlap.</returns>
    public bool Overlaps(PgRange<T> other) => Test(RangeOperation.Overlaps, other);

    /// <summary>
    /// Tests whether two ranges are adjacent on the active backend.
    /// </summary>
    /// <param name="other">The other range.</param>
    /// <returns>Whether the ranges are adjacent.</returns>
    public bool IsAdjacentTo(PgRange<T> other) => Test(RangeOperation.Adjacent, other);

    /// <summary>
    /// Unites overlapping or adjacent ranges. PostgreSQL raises an error if the result would be disjoint.
    /// </summary>
    /// <param name="other">The other range.</param>
    /// <returns>The canonical union.</returns>
    public PgRange<T> Union(PgRange<T> other) => Combine(RangeOperation.Union, other);

    /// <summary>
    /// Intersects two ranges on the active backend.
    /// </summary>
    /// <param name="other">The other range.</param>
    /// <returns>The intersection, possibly empty.</returns>
    public PgRange<T> Intersect(PgRange<T> other) => Combine(RangeOperation.Intersect, other);

    /// <summary>
    /// Subtracts a range. PostgreSQL raises an error if the result would require two separate ranges.
    /// </summary>
    /// <param name="other">The range to subtract.</param>
    /// <returns>The canonical difference.</returns>
    public PgRange<T> Except(PgRange<T> other) => Combine(RangeOperation.Difference, other);

    /// <summary>
    /// Returns the smallest range spanning both operands, including any gap between them.
    /// </summary>
    /// <param name="other">The other range.</param>
    /// <returns>The spanning range.</returns>
    public PgRange<T> Merge(PgRange<T> other) => Combine(RangeOperation.Merge, other);

    /// <summary>
    /// Formats the canonical range using PostgreSQL on the active backend, including session temporal settings.
    /// </summary>
    /// <returns>The PostgreSQL range text.</returns>
    public string ToPostgresString() => NativeBackend.Range<string>(RangeOperation.Format, [SpiParameter.Create(this)]);

    /// <summary>
    /// Describes the stored bounds without backend access or canonicalization. Use ToPostgresString for SQL input text.
    /// </summary>
    /// <returns>A diagnostic representation using the bound types' formatting.</returns>
    public override string ToString() => IsEmpty ? "empty" :
        (LowerInclusive ? "[" : "(") + System.Convert.ToString(Lower, System.Globalization.CultureInfo.InvariantCulture) + "," +
        System.Convert.ToString(Upper, System.Globalization.CultureInfo.InvariantCulture) + (UpperInclusive ? "]" : ")");

    /// <summary>
    /// Compares stored bounds and inclusion flags without backend access or canonicalization.
    /// </summary>
    /// <param name="other">The other range.</param>
    /// <returns>Whether their stored representations are equal.</returns>
    public bool Equals(PgRange<T>? other) => other is not null && IsEmpty == other.IsEmpty &&
        LowerInclusive == other.LowerInclusive && UpperInclusive == other.UpperInclusive &&
        Nullable.Equals(Lower, other.Lower) && Nullable.Equals(Upper, other.Upper);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PgRange<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsEmpty, Lower, Upper, LowerInclusive, UpperInclusive);

    uint IPgRange.TypeOid => SpiRange.GetOid(typeof(PgRange<T>));
    object? IPgRange.LowerValue => Lower;
    object? IPgRange.UpperValue => Upper;

    private bool Test(RangeOperation operation, PgRange<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return NativeBackend.Range<bool>(operation, [SpiParameter.Create(this), SpiParameter.Create(other)]);
    }

    private PgRange<T> Combine(RangeOperation operation, PgRange<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return NativeBackend.Range<PgRange<T>>(operation, [SpiParameter.Create(this), SpiParameter.Create(other)]);
    }
}
