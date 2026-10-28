using System;

namespace Ankus;

/// <summary>
/// Selects how benchmark iterations are grouped into batches.
/// </summary>
public readonly struct PgBenchmarkBatchSize : IEquatable<PgBenchmarkBatchSize>
{
    private readonly BatchKind _kind;
    private readonly long _value;

    private PgBenchmarkBatchSize(BatchKind kind, long value = 0)
    {
        _kind = kind;
        _value = value;
    }

    /// <summary>
    /// Gets a strategy that groups the measured iterations into about ten batches.
    /// </summary>
    public static PgBenchmarkBatchSize SmallInput { get; } = new(BatchKind.SmallInput);

    /// <summary>
    /// Gets a strategy that groups the measured iterations into about one thousand batches.
    /// </summary>
    public static PgBenchmarkBatchSize LargeInput { get; } = new(BatchKind.LargeInput);

    /// <summary>
    /// Gets a strategy that prepares one input for every measured iteration.
    /// </summary>
    public static PgBenchmarkBatchSize PerIteration { get; } = new(BatchKind.PerIteration);

    /// <summary>
    /// Creates a strategy that divides the measured iterations among a fixed number of batches.
    /// </summary>
    /// <param name="count">The positive number of batches.</param>
    /// <returns>The requested batch strategy.</returns>
    public static PgBenchmarkBatchSize NumBatches(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return new(BatchKind.NumBatches, count);
    }

    /// <summary>
    /// Creates a strategy with a fixed number of iterations per batch.
    /// </summary>
    /// <param name="count">The positive number of iterations.</param>
    /// <returns>The requested batch strategy.</returns>
    public static PgBenchmarkBatchSize NumIterations(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return new(BatchKind.NumIterations, count);
    }

    /// <summary>
    /// Compares the exact strategy and configured value.
    /// </summary>
    /// <param name="other">The other strategy.</param>
    /// <returns>Whether both values describe the same batching strategy.</returns>
    public bool Equals(PgBenchmarkBatchSize other) => _kind == other._kind && _value == other._value;

    /// <summary>
    /// Compares an object with this strategy.
    /// </summary>
    /// <param name="obj">The other value.</param>
    /// <returns>Whether the object is an equal batch strategy.</returns>
    public override bool Equals(object? obj) => obj is PgBenchmarkBatchSize other && Equals(other);

    /// <summary>
    /// Gets a hash for the exact strategy and configured value.
    /// </summary>
    /// <returns>The value hash.</returns>
    public override int GetHashCode() => HashCode.Combine(_kind, _value);

    /// <summary>
    /// Compares two batch strategies.
    /// </summary>
    public static bool operator ==(PgBenchmarkBatchSize left, PgBenchmarkBatchSize right) => left.Equals(right);

    /// <summary>
    /// Compares two batch strategies for inequality.
    /// </summary>
    public static bool operator !=(PgBenchmarkBatchSize left, PgBenchmarkBatchSize right) => !left.Equals(right);

    /// <summary>
    /// Resolves the number of iterations in one batch.
    /// </summary>
    internal long GetIterationsPerBatch(long iterations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
        return _kind switch
        {
            BatchKind.SmallInput => DivideRoundUp(iterations, 10),
            BatchKind.LargeInput => DivideRoundUp(iterations, 1_000),
            BatchKind.PerIteration => 1,
            BatchKind.NumBatches => DivideRoundUp(iterations, _value),
            BatchKind.NumIterations => _value,
            _ => throw new InvalidOperationException("The benchmark batch strategy is invalid."),
        };
    }

    private static long DivideRoundUp(long value, long divisor)
        => value / divisor + (value % divisor == 0 ? 0 : 1);

    private enum BatchKind
    {
        SmallInput,
        LargeInput,
        PerIteration,
        NumBatches,
        NumIterations,
    }
}
