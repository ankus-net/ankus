using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Calls PostgreSQL functions through native lookup, expression evaluation, and guarded error transport.
/// </summary>
/// <remarks>
/// Catalog calls support PgAnyElement and PgAnyArray results owned by the current callback or iterator.
/// PgArrayView and PgArrayView&lt;T&gt; expose checked cells over callback-owned result snapshots.
/// Typed array results validate their declared element identity before catalog execution, including for SQL NULL.
/// Ordinary managed results are independent copies. Use CallRaw to select an explicit native owner.
/// Registered datum readers use an exact declared result OID check before invoking a catalog function
/// and return independent managed values before temporary native storage is released.
/// Native-address calls use the selected mapping as the caller's asserted result type without catalog declaration proof.
/// </remarks>
public static class PgFunctions
{
    /// <summary>
    /// Copies the metadata of a function, procedure, aggregate or window function from pg_proc.
    /// </summary>
    /// <param name="functionOid">The exact catalog identity, including zero for a missing entry.</param>
    /// <returns>An immutable detached snapshot, or null when the OID has no pg_proc row.</returns>
    public static PgFunctionInfo? GetInfo(uint functionOid) => NativeBackend.FunctionInfo(functionOid);

    /// <summary>
    /// Calls a native PostgreSQL version-1 entry point and copies a supported result or invokes its registered reader.
    /// </summary>
    /// <typeparam name="T">The entry point's actual result type.</typeparam>
    /// <param name="function">The valid native function address.</param>
    /// <param name="collationOid">The input collation, or zero for none.</param>
    /// <param name="arguments">Live raw arguments matching the entry point's ABI and NULL contract.</param>
    /// <returns>The copied managed result, including SQL NULL for nullable types.</returns>
    /// <remarks>
    /// The caller must prove the address and argument/result representations are correct. The native
    /// call has no FmgrInfo, context, or resultinfo; use catalog calls for functions needing those fields.
    /// Registered scalar and array readers return independent managed data before temporary result storage is released.
    /// Their current mapped type is the caller's result contract; the address supplies no catalog return declaration.
    /// A PgArrayView&lt;T&gt; result uses the array type of T as that asserted contract and retains callback-owned storage.
    /// PostgreSQL errors remain guarded, but invalid pointers or ABI contracts can crash the backend.
    /// </remarks>
    [NativeUnsafeAccess]
    public static T DangerousCall<T>(nint function, uint collationOid, params ReadOnlySpan<PgDatum> arguments)
        => NativeBackend.CallNativeFunction<T>(function, collationOid, arguments);

    /// <summary>
    /// Calls a native PostgreSQL version-1 entry point and copies its raw result to a selected owner.
    /// </summary>
    /// <param name="function">The valid native entry-point address.</param>
    /// <param name="resultTypeOid">The actual PostgreSQL return type.</param>
    /// <param name="context">The returned datum's owner.</param>
    /// <param name="collationOid">The input collation, or zero for none.</param>
    /// <param name="arguments">The exact raw arguments and SQL NULL flags.</param>
    /// <returns>A checked copy of the returned datum.</returns>
    /// <remarks>
    /// The caller owns ABI correctness and pointer validity. FmgrInfo, context, and resultinfo are null.
    /// Pointer-bearing types such as internal retain their bits; copying them does not clone their pointees.
    /// </remarks>
    [NativeUnsafeAccess]
    public static PgDatum DangerousCallRaw(nint function, uint resultTypeOid, PgMemoryContext context,
        uint collationOid, params ReadOnlySpan<PgDatum> arguments)
        => NativeBackend.CallRawNativeFunction(function, resultTypeOid, context, collationOid, arguments);

    /// <summary>
    /// Calls a scalar function by its optionally schema-qualified SQL name.
    /// </summary>
    /// <typeparam name="T">The exact managed result type; use a nullable type when SQL NULL is possible.</typeparam>
    /// <param name="name">The SQL identifier, with ordinary PostgreSQL quoting and search-path rules.</param>
    /// <param name="arguments">Typed values, NULLs, and defaults. Trailing default arguments may be omitted.</param>
    /// <returns>An independent managed copy or callback-owned polymorphic result.</returns>
    public static T Call<T>(string name, params ReadOnlySpan<PgFunctionArgument> arguments)
        => NativeBackend.CallFunction<T>(name, 0, null, arguments);

    /// <summary>
    /// Calls a named scalar function with explicit collation or variadic binding options.
    /// </summary>
    /// <typeparam name="T">The exact managed result type.</typeparam>
    /// <param name="name">The SQL function identifier.</param>
    /// <param name="options">The call options.</param>
    /// <param name="arguments">The typed arguments.</param>
    /// <returns>An independent managed copy or callback-owned polymorphic result.</returns>
    public static T Call<T>(string name, PgFunctionCallOptions options, params ReadOnlySpan<PgFunctionArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(options);
        return NativeBackend.CallFunction<T>(name, 0, options, arguments);
    }

    /// <summary>
    /// Calls a scalar function by catalog OID using its declared argument list.
    /// </summary>
    /// <typeparam name="T">The exact managed result type.</typeparam>
    /// <param name="functionOid">The nonzero function OID.</param>
    /// <param name="arguments">Typed arguments; supply a variadic parameter as an array.</param>
    /// <returns>An independent managed copy or callback-owned polymorphic result.</returns>
    public static T Call<T>(uint functionOid, params ReadOnlySpan<PgFunctionArgument> arguments)
    {
        ArgumentOutOfRangeException.ThrowIfZero(functionOid);
        return NativeBackend.CallFunction<T>(null, functionOid, null, arguments);
    }

    /// <summary>
    /// Calls a scalar function by catalog OID with explicit collation options.
    /// </summary>
    /// <typeparam name="T">The exact managed result type.</typeparam>
    /// <param name="functionOid">The nonzero function OID.</param>
    /// <param name="options">The call options.</param>
    /// <param name="arguments">The declared argument list, including any variadic array.</param>
    /// <returns>An independent managed copy or callback-owned polymorphic result.</returns>
    public static T Call<T>(uint functionOid, PgFunctionCallOptions options, params ReadOnlySpan<PgFunctionArgument> arguments)
    {
        ArgumentOutOfRangeException.ThrowIfZero(functionOid);
        ArgumentNullException.ThrowIfNull(options);
        return NativeBackend.CallFunction<T>(null, functionOid, options, arguments);
    }

    /// <summary>
    /// Calls a named function returning void.
    /// </summary>
    /// <param name="name">The SQL function identifier.</param>
    /// <param name="arguments">The typed arguments.</param>
    public static void Call(string name, params ReadOnlySpan<PgFunctionArgument> arguments)
        => NativeBackend.CallVoidFunction(name, 0, null, arguments);

    /// <summary>
    /// Calls a named void-returning function with explicit collation or variadic options.
    /// </summary>
    /// <param name="name">The SQL function identifier.</param>
    /// <param name="options">The call options.</param>
    /// <param name="arguments">The typed arguments.</param>
    public static void Call(string name, PgFunctionCallOptions options, params ReadOnlySpan<PgFunctionArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(options);
        NativeBackend.CallVoidFunction(name, 0, options, arguments);
    }

    /// <summary>
    /// Calls a function returning void by catalog OID.
    /// </summary>
    /// <param name="functionOid">The nonzero function OID.</param>
    /// <param name="arguments">The declared argument list.</param>
    public static void Call(uint functionOid, params ReadOnlySpan<PgFunctionArgument> arguments)
    {
        ArgumentOutOfRangeException.ThrowIfZero(functionOid);
        NativeBackend.CallVoidFunction(null, functionOid, null, arguments);
    }

    /// <summary>
    /// Calls a void-returning function by OID with explicit collation options.
    /// </summary>
    /// <param name="functionOid">The nonzero function OID.</param>
    /// <param name="options">The call options.</param>
    /// <param name="arguments">The declared argument list.</param>
    public static void Call(uint functionOid, PgFunctionCallOptions options, params ReadOnlySpan<PgFunctionArgument> arguments)
    {
        ArgumentOutOfRangeException.ThrowIfZero(functionOid);
        ArgumentNullException.ThrowIfNull(options);
        NativeBackend.CallVoidFunction(null, functionOid, options, arguments);
    }

    /// <summary>
    /// Calls a named scalar function and copies its exact native result into a selected memory context.
    /// </summary>
    /// <param name="name">The SQL function identifier.</param>
    /// <param name="context">The owner of the returned native value.</param>
    /// <param name="arguments">The typed arguments.</param>
    /// <returns>A checked datum preserving the actual result type and SQL NULL.</returns>
    public static PgDatum CallRaw(string name, PgMemoryContext context, params ReadOnlySpan<PgFunctionArgument> arguments)
        => NativeBackend.CallRawFunction(name, 0, context, null, arguments);

    /// <summary>
    /// Calls a named scalar function with options and copies its exact native result into a selected context.
    /// </summary>
    /// <param name="name">The SQL function identifier.</param>
    /// <param name="context">The result owner.</param>
    /// <param name="options">The call options.</param>
    /// <param name="arguments">The typed arguments.</param>
    /// <returns>The context-owned result.</returns>
    public static PgDatum CallRaw(string name, PgMemoryContext context, PgFunctionCallOptions options, params ReadOnlySpan<PgFunctionArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(options);
        return NativeBackend.CallRawFunction(name, 0, context, options, arguments);
    }

    /// <summary>
    /// Calls a scalar function by catalog OID and copies its result into a selected memory context.
    /// </summary>
    /// <param name="functionOid">The nonzero function OID.</param>
    /// <param name="context">The result owner.</param>
    /// <param name="arguments">The declared argument list.</param>
    /// <returns>The context-owned result with exact type identity.</returns>
    public static PgDatum CallRaw(uint functionOid, PgMemoryContext context, params ReadOnlySpan<PgFunctionArgument> arguments)
    {
        ArgumentOutOfRangeException.ThrowIfZero(functionOid);
        return NativeBackend.CallRawFunction(null, functionOid, context, null, arguments);
    }

    /// <summary>
    /// Calls a scalar function by catalog OID with options and copies its result into a selected context.
    /// </summary>
    /// <param name="functionOid">The nonzero function OID.</param>
    /// <param name="context">The result owner.</param>
    /// <param name="options">The call options.</param>
    /// <param name="arguments">The declared argument list.</param>
    /// <returns>The context-owned result with exact type identity.</returns>
    public static PgDatum CallRaw(uint functionOid, PgMemoryContext context, PgFunctionCallOptions options, params ReadOnlySpan<PgFunctionArgument> arguments)
    {
        ArgumentOutOfRangeException.ThrowIfZero(functionOid);
        ArgumentNullException.ThrowIfNull(options);
        return NativeBackend.CallRawFunction(null, functionOid, context, options, arguments);
    }
}
