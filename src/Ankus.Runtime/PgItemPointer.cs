using System;
using System.Globalization;

namespace Ankus;

/// <summary>
/// Represents PostgreSQL's physical tuple location, <c>tid</c>, as exact block and offset numbers.
/// </summary>
/// <param name="BlockNumber">The raw unsigned block number.</param>
/// <param name="OffsetNumber">The raw unsigned offset number, including zero and native special values.</param>
/// <remarks>
/// An invalid offset is distinct from SQL NULL. This managed value is not a native ItemPointerData layout.
/// Tuple locations can change when PostgreSQL moves or rewrites a row; they are not permanent row identifiers.
/// </remarks>
public readonly record struct PgItemPointer(uint BlockNumber, ushort OffsetNumber) : IComparable<PgItemPointer>
{
    /// <summary>
    /// Gets PostgreSQL's invalid item pointer, with the maximum block number and offset zero.
    /// </summary>
    public static PgItemPointer Invalid => new(uint.MaxValue, 0);

    /// <summary>
    /// Gets PostgreSQL's marker for a tuple moved to another partition.
    /// </summary>
    public static PgItemPointer MovedPartitions => new(uint.MaxValue, 0xfffd);

    /// <summary>
    /// Gets whether the offset is nonzero, matching PostgreSQL's item-pointer validity check.
    /// </summary>
    public bool IsValid => OffsetNumber != 0;

    /// <summary>
    /// Gets whether this value is the native moved-partition marker.
    /// </summary>
    public bool IndicatesMovedPartitions => this == MovedPartitions;

    /// <summary>
    /// Reads the block number after requiring a nonzero offset.
    /// </summary>
    /// <returns>The block number.</returns>
    /// <exception cref="InvalidOperationException">The item pointer has offset zero.</exception>
    public uint GetBlockNumber()
    {
        EnsureValid();
        return BlockNumber;
    }

    /// <summary>
    /// Reads the offset number after requiring a nonzero offset.
    /// </summary>
    /// <returns>The offset number.</returns>
    /// <exception cref="InvalidOperationException">The item pointer has offset zero.</exception>
    public ushort GetOffsetNumber()
    {
        EnsureValid();
        return OffsetNumber;
    }

    /// <summary>
    /// Packs the block into bits 32–63 and the offset into bits 0–15, matching pgrx's item_pointer_to_u64.
    /// </summary>
    /// <returns>The sparse pgrx encoding, including for invalid item pointers.</returns>
    public ulong ToUInt64() => ((ulong)BlockNumber << 32) | OffsetNumber;

    /// <summary>
    /// Decodes the pgrx representation without discarding any nonzero bits.
    /// </summary>
    /// <param name="value">The sparse representation with bits 16–31 zero.</param>
    /// <returns>The exact item pointer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An unused bit is nonzero.</exception>
    public static PgItemPointer FromUInt64(ulong value)
    {
        if ((value & 0xffff0000UL) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The packed item-pointer encoding requires bits 16 through 31 to be zero.");
        }

        return FromUInt64Truncating(value);
    }

    /// <summary>
    /// Decodes the pgrx representation while explicitly discarding bits 16–31, matching its raw helper.
    /// </summary>
    /// <param name="value">The raw sparse representation.</param>
    /// <returns>The selected block and offset bits.</returns>
    public static PgItemPointer FromUInt64Truncating(ulong value) => new((uint)(value >> 32), unchecked((ushort)value));

    /// <summary>
    /// Packs a valid item pointer into PostgreSQL's dense index key, with the block shifted by 16 bits.
    /// </summary>
    /// <returns>The nonnegative 48-bit key used by PostgreSQL's itemptr_encode.</returns>
    /// <exception cref="InvalidOperationException">The item pointer has offset zero.</exception>
    public long ToIndexKey()
    {
        EnsureValid();
        return ((long)BlockNumber << 16) | OffsetNumber;
    }

    /// <summary>
    /// Decodes a nonnegative 48-bit PostgreSQL index key without losing bits.
    /// </summary>
    /// <param name="value">The dense index key; a zero offset is preserved.</param>
    /// <returns>The exact item pointer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The key is negative or exceeds 48 bits.</exception>
    public static PgItemPointer FromIndexKey(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 0xffffffffffffL);
        return FromIndexKeyTruncating(value);
    }

    /// <summary>
    /// Decodes the low 48 bits of a PostgreSQL index key, explicitly matching itemptr_decode's truncation.
    /// </summary>
    /// <param name="value">The raw dense representation.</param>
    /// <returns>The selected block and offset bits.</returns>
    public static PgItemPointer FromIndexKeyTruncating(long value) => new(unchecked((uint)(value >> 16)), unchecked((ushort)value));

    /// <summary>
    /// Advances the raw 48-bit location, carrying into the block and saturating at the maximum value.
    /// </summary>
    /// <returns>The next location, which may have offset zero.</returns>
    public PgItemPointer Increment() => OffsetNumber != ushort.MaxValue
        ? new(BlockNumber, (ushort)(OffsetNumber + 1))
        : BlockNumber != uint.MaxValue ? new(BlockNumber + 1, 0) : this;

    /// <summary>
    /// Retreats the raw 48-bit location, borrowing from the block and saturating at zero.
    /// </summary>
    /// <returns>The preceding location, which may have offset zero.</returns>
    public PgItemPointer Decrement() => OffsetNumber != 0
        ? new(BlockNumber, (ushort)(OffsetNumber - 1))
        : BlockNumber != 0 ? new(BlockNumber - 1, ushort.MaxValue) : this;

    /// <summary>
    /// Compares unsigned blocks followed by unsigned offsets, including invalid values, as PostgreSQL's tid operators do.
    /// </summary>
    /// <param name="other">The other location.</param>
    /// <returns>A negative value, zero, or a positive value according to the ordering.</returns>
    public int CompareTo(PgItemPointer other)
    {
        int block = BlockNumber.CompareTo(other.BlockNumber);
        return block == 0 ? OffsetNumber.CompareTo(other.OffsetNumber) : block;
    }

    /// <summary>
    /// Compares two locations in PostgreSQL tuple order.
    /// </summary>
    public static bool operator <(PgItemPointer left, PgItemPointer right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Compares two locations in PostgreSQL tuple order, including equality.
    /// </summary>
    public static bool operator <=(PgItemPointer left, PgItemPointer right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Compares two locations in reverse PostgreSQL tuple order.
    /// </summary>
    public static bool operator >(PgItemPointer left, PgItemPointer right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Compares two locations in reverse PostgreSQL tuple order, including equality.
    /// </summary>
    public static bool operator >=(PgItemPointer left, PgItemPointer right)
    {
        return left.CompareTo(right) >= 0;
    }

    /// <summary>
    /// Formats both raw fields as PostgreSQL's invariant <c>(block,offset)</c> text.
    /// </summary>
    /// <returns>The tuple-location text.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({BlockNumber},{OffsetNumber})");

    private void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("The PostgreSQL item pointer has offset zero.");
        }
    }
}
