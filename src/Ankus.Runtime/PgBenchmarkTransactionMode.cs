namespace Ankus;

/// <summary>
/// Selects the PostgreSQL transaction boundary applied while measuring a benchmark.
/// </summary>
public enum PgBenchmarkTransactionMode
{
    /// <summary>
    /// Runs every measured iteration in the caller's transaction.
    /// </summary>
    Shared,

    /// <summary>
    /// Runs each measured batch in its own internal subtransaction.
    /// </summary>
    SubtransactionPerBatch,

    /// <summary>
    /// Runs every measured iteration in its own internal subtransaction.
    /// </summary>
    SubtransactionPerIteration,

    /// <summary>
    /// Runs each measured batch in its own internal subtransaction and rolls it back, so a benchmark that changes data
    /// starts every batch from the same state. The rollback is part of the measured time.
    /// </summary>
    RollbackPerBatch,

    /// <summary>
    /// Runs every measured iteration in its own internal subtransaction and rolls it back, so a benchmark that changes data
    /// starts every iteration from the same state. The rollback is part of the measured time.
    /// </summary>
    RollbackPerIteration,
}
