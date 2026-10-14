using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    /// <summary>
    /// The slots a trigger call site sends unchanged on every call: names, arguments, columns and UPDATE OF columns.
    /// </summary>
    private static readonly int[] s_siteSlots = [3, 4, 5, 6, 7, 8, 11, 12];

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
        return new PgTriggerContext(eventBits, relationOid, triggerOid, site.Name, site.TableName, site.TableSchema,
            site.OldTransition, site.NewTransition, site.Arguments, oldRow, newRow, site.Descriptor,
            site.UpdateOf, updated, constraintOid, constraintFlags, metadataValidated: true);
    }

    /// <summary>
    /// Recognizes a trigger returning the NEW row it was given, unchanged, which PostgreSQL can keep as it is instead
    /// of receiving a converted copy.
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
    /// PostgreSQL keeps its original datum instead of converting it back.
    /// </remarks>
    public static NativeValue FromTriggerRow(PgTriggerContext context, PgHeapTuple row)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(row);
        if (!ReferenceEquals(row.Descriptor, context.Descriptor) && !ReferenceEquals(row.Descriptor, context.New?.Descriptor) &&
            !ReferenceEquals(row.Descriptor, context.Old?.Descriptor))
        {
            return FromTuple(row);
        }

        bool fromNew = ReferenceEquals(row, context.New);
        int count = row.Count;
        long capacity = Math.Max(count, 1);
        long* block = (long*)NativeMemory.AllocZeroed((nuint)(sizeof(long) + (capacity * sizeof(NativeValue)) + capacity));
        *block = capacity << 32;
        var cells = (NativeValue*)(block + 1);
        byte* owned = (byte*)(cells + capacity);
        try
        {
            for (int index = 0; index < count; index++)
            {
                PgTupleAttributeInfo attribute = row.Descriptor.Attributes[index];
                ref NativeValue cell = ref cells[index];
                *block = (capacity << 32) | (uint)(index + 1);
                if (attribute.IsDropped || attribute.IsUnavailable)
                {
                    cell._isNull = 1;
                    cell._auxiliary1 = attribute.IsUnavailable ? -12 : 0;
                    continue;
                }

                object? value = row.GetStoredCell(index);
                if (fromNew && !row.IsReplaced(index) && IsImmutableCell(value, includeBytes: false))
                {
                    // PostgreSQL keeps the datum this cell was read from.
                    cell._isNull = 1;
                    cell._auxiliary1 = -11;
                    continue;
                }

                CustomTypeMapping? mapping = PgTypeRegistry.FindValue(value, attribute.BaseTypeOid);
                if (value is string text && mapping is null && PgTypeRegistry.Find(typeof(string)) is null)
                {
                    cell = FromString(text);
                    cell._release = null;
                    owned[index] = 1;
                    continue;
                }

                cell = SpiType.ToNative(value, mapping, value is Array array ? PgTypeRegistry.FindArrayValue(array, attribute.BaseTypeOid) : null);
            }
        }
        catch
        {
            ReleaseCells(cells);
            throw;
        }

        return new NativeValue
        {
            _data = (byte*)cells,
            _length = count,
            _auxiliary1 = -10,
            _integer = row.Descriptor.BaseTypeOid,
            _release = &ReleaseCells,
        };
    }

    /// <summary>
    /// Reads a trigger row over the call site's descriptor. Native code sends one value per column of the relation;
    /// a self-describing row, as tests build, is read as any row is.
    /// </summary>
    private readonly PgHeapTuple ReadTriggerRow(TriggerSite site)
    {
        if (_auxiliary1 != -10)
        {
            return ReadTuple(site.Descriptor);
        }

        int count = site.Descriptor.Attributes.Count;
        if (_isNull != 0 || _length != count || (_data == null && count != 0) || _integer != site.Descriptor.BaseTypeOid)
        {
            throw new InvalidOperationException("A trigger row does not match its relation's columns.");
        }

        var cells = new ReadOnlySpan<NativeValue>(_data, count);
        PgTupleDescriptor descriptor = site.GetDescriptor(cells);
        object?[] values = new object?[count];
        for (int index = 0; index < count; index++)
        {
            PgTupleAttributeInfo attribute = descriptor.Attributes[index];
            NativeValue cell = cells[index];
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
    /// The first cell. The block starts with the row's column count in its high half and the number of cells written in
    /// its low half, and ends with one byte per cell that is nonzero for text this runtime encoded.
    /// </param>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseCells(void* data) => ReleaseCells((NativeValue*)data);

    private static void ReleaseCells(NativeValue* cells)
    {
        long* block = (long*)cells - 1;
        byte* owned = (byte*)(cells + Math.Max(*block >> 32, 1));
        *block &= uint.MaxValue;
        for (long index = 0; index < *block; index++)
        {
            // Text this method encoded is freed directly rather than through a native release entry.
            if (owned[index] != 0)
            {
                NativeMemory.Free(cells[index]._data);
            }
            else
            {
                cells[index].Release();
            }
        }

        NativeMemory.Free(block);
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
            if (!descriptor.Descriptor.Attributes[index].IsUnavailable && descriptor[index] is not null)
            {
                throw new InvalidOperationException("A trigger descriptor envelope must contain only SQL NULL cells.");
            }
        }

        int[] updateOf = arguments[12].ReadArray<int>().ToVector();

        // The site's names, arguments and columns are the same for every row it sends, so they are checked here once.
        PgTriggerContext.ValidateMetadata(name, tableName, tableSchema, oldTransition, newTransition, triggerArguments, updateOf,
            descriptor.Descriptor);
        var buffers = new (nint Data, int Length, byte IsNull)[s_siteSlots.Length];
        for (int index = 0; index < s_siteSlots.Length; index++)
        {
            NativeValue slot = arguments[s_siteSlots[index]];
            buffers[index] = ((nint)slot._data, slot._length, slot._isNull);
        }

        var site = new TriggerSite(identity, relationOid, triggerOid, buffers, name, tableName, tableSchema, oldTransition,
            newTransition, Array.AsReadOnly(triggerArguments), descriptor.Descriptor, Array.AsReadOnly(updateOf));
        if (identity != 0)
        {
            s_triggerSite = site;
        }

        return site;
    }

    /// <summary>
    /// Reads the native call site's identity; zero marks an invocation that is not cached.
    /// </summary>
    private static long ReadTriggerSiteIdentity(NativeValue value)
    {
        if (value._isNull != 0 || value._integer < 0 || value._data != null || value._length != 0 ||
            value._auxiliary1 != 0 || value._auxiliary2 != 0 || value._temporalInfinity != 0)
        {
            throw new InvalidOperationException("Invalid native trigger call site envelope.");
        }

        return value._integer;
    }

    private static (uint Oid, int Flags) ReadTriggerConstraint(NativeValue value)
    {
        if (value._isNull != 0 || value._integer is < 0 or > uint.MaxValue || value._data != null || value._length != 0 ||
            value._auxiliary1 is < 0 or > 3 || value._auxiliary2 != 0 || value._temporalInfinity != 0)
        {
            throw new InvalidOperationException("Invalid native trigger constraint envelope.");
        }

        return ((uint)value._integer, value._auxiliary1);
    }

    private static uint ReadTriggerUnsigned(NativeValue value)
    {
        if (value._isNull != 0 || value._integer is < 0 or > uint.MaxValue || value._data != null || value._length != 0 ||
            value._auxiliary1 != 0 || value._auxiliary2 != 0 || value._temporalInfinity != 0)
        {
            throw new InvalidOperationException("Invalid native trigger event or OID envelope.");
        }

        return (uint)value._integer;
    }

    private static string ReadTriggerName(NativeValue value)
    {
        if (value._isNull != 0 || value._integer != 0 || value._data == null || value._length is < 1 or > 252 ||
            value._auxiliary1 != 0 || value._auxiliary2 != 0 || value._temporalInfinity != 0)
        {
            throw new InvalidOperationException("Invalid native trigger name envelope.");
        }

        return value.ReadString();
    }

    private static bool ReadTriggerIsNull(NativeValue value)
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
    /// <param name="relationOid">The triggering relation.</param>
    /// <param name="triggerOid">The trigger.</param>
    /// <param name="buffers">The parsed slots' native buffers, in <see cref="s_siteSlots"/> order.</param>
    /// <param name="name">The trigger's name.</param>
    /// <param name="tableName">The relation's name.</param>
    /// <param name="tableSchema">The relation's schema.</param>
    /// <param name="oldTransition">The OLD transition table's name, if any.</param>
    /// <param name="newTransition">The NEW transition table's name, if any.</param>
    /// <param name="arguments">The trigger's arguments.</param>
    /// <param name="descriptor">The relation's columns.</param>
    /// <param name="updateOf">The columns named by UPDATE OF.</param>
    private sealed class TriggerSite(long identity, uint relationOid, uint triggerOid, (nint Data, int Length, byte IsNull)[] buffers,
        string name, string tableName, string tableSchema, string? oldTransition, string? newTransition,
        ReadOnlyCollection<string> arguments, PgTupleDescriptor descriptor, ReadOnlyCollection<int> updateOf)
    {
        private PgTupleDescriptor? _unavailable;

        internal string Name => name;

        internal string TableName => tableName;

        internal string TableSchema => tableSchema;

        internal string? OldTransition => oldTransition;

        internal string? NewTransition => newTransition;

        internal ReadOnlyCollection<string> Arguments => arguments;

        internal PgTupleDescriptor Descriptor => descriptor;

        internal ReadOnlyCollection<int> UpdateOf => updateOf;

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
                PgTupleAttributeInfo known = descriptor.Attributes[index];
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
                if (variant.Attributes[index].IsUnavailable != (cells[index]._auxiliary1 == -12))
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
            if (callIdentity == 0 || callIdentity != identity || callRelation != relationOid || callTrigger != triggerOid)
            {
                return false;
            }

            for (int index = 0; index < s_siteSlots.Length; index++)
            {
                NativeValue slot = slots[s_siteSlots[index]];
                if ((nint)slot._data != buffers[index].Data || slot._length != buffers[index].Length || slot._isNull != buffers[index].IsNull)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
