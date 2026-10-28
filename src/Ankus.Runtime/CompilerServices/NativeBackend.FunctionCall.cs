using System;

namespace Ankus.CompilerServices;

/// <summary>
/// Resolves function calls and transports owned arguments and results through the native error guard.
/// </summary>
public static unsafe partial class NativeBackend
{
    /// <summary>
    /// Calls an explicitly supplied native address and copies its managed result.
    /// </summary>
    /// <typeparam name="T">The actual return type.</typeparam>
    /// <param name="function">The validated native address.</param>
    /// <param name="collation">The native collation.</param>
    /// <param name="arguments">Checked raw values.</param>
    /// <returns>The independent result.</returns>
    internal static T CallNativeFunction<T>(nint function, uint collation, ReadOnlySpan<PgDatum> arguments)
    {
        if (PgDatumRegistry.Find(typeof(T)) is { } mapping)
        {
            mapping.RequireRead();
            ArgumentOutOfRangeException.ThrowIfZero(function);
            return CallMappedNativeFunction<T>(function, mapping.GetOid(), collation, arguments);
        }

        if (PgDatumRegistry.FindArray(typeof(T)) is { } array)
        {
            array.RequireRead();
            ArgumentOutOfRangeException.ThrowIfZero(function);
            return CallMappedNativeFunction<T>(function, array.GetOid(), collation, arguments);
        }

        PgDatumRegistry.RejectOrdinaryResult<T>();
        if (PgArrayViews.Find(typeof(T)) is { } view)
        {
            view.RequireRead();
            ArgumentOutOfRangeException.ThrowIfZero(function);
            return CallRawNativeFunction(function, view.GetOid(), PgMemoryContext.Callback, collation, arguments).Read<T>();
        }

        if (PgBufferViews.Is<T>())
        {
            return CallRawNativeFunction(function, SpiType.GetOid<T>(), PgMemoryContext.Callback, collation, arguments).Read<T>();
        }

        return RunNativeFunction(function, SpiType.GetOid<T>(), null, collation, arguments, static result =>
        {
            uint type = checked((uint)result._rowsAffected);
            object? value = result._text.IsEnum && result._text.IsNull == 0
                ? PgEnumRegistry.FindOid(type).FromLabel(result._text.ReadString())
                : SpiType.FromNative(result._text, type);
            return SpiRow.Convert<T>(value);
        });
    }

    /// <summary>
    /// Reads a caller-asserted native result before releasing its temporary callback-owned storage.
    /// </summary>
    /// <typeparam name="T">The registered reader's detached result type.</typeparam>
    /// <param name="function">The native address whose representation contract the caller supplies.</param>
    /// <param name="type">The captured current mapped result OID, without catalog proof of the entry point's result.</param>
    /// <param name="collation">The input collation.</param>
    /// <param name="arguments">The borrowed raw arguments.</param>
    /// <returns>The detached managed result.</returns>
    private static T CallMappedNativeFunction<T>(nint function, uint type, uint collation, ReadOnlySpan<PgDatum> arguments)
    {
        PgMemoryContext owner = PgMemoryContext.Create("Ankus mapped native result", PgMemoryContext.Callback);
        Exception? primary = null;
        try
        {
            return CallRawNativeFunction(function, type, owner, collation, arguments).Read<T>();
        }
        catch (Exception exception)
        {
            primary = exception;
            throw;
        }
        finally
        {
            PgResultCleanup.Dispose(owner, primary);
        }
    }

    /// <summary>
    /// Calls a native entry point with a separately owned raw result.
    /// </summary>
    /// <param name="function">The native address.</param>
    /// <param name="type">The actual result OID.</param>
    /// <param name="context">The native result owner.</param>
    /// <param name="collation">The input collation.</param>
    /// <param name="arguments">The checked raw arguments.</param>
    /// <returns>The context-owned result.</returns>
    internal static PgDatum CallRawNativeFunction(nint function, uint type, PgMemoryContext context,
        uint collation, ReadOnlySpan<PgDatum> arguments)
    {
        ArgumentOutOfRangeException.ThrowIfZero(type);
        ArgumentNullException.ThrowIfNull(context);
        var lifetime = new PgDatumLifetime(context);
        return RunNativeFunction(function, type, lifetime, collation, arguments, result =>
            new PgDatum(unchecked((nuint)result._text.Integral), type, result._text.IsNull != 0, lifetime));
    }

    /// <summary>
    /// Marshals direct-call parameters and balances every transport allocation on failure or success.
    /// </summary>
    /// <typeparam name="T">The copied result type.</typeparam>
    /// <param name="function">The native address.</param>
    /// <param name="type">The actual result type.</param>
    /// <param name="lifetime">The optional raw result lifetime.</param>
    /// <param name="collation">The input collation.</param>
    /// <param name="arguments">The raw arguments.</param>
    /// <param name="convert">The result copier.</param>
    /// <returns>The copied result.</returns>
    private static T RunNativeFunction<T>(nint function, uint type, PgDatumLifetime? lifetime,
        uint collation, ReadOnlySpan<PgDatum> arguments, Func<NativeSpiResult, T> convert)
    {
        ArgumentOutOfRangeException.ThrowIfZero(function);
        CheckAccess();
        lifetime?.Validate();
        var parameters = new SpiParameter[arguments.Length];
        for (int index = 0; index < arguments.Length; index++)
        {
            parameters[index] = SpiParameter.Create(arguments[index]);
        }

        NativeSpiRequest request = new()
        {
            _operation = SpiOperation.FunctionCall,
            _nativeFunction = function,
            _scalarResultOid = type,
            _collationOid = collation,
            _resultContext = lifetime?.ContextId ?? 0,
            _resultGeneration = lifetime?.Generation ?? 0,
        };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, parameters, &result);
            return convert(result);
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    /// <summary>
    /// Calls a catalog function and copies its value into the requested exact managed type.
    /// </summary>
    /// <typeparam name="T">The managed result type.</typeparam>
    /// <param name="name">The SQL identifier, or null for OID lookup.</param>
    /// <param name="oid">The function OID, or zero for name lookup.</param>
    /// <param name="options">Optional collation and binding settings.</param>
    /// <param name="arguments">The typed arguments and defaults.</param>
    /// <returns>The managed copy or callback-owned polymorphic result.</returns>
    internal static T CallFunction<T>(string? name, uint oid, PgFunctionCallOptions? options, ReadOnlySpan<PgFunctionArgument> arguments)
    {
        if (PgDatumRegistry.Find(typeof(T)) is { } mapping)
        {
            mapping.RequireRead();
            return CallMappedFunction<T>(name, oid, options, arguments, mapping.GetOid());
        }

        if (PgDatumRegistry.FindArray(typeof(T)) is { } array)
        {
            array.RequireRead();
            return CallMappedFunction<T>(name, oid, options, arguments, array.GetOid());
        }

        PgDatumRegistry.RejectOrdinaryResult<T>();
        PgArrayViewMapping? view = PgArrayViews.Find(typeof(T));
        view?.RequireRead();
        if (PgPolymorphic.Is<T>() || PgBufferViews.Is<T>() || view is not null)
        {
            var lifetime = new PgDatumLifetime(PgMemoryContext.Callback);
            uint expected = view is not null ? view.GetElementOid() : PgBufferViews.Is<T>() ? SpiType.GetOid<T>() :
                typeof(T) == typeof(PgAnyArray) || typeof(T) == typeof(PgArrayView) ? 2277U : 2283U;
            return RunFunction(name, oid, options, arguments, expected, lifetime, result =>
                new PgDatum(unchecked((nuint)result._text.Integral),
                    result._resultTypeOid, result._text.IsNull != 0, lifetime).Read<T>(),
                exactResult: view?.ExactElementIdentity == true, arrayElement: view is not null);
        }

        return RunFunction(name, oid, options, arguments, SpiType.GetOid<T>(), null, static result =>
        {
            uint baseType = checked((uint)result._rowsAffected);
            object? value = result._text.IsEnum && result._text.IsNull == 0
                ? PgEnumRegistry.FindOid(baseType).FromLabel(result._text.ReadString())
                : SpiType.FromNative(result._text, baseType);
            return SpiRow.Convert<T>(value);
        });
    }

    /// <summary>
    /// Reads an exactly typed catalog result before deleting its temporary callback-owned storage.
    /// </summary>
    /// <typeparam name="T">The registered reader's managed result type.</typeparam>
    /// <param name="name">The SQL identifier, or null for OID lookup.</param>
    /// <param name="oid">The catalog function OID, or zero for name lookup.</param>
    /// <param name="options">The optional call settings.</param>
    /// <param name="arguments">The borrowed typed arguments and defaults.</param>
    /// <param name="expected">The current exact result OID of the preflighted reader contract.</param>
    /// <returns>The detached managed value.</returns>
    private static T CallMappedFunction<T>(string? name, uint oid, PgFunctionCallOptions? options,
        ReadOnlySpan<PgFunctionArgument> arguments, uint expected)
    {
        PgMemoryContext owner = PgMemoryContext.Create("Ankus mapped function result", PgMemoryContext.Callback);
        Exception? primary = null;
        try
        {
            var lifetime = new PgDatumLifetime(owner);
            return RunFunction(name, oid, options, arguments, expected, lifetime, result =>
                new PgDatum(unchecked((nuint)result._text.Integral), result._resultTypeOid,
                    result._text.IsNull != 0, lifetime).Read<T>(), exactResult: true);
        }
        catch (Exception exception)
        {
            primary = exception;
            throw;
        }
        finally
        {
            PgResultCleanup.Dispose(owner, primary);
        }
    }

    /// <summary>
    /// Calls a function whose catalog result type is void.
    /// </summary>
    /// <param name="name">The SQL identifier.</param>
    /// <param name="oid">The explicit function identity.</param>
    /// <param name="options">The optional call options.</param>
    /// <param name="arguments">The typed arguments.</param>
    internal static void CallVoidFunction(string? name, uint oid, PgFunctionCallOptions? options, ReadOnlySpan<PgFunctionArgument> arguments)
        => RunFunction(name, oid, options, arguments, 2278, null, static _ => 0);

    /// <summary>
    /// Calls a function and copies its result before PostgreSQL releases execution storage.
    /// </summary>
    /// <param name="name">The SQL identifier.</param>
    /// <param name="oid">The explicit function identity.</param>
    /// <param name="context">The independently selected result owner.</param>
    /// <param name="options">The optional call options.</param>
    /// <param name="arguments">The typed arguments.</param>
    /// <returns>A checked native result.</returns>
    internal static PgDatum CallRawFunction(string? name, uint oid, PgMemoryContext context,
        PgFunctionCallOptions? options, ReadOnlySpan<PgFunctionArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lifetime = new PgDatumLifetime(context);
        return RunFunction(name, oid, options, arguments, 0, lifetime, result =>
            new PgDatum(unchecked((nuint)result._text.Integral), result._resultTypeOid, result._text.IsNull != 0, lifetime));
    }

    /// <summary>
    /// Validates and pins the call envelope, preserving all parameter and result ownership on every exit.
    /// </summary>
    /// <typeparam name="T">The converted result type.</typeparam>
    /// <param name="name">The SQL name.</param>
    /// <param name="oid">The function OID.</param>
    /// <param name="options">The call options.</param>
    /// <param name="arguments">The typed argument descriptors.</param>
    /// <param name="resultType">The expected managed result OID, or zero for raw capture.</param>
    /// <param name="lifetime">The optional raw result destination.</param>
    /// <param name="convert">The synchronous result copier.</param>
    /// <param name="exactResult">Whether the declared result OID must match before the callee executes.</param>
    /// <param name="arrayElement">
    /// Whether the expected OID describes an array's element; otherwise it describes the entire result.
    /// </param>
    /// <returns>The owned converted result.</returns>
    private static T RunFunction<T>(string? name, uint oid, PgFunctionCallOptions? options,
        ReadOnlySpan<PgFunctionArgument> arguments, uint resultType, PgDatumLifetime? lifetime, Func<NativeSpiResult, T> convert,
        bool exactResult = false, bool arrayElement = false)
    {
        CheckAccess();
        byte[] encoded = oid == 0 ? EncodeFunctionName(name!) : [];
        byte[] defaults = new byte[arguments.Length];
        var parameters = new SpiParameter[arguments.Length];
        for (int index = 0; index < arguments.Length; index++)
        {
            parameters[index] = arguments[index].Parameter;
            defaults[index] = arguments[index].IsDefault ? (byte)1 : (byte)0;
        }

        lifetime?.Validate();
        fixed (byte* text = encoded)
        fixed (byte* argumentDefaults = defaults)
        {
            NativeSpiRequest request = new()
            {
                _operation = SpiOperation.FunctionCall,
                _command = text,
                _commandLength = encoded.Length == 0 ? 0 : encoded.Length - 1,
                _functionOid = oid,
                _collationOid = options?.CollationOid ?? 0,
                _hasCollation = options?.CollationOid is not null ? (byte)1 : (byte)0,
                _variadic = options?.Variadic == true ? (byte)1 : (byte)0,
                _argumentDefaults = argumentDefaults,
                _scalarOperation = (arrayElement ? 2 : 0) + (exactResult ? 1 : 0),
                _scalarResultOid = resultType,
                _resultContext = lifetime?.ContextId ?? 0,
                _resultGeneration = lifetime?.Generation ?? 0,
            };
            NativeSpiResult result = default;
            try
            {
                InvokeParameters(&request, parameters, &result);
                return convert(result);
            }
            finally
            {
                ReleaseResult(&result);
            }
        }
    }

    /// <summary>
    /// Encodes a function name for PostgreSQL's own parser, which reports blank and malformed names alike as invalid
    /// name syntax, as pgrx reports both as invalid identifiers.
    /// </summary>
    /// <param name="name">The optionally qualified SQL name.</param>
    /// <returns>The null-terminated UTF-8 name.</returns>
    private static byte[] EncodeFunctionName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Function names cannot contain a zero character.", nameof(name));
        }

        return EncodeUtf8(name);
    }
}
