using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    /// <summary>
    /// Gets the slots a trigger call site sends unchanged on every call: names, arguments, columns and UPDATE OF
    /// columns.
    /// </summary>
    private static ReadOnlySpan<int> SiteSlots => [3, 4, 5, 6, 7, 8, 11, 12];

    [ThreadStatic]
    private static TriggerSite? s_triggerSite;

    /// <summary>
    /// Copies a validated sixteen-slot native trigger invocation into owned managed context and row values.
    /// Input buffers remain borrowed; the caller retains responsibility for releasing their native storage.
    /// </summary>
    /// <param name="arguments">The generated native trigger argument envelope.</param>
    /// <returns>The detached trigger context.</returns>
    /// <remarks>
    /// The last slot identifies the native call site, which sends the same buffers for its names, arguments and
    /// columns on every call. A call from the site read last reuses what was parsed then, and its rows reuse the
    /// parsed descriptor; any other call, or one whose buffers differ, is read in full.
    /// </remarks>
    public static PgTriggerContext ReadTriggerContext(ReadOnlySpan<NativeValue> arguments)
    {
        if (arguments.Length != 16)
        {
            throw new InvalidOperationException("A native trigger invocation must contain exactly sixteen context slots.");
        }

        uint eventBits = ReadTriggerUnsigned(arguments[0]);
        uint relationOid = ReadTriggerUnsigned(arguments[1]);
        uint triggerOid = ReadTriggerUnsigned(arguments[2]);
        long identity = ReadTriggerSiteIdentity(arguments[15]);
        TriggerSite site = s_triggerSite is { } cached && cached.Matches(identity, relationOid, triggerOid, arguments)
            ? cached
            : ReadTriggerSite(identity, relationOid, triggerOid, arguments);
        PgHeapTuple? oldRow = ReadTriggerIsNull(arguments[9]) ? null : arguments[9].ReadTriggerRow(site);
        PgHeapTuple? newRow = ReadTriggerIsNull(arguments[10]) ? null : arguments[10].ReadTriggerRow(site);
        int[]? updated = ReadTriggerIsNull(arguments[13]) ? null : arguments[13].ReadArray<int>().ToVector();
        (uint constraintOid, int constraintFlags) = ReadTriggerConstraint(arguments[14]);
        return new PgTriggerContext(site.Definition, eventBits, oldRow, newRow, updated, constraintOid, constraintFlags,
            metadataValidated: true);
    }

    /// <summary>
    /// Recognizes a trigger returning the NEW row it was given, unchanged, which PostgreSQL can keep as it is without
    /// receiving a converted copy.
    /// </summary>
    /// <param name="context">The trigger's context.</param>
    /// <param name="row">The row the trigger returned.</param>
    /// <returns>
    /// Whether the row is the context's NEW row with no replaced cells. A row holding a mutable value, such as an array,
    /// could have changed in place and is always converted.
    /// </returns>
    public static bool IsUnchangedTriggerRow(PgTriggerContext context, PgHeapTuple row)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(row);
        return ReferenceEquals(row, context.New) && !row.IsModified && HasOnlyScalarCells(row, includeBytes: false);
    }

    /// <summary>
    /// Converts the row a trigger returns, sending only the cells PostgreSQL needs when the row has the relation's
    /// columns.
    /// </summary>
    /// <param name="context">The trigger's context.</param>
    /// <param name="row">The row the trigger returned.</param>
    /// <returns>An owned transport: one value per column, or a self-describing row for a row of another shape.</returns>
    /// <remarks>
    /// A cell of the context's NEW row that was not replaced and holds an immutable value is sent as kept, and
    /// PostgreSQL keeps its original datum without converting it back. Text is encoded into the same allocation as
    /// the cells.
    /// </remarks>
    public static NativeValue FromTriggerRow(PgTriggerContext context, PgHeapTuple row) => FromTriggerRow(context, row, null, 0);

    /// <summary>
    /// Converts the row a trigger returns, as <see cref="FromTriggerRow(PgTriggerContext, PgHeapTuple)"/> does, into the
    /// buffer the native call site lends for it when the row fits.
    /// </summary>
    /// <param name="context">The trigger's context.</param>
    /// <param name="row">The row the trigger returned.</param>
    /// <param name="arguments">The invocation's native envelope, whose last slot carries the lent buffer, if any.</param>
    /// <returns>A transport that borrows the lent buffer, or an owned one as the two-argument overload returns.</returns>
    /// <remarks>
    /// A site lends its buffer only to a call it is not already running, and keeps it until its statement ends, so a
    /// row that fits needs no allocation and, unless a cell owns native memory, no release. A row of the relation's
    /// columns reports the bytes it needed, and the site enlarges its buffer for the rows that follow.
    /// </remarks>
    public static NativeValue FromTriggerRow(PgTriggerContext context, PgHeapTuple row, ReadOnlySpan<NativeValue> arguments)
    {
        if (arguments.Length != 16)
        {
            throw new InvalidOperationException("A native trigger invocation must contain exactly sixteen context slots.");
        }

        ref readonly NativeValue site = ref arguments[15];
        _ = ReadTriggerSiteIdentity(site);
        return FromTriggerRow(context, row, site._data, site._length);
    }

    private static NativeValue FromTriggerRow(PgTriggerContext context, PgHeapTuple row, byte* lent, int capacity)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(row);
        if (!ReferenceEquals(row.Descriptor, context.Descriptor) && !ReferenceEquals(row.Descriptor, context.New?.Descriptor) &&
            !ReferenceEquals(row.Descriptor, context.Old?.Descriptor))
        {
            return FromTuple(row);
        }

        bool fromNew = ReferenceEquals(row, context.New);
        ReadOnlySpan<PgTupleAttributeInfo> attributes = row.Descriptor.AttributeSpan;
        int count = attributes.Length;
        long cellCapacity = Math.Max(count, 1);
        long size = sizeof(long) + (cellCapacity * sizeof(NativeValue));

        // The UTF-8 length of each text cell encoded into the block, or minus one for any other cell.
        Span<int> encoded = count <= 64 ? stackalloc int[count] : new int[count];
        for (int index = 0; index < count; index++)
        {
            encoded[index] = -1;
            if (GetEncodedText(row, attributes[index], index, fromNew) is { } text)
            {
                encoded[index] = GetTextByteCount(text);

                // Each text keeps the terminating zero byte that every native text buffer has.
                size += encoded[index] + 1;
                if (size > 0x3FFFFFFF)
                {
                    throw new ArgumentOutOfRangeException(nameof(row), "The trigger row's text exceeds PostgreSQL's allocation limit.");
                }
            }
        }

        bool borrowed = size <= capacity;
        long* block = borrowed ? (long*)lent : (long*)NativeMemory.Alloc((nuint)size);
        *block = 0;
        var cells = (NativeValue*)(block + 1);
        new Span<NativeValue>(cells, (int)cellCapacity).Clear();
        byte* next = (byte*)(cells + cellCapacity);
        bool released = false;
        try
        {
            for (int index = 0; index < count; index++)
            {
                PgTupleAttributeInfo attribute = attributes[index];
                ref NativeValue cell = ref cells[index];
                *block = index + 1;
                if (attribute.IsDropped || attribute.IsUnavailable)
                {
                    cell._isNull = 1;
                    cell._auxiliary1 = attribute.IsUnavailable ? -12 : 0;
                    continue;
                }

                object? value = row.GetAvailableCell(index);
                if (encoded[index] >= 0)
                {
                    int length = s_utf8.GetBytes((string)value!, new Span<byte>(next, encoded[index]));
                    next[length] = 0;
                    cell._data = next;
                    cell._length = length;
                    next += length + 1;
                    continue;
                }

                if (fromNew && !row.IsReplaced(index) && IsImmutableCell(value, includeBytes: false))
                {
                    // PostgreSQL keeps the datum this cell was read from.
                    cell._isNull = 1;
                    cell._auxiliary1 = -11;
                    continue;
                }

                cell = SpiType.ToNative(value, PgTypeRegistry.FindValue(value, attribute.BaseTypeOid),
                    value is Array array ? PgTypeRegistry.FindArrayValue(array, attribute.BaseTypeOid) : null);
                released |= cell._release != null;
            }
        }
        catch
        {
            ReleaseCellValues(cells);
            if (!borrowed)
            {
                NativeMemory.Free(block);
            }

            throw;
        }

        return new NativeValue
        {
            _data = (byte*)cells,
            _length = count,
            _auxiliary1 = -10,
            _auxiliary2 = (int)size,
            _integer = row.Descriptor.BaseTypeOid,
            _release = !borrowed ? &ReleaseCells : released ? &ReleaseLentCells : null,
        };
    }

    /// <summary>
    /// Gets a returned row's text cell that is encoded directly into the row's transport: replaced text of the context's
    /// NEW row, or any text of another row of the relation, unless a generated type mapping claims it.
    /// </summary>
    private static string? GetEncodedText(PgHeapTuple row, PgTupleAttributeInfo attribute, int index, bool fromNew)
    {
        if (attribute.IsDropped || attribute.IsUnavailable || row.GetAvailableCell(index) is not string text ||
            (fromNew && !row.IsReplaced(index)) || PgTypeRegistry.FindValue(text, attribute.BaseTypeOid) is not null)
        {
            return null;
        }

        return text;
    }

    /// <summary>
    /// Counts the UTF-8 bytes of text PostgreSQL can store, which cannot contain a zero character.
    /// </summary>
    private static int GetTextByteCount(string text) => text.Contains('\0', StringComparison.Ordinal)
        ? throw new ArgumentException("PostgreSQL text cannot contain a zero character.", nameof(text))
        : s_utf8.GetByteCount(text);

    /// <summary>
    /// Reads a trigger row over the call site's descriptor. Native code sends one value per column of the relation;
    /// a self-describing row, as tests build, is read as any row is.
    /// </summary>
    private readonly PgHeapTuple ReadTriggerRow(TriggerSite site)
    {
        if (_auxiliary1 != -10)
        {
            return ReadTuple(site.Definition.Descriptor);
        }

        PgTupleDescriptor known = site.Definition.Descriptor;
        int count = known.AttributeSpan.Length;
        if (_isNull != 0 || _length != count || (_data == null && count != 0) || _integer != known.BaseTypeOid)
        {
            throw new InvalidOperationException("A trigger row does not match its relation's columns.");
        }

        var cells = new ReadOnlySpan<NativeValue>(_data, count);
        PgTupleDescriptor descriptor = site.GetDescriptor(cells);
        ReadOnlySpan<PgTupleAttributeInfo> attributes = descriptor.AttributeSpan;
        object?[] values = new object?[count];
        for (int index = 0; index < count; index++)
        {
            PgTupleAttributeInfo attribute = attributes[index];
            ref readonly NativeValue cell = ref cells[index];
            if (cell._isNull != 0 && cell._auxiliary1 is 0 or -12)
            {
                continue;
            }

            if (cell._isNull != 0 || cell._auxiliary1 == -12 || attribute.IsDropped || cell.IsTuple != attribute.IsComposite)
            {
                throw new InvalidOperationException("A trigger row cell does not match its column.");
            }

            values[index] = SpiType.FromNative(cell, attribute.BaseTypeOid);
            if (values[index] is PgHeapTuple nested && attribute.BaseTypeOid != 2249 && nested.Descriptor.BaseTypeOid != attribute.BaseTypeOid)
            {
                throw new InvalidOperationException("Nested tuple identity does not match its attribute type.");
            }
        }

        return PgHeapTuple.FromOwnedCells(descriptor, values);
    }

    /// <summary>
    /// Recognizes SQL NULL, text and primitive values, which cannot change after they were read, optionally also
    /// byte arrays.
    /// </summary>
    private static bool IsImmutableCell(object? cell, bool includeBytes)
        => cell is null or string or bool or char or sbyte or byte or short or ushort or int or uint or long or ulong or float or
            double or decimal or Guid || (includeBytes && cell is byte[]);

    /// <summary>
    /// Releases the cells of a trigger row transport and the block that holds them.
    /// </summary>
    /// <param name="data">
    /// The first cell. The block starts with the number of cells written, and text encoded into the block after the
    /// cells has no release of its own.
    /// </param>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseCells(void* data)
    {
        var cells = (NativeValue*)data;
        ReleaseCellValues(cells);
        NativeMemory.Free((long*)cells - 1);
    }

    /// <summary>
    /// Releases the cells of a trigger row transport written into a buffer the native call site lent, which the site
    /// keeps.
    /// </summary>
    /// <param name="data">The first cell, laid out as <see cref="ReleaseCells(void*)"/> describes.</param>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseLentCells(void* data) => ReleaseCellValues((NativeValue*)data);

    private static void ReleaseCellValues(NativeValue* cells)
    {
        long* block = (long*)cells - 1;
        for (long index = 0; index < *block; index++)
        {
            cells[index].Release();
        }

        *block = 0;
    }

    /// <summary>
    /// Parses a call site's unchanging slots and, when the site is identified, keeps them for its next call.
    /// </summary>
    private static TriggerSite ReadTriggerSite(long identity, uint relationOid, uint triggerOid, ReadOnlySpan<NativeValue> arguments)
    {
        string name = ReadTriggerName(arguments[3]);
        string tableName = ReadTriggerName(arguments[4]);
        string tableSchema = ReadTriggerName(arguments[5]);
        string? oldTransition = ReadTriggerIsNull(arguments[6]) ? null : ReadTriggerName(arguments[6]);
        string? newTransition = ReadTriggerIsNull(arguments[7]) ? null : ReadTriggerName(arguments[7]);
        string[] triggerArguments = arguments[8].ReadArray<string>().ToVector();
        PgHeapTuple descriptor = arguments[11].ReadTuple();
        for (int index = 0; index < descriptor.Count; index++)
        {
            if (!descriptor.Descriptor.AttributeSpan[index].IsUnavailable && descriptor[index] is not null)
            {
                throw new InvalidOperationException("A trigger descriptor envelope must contain only SQL NULL cells.");
            }
        }

        int[] updateOf = arguments[12].ReadArray<int>().ToVector();

        // The site's names, arguments and columns are the same for every row it sends, so they are checked here once.
        PgTriggerContext.ValidateMetadata(name, tableName, tableSchema, oldTransition, newTransition, triggerArguments, updateOf,
            descriptor.Descriptor);
        var buffers = new (nint Data, int Length, byte IsNull)[SiteSlots.Length];
        for (int index = 0; index < SiteSlots.Length; index++)
        {
            NativeValue slot = arguments[SiteSlots[index]];
            buffers[index] = ((nint)slot._data, slot._length, slot._isNull);
        }

        var site = new TriggerSite(identity, buffers, new PgTriggerDefinition(relationOid, triggerOid, name, tableName, tableSchema,
            oldTransition, newTransition, Array.AsReadOnly(triggerArguments), descriptor.Descriptor, Array.AsReadOnly(updateOf)));
        if (identity != 0)
        {
            s_triggerSite = site;
        }

        return site;
    }

    /// <summary>
    /// Reads the native call site's identity; zero marks an invocation that is not cached.
    /// </summary>
    private static long ReadTriggerSiteIdentity(in NativeValue value)
    {
        // The site can also lend a buffer for the returned row.
        if (value._isNull != 0 || value._integer < 0 || (value._data == null) != (value._length == 0) || value._length < 0 ||
            value._auxiliary1 != 0 || value._auxiliary2 != 0 || value._temporalInfinity != 0)
        {
            throw new InvalidOperationException("Invalid native trigger call site envelope.");
        }

        return value._integer;
    }

    private static (uint Oid, int Flags) ReadTriggerConstraint(in NativeValue value)
    {
        if (value._isNull != 0 || value._integer is < 0 or > uint.MaxValue || value._data != null || value._length != 0 ||
            value._auxiliary1 is < 0 or > 3 || value._auxiliary2 != 0 || value._temporalInfinity != 0)
        {
            throw new InvalidOperationException("Invalid native trigger constraint envelope.");
        }

        return ((uint)value._integer, value._auxiliary1);
    }

    private static uint ReadTriggerUnsigned(in NativeValue value)
    {
        if (value._isNull != 0 || value._integer is < 0 or > uint.MaxValue || value._data != null || value._length != 0 ||
            value._auxiliary1 != 0 || value._auxiliary2 != 0 || value._temporalInfinity != 0)
        {
            throw new InvalidOperationException("Invalid native trigger event or OID envelope.");
        }

        return (uint)value._integer;
    }

    private static string ReadTriggerName(in NativeValue value)
    {
        if (value._isNull != 0 || value._integer != 0 || value._data == null || value._length is < 1 or > 252 ||
            value._auxiliary1 != 0 || value._auxiliary2 != 0 || value._temporalInfinity != 0)
        {
            throw new InvalidOperationException("Invalid native trigger name envelope.");
        }

        return value.ReadString();
    }

    private static bool ReadTriggerIsNull(in NativeValue value)
    {
        if (value._isNull == 0)
        {
            return false;
        }

        if (value._isNull != 1 || value._integer != 0 || value._data != null || value._length != 0 ||
            value._auxiliary1 != 0 || value._auxiliary2 != 0 || value._temporalInfinity != 0)
        {
            throw new InvalidOperationException("Invalid native trigger SQL NULL envelope.");
        }

        return true;
    }

    /// <summary>
    /// Holds what one native trigger call site sends unchanged, parsed once, with the buffers it was parsed from.
    /// </summary>
    /// <param name="identity">The native call site's identity.</param>
    /// <param name="buffers">The parsed slots' native buffers, in <see cref="SiteSlots"/> order.</param>
    /// <param name="definition">The trigger, its relation and its arguments, shared by every context the site creates.</param>
    private sealed class TriggerSite(long identity, (nint Data, int Length, byte IsNull)[] buffers, PgTriggerDefinition definition)
    {
        private PgTupleDescriptor? _unavailable;

        /// <summary>
        /// Gets the trigger, its relation and its arguments.
        /// </summary>
        internal PgTriggerDefinition Definition => definition;

        /// <summary>
        /// Gets the descriptor for a row's cells: the site's own, or one that marks the generated columns PostgreSQL
        /// has not computed yet, kept for the site's next row.
        /// </summary>
        internal PgTupleDescriptor GetDescriptor(ReadOnlySpan<NativeValue> cells)
        {
            int first = 0;
            while (first < cells.Length && cells[first]._auxiliary1 != -12)
            {
                first++;
            }

            PgTupleDescriptor descriptor = definition.Descriptor;
            if (first == cells.Length)
            {
                return descriptor;
            }

            if (_unavailable is { } cached && Marks(cached, cells))
            {
                return cached;
            }

            var attributes = new PgTupleAttributeInfo[cells.Length];
            for (int index = 0; index < cells.Length; index++)
            {
                PgTupleAttributeInfo known = descriptor.AttributeSpan[index];
                bool unavailable = cells[index]._auxiliary1 == -12;
                attributes[index] = unavailable == known.IsUnavailable ? known : new PgTupleAttributeInfo(known.Name, known.TypeOid,
                    known.BaseTypeOid, known.TypeModifier, known.CollationOid, known.IsDropped, known.IsNotNull, known.IsComposite, unavailable);
            }

            _unavailable = new PgTupleDescriptor(descriptor.TypeOid, descriptor.TypeModifier, attributes, descriptor.BaseTypeOid);
            return _unavailable;
        }

        private static bool Marks(PgTupleDescriptor variant, ReadOnlySpan<NativeValue> cells)
        {
            for (int index = 0; index < cells.Length; index++)
            {
                if (variant.AttributeSpan[index].IsUnavailable != (cells[index]._auxiliary1 == -12))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks that a call comes from this site and sends the very buffers this site was parsed from.
        /// </summary>
        internal bool Matches(long callIdentity, uint callRelation, uint callTrigger, ReadOnlySpan<NativeValue> slots)
        {
            if (callIdentity == 0 || callIdentity != identity || callRelation != definition.RelationOid ||
                callTrigger != definition.TriggerOid)
            {
                return false;
            }

            ReadOnlySpan<int> indexes = SiteSlots;
            ReadOnlySpan<(nint Data, int Length, byte IsNull)> expected = buffers;
            for (int index = 0; index < expected.Length; index++)
            {
                ref readonly NativeValue slot = ref slots[indexes[index]];
                if ((nint)slot._data != expected[index].Data || slot._length != expected[index].Length ||
                    slot._isNull != expected[index].IsNull)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
