using System;

namespace Ankus;

/// <summary>
/// Defines a compiler-checked PostgreSQL aggregate transition.
/// </summary>
/// <typeparam name="TState">The state, including its SQL nullability.</typeparam>
/// <typeparam name="TArgs">One input, a tuple of inputs, or <see cref="ValueTuple"/> for no inputs.</typeparam>
/// <remarks>
/// Apply <see cref="PgAggregateAttribute"/> to the implementing class or struct.
/// Tuple elements become separate SQL arguments. Arrays remain single SQL values.
/// Additional interfaces supply optional final, parallel and moving capabilities.
/// </remarks>
public interface IPgAggregate<TState, TArgs>
{
    /// <summary>
    /// Incorporates one row into the aggregate state.
    /// </summary>
    /// <param name="context">The current aggregate invocation and state owner.</param>
    /// <param name="state">The current state.</param>
    /// <param name="arguments">The row's aggregated inputs.</param>
    /// <returns>The updated state.</returns>
    static abstract TState Transition(PgAggregateContext context, TState state, TArgs arguments);
}
