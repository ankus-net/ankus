using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    /// <summary>
    /// Reads detached bounds from the pointer-free range transport, applying exact .NET scalar conversions.
    /// </summary>
    /// <typeparam name="T">The supported bound type.</typeparam>
    /// <returns>The owned range.</returns>
    public readonly PgRange<T> ReadRange<T>() where T : struct
    {
        if (PgDatumRegistry.Find(typeof(PgRange<T>)) is { RangeBound: not null })
        {
            return ReadMapped<PgRange<T>>();
        }

        if (_isNull != 0 || _auxiliary1 != -2 || _data == null || _length < 8)
        {
            throw new InvalidOperationException("Invalid native range header.");
        }

        ReadOnlySpan<byte> data = new(_data, _length);
        uint subtype = SpiType.GetOid<T>();
        if (BinaryPrimitives.ReadUInt32BigEndian(data) != SpiRange.RangeOid(subtype))
        {
            throw new InvalidCastException("The range subtype does not match the requested managed type.");
        }

        int flags = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        if ((flags & ~31) != 0 || (flags & 1) != 0 && flags != 1 ||
            (flags & 10) == 10 || (flags & 20) == 20)
        {
            throw new InvalidOperationException("Invalid native range flags.");
        }

        int offset = 8;
        T? lower = null;
        T? upper = null;
        if ((flags & 1) == 0)
        {
            if ((flags & 8) == 0)
            {
                lower = ReadRangeBound<T>(data, ref offset, subtype);
            }

            if ((flags & 16) == 0)
            {
                upper = ReadRangeBound<T>(data, ref offset, subtype);
            }
        }

        if (offset != data.Length)
        {
            throw new InvalidOperationException("Unexpected trailing range data.");
        }

        return flags == 1 ? new PgRange<T>() : new PgRange<T>(lower, upper, (flags & 2) != 0, (flags & 4) != 0);
    }

    /// <summary>
    /// Copies an owned range into one contiguous native transport buffer.
    /// </summary>
    /// <typeparam name="T">The supported bound type.</typeparam>
    /// <param name="value">The range.</param>
    /// <returns>The owned native transport, which the caller must release.</returns>
    public static NativeValue FromRange<T>(PgRange<T> value) where T : struct => FromRange((IPgRange)value);

    /// <summary>
    /// Serializes a statically supported range and its scalar bounds without pointers to separate allocations.
    /// </summary>
    /// <param name="value">The range.</param>
    /// <returns>The owned native transport, which the caller must release.</returns>
    internal static NativeValue FromRange(IPgRange value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (PgDatumRegistry.Find(value.GetType()) is { RangeBound: not null } mapping)
        {
            return mapping.Write(value);
        }

        var buffer = new ArrayBufferWriter<byte>();
        WriteInt(buffer, checked((int)value.TypeOid));
        int flags = value.IsEmpty ? 1 : (value.LowerValue is null ? 8 : value.LowerInclusive ? 2 : 0) |
            (value.UpperValue is null ? 16 : value.UpperInclusive ? 4 : 0);
        WriteInt(buffer, flags);
        if (!value.IsEmpty)
        {
            if (value.LowerValue is { } lower)
            {
                WriteRangeBound(buffer, lower);
            }

            if (value.UpperValue is { } upper)
            {
                WriteRangeBound(buffer, upper);
            }
        }

        NativeValue result = FromBytes(buffer.WrittenSpan);
        result._auxiliary1 = -2;
        return result;
    }

    private static T ReadRangeBound<T>(ReadOnlySpan<byte> data, ref int offset, uint subtype) where T : struct
    {
        if (data.Length - offset < 28)
        {
            throw new InvalidOperationException("Truncated range bound.");
        }

        ReadOnlySpan<byte> header = data[offset..];
        int length = BinaryPrimitives.ReadInt32BigEndian(header[24..]);
        int isNull = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
        offset += 28;
        long integral = BinaryPrimitives.ReadInt64BigEndian(header);
        if (isNull != 0 || length < 0 || length > data.Length - offset || subtype != 1700 && length != 0 ||
            subtype == 23 && integral is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidOperationException("Invalid range bound length or NULL marker.");
        }

        fixed (byte* pointer = data)
        {
            var bound = new NativeValue
            {
                _integer = integral,
                _auxiliary1 = BinaryPrimitives.ReadInt32BigEndian(header[8..]),
                _auxiliary2 = BinaryPrimitives.ReadInt32BigEndian(header[12..]),
                _temporalInfinity = BinaryPrimitives.ReadInt32BigEndian(header[16..]),
                _data = pointer + offset,
                _length = length,
            };
            T result = SpiRow.Convert<T>(SpiType.FromNative(bound, subtype));
            offset += length;
            return result;
        }
    }

    private static void WriteRangeBound(ArrayBufferWriter<byte> buffer, object value)
    {
        NativeValue bound = SpiType.ToNative(value);
        try
        {
            Span<byte> header = buffer.GetSpan(28);
            BinaryPrimitives.WriteInt64BigEndian(header, bound._integer);
            BinaryPrimitives.WriteInt32BigEndian(header[8..], bound._auxiliary1);
            BinaryPrimitives.WriteInt32BigEndian(header[12..], bound._auxiliary2);
            BinaryPrimitives.WriteInt32BigEndian(header[16..], bound._temporalInfinity);
            BinaryPrimitives.WriteInt32BigEndian(header[20..], 0);
            BinaryPrimitives.WriteInt32BigEndian(header[24..], bound._length);
            buffer.Advance(28);
            buffer.Write(new ReadOnlySpan<byte>(bound._data, bound._length));
        }
        finally
        {
            bound.Release();
        }
    }
}
