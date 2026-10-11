namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    /// <summary>
    /// Copies a validated fifteen-slot native trigger invocation into owned managed context and row values.
    /// Input buffers remain borrowed; the caller retains responsibility for releasing their native storage.
    /// </summary>
    /// <param name="arguments">The generated native trigger argument envelope.</param>
    /// <returns>The detached trigger context.</returns>
    public static PgTriggerContext ReadTriggerContext(ReadOnlySpan<NativeValue> arguments)
    {
        if (arguments.Length != 15)
        {
            throw new InvalidOperationException("A native trigger invocation must contain exactly fifteen context slots.");
        }

        uint eventBits = ReadTriggerUnsigned(arguments[0]);
        uint relationOid = ReadTriggerUnsigned(arguments[1]);
        uint triggerOid = ReadTriggerUnsigned(arguments[2]);
        string name = ReadTriggerName(arguments[3]);
        string tableName = ReadTriggerName(arguments[4]);
        string tableSchema = ReadTriggerName(arguments[5]);
        string? oldTransition = ReadTriggerIsNull(arguments[6]) ? null : ReadTriggerName(arguments[6]);
        string? newTransition = ReadTriggerIsNull(arguments[7]) ? null : ReadTriggerName(arguments[7]);
        string[] triggerArguments = arguments[8].ReadArray<string>().ToVector();
        PgHeapTuple? oldRow = ReadTriggerIsNull(arguments[9]) ? null : arguments[9].ReadTuple();
        PgHeapTuple? newRow = ReadTriggerIsNull(arguments[10]) ? null : arguments[10].ReadTuple();
        PgHeapTuple descriptor = arguments[11].ReadTuple();
        for (int index = 0; index < descriptor.Count; index++)
        {
            if (!descriptor.Descriptor.Attributes[index].IsUnavailable && descriptor[index] is not null)
            {
                throw new InvalidOperationException("A trigger descriptor envelope must contain only SQL NULL cells.");
            }
        }

        int[] updateOf = arguments[12].ReadArray<int>().ToVector();
        int[]? updated = ReadTriggerIsNull(arguments[13]) ? null : arguments[13].ReadArray<int>().ToVector();
        (uint constraintOid, int constraintFlags) = ReadTriggerConstraint(arguments[14]);
        return new PgTriggerContext(eventBits, relationOid, triggerOid, name, tableName, tableSchema,
            oldTransition, newTransition, triggerArguments, oldRow, newRow, descriptor.Descriptor,
            updateOf, updated, constraintOid, constraintFlags);
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
}
