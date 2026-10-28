using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    /// <summary>
    /// Gets whether the auxiliary field marks a pointer-free array transport for a non-scalar datum.
    /// </summary>
    internal readonly bool IsArray => _auxiliary1 == -1;

    /// <summary>
    /// Reads an owned array with its PostgreSQL dimensions and lower bounds.
    /// </summary>
    /// <typeparam name="T">The scalar element type.</typeparam>
    /// <returns>The converted array. SQL NULL elements require reference or nullable value types.</returns>
    public readonly PgArray<T> ReadArray<T>() => ReadArrayData<T>(ReadArrayElementOid<T>(), PgEnumRegistry.Find(typeof(T)));

    /// <summary>
    /// Reads a one-dimensional array with lower bound one, or an empty array, as a managed vector without an
    /// intermediate <see cref="PgArray{T}"/>.
    /// </summary>
    /// <typeparam name="T">The scalar element type.</typeparam>
    /// <returns>The converted elements. SQL NULL elements require reference or nullable value types.</returns>
    /// <exception cref="InvalidOperationException">The array has several dimensions or another lower bound.</exception>
    public readonly T[] ReadVector<T>()
    {
        uint oid = ReadArrayElementOid<T>();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return ReadArrayValues<T>(oid, PgEnumRegistry.Find(typeof(T)), vector: true, out _, out _);
    }

    /// <summary>
    /// Validates the transport and its element type against the requested managed element type.
    /// </summary>
    private readonly uint ReadArrayElementOid<T>()
    {
        PgDatumRegistry.RejectOrdinaryArray(typeof(PgArray<T>));
        uint oid = ReadArrayElementOid();
        if (typeof(T) == typeof(PgHeapTuple) ? _auxiliary2 != 2 : CharacterTextElementOid(SpiType.GetOid<T>()) != oid)
        {
            throw new InvalidCastException($"Array element OID {oid} cannot be read as '{typeof(T)}'.");
        }

        return oid;
    }

    /// <summary>
    /// Copies an array into one allocator-matched native transport buffer.
    /// </summary>
    /// <typeparam name="T">The scalar element type.</typeparam>
    /// <param name="value">The array.</param>
    /// <returns>The owned transport.</returns>
    public static NativeValue FromArray<T>(PgArray<T> value) => FromArray((IPgArray)value);

    /// <summary>
    /// Copies a vector of a primitive that PostgreSQL stores as a packed native array straight into the transport,
    /// without an intermediate <see cref="PgArray{T}"/>.
    /// </summary>
    /// <typeparam name="T"><see langword="bool"/>, <see langword="sbyte"/>, <see langword="short"/>, <see langword="int"/>,
    /// <see langword="long"/>, <see langword="uint"/>, <see langword="float"/> or <see langword="double"/>.</typeparam>
    /// <param name="value">The vector.</param>
    /// <returns>The owned transport.</returns>
    public static NativeValue FromVector<T>(T[] value) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(value);
        uint oid = typeof(T) == typeof(bool) ? 16u : typeof(T) == typeof(sbyte) ? 18u : typeof(T) == typeof(short) ? 21u :
            typeof(T) == typeof(int) ? 23u : typeof(T) == typeof(long) ? 20u : typeof(T) == typeof(uint) ? 26u :
            typeof(T) == typeof(float) ? 700u : typeof(T) == typeof(double) ? 701u :
            throw new NotSupportedException($"'{typeof(T)}' is not a packed PostgreSQL array element.");
        int rank = value.Length == 0 ? 0 : 1;
        long length = 12L + rank * 8L + (long)value.Length * Unsafe.SizeOf<T>();
        if (length > 0x3FFFFFFF - 4)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The converted array exceeds PostgreSQL's buffer capacity.");
        }

        NativeValue result = Allocate((int)length);
        Span<byte> data = new(result._data, result._length);
        BinaryPrimitives.WriteInt32BigEndian(data, rank);
        BinaryPrimitives.WriteInt32BigEndian(data[4..], value.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data[8..], oid);
        if (rank == 1)
        {
            BinaryPrimitives.WriteInt32BigEndian(data[12..], value.Length);
            BinaryPrimitives.WriteInt32BigEndian(data[16..], 1);
        }

        MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(data[(12 + rank * 8)..]);
        result._auxiliary1 = -1;
        result._auxiliary2 = 4;
        return result;
    }

    /// <summary>
    /// Serializes array shape and scalar elements into one allocator-matched native buffer.
    /// </summary>
    /// <param name="value">The source array.</param>
    /// <returns>The owned transport, which the caller must release.</returns>
    internal static NativeValue FromArray(IPgArray value)
    {
        ArgumentNullException.ThrowIfNull(value);
        PgDatumRegistry.RejectOrdinaryArray(value.GetType());
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return FromArrayCore(value);
    }

    private static NativeValue FromArrayCore(IPgArray value)
    {
        CustomTypeMapping? customMapping = PgTypeRegistry.FindArray(value.GetType());
        var buffer = new ArrayBufferWriter<byte>();
        WriteInt(buffer, value.Lengths.Length);
        WriteInt(buffer, value.Count);
        WriteInt(buffer, unchecked((int)value.ElementOid));
        for (int index = 0; index < value.Lengths.Length; index++)
        {
            WriteInt(buffer, value.Lengths[index]);
            WriteInt(buffer, value.LowerBounds[index]);
        }

        if (customMapping is null && value.TryWritePacked(buffer))
        {
            NativeValue packed = FromBytes(buffer.WrittenSpan);
            packed._auxiliary1 = -1;
            packed._auxiliary2 = 4;
            return packed;
        }

        if (value is PgArray<string> { ElementTypeOid: 25 } texts && NativeMemoryContext.IsUtf8Database)
        {
            WritePackedText(texts, buffer);
            NativeValue packed = FromBytes(buffer.WrittenSpan);
            packed._auxiliary1 = -1;
            packed._auxiliary2 = 5;
            return packed;
        }

        for (int index = 0; index < value.Count; index++)
        {
            NativeValue item = SpiType.ToNative(value.GetElement(index), customMapping);
            try
            {
                if ((long)buffer.WrittenCount + 28 + item._length > 0x3FFFFFFF - 4)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "The converted array exceeds PostgreSQL's buffer capacity.");
                }

                Span<byte> header = buffer.GetSpan(28);
                BinaryPrimitives.WriteInt64BigEndian(header, item._integer);
                BinaryPrimitives.WriteInt32BigEndian(header[8..], item._auxiliary1);
                BinaryPrimitives.WriteInt32BigEndian(header[12..], item._auxiliary2);
                BinaryPrimitives.WriteInt32BigEndian(header[16..], item._temporalInfinity);
                BinaryPrimitives.WriteInt32BigEndian(header[20..], item._isNull);
                BinaryPrimitives.WriteInt32BigEndian(header[24..], item._length);
                buffer.Advance(28);
                buffer.Write(new ReadOnlySpan<byte>(item._data, item._length));
            }
            finally
            {
                item.Release();
            }
        }

        NativeValue result = FromBytes(buffer.WrittenSpan);
        result._auxiliary1 = -1;
        result._auxiliary2 = value is PgArray<PgHeapTuple> ? 2 : customMapping is not null ? 3 :
            PgEnumRegistry.FindArray(value.GetType()) is null ? 0 : 1;
        if (value is PgArray<PgHeapTuple> tuples)
        {
            result._integer = tuples.ElementBaseTypeOid;
        }

        return result;
    }

    private readonly uint ReadArrayElementOid()
    {
        if (!IsArray || _isNull != 0 || _data == null || _length < 12)
        {
            throw new InvalidOperationException("Invalid native array header.");
        }

        uint oid = CharacterTextElementOid(BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(_data + 8, 4)));
        if (_auxiliary2 == 0 || (_auxiliary2 == 4 && PackedWidth(oid) != 0) || (_auxiliary2 == 5 && oid == 25))
        {
            _ = SpiArray.ArrayOid(oid);
        }
        else if (_auxiliary2 is not 1 and not 2 and not 3 || oid == 0 || (_auxiliary2 == 2 && _integer is < 0 or > uint.MaxValue))
        {
            throw new InvalidOperationException("Invalid array element conversion discriminator.");
        }

        return oid;
    }

    /// <summary>
    /// Reads bpchar and varchar elements as text, which shares their storage.
    /// </summary>
    /// <param name="oid">The element type.</param>
    /// <returns>The text type for character text, otherwise the element type.</returns>
    private static uint CharacterTextElementOid(uint oid) => oid is 1042 or 1043 ? 25 : oid;

    /// <summary>
    /// Decodes validated array elements using one resolved enum mapping when applicable.
    /// </summary>
    internal readonly PgArray<T> ReadArrayData<T>(uint oid, EnumMapping? enumeration = null)
    {
        PgDatumRegistry.RejectOrdinaryArray(typeof(PgArray<T>));
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return ReadArrayDataCore<T>(oid, enumeration);
    }

    private readonly PgArray<T> ReadArrayDataCore<T>(uint oid, EnumMapping? enumeration)
    {
        T[] values = ReadArrayValues<T>(oid, enumeration, vector: false, out int[] lengths, out int[] bounds);
        uint baseOid = _auxiliary2 == 2 && _integer != 0 ? (uint)_integer : oid;
        return new PgArray<T>(values, (lengths, bounds), _auxiliary2 == 2 ? oid : 0, _auxiliary2 == 2 ? baseOid : 0);
    }

    /// <summary>
    /// Decodes the elements after validating the shape, which a vector must keep one-dimensional with lower bound one.
    /// </summary>
    private readonly T[] ReadArrayValues<T>(uint oid, EnumMapping? enumeration, bool vector, out int[] lengths, out int[] bounds)
    {
        ReadOnlySpan<byte> data = new(_data, _length);
        int rank = BinaryPrimitives.ReadInt32BigEndian(data);
        int count = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        int width = _auxiliary2 == 4 ? PackedWidth(oid) : 0;
        long minimum = 12L + rank * 8L + count * (width != 0 ? width : _auxiliary2 == 5 ? 4L : 28L);
        if (rank is < 0 or > 6 || count < 0 || (width == 0 ? minimum > data.Length : minimum != data.Length))
        {
            throw new InvalidOperationException("Invalid native array shape or element count.");
        }

        lengths = new int[rank];
        bounds = new int[rank];
        int offset = 12;
        for (int index = 0; index < rank; index++, offset += 8)
        {
            lengths[index] = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
            bounds[index] = BinaryPrimitives.ReadInt32BigEndian(data[(offset + 4)..]);
        }

        SpiArray.ValidateShape(count, lengths, bounds);
        if (count == 0 && rank != 0)
        {
            throw new InvalidOperationException("Empty native arrays must have rank zero.");
        }

        if (vector && (rank > 1 || (rank == 1 && bounds[0] != 1)))
        {
            throw new InvalidOperationException(SpiArray.VectorShapeMessage);
        }

        var values = new T[count];
        try
        {
            if (width != 0)
            {
                ReadPackedElements(values, data[offset..], oid);
            }
            else if (_auxiliary2 == 5)
            {
                ReadPackedText(values, data[offset..]);
            }
            else
            {
                ReadArrayElements(values, data, offset, oid, enumeration);
            }

            return values;
        }
        catch (Exception primary)
        {
            NativeRelationScope.Release(values, primary);
            VarlenaCleanup.Release<T>(values, primary);
            throw;
        }
    }

    /// <summary>
    /// Gets the width of an element type whose array storage, without NULLs, is a packed native array.
    /// </summary>
    /// <param name="oid">The element type.</param>
    /// <returns>The element width, or zero for other types.</returns>
    private static int PackedWidth(uint oid) => oid switch
    {
        16 or 18 => 1,
        21 => 2,
        23 or 26 or 700 => 4,
        20 or 701 => 8,
        _ => 0,
    };

    /// <summary>
    /// Copies elements that PostgreSQL stored as a packed native array, as one block when the managed element type is
    /// the native one.
    /// </summary>
    private static void ReadPackedElements<T>(T[] values, ReadOnlySpan<byte> data, uint oid)
    {
        switch (oid)
        {
            case 16:
                if (data.ContainsAnyExcept((byte)0, (byte)1))
                {
                    throw new InvalidOperationException("Invalid native Boolean array element.");
                }

                CopyPacked<bool, T>(values, data);
                break;
            case 18:
                CopyPacked<sbyte, T>(values, data);
                break;
            case 21:
                CopyPacked<short, T>(values, data);
                break;
            case 23:
                CopyPacked<int, T>(values, data);
                break;
            case 26:
                CopyPacked<uint, T>(values, data);
                break;
            case 700:
                CopyPacked<float, T>(values, data);
                break;
            case 20:
                CopyPacked<long, T>(values, data);
                break;
            case 701:
                CopyPacked<double, T>(values, data);
                break;
            default:
                throw new InvalidOperationException("Invalid packed native array element type.");
        }
    }

    /// <summary>
    /// Decodes UTF-8 text elements sent as a native length, or -1 for SQL NULL, followed by their bytes.
    /// </summary>
    private static void ReadPackedText<T>(T[] values, ReadOnlySpan<byte> data)
    {
        int offset = 0;
        for (int index = 0; index < values.Length; index++)
        {
            if (data.Length - offset < 4)
            {
                throw new InvalidOperationException("Truncated native array element.");
            }

            int length = MemoryMarshal.Read<int>(data[offset..]);
            offset += 4;
            if (length == -1)
            {
                values[index] = SpiRow.Convert<T>(null);
                continue;
            }

            if (length < 0 || length > data.Length - offset)
            {
                throw new InvalidOperationException("Invalid native array element.");
            }

            string text = s_utf8.GetString(data.Slice(offset, length));
            offset += length;
            values[index] = text is T typed ? typed : SpiRow.Convert<T>(text);
        }

        if (offset != data.Length)
        {
            throw new InvalidOperationException("Unexpected trailing native array data.");
        }
    }

    /// <summary>
    /// Encodes UTF-8 text elements as a native length, or -1 for SQL NULL, followed by their bytes.
    /// </summary>
    private static void WritePackedText(PgArray<string> value, ArrayBufferWriter<byte> buffer)
    {
        foreach (string? text in value)
        {
            if (text is null)
            {
                MemoryMarshal.Write(buffer.GetSpan(4), -1);
                buffer.Advance(4);
                continue;
            }

            int length = s_utf8.GetByteCount(text);
            if ((long)buffer.WrittenCount + 4 + length > 0x3FFFFFFF - 4)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The converted array exceeds PostgreSQL's buffer capacity.");
            }

            Span<byte> target = buffer.GetSpan(4 + length);
            MemoryMarshal.Write(target, length);
            s_utf8.GetBytes(text, target[4..]);
            buffer.Advance(4 + length);
        }
    }

    private static void CopyPacked<TElement, T>(T[] values, ReadOnlySpan<byte> data) where TElement : unmanaged
    {
        int width = Unsafe.SizeOf<TElement>();
        if (typeof(T) == typeof(TElement))
        {
            data.CopyTo(MemoryMarshal.AsBytes(Unsafe.As<TElement[]>(values).AsSpan()));
        }
        else if (typeof(T) == typeof(TElement?))
        {
            TElement?[] target = Unsafe.As<TElement?[]>(values);
            for (int index = 0; index < target.Length; index++)
            {
                target[index] = MemoryMarshal.Read<TElement>(data[(index * width)..]);
            }
        }
        else
        {
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = SpiRow.Convert<T>(MemoryMarshal.Read<TElement>(data[(index * width)..]));
            }
        }
    }

    /// <summary>
    /// Converts each independently owned element after validating the collection's dimensions.
    /// </summary>
    private readonly void ReadArrayElements<T>(T[] values, ReadOnlySpan<byte> data, int offset, uint oid, EnumMapping? enumeration)
    {
        uint baseOid = _auxiliary2 == 2 && _integer != 0 ? (uint)_integer : oid;
        // A scalar element of the requested type needs none of the mapped-array checks a general conversion makes.
        for (int index = 0; index < values.Length; index++)
        {
            if (data.Length - offset < 28)
            {
                throw new InvalidOperationException("Truncated native array element.");
            }

            ReadOnlySpan<byte> header = data[offset..];
            int length = BinaryPrimitives.ReadInt32BigEndian(header[24..]);
            int isNull = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
            if (length < 0 || length > data.Length - offset - 28 || isNull is < 0 or > 1 || (isNull == 1 && length != 0))
            {
                throw new InvalidOperationException("Invalid native array element.");
            }

            var item = new NativeValue
            {
                _integer = BinaryPrimitives.ReadInt64BigEndian(header),
                _auxiliary1 = BinaryPrimitives.ReadInt32BigEndian(header[8..]),
                _auxiliary2 = BinaryPrimitives.ReadInt32BigEndian(header[12..]),
                _temporalInfinity = BinaryPrimitives.ReadInt32BigEndian(header[16..]),
                _isNull = (byte)isNull,
                _length = length,
                _data = _data + offset + 28,
            };
            object? element = item.IsNull != 0 ? null : _auxiliary2 == 2 ? item.ReadTuple() :
                enumeration is null ? SpiType.FromNative(item, oid) : enumeration.FromLabel(item.ReadString());
            if (element is PgHeapTuple tuple && baseOid != 2249 && tuple.Descriptor.BaseTypeOid != baseOid)
            {
                throw new InvalidOperationException("Composite array element identity does not match its array type.");
            }

            values[index] = element is T typed and not Array ? typed : SpiRow.Convert<T>(element);
            offset += 28 + length;
        }

        if (offset != data.Length)
        {
            throw new InvalidOperationException("Unexpected trailing native array data.");
        }
    }

    /// <summary>
    /// Materializes the transport's scalar element type with nullable value elements and owned managed storage.
    /// </summary>
    /// <returns>The converted array, retaining its dimensions and lower bounds.</returns>
    internal readonly IPgArray ReadArray()
    {
        uint oid = ReadArrayElementOid();
        if (_auxiliary2 == 2)
        {
            return ReadArrayData<PgHeapTuple?>(oid);
        }

        if (_auxiliary2 == 3)
        {
            return PgTypeRegistry.FindOid(oid).ReadArray(this, oid);
        }

        return oid switch
        {
            16 => ReadArrayData<bool?>(oid),
            17 => ReadArrayData<byte[]?>(oid),
            18 => ReadArrayData<sbyte?>(oid),
            20 => ReadArrayData<long?>(oid),
            21 => ReadArrayData<short?>(oid),
            23 => ReadArrayData<int?>(oid),
            25 => ReadArrayData<string?>(oid),
            2275 => ReadArrayData<PgCString?>(oid),
            26 => ReadArrayData<uint?>(oid),
            27 => ReadArrayData<PgItemPointer?>(oid),
            28 => ReadArrayData<PgTransactionId?>(oid),
            2205 => ReadArrayData<PgRelationIdentity?>(oid),
            700 => ReadArrayData<float?>(oid),
            701 => ReadArrayData<double?>(oid),
            2950 => ReadArrayData<Guid?>(oid),
            114 => ReadArrayData<PgJson?>(oid),
            3802 => ReadArrayData<PgJsonb?>(oid),
            1700 => ReadArrayData<PgNumeric?>(oid),
            1082 => ReadArrayData<PgDate?>(oid),
            1083 => ReadArrayData<PgTime?>(oid),
            1266 => ReadArrayData<PgTimeTz?>(oid),
            1114 => ReadArrayData<PgTimestamp?>(oid),
            1184 => ReadArrayData<PgTimestampTz?>(oid),
            1186 => ReadArrayData<PgInterval?>(oid),
            869 => ReadArrayData<PgInet?>(oid),
            650 => ReadArrayData<PgCidr?>(oid),
            600 => ReadArrayData<PgPoint?>(oid),
            601 => ReadArrayData<PgLineSegment?>(oid),
            602 => ReadArrayData<PgPath>(oid),
            603 => ReadArrayData<PgBox?>(oid),
            604 => ReadArrayData<PgPolygon>(oid),
            628 => ReadArrayData<PgLine?>(oid),
            718 => ReadArrayData<PgCircle?>(oid),
            3904 => ReadArrayData<PgRange<int>>(oid),
            3926 => ReadArrayData<PgRange<long>>(oid),
            3906 => ReadArrayData<PgRange<PgNumeric>>(oid),
            3912 => ReadArrayData<PgRange<PgDate>>(oid),
            3908 => ReadArrayData<PgRange<PgTimestamp>>(oid),
            3910 => ReadArrayData<PgRange<PgTimestampTz>>(oid),
            _ => PgEnumRegistry.FindOid(oid).ReadArray(this, oid),
        };
    }

    private static void WriteInt(ArrayBufferWriter<byte> buffer, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(buffer.GetSpan(4), value);
        buffer.Advance(4);
    }
}
