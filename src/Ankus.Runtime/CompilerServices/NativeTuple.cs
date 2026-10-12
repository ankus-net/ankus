using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    [ThreadStatic]
    private static HashSet<PgHeapTuple>? s_writingTuples;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? s_tupleBuffer;

    [ThreadStatic]
    private static PgTupleDescriptor?[]? s_recentDescriptors;

    [ThreadStatic]
    private static int s_nextRecentDescriptor;

    [ThreadStatic]
    private static int s_tupleWriteDepth;

    /// <summary>
    /// Gets whether this value carries a pointer-free tuple envelope.
    /// </summary>
    internal readonly bool IsTuple => _auxiliary1 == -4;

    /// <summary>
    /// Copies a composite or anonymous record, including its physical descriptor and nested owned values.
    /// </summary>
    /// <returns>The detached tuple. A SQL NULL transport must be handled by the caller.</returns>
    /// <remarks>
    /// Rows of one type usually arrive one after another, such as a composite argument on every call. The descriptor of
    /// a recently read row of the same type and modifier is reused when every transported column matches it byte for
    /// byte; otherwise the row gets a descriptor of its own.
    /// </remarks>
    public readonly PgHeapTuple ReadTuple()
    {
        PgTupleDescriptor? recent = FindRecentDescriptor();
        PgHeapTuple tuple = ReadTuple(recent);
        if (!ReferenceEquals(tuple.Descriptor, recent))
        {
            PgTupleDescriptor?[] descriptors = s_recentDescriptors ??= new PgTupleDescriptor?[8];
            descriptors[s_nextRecentDescriptor] = tuple.Descriptor;
            s_nextRecentDescriptor = (s_nextRecentDescriptor + 1) % descriptors.Length;
        }

        return tuple;
    }

    /// <summary>
    /// Finds a recently read descriptor with the transported row type and modifier, without validating the transport.
    /// </summary>
    private readonly PgTupleDescriptor? FindRecentDescriptor()
    {
        if (s_recentDescriptors is not { } descriptors || !IsTuple || _data == null || _length < 12)
        {
            return null;
        }

        ReadOnlySpan<byte> header = new(_data, 8);
        uint oid = BinaryPrimitives.ReadUInt32BigEndian(header);
        int typeModifier = BinaryPrimitives.ReadInt32BigEndian(header[4..]);
        foreach (PgTupleDescriptor? descriptor in descriptors)
        {
            if (descriptor is not null && descriptor.TypeOid == oid && descriptor.TypeModifier == typeModifier)
            {
                return descriptor;
            }
        }

        return null;
    }

    /// <summary>
    /// Copies a tuple, reusing a known descriptor when every transported attribute matches it, as each row of one
    /// relation does.
    /// </summary>
    /// <param name="expected">The descriptor to reuse, or null to build one from the transport.</param>
    /// <returns>The detached tuple. A SQL NULL transport must be handled by the caller.</returns>
    internal readonly PgHeapTuple ReadTuple(PgTupleDescriptor? expected)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        PgTupleDescriptor descriptor = ReadTupleDescriptor(false, out object?[] values, expected);
        return PgHeapTuple.FromOwnedCells(descriptor, values);
    }

    /// <summary>
    /// Copies physical relation metadata, retaining an absent row type on older PostgreSQL indexes.
    /// </summary>
    internal readonly PgTupleDescriptor ReadRelationDescriptor() => ReadTupleDescriptor(true, out _);

    /// <summary>
    /// Serializes a tuple and its descriptor into an owned native buffer without embedding managed pointers.
    /// Cyclic values are rejected, and nested conversion checks the available execution stack.
    /// </summary>
    /// <param name="value">The tuple to copy.</param>
    /// <returns>The owned transport buffer.</returns>
    public static NativeValue FromTuple(PgHeapTuple value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RuntimeHelpers.EnsureSufficientExecutionStack();

        // Only a cell that can hold another row can lead back to this one, so plain rows skip cycle tracking.
        bool tracked = !HasOnlyLeafCells(value);
        if (tracked && !(s_writingTuples ??= []).Add(value))
        {
            throw new InvalidOperationException("Composite values cannot contain reference cycles.");
        }

        // The outermost row reuses one buffer per thread; its bytes are copied to native memory before it returns.
        ArrayBufferWriter<byte> buffer = s_tupleWriteDepth == 0 ? s_tupleBuffer ??= new() : new();
        buffer.ResetWrittenCount();
        s_tupleWriteDepth++;
        try
        {
            WriteInt(buffer, unchecked((int)value.Descriptor.TypeOid));
            WriteInt(buffer, value.Descriptor.TypeModifier);
            WriteInt(buffer, value.Count);
            for (int index = 0; index < value.Count; index++)
            {
                PgTupleAttributeInfo attribute = value.Descriptor.Attributes[index];
                ReadOnlySpan<byte> name = attribute.Utf8Name;
                if (name.Length > 252)
                {
                    throw new InvalidOperationException("Tuple attribute names exceed PostgreSQL's identifier capacity.");
                }

                EnsureContainerCapacity(buffer, 24L + name.Length);
                WriteInt(buffer, unchecked((int)attribute.TypeOid));
                WriteInt(buffer, unchecked((int)attribute.BaseTypeOid));
                WriteInt(buffer, attribute.TypeModifier);
                WriteInt(buffer, unchecked((int)attribute.CollationOid));
                WriteInt(buffer, (attribute.IsDropped ? 1 : 0) | (attribute.IsNotNull ? 2 : 0) |
                    (attribute.IsComposite ? 4 : 0) | (attribute.IsUnavailable ? 8 : 0));
                WriteInt(buffer, name.Length);
                buffer.Write(name);
                object? cell = attribute.IsUnavailable ? null : value.GetStoredCell(index);
                CustomTypeMapping? mapping = PgTypeRegistry.FindValue(cell, attribute.BaseTypeOid);
                if (cell is string text && mapping is null && PgTypeRegistry.Find(typeof(string)) is null)
                {
                    WriteContainerText(buffer, text);
                    continue;
                }

                NativeValue item = SpiType.ToNative(cell, mapping,
                    cell is Array array ? PgTypeRegistry.FindArrayValue(array, attribute.BaseTypeOid) : null);
                try
                {
                    WriteContainerValue(buffer, item);
                }
                finally
                {
                    item.Release();
                }
            }

            NativeValue result = FromBytes(buffer.WrittenSpan);
            result._auxiliary1 = -4;
            result._integer = value.Descriptor.BaseTypeOid;
            return result;
        }
        finally
        {
            s_tupleWriteDepth--;
            if (s_tupleWriteDepth == 0 && buffer.Capacity > 65536)
            {
                // A large row does not keep its buffer for the rest of the backend's life.
                s_tupleBuffer = null;
            }

            if (tracked)
            {
                s_writingTuples!.Remove(value);
            }
        }
    }

    /// <summary>
    /// Recognizes a row whose cells are SQL NULL, text, bytes or primitive values, none of which can contain a row.
    /// </summary>
    private static bool HasOnlyLeafCells(PgHeapTuple value)
    {
        for (int index = 0; index < value.Count; index++)
        {
            // An unavailable generated column has no value to write.
            if (!value.Descriptor.Attributes[index].IsUnavailable && value.GetStoredCell(index) is not (null or string or byte[] or bool or
                char or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal or Guid))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates the shared transport while keeping absent row identities confined to physical relation metadata.
    /// </summary>
    private readonly PgTupleDescriptor ReadTupleDescriptor(bool physicalMetadata, out object?[] values,
        PgTupleDescriptor? expected = null)
    {
        if (!IsTuple || _auxiliary2 != 0 || _isNull != 0 || _data == null || _length < 12)
        {
            throw new InvalidOperationException("Invalid native tuple header.");
        }

        ReadOnlySpan<byte> data = new(_data, _length);
        uint oid = BinaryPrimitives.ReadUInt32BigEndian(data);
        int typeModifier = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        int count = BinaryPrimitives.ReadInt32BigEndian(data[8..]);
        if ((oid == 0 && (!physicalMetadata || _integer != 0 || typeModifier != -1)) ||
            _integer is < 0 or > uint.MaxValue || count is < 0 or > 1664 || 12L + count * 52L > data.Length)
        {
            throw new InvalidOperationException("Invalid native tuple identity or attribute count.");
        }

        uint baseTypeOid = _integer == 0 ? oid : (uint)_integer;
        bool reuse = expected is not null && expected.TypeOid == oid && expected.TypeModifier == typeModifier &&
            expected.BaseTypeOid == baseTypeOid && expected.Attributes.Count == count;
        PgTupleAttributeInfo[]? attributes = reuse ? null : new PgTupleAttributeInfo[count];
        values = new object?[count];
        int offset = 12;
        for (int index = 0; index < count; index++)
        {
            if (data.Length - offset < 24)
            {
                throw new InvalidOperationException("Truncated native tuple attribute.");
            }

            ReadOnlySpan<byte> header = data[offset..];
            uint declaredOid = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint baseOid = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            int modifier = BinaryPrimitives.ReadInt32BigEndian(header[8..]);
            uint collation = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
            int flags = BinaryPrimitives.ReadInt32BigEndian(header[16..]);
            int nameLength = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
            offset += 24;
            if ((flags & ~15) != 0 || nameLength is < 0 or > 252 || nameLength > data.Length - offset)
            {
                throw new InvalidOperationException("Invalid native tuple attribute metadata.");
            }

            ReadOnlySpan<byte> nameBytes = data.Slice(offset, nameLength);
            offset += nameLength;
            bool dropped = (flags & 1) != 0;
            bool composite = (flags & 4) != 0;
            bool unavailable = (flags & 8) != 0;
            PgTupleAttributeInfo? attribute = null;
            if (reuse)
            {
                PgTupleAttributeInfo known = expected!.Attributes[index];
                if (known.TypeOid == declaredOid && known.BaseTypeOid == baseOid && known.TypeModifier == modifier &&
                    known.CollationOid == collation && known.IsDropped == dropped && known.IsNotNull == ((flags & 2) != 0) &&
                    known.IsComposite == composite && known.IsUnavailable == unavailable && nameBytes.SequenceEqual(known.Utf8Name))
                {
                    attribute = known;
                }
                else
                {
                    // The first difference ends reuse; the attributes already read are the known ones.
                    reuse = false;
                    attributes = new PgTupleAttributeInfo[count];
                    for (int prior = 0; prior < index; prior++)
                    {
                        attributes[prior] = expected.Attributes[prior];
                    }
                }
            }

            if (attribute is null)
            {
                string name = s_utf8.GetString(nameBytes);
                if (name.Contains('\0', StringComparison.Ordinal) || (!dropped && (name.Length == 0 || declaredOid == 0 || baseOid == 0)))
                {
                    throw new InvalidOperationException("Invalid native tuple attribute name or identity.");
                }

                attribute = new PgTupleAttributeInfo(name, declaredOid, baseOid, modifier, collation, dropped, (flags & 2) != 0, composite,
                    unavailable);
            }

            NativeValue item = ReadContainerValue(data, ref offset);
            if (((physicalMetadata || dropped || unavailable) && item.IsNull == 0) || (item.IsNull == 0 && composite != item.IsTuple))
            {
                throw new InvalidOperationException("Tuple field transport does not match its descriptor.");
            }

            if (attributes is not null)
            {
                attributes[index] = attribute;
            }

            values[index] = SpiType.FromNative(item, baseOid);
            if (values[index] is PgHeapTuple nested && baseOid != 2249 && nested.Descriptor.BaseTypeOid != baseOid)
            {
                throw new InvalidOperationException("Nested tuple identity does not match its attribute type.");
            }
        }

        if (offset != data.Length)
        {
            throw new InvalidOperationException("Unexpected trailing native tuple data.");
        }

        return reuse ? expected! : new PgTupleDescriptor(oid, typeModifier, attributes!, baseTypeOid, physicalMetadata);
    }

    private readonly NativeValue ReadContainerValue(ReadOnlySpan<byte> data, ref int offset)
    {
        if (data.Length - offset < 28)
        {
            throw new InvalidOperationException("Truncated native container value.");
        }

        ReadOnlySpan<byte> header = data[offset..];
        int length = BinaryPrimitives.ReadInt32BigEndian(header[24..]);
        int isNull = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
        if (length < 0 || length > data.Length - offset - 28 || isNull is < 0 or > 1 || (isNull == 1 && length != 0))
        {
            throw new InvalidOperationException("Invalid native container value.");
        }

        var result = new NativeValue
        {
            _integer = BinaryPrimitives.ReadInt64BigEndian(header),
            _auxiliary1 = BinaryPrimitives.ReadInt32BigEndian(header[8..]),
            _auxiliary2 = BinaryPrimitives.ReadInt32BigEndian(header[12..]),
            _temporalInfinity = BinaryPrimitives.ReadInt32BigEndian(header[16..]),
            _isNull = (byte)isNull,
            _length = length,
            _data = _data + offset + 28,
        };

        offset += 28 + length;
        return result;
    }

    private static void WriteContainerValue(ArrayBufferWriter<byte> buffer, NativeValue item)
    {
        EnsureContainerCapacity(buffer, 28L + item._length);
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

    /// <summary>
    /// Writes text as <see cref="NativeValue.FromString(string)"/> would transport it, encoding it straight into the
    /// container instead of into a native buffer that is then copied and freed.
    /// </summary>
    private static void WriteContainerText(ArrayBufferWriter<byte> buffer, string value)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("PostgreSQL text cannot contain a zero character.", nameof(value));
        }

        int length = s_utf8.GetByteCount(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 0x3FFFFFFF - 4);
        EnsureContainerCapacity(buffer, 28L + length);
        // A non-null text value has no scalar, auxiliary or infinity fields; only its length is set.
        Span<byte> header = buffer.GetSpan(28);
        header[..24].Clear();
        BinaryPrimitives.WriteInt32BigEndian(header[24..], length);
        buffer.Advance(28);
        buffer.Advance(s_utf8.GetBytes(value, buffer.GetSpan(length)));
    }

    private static void EnsureContainerCapacity(ArrayBufferWriter<byte> buffer, long additional)
    {
        if (buffer.WrittenCount + additional > 0x3FFFFFFF - 4)
        {
            throw new InvalidOperationException("The converted container exceeds PostgreSQL's buffer capacity.");
        }
    }
}
