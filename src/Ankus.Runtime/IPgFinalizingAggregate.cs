using System;

namespace Ankus;

/// <summary>
/// Converts ordinary aggregate state into a SQL result.
/// </summary>
/// <typeparam name="TState">The state accepted by finalization, including SQL nullability.</typeparam>
/// <typeparam name="TDirect">Ordered-set direct arguments, or <see cref="ValueTuple"/> for none.</typeparam>
/// <typeparam name="TResult">The SQL result, including its nullability.</typeparam>
public interface IPgFinalizingAggregate<TState, TDirect, TResult>
{
    /// <summary>
    /// Produces the group's result using any direct arguments.
    /// </summary>
    /// <param name="context">The current aggregate invocation and state owner.</param>
    /// <param name="state">The completed state.</param>
    /// <param name="arguments">Arguments evaluated once for this group.</param>
    /// <returns>The aggregate result.</returns>
    static abstract TResult Final(PgAggregateContext context, TState state, TDirect arguments);
}
