using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Binds aggregate callbacks to guarded native ownership and comparison operations without dereferencing stale managed handles.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class NativeAggregate
{
    private static long s_nextRootId;

    [ThreadStatic]
    private static Dictionary<nint, IAggregateState>? s_roots;

    [ThreadStatic]
    private static PgAggregateContext? s_current;

    [ThreadStatic]
    private static int s_cleanupDepth;

    /// <summary>
    /// Prevents independent cleanup actions from inheriting an enclosing aggregate callback's native capability.
    /// </summary>
    internal static void EnterCleanup() => s_cleanupDepth++;

    /// <summary>
    /// Restores aggregate access after the matching independent cleanup action returns.
    /// </summary>
    internal static void ExitCleanup() => s_cleanupDepth--;

    /// <summary>
    /// Copies aggregate metadata and enters the native owner's scope after every scalar and sort key has been validated.
    /// </summary>
    /// <param name="metadata">Four scalar context fields followed by five scalar fields per sort key.</param>
    /// <param name="owner">The native memory context that owns returned state.</param>
    /// <param name="api">The guarded native adoption, comparison, and memory-owner entry point.</param>
    /// <returns>The active context, which must be passed to Exit in the generated finally block.</returns>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// The owner and API must be live capabilities supplied by the matching native aggregate dispatcher.
    /// </remarks>
    [NativeUnsafeAccess]
    public static PgAggregateContext Enter(ReadOnlySpan<NativeValue> metadata, nint owner, nint api)
    {
        if (s_cleanupDepth != 0)
        {
            throw new InvalidOperationException("Aggregate callbacks cannot enter while managed state is being released.");
        }

        if (owner == 0 || api == 0 || metadata.Length < 4 || (metadata.Length - 4) % 5 != 0)
        {
            throw new InvalidOperationException("Invalid native aggregate context envelope or binding.");
        }

        long kind = metadata[0].ReadAggregateScalar();
        if (kind is not 1 and not 2)
        {
            throw new InvalidOperationException("Unknown PostgreSQL aggregate execution context kind.");
        }

        bool shared = ReadBoolean(metadata[1]);
        uint collation = ReadOid(metadata[2]);
        uint aggregateOid = ReadOid(metadata[3]);
        // Most aggregates have no ORDER BY; they share one empty key list instead of allocating one per row.
        PgAggregateSortKey[] keys = metadata.Length == 4 ? [] : new PgAggregateSortKey[(metadata.Length - 4) / 5];
        for (int index = 0; index < keys.Length; index++)
        {
            int offset = 4 + index * 5;
            long argument = metadata[offset].ReadAggregateScalar();
            if (argument is < 0 or > int.MaxValue)
            {
                throw new InvalidOperationException("Aggregate sort argument positions must be nonnegative Int32 values.");
            }

            keys[index] = new PgAggregateSortKey((int)argument, ReadOid(metadata[offset + 1]),
                ReadOid(metadata[offset + 2]), ReadOid(metadata[offset + 3]), ReadBoolean(metadata[offset + 4]));
        }

        var context = new PgAggregateContext((PgAggregateContextKind)kind, shared, collation,
            aggregateOid == 0 ? null : aggregateOid, keys, owner, api, s_current);
        s_current = context;
        return context;
    }

    /// <summary>
    /// Restores the enclosing aggregate callback and releases this context's parent link.
    /// </summary>
    /// <param name="context">The current context returned by Enter.</param>
    public static void Exit(PgAggregateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CheckContext(context);
        s_current = context.DetachParent();
    }

    /// <summary>
    /// Resolves a checked internal input ID to its exact typed managed state, including borrowed combine inputs from other owners.
    /// </summary>
    /// <typeparam name="T">The managed payload type.</typeparam>
    /// <param name="value">The native internal argument containing an opaque root ID or SQL NULL.</param>
    /// <returns>The live state, or null for SQL NULL.</returns>
    public static PgAggregateState<T>? Read<T>(NativeValue value) where T : notnull
    {
        long id = value.ReadAggregateScalar(allowNull: true);
        if (value.IsNull != 0)
        {
            return null;
        }

        _ = Current();
        if (id <= 0 || id > nint.MaxValue || s_roots is null || !s_roots.TryGetValue((nint)id, out IAggregateState? state))
        {
            throw new PgException("55000", "Invalid or expired Ankus aggregate state");
        }

        state.CheckAccess();
        return state as PgAggregateState<T>
            ?? throw new InvalidCastException("The aggregate state has a different managed payload type.");
    }

    /// <summary>
    /// Roots and adopts new state before returning its native header, or reuses state already attached to this exact owner.
    /// A borrowed state from another owner must be copied into a new wrapper before being returned.
    /// </summary>
    /// <typeparam name="T">The managed payload type.</typeparam>
    /// <param name="value">The state to return, or null for SQL NULL.</param>
    /// <returns>The native state header pointer, or SQL NULL.</returns>
    public static NativeValue Write<T>(PgAggregateState<T>? value) where T : notnull
    {
        if (value is null)
        {
            return new NativeValue { IsNull = 1 };
        }

        PgAggregateContext context = Current();
        IAggregateState state = value;
        state.CheckAccess();
        if (state.Owner != 0)
        {
            if (state.Owner != context.Owner)
            {
                throw new InvalidOperationException("Aggregate state belongs to a different native owner; return a new wrapper containing a copied payload.");
            }

            if (state.Pointer == 0)
            {
                throw new InvalidOperationException("Aggregate state registration has not completed.");
            }

            return new NativeValue { Integral = state.Pointer };
        }

        nint id = AllocateStateId(ref s_nextRootId);
        state.BeginAttachment(context.Owner);
        try
        {
            (s_roots ??= []).Add(id, state);
            nint pointer = Invoke(context, 0, id,
                (nint)(delegate* unmanaged[Cdecl]<void*, NativeCallError*, nint, nint, int>)&Release, null, 0);
            if (pointer == 0)
            {
                throw new InvalidOperationException("The native aggregate owner returned an invalid state header.");
            }

            state.CompleteAttachment(pointer);
            return new NativeValue { Integral = pointer };
        }
        catch (Exception adoptionError)
        {
            s_roots?.Remove(id);
            try
            {
                ReleaseState(state);
            }
            // codeql[cs/catch-of-all-exceptions] A failed cleanup is reported together with the failure that required it.
            catch (Exception cleanupError)
            {
                throw new AggregateException("Aggregate state registration and payload cleanup both failed.", adoptionError, cleanupError);
            }

            throw;
        }
    }

    /// <summary>
    /// Converts owned typed operands and delegates ordering to PostgreSQL's guarded sort support.
    /// </summary>
    internal static int Compare<T>(PgAggregateContext context, T left, T right, int sortKey)
    {
        CheckComparison(context, sortKey);
        return CompareValues(context, SpiParameter.Create(left), SpiParameter.Create(right), sortKey);
    }

    /// <summary>
    /// Preserves explicit PostgreSQL identities, including named composite NULLs, through the shared guarded comparator transport.
    /// </summary>
    internal static int Compare(PgAggregateContext context, SpiParameter left, SpiParameter right, int sortKey)
    {
        CheckComparison(context, sortKey);
        return CompareValues(context, left, right, sortKey);
    }

    private static int CompareValues(PgAggregateContext context, SpiParameter left, SpiParameter right, int sortKey)
    {
        ArgumentOutOfRangeException.ThrowIfZero(left.TypeOid, nameof(left));
        ArgumentOutOfRangeException.ThrowIfZero(right.TypeOid, nameof(right));
        NativeSpiParameter* values = stackalloc NativeSpiParameter[2];
        values[0] = default;
        values[1] = default;
        try
        {
            values[0]._typeOid = left.TypeOid;
            values[0]._value = SpiType.ToNative(left.Value, left.CustomMapping, left.CustomArrayMapping);
            values[1]._typeOid = right.TypeOid;
            values[1]._value = SpiType.ToNative(right.Value, right.CustomMapping, right.CustomArrayMapping);
            nint result = Invoke(context, 1, 0, 0, values, sortKey);
            if (result is < int.MinValue or > int.MaxValue)
            {
                throw new InvalidOperationException("The native aggregate comparator returned an invalid Int32 result.");
            }

            return (int)result;
        }
        finally
        {
            values[0]._value.Release();
            values[1]._value.Release();
        }
    }

    /// <summary>
    /// Atomically allocates a positive pointer-width identity and leaves an exhausted counter permanently saturated.
    /// </summary>
    /// <param name="counter">The monotonically increasing state identity counter.</param>
    /// <returns>The next nonzero identity.</returns>
    internal static nint AllocateStateId(ref long counter)
    {
        long current = Volatile.Read(ref counter);
        while (current >= 0 && current < nint.MaxValue)
        {
            long next = current + 1;
            long observed = Interlocked.CompareExchange(ref counter, next, current);
            if (observed == current)
            {
                return (nint)next;
            }

            current = observed;
        }

        throw new InvalidOperationException("The managed aggregate state identity space is exhausted.");
    }

    /// <summary>
    /// Resolves the active aggregate's result owner while preserving callback and thread checks.
    /// </summary>
    /// <param name="context">The exact current aggregate scope.</param>
    /// <returns>The borrowed checked storage owner.</returns>
    internal static PgMemoryContext GetMemoryContext(PgAggregateContext context)
    {
        CheckContext(context);
        nint provider = NativeMemoryContext.Provider;
        nint identity = Invoke(context, 2, context.Owner, 0, null, 0);
        if (identity == 0)
        {
            throw new InvalidOperationException("PostgreSQL did not expose the aggregate memory context.");
        }

        return PgMemoryContext.FromId(provider, identity);
    }

    /// <summary>
    /// Writes general internal state while preventing managed payloads from escaping a temporary aggregate owner.
    /// </summary>
    /// <param name="value">The returned state, or null for SQL NULL.</param>
    /// <returns>The checked internal transport.</returns>
    public static NativeValue WriteInternal(PgInternal? value)
    {
        PgAggregateContext context = Current();
        if (value is null)
        {
            return new NativeValue { IsNull = 1 };
        }

        value.Datum.Lifetime.Validate();
        if (value.IsManaged && value.Datum.Lifetime.ContextId != GetMemoryContext(context).Id)
        {
            throw new InvalidOperationException("Managed internal state must belong to the destination aggregate memory context.");
        }

        return NativeValue.FromInternal(value);
    }

    private static PgAggregateContext Current()
    {
        if (s_current is null || s_cleanupDepth != 0)
        {
            throw new InvalidOperationException("Aggregate state and comparisons require an active callback on the owning backend thread.");
        }

        return s_current;
    }

    private static void CheckContext(PgAggregateContext context)
    {
        if (!ReferenceEquals(Current(), context))
        {
            throw new InvalidOperationException("Aggregate operations require the exact innermost callback context.");
        }
    }

    private static void CheckComparison(PgAggregateContext context, int sortKey)
    {
        CheckContext(context);
        ArgumentOutOfRangeException.ThrowIfNegative(sortKey);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sortKey, context.SortKeys.Count);
    }

    private static bool ReadBoolean(NativeValue value)
    {
        long scalar = value.ReadAggregateScalar();
        return scalar switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidOperationException("Aggregate metadata booleans must be zero or one."),
        };
    }

    private static uint ReadOid(NativeValue value)
    {
        long scalar = value.ReadAggregateScalar();
        if (scalar is < 0 or > uint.MaxValue)
        {
            throw new InvalidOperationException("Aggregate metadata OIDs must be unsigned Int32 values.");
        }

        return (uint)scalar;
    }

    private static nint Invoke(PgAggregateContext context, int operation, nint handle, nint release,
        NativeSpiParameter* values, int sortKey)
    {
        var api = (delegate* unmanaged[Cdecl]<int, void*, void*, void**, NativeSpiParameter*, int, NativeCallError*, int>)context.Api;
        NativeCallError error = default;
        void* output = null;
        try
        {
            if (api(operation, (void*)handle, (void*)release, &output, values, sortKey, &error) != 0)
            {
                throw error.ToManagedException();
            }

            return (nint)output;
        }
        finally
        {
            error.Release();
        }
    }

    private static void ReleaseState(IAggregateState state)
    {
        PgAggregateContext? previous = s_current;
        s_current = null;
        s_cleanupDepth++;
        try
        {
            state.Release();
        }
        finally
        {
            s_cleanupDepth--;
            s_current = previous;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Release(void* handle, NativeCallError* error, nint execute, nint memory)
    {
        nint previous = NativeBackend.Enter(execute, abortCleanup: true);
        nint previousMemory = 0;
        bool memoryEntered = false;
        try
        {
            previousMemory = NativeMemoryContext.Enter(memory);
            memoryEntered = true;
            if (s_roots is null || !s_roots.Remove((nint)handle, out IAggregateState? state))
            {
                throw new InvalidOperationException("The aggregate state ID is stale, unknown, or belongs to another backend thread.");
            }

            ReleaseState(state);
            return 0;
        }
        // codeql[cs/catch-of-all-exceptions] Native code calls this, so every exception goes back to PostgreSQL as an error instead of unwinding through it.
        catch (Exception exception)
        {
            NativeError.Write(exception, error);
            return 1;
        }
        finally
        {
            if (memoryEntered)
            {
                NativeMemoryContext.Exit(previousMemory);
            }

            NativeBackend.Exit(previous, abortCleanup: true);
        }
    }
}
