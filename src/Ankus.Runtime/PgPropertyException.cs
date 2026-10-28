using System;

namespace Ankus;

/// <summary>
/// Reports a property that failed, with the smallest failing input found and the seed that reproduces it.
/// </summary>
public sealed class PgPropertyException : Exception
{
    /// <summary>
    /// Creates a property failure without details.
    /// </summary>
    public PgPropertyException() : this("A property test failed.")
    {
    }

    /// <summary>
    /// Creates a property failure with a message.
    /// </summary>
    /// <param name="message">The message.</param>
    public PgPropertyException(string message) : base(message)
    {
        Input = string.Empty;
    }

    /// <summary>
    /// Creates a property failure with a message and its cause.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The failure of the smallest input.</param>
    public PgPropertyException(string message, Exception innerException) : base(message, innerException)
    {
        Input = string.Empty;
    }

    /// <summary>
    /// Creates a property failure from a run's result.
    /// </summary>
    internal PgPropertyException(string message, Exception? innerException, ulong seed, string input, int passedCases, int shrinkRuns)
        : base(message, innerException)
    {
        Seed = seed;
        Input = input;
        PassedCases = passedCases;
        ShrinkRuns = shrinkRuns;
    }

    /// <summary>
    /// Gets the seed that reproduces the run through <see cref="PgPropertyOptions.Seed"/>.
    /// </summary>
    public ulong Seed { get; }

    /// <summary>
    /// Gets the smallest failing input found, formatted for display, or empty when no input failed.
    /// </summary>
    public string Input { get; }

    /// <summary>
    /// Gets the number of cases that passed before the first failure.
    /// </summary>
    public int PassedCases { get; }

    /// <summary>
    /// Gets the number of test runs spent shrinking the failing input.
    /// </summary>
    public int ShrinkRuns { get; }
}
