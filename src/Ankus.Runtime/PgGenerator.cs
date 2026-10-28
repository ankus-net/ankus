using System;

namespace Ankus;

/// <summary>
/// Generates random inputs for <see cref="PgPropertyRunner"/>, the counterpart of a proptest strategy.
/// </summary>
/// <typeparam name="T">The generated value.</typeparam>
/// <remarks>
/// Create generators with <see cref="PgGenerators"/> and compose them with <see cref="Select{TResult}"/>,
/// <see cref="Where"/> and LINQ query syntax. Every composed generator shrinks a failing input toward simpler values
/// without extra code: numbers toward zero, strings and arrays toward fewer and simpler elements, and choices toward
/// the first alternative.
/// </remarks>
public sealed class PgGenerator<T>
{
    /// <summary>
    /// The number of draws <see cref="Where"/> makes before rejecting the case.
    /// </summary>
    private const int FilterAttempts = 100;

    private readonly Func<PgPropertySource, T> _generate;

    /// <summary>
    /// Wraps a function that builds a value from source choices.
    /// </summary>
    internal PgGenerator(Func<PgPropertySource, T> generate)
    {
        ArgumentNullException.ThrowIfNull(generate);
        _generate = generate;
    }

    /// <summary>
    /// Projects each generated value.
    /// </summary>
    /// <typeparam name="TResult">The projected value.</typeparam>
    /// <param name="selector">The projection.</param>
    /// <returns>A generator of projected values, which shrink as their sources do.</returns>
    public PgGenerator<TResult> Select<TResult>(Func<T, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new(source => selector(Generate(source)));
    }

    /// <summary>
    /// Generates a value, then a value from a generator that depends on it.
    /// </summary>
    /// <typeparam name="TResult">The dependent value.</typeparam>
    /// <param name="selector">Chooses the dependent generator.</param>
    /// <returns>A generator of dependent values.</returns>
    public PgGenerator<TResult> SelectMany<TResult>(Func<T, PgGenerator<TResult>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new(source => selector(Generate(source)).Generate(source));
    }

    /// <summary>
    /// Generates a value, then a value from a generator that depends on it, and combines them; this supports
    /// multiple <c>from</c> clauses in LINQ query syntax.
    /// </summary>
    /// <typeparam name="TOther">The dependent value.</typeparam>
    /// <typeparam name="TResult">The combined value.</typeparam>
    /// <param name="selector">Chooses the dependent generator.</param>
    /// <param name="resultSelector">Combines both values.</param>
    /// <returns>A generator of combined values.</returns>
    public PgGenerator<TResult> SelectMany<TOther, TResult>(Func<T, PgGenerator<TOther>> selector, Func<T, TOther, TResult> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);
        return new(source =>
        {
            T value = Generate(source);
            return resultSelector(value, selector(value).Generate(source));
        });
    }

    /// <summary>
    /// Keeps only values that satisfy a condition, as proptest's <c>prop_filter</c> does.
    /// </summary>
    /// <param name="predicate">The condition.</param>
    /// <returns>A generator that draws again when the condition fails.</returns>
    /// <remarks>
    /// After 100 failed draws for one input, the runner rejects that case and generates another. A run fails when it
    /// rejects more cases than <see cref="PgPropertyOptions.MaxRejects"/> allows, so prefer constructing valid values
    /// with <see cref="Select{TResult}"/> over discarding most of them.
    /// </remarks>
    public PgGenerator<T> Where(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new(source =>
        {
            for (int attempt = 0; attempt < FilterAttempts; attempt++)
            {
                T value = Generate(source);
                if (predicate(value))
                {
                    return value;
                }
            }

            throw new PgPropertyRejectedException();
        });
    }

    /// <summary>
    /// Builds a value from source choices.
    /// </summary>
    internal T Generate(PgPropertySource source) => _generate(source);
}

/// <summary>
/// Reports that a filtered generator could not produce an acceptable value for one case.
/// </summary>
internal sealed class PgPropertyRejectedException() : Exception("A filtered generator rejected every value it drew for one case.");
