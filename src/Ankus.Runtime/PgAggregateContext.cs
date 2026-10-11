using System.Collections.ObjectModel;

namespace Ankus;

/// <summary>
/// Owns aggregate execution metadata and provides state storage and native ordering comparisons during its active callback.
/// Metadata remains readable after callback exit; storage lookup and comparison require the exact current context and backend thread.
/// </summary>
public sealed class PgAggregateContext
{
    /// <summary>
    /// Retains validated native bindings and copies the ordered sort metadata.
    /// </summary>
    internal PgAggregateContext(PgAggregateContextKind kind, bool isStateShared, uint collationOid, uint? aggregateOid,
        PgAggregateSortKey[] sortKeys, nint owner, nint api, PgAggregateContext? parent)
    {
        Kind = kind;
        IsStateShared = isStateShared;
        CollationOid = collationOid;
        AggregateOid = aggregateOid;
        SortKeys = sortKeys.Length == 0 ? ReadOnlyCollection<PgAggregateSortKey>.Empty
            : Array.AsReadOnly<PgAggregateSortKey>([.. sortKeys]);
        Owner = owner;
        Api = api;
        Parent = parent;
    }

    /// <summary>
    /// Gets whether this callback belongs to an aggregate or window executor context.
    /// </summary>
    public PgAggregateContextKind Kind { get; }

    /// <summary>
    /// Gets whether PostgreSQL can share the transition state with other aggregate evaluations.
    /// </summary>
    public bool IsStateShared { get; }

    /// <summary>
    /// Gets the callback's input collation OID, or zero when no collation applies.
    /// </summary>
    public uint CollationOid { get; }

    /// <summary>
    /// Gets the aggregate function OID when an Aggref is available, otherwise null, including window contexts.
    /// </summary>
    public uint? AggregateOid { get; }

    /// <summary>
    /// Gets immutable ordering keys copied from the aggregate invocation.
    /// </summary>
    public IReadOnlyList<PgAggregateSortKey> SortKeys { get; }

    /// <summary>
    /// Gets the borrowed PostgreSQL owner for values retained in this aggregate's state.
    /// </summary>
    /// <remarks>
    /// Access requires the current aggregate callback. Copy polymorphic inputs here before retaining
    /// them in managed state. The returned handle and copied values expire when PostgreSQL resets
    /// or deletes the owner, including moving-window restarts and group completion.
    /// </remarks>
    public PgMemoryContext MemoryContext => NativeAggregate.GetMemoryContext(this);

    /// <summary>
    /// Gets the native memory context that owns new managed states returned by this callback.
    /// </summary>
    internal nint Owner { get; }

    /// <summary>
    /// Gets the guarded native registration, comparison, and memory-owner entry point.
    /// </summary>
    internal nint Api { get; }

    /// <summary>
    /// Gets the enclosing aggregate callback while this scope remains active.
    /// </summary>
    internal PgAggregateContext? Parent
    {
        get;
        private set;
    }

    /// <summary>
    /// Releases the enclosing context link and returns the context to restore after this callback exits.
    /// </summary>
    internal PgAggregateContext? DetachParent()
    {
        PgAggregateContext? parent = Parent;
        Parent = null;
        return parent;
    }

    /// <summary>
    /// Compares two values using the selected PostgreSQL sort operator, collation and NULL placement.
    /// Use explicitly typed SpiParameter operands when a NULL named composite or composite array has no managed value carrying its identity.
    /// </summary>
    /// <typeparam name="T">The managed type of both operands, including nullability for SQL NULL.</typeparam>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="sortKey">The zero-based index in SortKeys.</param>
    /// <returns>A negative value, zero, or a positive value according to PostgreSQL's ordering.</returns>
    public int Compare<T>(T left, T right, int sortKey = 0) => NativeAggregate.Compare(this, left, right, sortKey);

    /// <summary>
    /// Compares explicitly typed operands using the selected PostgreSQL sort operator, collation and NULL placement.
    /// Descriptor-based SpiParameter factories preserve named composite and composite-array identities even when an operand is SQL NULL.
    /// </summary>
    /// <param name="left">The typed left operand, including its PostgreSQL identity when SQL NULL.</param>
    /// <param name="right">The typed right operand, including its PostgreSQL identity when SQL NULL.</param>
    /// <param name="sortKey">The zero-based index in SortKeys.</param>
    /// <returns>A negative value, zero, or a positive value according to PostgreSQL's ordering.</returns>
    public int Compare(SpiParameter left, SpiParameter right, int sortKey = 0) => NativeAggregate.Compare(this, left, right, sortKey);
}
