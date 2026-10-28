using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ankus;

/// <summary>
/// Runs a property test inside PostgreSQL, the counterpart of pgrx's <c>PgTestRunner</c>: it checks a condition
/// against many generated inputs, reports a backend error as a failing input, and shrinks a failing input to a
/// small one.
/// </summary>
/// <param name="options">The case count, seed and limits, or null for proptest's defaults.</param>
/// <remarks>
/// Each case runs in its own internal subtransaction, so a PostgreSQL error rolls back that case's work and the run
/// continues; a passing case keeps its changes in the enclosing transaction. Any exception from the test, including
/// a <see cref="PgException"/>, fails the case. Query cancellation and other operator-intervention errors stop the
/// run instead. Call it from a <see cref="PgTestAttribute"/> method or another backend callback.
/// </remarks>
/// <example>
/// <code>
/// [PgTest]
/// public static void DatesRoundTrip()
///     => new PgPropertyRunner().Run(
///         PgGenerators.Number&lt;int&gt;().Select(PgDate.FromRawSaturating),
///         date => PgAssert.AreEqual(date, Spi.ExecuteScalar&lt;PgDate&gt;("SELECT $1", SpiParameter.Create(date))));
/// </code>
/// </example>
public sealed class PgPropertyRunner(PgPropertyOptions? options = null)
{
    private readonly PgPropertyOptions _options = options ?? new();

    /// <summary>
    /// Checks a property against generated inputs.
    /// </summary>
    /// <typeparam name="T">The input type.</typeparam>
    /// <param name="generator">The input generator.</param>
    /// <param name="test">The property, which throws to fail.</param>
    /// <exception cref="PgPropertyException">
    /// An input failed, with the smallest failing input as <see cref="PgPropertyException.Input"/> and its failure as the
    /// inner exception, or the generator rejected too many cases.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its range.</exception>
    public void Run<T>(PgGenerator<T> generator, Action<T> test)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(test);
        Run(generator, test, static action => PgTransaction.RunInSubtransaction(action));
    }

    /// <summary>
    /// Checks a property, running each case through <paramref name="isolate"/>.
    /// </summary>
    internal void Run<T>(PgGenerator<T> generator, Action<T> test, Action<Action> isolate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Cases, 1, nameof(PgPropertyOptions.Cases));
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxShrinkRuns, nameof(PgPropertyOptions.MaxShrinkRuns));
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxRejects, nameof(PgPropertyOptions.MaxRejects));
        ulong seed = _options.Seed ?? unchecked((ulong)Random.Shared.NextInt64(long.MinValue, long.MaxValue));
        int passed = 0;
        int rejected = 0;
        for (int attempt = 0; passed < _options.Cases; attempt++)
        {
            PgPropertySource source = PgPropertySource.Random(PgPropertySource.CaseSeed(seed, attempt));
            Case<T> result = Execute(generator, test, isolate, source);
            if (result.Rejected)
            {
                if (++rejected > _options.MaxRejects)
                {
                    throw new PgPropertyException(string.Create(CultureInfo.InvariantCulture,
                        $"Too many rejected cases: {rejected} cases were rejected after {passed} passed (seed {seed})."),
                        null, seed, string.Empty, passed, 0);
                }

                continue;
            }

            if (result.Failure is null)
            {
                passed++;
                continue;
            }

            (Case<T> smallest, int runs) = Shrink(generator, test, isolate, result);
            string input = Describe(smallest.Value);
            throw new PgPropertyException(string.Create(CultureInfo.InvariantCulture,
                $"Test failed: {smallest.Failure!.Message}; minimal failing input: {input}. {passed} cases passed before it; " +
                $"rerun with seed {seed} to reproduce it ({runs} shrinking runs)."), smallest.Failure, seed, input, passed, runs);
        }
    }

    /// <summary>
    /// Formats an input for a failure report.
    /// </summary>
    /// <param name="value">The input.</param>
    /// <returns>Text and characters quoted, sequences in brackets, tuples in parentheses and numbers invariantly.</returns>
    internal static string Describe(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string text:
                return Quote(text, '"');
            case char character:
                return Quote(character.ToString(), '\'');
            case Rune rune:
                return Quote(rune.ToString(), '\'');
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            case ITuple tuple:
                string[] items = new string[tuple.Length];
                for (int index = 0; index < tuple.Length; index++)
                {
                    items[index] = Describe(tuple[index]);
                }

                return "(" + string.Join(", ", items) + ")";
            case IEnumerable sequence:
                var elements = new List<string>();
                foreach (object? element in sequence)
                {
                    elements.Add(Describe(element));
                }

                return "[" + string.Join(", ", elements) + "]";
            default:
                return value.ToString() ?? string.Empty;
        }
    }

    private static Case<T> Execute<T>(PgGenerator<T> generator, Action<T> test, Action<Action> isolate, PgPropertySource source)
    {
        T value;
        try
        {
            value = generator.Generate(source);
        }
        catch (Exception error) when (error is PgPropertyRejectedException or PgPropertyOverrunException)
        {
            return new(default!, [.. source.Choices], null, Rejected: true);
        }

        ulong[] choices = [.. source.Choices];
        try
        {
            isolate(() => test(value));
            return new(value, choices, null, Rejected: false);
        }
        catch (PgException error) when (error.SqlState.StartsWith("57", StringComparison.Ordinal))
        {
            // Class 57, operator intervention, includes query cancellation; it ends the run rather than an input.
            throw;
        }
        // codeql[cs/catch-of-all-exceptions] A property test records whatever the code under test throws for each input.
        catch (Exception error)
        {
            return new(value, choices, error, Rejected: false);
        }
    }

    /// <summary>
    /// Shrinks a failing case's choices by deleting, zeroing and halving them while the test still fails, keeping
    /// each shorter or lexicographically smaller sequence, until no pass improves it or the run limit is reached.
    /// </summary>
    private (Case<T> Smallest, int Runs) Shrink<T>(PgGenerator<T> generator, Action<T> test, Action<Action> isolate, Case<T> failing)
    {
        Case<T> best = failing;
        int runs = 0;
        bool Attempt(ulong[] candidate)
        {
            if (runs >= _options.MaxShrinkRuns)
            {
                return false;
            }

            runs++;
            Case<T> result = Execute(generator, test, isolate, PgPropertySource.Replay(candidate));
            if (result.Failure is null || result.Rejected || !Simpler(result.Choices, best.Choices))
            {
                return false;
            }

            best = result;
            return true;
        }

        ReadOnlySpan<int> sizes = [8, 4, 2, 1];
        bool improved = true;
        while (improved && runs < _options.MaxShrinkRuns)
        {
            improved = false;
            foreach (int size in sizes)
            {
                for (int start = best.Choices.Length - size; start >= 0; start--)
                {
                    start = Math.Min(start, best.Choices.Length - size);
                    ulong[] current = best.Choices;
                    improved |= start >= 0 && Attempt([.. current.AsSpan(0, start), .. current.AsSpan(start + size)]);
                }
            }

            foreach (int size in sizes)
            {
                for (int start = 0; start + size <= best.Choices.Length; start++)
                {
                    if (best.Choices.AsSpan(start, size).IndexOfAnyExcept(0UL) >= 0)
                    {
                        ulong[] candidate = [.. best.Choices];
                        candidate.AsSpan(start, size).Clear();
                        improved |= Attempt(candidate);
                    }
                }
            }

            for (int index = 0; index < best.Choices.Length; index++)
            {
                // Binary search for the smallest choice that still fails, assuming larger choices keep failing.
                ulong low = 0;
                while (index < best.Choices.Length && best.Choices[index] > low)
                {
                    ulong high = best.Choices[index];
                    ulong middle = low + ((high - low) / 2);
                    ulong[] candidate = [.. best.Choices];
                    candidate[index] = middle;
                    if (Attempt(candidate))
                    {
                        improved = true;
                    }
                    else if (middle == low)
                    {
                        break;
                    }
                    else
                    {
                        low = middle + 1;
                    }
                }
            }
        }

        return (best, runs);
    }

    /// <summary>
    /// Orders choice sequences by length, then lexicographically, so shrinking always terminates.
    /// </summary>
    private static bool Simpler(ulong[] candidate, ulong[] current)
        => candidate.Length != current.Length ? candidate.Length < current.Length : candidate.AsSpan().SequenceCompareTo(current) < 0;

    private static string Quote(string text, char quote)
    {
        StringBuilder result = new StringBuilder().Append(quote);
        foreach (char character in text)
        {
            _ = character switch
            {
                '\\' => result.Append(@"\\"),
                '\n' => result.Append(@"\n"),
                '\r' => result.Append(@"\r"),
                '\t' => result.Append(@"\t"),
                _ when character == quote => result.Append('\\').Append(character),
                _ when char.IsControl(character) => result.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}"),
                _ => result.Append(character),
            };
        }

        return result.Append(quote).ToString();
    }

    /// <summary>
    /// One case's input, the choices that produced it, and its failure or rejection.
    /// </summary>
    private readonly record struct Case<T>(T Value, ulong[] Choices, Exception? Failure, bool Rejected);
}
