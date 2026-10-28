using System;

namespace Ankus;

/// <summary>
/// Converts moving-window state into the aggregate's SQL result.
/// </summary>
/// <typeparam name="TState">The moving state accepted by finalization.</typeparam>
/// <typeparam name="TDirect">The direct arguments, or <see cref="ValueTuple"/> for none.</typeparam>
/// <typeparam name="TResult">The same SQL result type as ordinary aggregation.</typeparam>
public interface IPgMovingFinalizingAggregate<TState, TDirect, TResult>
{
    /// <summary>
    /// Produces the current window's aggregate result.
    /// </summary>
    /// <param name="context">The window invocation and state owner.</param>
    /// <param name="state">The current moving state.</param>
    /// <param name="arguments">The direct arguments.</param>
    /// <returns>The current result.</returns>
    static abstract TResult MovingFinal(PgAggregateContext context, TState state, TDirect arguments);
}
