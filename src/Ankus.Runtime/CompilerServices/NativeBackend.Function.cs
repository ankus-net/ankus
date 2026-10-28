using System;

namespace Ankus.CompilerServices;

/// <summary>
/// Captures function-call metadata through the guarded backend transport.
/// </summary>
public static unsafe partial class NativeBackend
{
    /// <summary>
    /// Copies a live PostgreSQL FunctionCallInfo supplied by generated dispatch.
    /// </summary>
    /// <param name="functionCall">The live native call pointer, valid until this synchronous capture completes.</param>
    /// <returns>Metadata and checked argument copies owned by the current callback or set context.</returns>
    /// <remarks>
    /// This generator contract requires a valid native pointer; extension authors receive the injected snapshot.
    /// This raw transport contract requires an explicit unsafe context.
    /// The function-call address must remain a live PostgreSQL call frame for the entire capture.
    /// </remarks>
    [NativeUnsafeAccess]
    public static PgFunctionContext CaptureFunction(nint functionCall)
    {
        CheckAccess();
        ArgumentOutOfRangeException.ThrowIfZero(functionCall);
        PgMemoryContext context = PgMemoryContext.Current;
        NativeSpiResult result = default;
        try
        {
            var lifetime = new PgDatumLifetime(context);
            NativeSpiRequest request = new()
            {
                _operation = SpiOperation.FunctionContext,
                _functionCall = functionCall,
                _resultContext = lifetime.ContextId,
                _resultGeneration = lifetime.Generation,
            };
            Invoke(&request, &result);
            var arguments = new PgDatum[result._columnCount];
            for (int index = 0; index < arguments.Length; index++)
            {
                NativeValue value = result._values[index];
                arguments[index] = new PgDatum(unchecked((nuint)value.Integral), result._columns[index]._typeOid,
                    value.IsNull != 0, lifetime);
            }

            var site = new PgFunctionStateScope(NativeMemoryContext.Provider, result._functionSite,
                result._functionMemory, result._functionGeneration);
            return new PgFunctionContext(result._functionOid, result._resultTypeOid, result._collationOid, arguments, site);
        }
        finally
        {
            ReleaseResult(&result);
        }
    }
}
