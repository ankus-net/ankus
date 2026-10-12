using System.Collections.ObjectModel;

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
        PgHeapTuple? oldRow = ReadTriggerIsNull(arguments[9]) ? null : arguments[9].ReadTuple(site.Descriptor);
        PgHeapTuple? newRow = ReadTriggerIsNull(arguments[10]) ? null : arguments[10].ReadTuple(site.Descriptor);
        int[]? updated = ReadTriggerIsNull(arguments[13]) ? null : arguments[13].ReadArray<int>().ToVector();
        (uint constraintOid, int constraintFlags) = ReadTriggerConstraint(arguments[14]);
        return new PgTriggerContext(eventBits, relationOid, triggerOid, site.Name, site.TableName, site.TableSchema,
            site.OldTransition, site.NewTransition, site.Arguments, oldRow, newRow, site.Descriptor,
            site.UpdateOf, updated, constraintOid, constraintFlags);
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
        internal string Name => name;

        internal string TableName => tableName;

        internal string TableSchema => tableSchema;

        internal string? OldTransition => oldTransition;

        internal string? NewTransition => newTransition;

        internal ReadOnlyCollection<string> Arguments => arguments;

        internal PgTupleDescriptor Descriptor => descriptor;

        internal ReadOnlyCollection<int> UpdateOf => updateOf;

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
