using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Borrows one exact native argument representation for a generated C call body.
/// </summary>
/// <param name="Address">The live, correctly aligned address of the argument's C object representation.</param>
/// <param name="Size">The exact native byte size of that representation.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeCallArgument(nint Address, nuint Size);

/// <summary>
/// Invokes generated native call bodies beneath the active callback's PostgreSQL error guard.
/// </summary>
/// <remarks>
/// This is a generated-code contract. The body must be an actual native C function
/// with the generated storage-frame signature, compiled for the active backend.
/// It must never be a managed function or a thunk that enters managed code without
/// a separate callback error boundary. All argument objects and referenced native
/// storage must remain valid for the entire call. The result address must designate
/// writable storage of the supplied length. This API cannot validate raw addresses
/// or establish pointer ownership and callback lifetimes. The caller must satisfy
/// the native function's transaction and cleanup-state preconditions, including
/// during disposal while PostgreSQL unwinds a query.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class NativeRawCall
{
    /// <summary>
    /// Narrows a 64-bit argument to a 32-bit C long, as on Windows, rejecting a value it cannot hold.
    /// </summary>
    /// <param name="value">The argument.</param>
    /// <param name="parameter">The parameter's name.</param>
    /// <returns>The same value as a 32-bit integer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit in 32 bits.</exception>
    public static int NarrowLong(long value, string parameter)
        => value is >= int.MinValue and <= int.MaxValue ? (int)value
            : throw new ArgumentOutOfRangeException(parameter, value, "The value does not fit in this platform's 32-bit C long.");

    /// <summary>
    /// Narrows a 64-bit argument to a 32-bit C unsigned long, as on Windows, rejecting a value it cannot hold.
    /// </summary>
    /// <param name="value">The argument.</param>
    /// <param name="parameter">The parameter's name.</param>
    /// <returns>The same value as a 32-bit unsigned integer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit in 32 bits.</exception>
    public static uint NarrowUnsignedLong(ulong value, string parameter)
        => value <= uint.MaxValue ? (uint)value
            : throw new ArgumentOutOfRangeException(parameter, value, "The value does not fit in this platform's 32-bit C unsigned long.");

    /// <summary>
    /// Validates the generated call's complete declaration contract before obtaining or invoking its native body.
    /// </summary>
    /// <param name="identity">The generated UTF-8 identity of the complete selected-header graph.</param>
    /// <param name="postgresMajor">The PostgreSQL major version used to generate the call.</param>
    /// <exception cref="InvalidOperationException">No usable backend callback is active.</exception>
    /// <exception cref="PgException">The active extension has no matching native declaration contract.</exception>
    /// <remarks>
    /// A provider's native contract does not change, so a contract it accepted is accepted again without entering
    /// PostgreSQL while its guard would admit the check; a different provider, identity or major is checked natively.
    /// The subsequent body accessor must be a pure native address lookup; invocation still uses
    /// <see cref="Invoke(nint, ReadOnlySpan{NativeCallArgument}, nint, nuint)"/>.
    /// </remarks>
    public static void ValidateBinding(ReadOnlySpan<byte> identity, int postgresMajor)
    {
        NativeSubtransaction.CheckAccess();
        NativeBindingContract.Validate(identity, postgresMajor);
    }

    /// <summary>
    /// Validates the generated call's declaration contract as <see cref="ValidateBinding(ReadOnlySpan{byte}, int)"/>
    /// does, and records the provider that accepted it in the binding's own cache.
    /// </summary>
    /// <param name="identity">The generated UTF-8 identity of the complete selected-header graph.</param>
    /// <param name="postgresMajor">The PostgreSQL major version used to generate the call.</param>
    /// <param name="accepted">The binding's cache of the provider that accepted its contract, or zero.</param>
    /// <exception cref="InvalidOperationException">No usable backend callback is active.</exception>
    /// <exception cref="PgException">The active extension has no matching native declaration contract.</exception>
    /// <remarks>
    /// A check the cache vouches for costs one comparison. Any other call runs the checks the uncached overload runs,
    /// in the same order, and reports exactly what they find.
    /// </remarks>
    public static void ValidateBinding(ReadOnlySpan<byte> identity, int postgresMajor, ref nint accepted)
    {
        if (AdmittedApi(accepted) == null)
        {
            _ = Admit(identity, postgresMajor, ref accepted);
        }
    }

    /// <summary>
    /// Executes one C body and converts native diagnostics only after its guarded native frames return.
    /// </summary>
    /// <param name="body">The native generated C body address.</param>
    /// <param name="arguments">The borrowed native argument representations.</param>
    /// <param name="result">The native result destination, or zero for a void call.</param>
    /// <param name="resultSize">The exact result representation size, or zero for a void call.</param>
    /// <exception cref="ArgumentOutOfRangeException">The body address is zero.</exception>
    /// <exception cref="InvalidOperationException">No usable callback is active, or the generated body rejects its storage contract.</exception>
    /// <exception cref="PgException">PostgreSQL raises an error inside the native body.</exception>
    [NativeUnsafeAccess]
    public static void Invoke(nint body, ReadOnlySpan<NativeCallArgument> arguments, nint result, nuint resultSize)
    {
        ArgumentOutOfRangeException.ThrowIfZero(body);
        NativeSubtransaction.CheckAccess();
        NativeMemoryApi* api = NativeMemoryContext.Api;
        NativeBorrowScope.CheckBackendAccess();
        Execute(api, body, arguments, result, resultSize);
    }

    /// <summary>
    /// Validates the generated call's declaration contract, then looks up and executes its C body, as
    /// <see cref="ValidateBinding(ReadOnlySpan{byte}, int)"/> followed by <see cref="Invoke(nint, ReadOnlySpan{NativeCallArgument}, nint, nuint)"/> do.
    /// </summary>
    /// <param name="identity">The generated UTF-8 identity of the complete selected-header graph.</param>
    /// <param name="postgresMajor">The PostgreSQL major version used to generate the call.</param>
    /// <param name="accessor">The pure lookup of the generated C body, called only after the contract is accepted.</param>
    /// <param name="arguments">The borrowed native argument representations.</param>
    /// <param name="result">The native result destination, or zero for a void call.</param>
    /// <param name="resultSize">The exact result representation size, or zero for a void call.</param>
    /// <exception cref="ArgumentOutOfRangeException">The body address is zero.</exception>
    /// <exception cref="InvalidOperationException">No usable callback is active, or the generated body rejects its storage contract.</exception>
    /// <exception cref="PgException">
    /// The active extension has no matching native declaration contract, or PostgreSQL raises an error inside the body.
    /// </exception>
    /// <remarks>
    /// A contract the provider already accepted is not checked again while the guard would admit the call. Otherwise
    /// the contract is validated natively first, so a refused call fails exactly as the separate validation would.
    /// </remarks>
    [NativeUnsafeAccess]
    public static void Invoke(ReadOnlySpan<byte> identity, int postgresMajor, delegate*<nint> accessor,
        ReadOnlySpan<NativeCallArgument> arguments, nint result, nuint resultSize)
    {
        nint accepted = 0;
        Invoke(identity, postgresMajor, ref accepted, accessor, arguments, result, resultSize);
    }

    /// <summary>
    /// Validates the generated call's declaration contract, then looks up and executes its C body, as the overload
    /// without a cache does, using the binding's own cache of the provider that accepted its contract.
    /// </summary>
    /// <param name="identity">The generated UTF-8 identity of the complete selected-header graph.</param>
    /// <param name="postgresMajor">The PostgreSQL major version used to generate the call.</param>
    /// <param name="accepted">The binding's cache of the provider that accepted its contract, or zero.</param>
    /// <param name="accessor">The pure lookup of the generated C body, called only after the contract is accepted.</param>
    /// <param name="arguments">The borrowed native argument representations.</param>
    /// <param name="result">The native result destination, or zero for a void call.</param>
    /// <param name="resultSize">The exact result representation size, or zero for a void call.</param>
    /// <exception cref="ArgumentOutOfRangeException">The body address is zero.</exception>
    /// <exception cref="InvalidOperationException">No usable callback is active, or the generated body rejects its storage contract.</exception>
    /// <exception cref="PgException">
    /// The active extension has no matching native declaration contract, or PostgreSQL raises an error inside the body.
    /// </exception>
    /// <remarks>
    /// When the cache names the active callback's provider, no guarded scope has failed, no lock lease is held and the
    /// guard would admit the call, the call goes straight to its body. Otherwise it runs the uncached checks in their
    /// order, so a refused call fails exactly as before.
    /// </remarks>
    [NativeUnsafeAccess]
    public static void Invoke(ReadOnlySpan<byte> identity, int postgresMajor, ref nint accepted, delegate*<nint> accessor,
        ReadOnlySpan<NativeCallArgument> arguments, nint result, nuint resultSize)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        NativeMemoryApi* api = AdmittedApi(accepted);
        if (api == null)
        {
            api = Admit(identity, postgresMajor, ref accepted);
        }

        nint body = accessor();
        ArgumentOutOfRangeException.ThrowIfZero(body);
        Execute(api, body, arguments, result, resultSize);
    }

    /// <summary>
    /// Validates the generated call's declaration contract, then looks up and calls a C body that cannot raise a
    /// PostgreSQL error, without the error guard.
    /// </summary>
    /// <param name="identity">The generated UTF-8 identity of the complete selected-header graph.</param>
    /// <param name="postgresMajor">The PostgreSQL major version used to generate the call.</param>
    /// <param name="accepted">The binding's cache of the provider that accepted its contract, or zero.</param>
    /// <param name="accessor">The pure lookup of the generated C body, called only after the contract is accepted.</param>
    /// <param name="arguments">The borrowed native argument representations.</param>
    /// <param name="result">The native result destination, or zero for a void call.</param>
    /// <param name="resultSize">The exact result representation size, or zero for a void call.</param>
    /// <exception cref="ArgumentOutOfRangeException">The body address is zero.</exception>
    /// <exception cref="InvalidOperationException">No usable callback is active, or the generated body rejects its storage contract.</exception>
    /// <exception cref="PgException">The active extension has no matching native declaration contract.</exception>
    /// <remarks>
    /// The generator uses this only for a helper whose body the selected headers show cannot raise an error, block or
    /// call back into managed code, such as an inline accessor or a macro wrapper, so the body runs as a C caller would
    /// run it, without a guard or a GC transition. The call is admitted exactly as a guarded call is.
    /// </remarks>
    [NativeUnsafeAccess]
    public static void InvokeDirect(ReadOnlySpan<byte> identity, int postgresMajor, ref nint accepted, delegate*<nint> accessor,
        ReadOnlySpan<NativeCallArgument> arguments, nint result, nuint resultSize)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        if (AdmittedApi(accepted) == null)
        {
            _ = Admit(identity, postgresMajor, ref accepted);
        }

        nint body = accessor();
        ArgumentOutOfRangeException.ThrowIfZero(body);
        fixed (NativeCallArgument* values = arguments)
        {
            nint status = ((delegate* unmanaged[Cdecl, SuppressGCTransition]<NativeCallArgument*, nuint, nint, nuint, int>)body)(
                values, (nuint)arguments.Length, result, resultSize);
            if (status != 0)
            {
                throw Rejected(status);
            }
        }
    }

    /// <summary>
    /// Gets the address of a native global for a generated access that reads or writes it in place.
    /// </summary>
    /// <param name="identity">The generated UTF-8 identity of the complete selected-header graph.</param>
    /// <param name="postgresMajor">The PostgreSQL major version used to generate the access.</param>
    /// <param name="address">The access's cached address, or zero before its first use.</param>
    /// <param name="accessor">The pure lookup of the generated body that writes the global's address.</param>
    /// <returns>The global's address.</returns>
    /// <exception cref="InvalidOperationException">No usable backend callback is active.</exception>
    /// <exception cref="PgException">
    /// The active extension has no matching native declaration contract, or the guard refuses the access.
    /// </exception>
    /// <remarks>
    /// Reading or writing a variable cannot raise a PostgreSQL error, so an access needs no guard once the contract is
    /// accepted and the guard would admit it. Otherwise the binding is validated and the address obtained beneath the
    /// guard, which reports a retained failure exactly as a guarded access would. The global must not be thread-local or
    /// need qualified C loads and stores.
    /// </remarks>
    [NativeUnsafeAccess]
    public static nint GlobalAddress(ReadOnlySpan<byte> identity, int postgresMajor, ref nint address, delegate*<nint> accessor)
    {
        nint accepted = 0;
        return GlobalAddress(identity, postgresMajor, ref accepted, ref address, accessor);
    }

    /// <summary>
    /// Gets the address of a native global for a generated access that reads or writes it in place, as the overload
    /// without a cache does, using the binding's own cache of the provider that accepted its contract.
    /// </summary>
    /// <param name="identity">The generated UTF-8 identity of the complete selected-header graph.</param>
    /// <param name="postgresMajor">The PostgreSQL major version used to generate the access.</param>
    /// <param name="accepted">The binding's cache of the provider that accepted its contract, or zero.</param>
    /// <param name="address">The access's cached address, or zero before its first use.</param>
    /// <param name="accessor">The pure lookup of the generated body that writes the global's address.</param>
    /// <returns>The global's address.</returns>
    /// <exception cref="InvalidOperationException">No usable backend callback is active.</exception>
    /// <exception cref="PgException">
    /// The active extension has no matching native declaration contract, or the guard refuses the access.
    /// </exception>
    [NativeUnsafeAccess]
    public static nint GlobalAddress(ReadOnlySpan<byte> identity, int postgresMajor, ref nint accepted, ref nint address,
        delegate*<nint> accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        nint known = address;
        if (known != 0 && AdmittedApi(accepted) != null)
        {
            return known;
        }

        // An access the cache does not vouch for runs exactly the uncached checks.
        NativeSubtransaction.CheckAccess();
        NativeBorrowScope.CheckBackendAccess();
        NativeMemoryApi* api = NativeMemoryContext.CurrentApi;
        if (known != 0 && NativeMemoryContext.Admits(api) && NativeBindingContract.Accepted(api, identity, postgresMajor))
        {
            accepted = api->_provider;
            return known;
        }

        NativeBindingContract.Validate(identity, postgresMajor);
        api = NativeMemoryContext.Api;
        nint value = 0;
        Execute(api, accessor(), [], (nint)(&value), (nuint)sizeof(nint));
        accepted = api->_provider;
        address = value;
        return value;
    }

    /// <summary>
    /// Gets the active callback's capability when a binding's cache names its provider and nothing would refuse a raw
    /// call: no guarded scope has failed, no lock lease is held and the native guard would admit the call.
    /// </summary>
    /// <param name="accepted">The binding's cache of the provider that accepted its contract, or zero.</param>
    /// <returns>The capability, or null when the call must run the full checks.</returns>
    private static NativeMemoryApi* AdmittedApi(nint accepted)
    {
        NativeThread? thread = NativeThread.Current;
        if (thread is null)
        {
            return null;
        }

        var api = (NativeMemoryApi*)thread._api;
        return api != null && api->_provider == accepted && thread.AdmitsBackendCalls && NativeMemoryContext.Admits(api) ? api : null;
    }

    /// <summary>
    /// Runs every check a raw call makes before entering PostgreSQL, in order, then records the provider that accepted
    /// the binding's contract.
    /// </summary>
    /// <param name="identity">The generated UTF-8 identity of the complete selected-header graph.</param>
    /// <param name="postgresMajor">The PostgreSQL major version used to generate the call.</param>
    /// <param name="accepted">The binding's cache, set to the accepting provider.</param>
    /// <returns>The active callback's capability.</returns>
    private static NativeMemoryApi* Admit(ReadOnlySpan<byte> identity, int postgresMajor, ref nint accepted)
    {
        NativeSubtransaction.CheckAccess();
        NativeBorrowScope.CheckBackendAccess();
        NativeMemoryApi* api = NativeMemoryContext.CurrentApi;
        if (!NativeMemoryContext.Admits(api) || !NativeBindingContract.Accepted(api, identity, postgresMajor))
        {
            NativeBindingContract.Validate(identity, postgresMajor);
            api = NativeMemoryContext.Api;
        }

        accepted = api->_provider;
        return api;
    }

    /// <summary>
    /// Runs one C body beneath the native guard and converts a rejected frame.
    /// </summary>
    /// <param name="api">The active callback's native capability, whose backend access was just checked.</param>
    /// <param name="body">The native generated C body address.</param>
    /// <param name="arguments">The borrowed native argument representations.</param>
    /// <param name="result">The native result destination, or zero for a void call.</param>
    /// <param name="resultSize">The exact result representation size, or zero for a void call.</param>
    private static void Execute(NativeMemoryApi* api, nint body, ReadOnlySpan<NativeCallArgument> arguments, nint result, nuint resultSize)
    {
        fixed (NativeCallArgument* values = arguments)
        {
            var frame = new NativeCallFrame
            {
                _arguments = values,
                _count = (nuint)arguments.Length,
                _result = result,
                _resultSize = resultSize,
            };
            nint status;
            try
            {
                status = NativeMemoryContext.InvokeCall(api, body, &frame);
            }
            catch (Exception exception) when (exception is PgException or PgQueryCanceledException)
            {
                NativeSubtransaction.RecordFailure(exception);
                throw;
            }

            if (status != 0)
            {
                throw Rejected(status);
            }
        }
    }

    /// <summary>
    /// Describes a generated body's refusal of its frame.
    /// </summary>
    /// <param name="status">The body's nonzero storage status.</param>
    /// <returns>The exception to throw.</returns>
    private static InvalidOperationException Rejected(nint status)
    {
        string reason = status switch
        {
            1 => "argument count",
            2 => "argument array",
            3 => "result storage",
            4 => "argument storage",
            5 => "argument alignment",
            6 => "function address",
            _ => "unknown status",
        };
        return new InvalidOperationException($"The generated native call rejected its {reason} (status {status}).");
    }
}

/// <summary>
/// Borrows a complete frame while the native guard invokes its generated C body.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeCallFrame
{
    /// <summary>
    /// Points to the pinned argument representations.
    /// </summary>
    internal NativeCallArgument* _arguments;
    /// <summary>
    /// Holds the element count of the argument representations.
    /// </summary>
    internal nuint _count;
    /// <summary>
    /// Borrows the result destination without assuming its alignment.
    /// </summary>
    internal nint _result;
    /// <summary>
    /// Carries the complete native result byte length.
    /// </summary>
    internal nuint _resultSize;
}
