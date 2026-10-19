using System.Runtime.CompilerServices;

namespace Ankus;

/// <summary>
/// Carries PostgreSQL internal state as an owned managed value or a borrowed native pointer.
/// </summary>
/// <remarks>
/// SQL NULL is represented by a null wrapper. A present value can contain a zero native word.
/// Managed state is rooted until its PostgreSQL owner resets or is deleted. Native pointers
/// retain their original ownership; passing one does not copy or extend its pointee's lifetime.
/// </remarks>
public sealed class PgInternal
{
    [ThreadStatic]
    private static Dictionary<(nint Provider, nuint Address), ManagedState>? s_states;

    private readonly ManagedState? _state;
    private readonly PgDatum _datum;

    /// <summary>
    /// Wraps a present internal datum, recovering managed ownership when it belongs to this extension.
    /// </summary>
    /// <param name="datum">A live, non-null PostgreSQL internal value.</param>
    public PgInternal(PgDatum datum)
    {
        ArgumentNullException.ThrowIfNull(datum);
        if (datum.TypeOid != 2281 || datum.IsNull)
        {
            throw new ArgumentException("An internal wrapper requires a present PostgreSQL internal datum.", nameof(datum));
        }

        nuint address = datum.DangerousGetBits();
        s_states?.TryGetValue((NativeMemoryContext.Provider, address), out _state);
        _datum = _state?.Datum ?? datum;
    }

    /// <summary>
    /// Constructs an owned wrapper before its native cleanup registration becomes visible.
    /// </summary>
    private PgInternal(PgDatum datum, ManagedState state)
    {
        _datum = datum;
        _state = state;
    }

    /// <summary>
    /// Gets the raw datum and its checked lifetime without copying the pointed-to state.
    /// </summary>
    public PgDatum Datum
    {
        get
        {
            _state?.Validate();
            return _datum;
        }
    }

    /// <summary>
    /// Gets whether this wrapper refers to managed state created by this extension.
    /// </summary>
    public bool IsManaged => _state is not null;

    /// <summary>
    /// Roots a managed value under a PostgreSQL owner and arranges its eventual cleanup.
    /// </summary>
    /// <typeparam name="T">The exact managed value type.</typeparam>
    /// <param name="value">The non-null value to retain; IDisposable is called once at owner cleanup.</param>
    /// <param name="context">The storage owner, or the current callback's result owner when omitted.</param>
    /// <returns>The present internal state.</returns>
    /// <remarks>
    /// Use mutable classes for state updated across callbacks. An explicit owner controls longer lifetimes.
    /// The native address is an opaque identity and does not point to the managed object's representation.
    /// </remarks>
    public static unsafe PgInternal Create<T>(T value, PgMemoryContext? context = null) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(value);
        context ??= PgMemoryContext.Callback;
        Dictionary<(nint Provider, nuint Address), ManagedState> states = s_states ??= [];
        PgAllocation identity = context.AllocateZeroed<byte>();
        (nint Provider, nuint Address) key = default;
        bool added = false;
        try
        {
            nuint address = (nuint)identity.DangerousGetPointer();
            PgDatum datum = PgDatum.DangerousCreate(address, 2281, context);
            key = (NativeMemoryContext.Provider, address);
            var state = new ManagedState(key, datum, typeof(T), value);
            var result = new PgInternal(datum, state);
            states.Add(key, state);
            added = true;
            _ = context.RegisterResetCallback(state.Release);
            return result;
        }
        catch (Exception primary)
        {
            if (added)
            {
                states.Remove(key);
            }

            try
            {
                identity.Dispose();
            }
            // codeql[cs/catch-of-all-exceptions] A failed cleanup is reported together with the failure that required it.
            catch (Exception cleanup)
            {
                throw new AggregateException("Creating internal state and releasing its native identity failed.", primary, cleanup);
            }

            throw;
        }
    }

    /// <summary>
    /// Reads managed state after checking its exact type, owner, and backend thread.
    /// </summary>
    /// <typeparam name="T">The exact type supplied to Create.</typeparam>
    /// <returns>The retained value. Value types are returned by value.</returns>
    public T Get<T>() where T : notnull
    {
        Datum.Lifetime.Validate();
        if (_state is null)
        {
            throw new InvalidOperationException("This internal value is a native pointer; only managed state created by Ankus can be read this way.");
        }

        return _state.Get<T>();
    }

    /// <summary>
    /// Borrows a native internal word under an explicit lifetime anchor without acquiring ownership.
    /// </summary>
    /// <param name="bits">The PostgreSQL internal word, including a present zero pointer.</param>
    /// <param name="context">The lifetime anchor, which must not outlive the pointed-to storage.</param>
    /// <returns>The borrowed internal value.</returns>
    /// <remarks>
    /// The caller guarantees pointer validity and its representation. No earlier native free is detected.
    /// </remarks>
    [NativeUnsafeAccess]
    public static PgInternal DangerousCreate(nuint bits, PgMemoryContext context)
    {
        unsafe
        {
            return new(PgDatum.DangerousCreate(bits, 2281, context));
        }
    }

    /// <summary>
    /// Returns the native word after checking its owner and backend thread.
    /// </summary>
    /// <returns>The opaque managed identity or borrowed native word.</returns>
    public nuint DangerousGetBits()
    {
        return Datum.DangerousGetBits();
    }

    /// <summary>
    /// Rejects released managed state before producing its native parameter or result envelope.
    /// </summary>
    /// <returns>The live internal datum transport.</returns>
    internal NativeValue ToNative()
    {
        return Datum.ToNative();
    }

    /// <summary>
    /// Borrows a native pointee with an explicit unmanaged representation and checked context lifetime.
    /// </summary>
    /// <typeparam name="T">The caller-proven native representation.</typeparam>
    /// <returns>The borrowed reference, or null for a zero pointer.</returns>
    /// <remarks>
    /// The caller guarantees at least sizeof(T) initialized, accessible bytes with the correct layout.
    /// This method rejects managed state, whose opaque identity is not a native value representation.
    /// </remarks>
    [NativeUnsafeAccess]
    public PgNativeReference<T>? DangerousBorrow<T>() where T : unmanaged
    {
        nuint address = DangerousGetBits();
        if (IsManaged)
        {
            throw new InvalidOperationException("Managed internal state has no unmanaged value representation.");
        }

        return address == 0 ? null : new PgNativeReference<T>(NativeMemoryContext.Provider,
            Datum.Lifetime.ContextId, unchecked((nint)Datum.Lifetime.Generation), (nint)address, (nuint)Unsafe.SizeOf<T>());
    }

    /// <summary>
    /// Owns one rooted value and invalidates all wrappers before running user cleanup.
    /// </summary>
    /// <param name="key">The native provider and allocation identity.</param>
    /// <param name="datum">The native identity's lifetime.</param>
    /// <param name="type">The exact managed value type.</param>
    /// <param name="value">The rooted payload.</param>
    private sealed class ManagedState((nint Provider, nuint Address) key, PgDatum datum, Type type, object value)
    {
        private object? _value = value;

        /// <summary>
        /// Gets the original storage lifetime shared by all received wrappers.
        /// </summary>
        internal PgDatum Datum { get; } = datum;

        /// <summary>
        /// Reads a live payload without permitting implicit casts between state types.
        /// </summary>
        /// <typeparam name="T">The expected exact state type.</typeparam>
        /// <returns>The live state.</returns>
        internal T Get<T>() where T : notnull
        {
            Validate();
            if (typeof(T) != type)
            {
                throw new InvalidCastException($"Internal state contains {type}; {typeof(T)} was requested.");
            }

            return (T)_value!;
        }

        /// <summary>
        /// Rejects consumed state even when native cleanup stopped before context invalidation.
        /// </summary>
        internal void Validate() => ObjectDisposedException.ThrowIf(_value is null, nameof(PgInternal));

        /// <summary>
        /// Removes native identity lookup and managed ownership before invoking IDisposable once.
        /// </summary>
        internal void Release()
        {
            object? current = _value;
            _value = null;
            s_states?.Remove(key);
            if (current is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
