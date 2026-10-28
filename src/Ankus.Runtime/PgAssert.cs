using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Ankus;

/// <summary>
/// Checks conditions inside backend tests, as pgrx tests use <c>assert!</c> and <c>assert_eq!</c>.
/// </summary>
/// <remarks>
/// A failed check throws <see cref="PgAssertException"/>, which fails the <see cref="PgTestAttribute"/> method with a
/// message naming the expected and actual values. Test frameworks such as MSTest do not load inside an extension, so
/// use these checks in backend code and the framework's own assertions in managed tests.
/// </remarks>
public static class PgAssert
{
    /// <summary>
    /// Checks that a condition is true.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="message">An optional explanation.</param>
    /// <param name="expression">The condition's source text, supplied by the compiler.</param>
    /// <exception cref="PgAssertException">The condition is false.</exception>
    public static void IsTrue(bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition)
        {
            Fail("IsTrue", $"Expected true: {expression}.", message);
        }
    }

    /// <summary>
    /// Checks that a condition is false.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="message">An optional explanation.</param>
    /// <param name="expression">The condition's source text, supplied by the compiler.</param>
    /// <exception cref="PgAssertException">The condition is true.</exception>
    public static void IsFalse(bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (condition)
        {
            Fail("IsFalse", $"Expected false: {expression}.", message);
        }
    }

    /// <summary>
    /// Checks that two values are equal by <see cref="EqualityComparer{T}.Default"/>.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="expected">The expected value.</param>
    /// <param name="actual">The actual value.</param>
    /// <param name="message">An optional explanation.</param>
    /// <exception cref="PgAssertException">The values differ.</exception>
    public static void AreEqual<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Fail("AreEqual", $"Expected: {PgPropertyRunner.Describe(expected)}. Actual: {PgPropertyRunner.Describe(actual)}.", message);
        }
    }

    /// <summary>
    /// Checks that two values differ by <see cref="EqualityComparer{T}.Default"/>.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="notExpected">The value that must not appear.</param>
    /// <param name="actual">The actual value.</param>
    /// <param name="message">An optional explanation.</param>
    /// <exception cref="PgAssertException">The values are equal.</exception>
    public static void AreNotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            Fail("AreNotEqual", $"Expected any value except: {PgPropertyRunner.Describe(notExpected)}.", message);
        }
    }

    /// <summary>
    /// Checks that two floating-point values differ by no more than a tolerance, as MSTest's <c>Assert.AreEqual</c>
    /// with a delta does.
    /// </summary>
    /// <param name="expected">The expected value.</param>
    /// <param name="actual">The actual value.</param>
    /// <param name="delta">The largest allowed difference; NaN equals NaN.</param>
    /// <param name="message">An optional explanation.</param>
    /// <exception cref="PgAssertException">The values differ by more than the tolerance.</exception>
    public static void AreEqual(double expected, double actual, double delta, string? message = null)
    {
        if (double.IsNaN(delta) || delta < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "The tolerance must be zero or positive.");
        }

        double difference = Math.Abs(expected - actual);
        if (!expected.Equals(actual) && (double.IsNaN(difference) || difference > delta))
        {
            Fail("AreEqual", $"Expected: {PgPropertyRunner.Describe(expected)} within {PgPropertyRunner.Describe(delta)}. " +
                $"Actual: {PgPropertyRunner.Describe(actual)}.", message);
        }
    }

    /// <summary>
    /// Checks that a string contains a substring, compared ordinally.
    /// </summary>
    /// <param name="substring">The text that must appear.</param>
    /// <param name="value">The string to search.</param>
    /// <param name="message">An optional explanation.</param>
    /// <exception cref="PgAssertException">The string does not contain the substring.</exception>
    public static void Contains(string substring, string? value, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(substring);
        if (value is null || !value.Contains(substring, StringComparison.Ordinal))
        {
            Fail("Contains", $"Expected text containing: {PgPropertyRunner.Describe(substring)}. Actual: {PgPropertyRunner.Describe(value)}.", message);
        }
    }

    /// <summary>
    /// Checks that a string does not contain a substring, compared ordinally.
    /// </summary>
    /// <param name="substring">The text that must not appear.</param>
    /// <param name="value">The string to search.</param>
    /// <param name="message">An optional explanation.</param>
    /// <exception cref="PgAssertException">The string contains the substring.</exception>
    public static void DoesNotContain(string substring, string? value, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(substring);
        if (value is not null && value.Contains(substring, StringComparison.Ordinal))
        {
            Fail("DoesNotContain", $"Expected text without: {PgPropertyRunner.Describe(substring)}. Actual: {PgPropertyRunner.Describe(value)}.", message);
        }
    }

    /// <summary>
    /// Checks that a string starts with a prefix, compared ordinally.
    /// </summary>
    /// <param name="prefix">The expected beginning.</param>
    /// <param name="value">The string.</param>
    /// <param name="message">An optional explanation.</param>
    /// <exception cref="PgAssertException">The string does not start with the prefix.</exception>
    public static void StartsWith(string prefix, string? value, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            Fail("StartsWith", $"Expected text starting with: {PgPropertyRunner.Describe(prefix)}. Actual: {PgPropertyRunner.Describe(value)}.", message);
        }
    }

    /// <summary>
    /// Checks that a string ends with a suffix, compared ordinally.
    /// </summary>
    /// <param name="suffix">The expected end.</param>
    /// <param name="value">The string.</param>
    /// <param name="message">An optional explanation.</param>
    /// <exception cref="PgAssertException">The string does not end with the suffix.</exception>
    public static void EndsWith(string suffix, string? value, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        if (value is null || !value.EndsWith(suffix, StringComparison.Ordinal))
        {
            Fail("EndsWith", $"Expected text ending with: {PgPropertyRunner.Describe(suffix)}. Actual: {PgPropertyRunner.Describe(value)}.", message);
        }
    }

    /// <summary>
    /// Checks that a sequence has exactly the given number of elements.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="expected">The expected count.</param>
    /// <param name="values">The sequence.</param>
    /// <param name="message">An optional explanation.</param>
    /// <exception cref="PgAssertException">The count differs.</exception>
    public static void HasCount<T>(int expected, IEnumerable<T> values, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        T[] items = [.. values];
        if (items.Length != expected)
        {
            Fail("HasCount", $"Expected {expected} elements. Actual: {items.Length}: {PgPropertyRunner.Describe(items)}.", message);
        }
    }

    /// <summary>
    /// Checks that a sequence has no elements.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="values">The sequence.</param>
    /// <param name="message">An optional explanation.</param>
    /// <exception cref="PgAssertException">The sequence has elements.</exception>
    public static void IsEmpty<T>(IEnumerable<T> values, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        T[] items = [.. values];
        if (items.Length != 0)
        {
            Fail("IsEmpty", $"Expected no elements. Actual: {PgPropertyRunner.Describe(items)}.", message);
        }
    }

    /// <summary>
    /// Checks that two sequences hold equal elements in the same order.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="expected">The expected elements.</param>
    /// <param name="actual">The actual elements.</param>
    /// <param name="message">An optional explanation.</param>
    /// <exception cref="PgAssertException">The sequences differ.</exception>
    public static void AreSequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        T[] left = [.. expected];
        T[] right = [.. actual];
        if (!left.SequenceEqual(right))
        {
            Fail("AreSequenceEqual", $"Expected: {PgPropertyRunner.Describe(left)}. Actual: {PgPropertyRunner.Describe(right)}.", message);
        }
    }

    /// <summary>
    /// Checks that a value is null, which is SQL NULL for values read from PostgreSQL.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="message">An optional explanation.</param>
    /// <exception cref="PgAssertException">The value is not null.</exception>
    public static void IsNull(object? value, string? message = null)
    {
        if (value is not null)
        {
            Fail("IsNull", $"Expected null. Actual: {PgPropertyRunner.Describe(value)}.", message);
        }
    }

    /// <summary>
    /// Checks that a value is not null.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="message">An optional explanation.</param>
    /// <exception cref="PgAssertException">The value is null.</exception>
    public static void IsNotNull(object? value, string? message = null)
    {
        if (value is null)
        {
            Fail("IsNotNull", "Expected a value. Actual: null.", message);
        }
    }

    /// <summary>
    /// Checks that work throws an exception of exactly the given type.
    /// </summary>
    /// <typeparam name="TException">The expected exception type.</typeparam>
    /// <param name="action">The work, which runs in its own subtransaction.</param>
    /// <param name="message">An optional explanation.</param>
    /// <returns>The exception.</returns>
    /// <remarks>
    /// Running the work in a subtransaction rolls back its changes and any PostgreSQL error, so the test continues
    /// in the same transaction.
    /// </remarks>
    /// <exception cref="PgAssertException">The work completed, or threw another exception type.</exception>
    public static TException ThrowsExactly<TException>(Action action, string? message = null) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        return ThrowsExactly<TException>(action, message, static work => PgTransaction.RunInSubtransaction(work));
    }

    /// <summary>
    /// Checks that work raises a PostgreSQL error with the given SQLSTATE, as a pgrx test's
    /// <c>#[pg_test(error = "...")]</c> expects an error.
    /// </summary>
    /// <param name="sqlState">The expected five-character SQLSTATE, such as <see cref="PgSqlStates.DivisionByZero"/>.</param>
    /// <param name="action">The work, which runs in its own subtransaction.</param>
    /// <param name="message">An optional explanation.</param>
    /// <returns>The error, for checking its message and other fields.</returns>
    /// <exception cref="PgAssertException">The work completed, raised another SQLSTATE or threw another exception.</exception>
    public static PgException ThrowsSqlState(string sqlState, Action action, string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlState);
        ArgumentNullException.ThrowIfNull(action);
        return ThrowsSqlState(sqlState, action, message, static work => PgTransaction.RunInSubtransaction(work));
    }

    /// <summary>
    /// Fails the test.
    /// </summary>
    /// <param name="message">The explanation.</param>
    /// <exception cref="PgAssertException">Always.</exception>
    public static void Fail(string message) => Fail("Fail", null, message);

    /// <summary>
    /// Checks the exception type, running the work through <paramref name="isolate"/>.
    /// </summary>
    internal static TException ThrowsExactly<TException>(Action action, string? message, Action<Action> isolate) where TException : Exception
    {
        Exception? thrown = Capture(action, isolate);
        if (thrown is null)
        {
            Fail("ThrowsExactly", $"Expected {typeof(TException).Name}, but no exception was thrown.", message);
        }

        if (thrown!.GetType() != typeof(TException))
        {
            Fail("ThrowsExactly", $"Expected {typeof(TException).Name}. Actual: {thrown.GetType().Name}: {thrown.Message}", message);
        }

        return (TException)thrown;
    }

    /// <summary>
    /// Checks the SQLSTATE, running the work through <paramref name="isolate"/>.
    /// </summary>
    internal static PgException ThrowsSqlState(string sqlState, Action action, string? message, Action<Action> isolate)
    {
        Exception? thrown = Capture(action, isolate);
        if (thrown is PgException error && string.Equals(error.SqlState, sqlState, StringComparison.Ordinal))
        {
            return error;
        }

        Fail("ThrowsSqlState", thrown switch
        {
            null => $"Expected SQLSTATE {sqlState}, but no error was raised.",
            PgException other => $"Expected SQLSTATE {sqlState}. Actual: {other.SqlState}: {other.Message}",
            _ => $"Expected SQLSTATE {sqlState}. Actual: {thrown.GetType().Name}: {thrown.Message}",
        }, message);
        return null!;
    }

    private static Exception? Capture(Action action, Action<Action> isolate)
    {
        try
        {
            isolate(action);
            return null;
        }
        catch (PgException error) when (error.SqlState.StartsWith("57", StringComparison.Ordinal))
        {
            // Class 57, operator intervention, includes query cancellation; it ends the test rather than satisfying it.
            throw;
        }
        // codeql[cs/catch-of-all-exceptions] An assertion observes whatever the code under test throws.
        catch (Exception error)
        {
            return error;
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string assertion, string? detail, string? message)
        => throw new PgAssertException(string.Join(' ', new[] { $"PgAssert.{assertion} failed.", detail, message }
            .Where(static part => !string.IsNullOrEmpty(part))));
}

/// <summary>
/// Reports a failed <see cref="PgAssert"/> check.
/// </summary>
public sealed class PgAssertException : Exception
{
    /// <summary>
    /// Creates a failure without details.
    /// </summary>
    public PgAssertException() : this("A PgAssert check failed.")
    {
    }

    /// <summary>
    /// Creates a failure with a message.
    /// </summary>
    /// <param name="message">The message.</param>
    public PgAssertException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates a failure with a message and its cause.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public PgAssertException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
