using System;
using System.Collections.Generic;

namespace Ankus;

/// <summary>
/// Describes the PostgreSQL function call and provides checked raw copies of its SQL arguments.
/// </summary>
/// <remarks>
/// Declare this type as a function parameter to receive it without adding a SQL argument.
/// Metadata is an independent managed snapshot. Argument storage belongs to the scalar callback's
/// memory context or the set function's multi-call context. Copy an argument to another context
/// when it needs a longer lifetime. Native argument access requires the owning backend thread.
/// </remarks>
public sealed class PgFunctionContext
{
    private readonly PgFunctionStateScope? _state;

    /// <summary>
    /// Creates an immutable snapshot after native argument copies have completed.
    /// </summary>
    /// <param name="functionOid">The invoked function's catalog identity.</param>
    /// <param name="resultTypeOid">The resolved result type.</param>
    /// <param name="collationOid">The call's collation, or zero when no collation applies.</param>
    /// <param name="arguments">The checked raw SQL arguments in declaration order.</param>
    /// <param name="state">The checked call-site cache owner, when supplied by native dispatch.</param>
    internal PgFunctionContext(uint functionOid, uint resultTypeOid, uint collationOid, PgDatum[] arguments, PgFunctionStateScope? state = null)
    {
        FunctionOid = functionOid;
        ResultTypeOid = resultTypeOid;
        CollationOid = collationOid;
        Arguments = Array.AsReadOnly(arguments);
        _state = state;
    }

    /// <summary>
    /// Gets the catalog OID of the PostgreSQL function being invoked.
    /// </summary>
    public uint FunctionOid { get; }

    /// <summary>
    /// Gets the resolved PostgreSQL result type OID, including domains and record results.
    /// </summary>
    public uint ResultTypeOid { get; }

    /// <summary>
    /// Gets the collation selected for this call, or zero when no collation applies.
    /// </summary>
    public uint CollationOid { get; }

    /// <summary>
    /// Gets the SQL arguments by zero-based ordinal, excluding injected managed parameters.
    /// Every value retains its actual type OID and SQL NULL flag.
    /// </summary>
    public IReadOnlyList<PgDatum> Arguments { get; }

    /// <summary>
    /// Gets the borrowed PostgreSQL context that owns this call site's cached state.
    /// </summary>
    /// <remarks>
    /// Use this context for native allocations retained by cached state. It can outlive individual
    /// arguments and iterator instances. Access requires the owning backend thread and a live owner.
    /// </remarks>
    public PgMemoryContext StateMemoryContext => State.GetMemoryContext();

    /// <summary>
    /// Returns this call site's cached state, creating it once after the first successful factory call.
    /// </summary>
    /// <typeparam name="T">The exact managed state type used consistently at this call site.</typeparam>
    /// <param name="factory">The synchronous factory, invoked only when no successful value has been cached.</param>
    /// <returns>The cached value, including null when the factory returns null.</returns>
    /// <remarks>
    /// Different PostgreSQL call sites have independent state, even when they invoke the same SQL function.
    /// Factory failures permit a later retry; recursive initialization of the same site is rejected.
    /// PostgreSQL reset or deletion releases the cached value and calls IDisposable.Dispose when implemented.
    /// State lookup requires the owning backend thread. Do not use a disposed state after its owner ends.
    /// </remarks>
    public T GetOrCreateState<T>(Func<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return State.GetOrCreate(factory);
    }

    /// <summary>
    /// Gets the native cache binding after rejecting manually constructed metadata-only snapshots.
    /// </summary>
    private PgFunctionStateScope State => _state ?? throw new InvalidOperationException("The function context has no native state owner.");
}
