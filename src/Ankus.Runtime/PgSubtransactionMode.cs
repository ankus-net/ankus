namespace Ankus;

/// <summary>
/// Selects how statements recover inside an explicit subtransaction scope.
/// </summary>
public enum PgSubtransactionMode
{
    /// <summary>
    /// Each SQL statement and recoverable native call runs in its own internal subtransaction, so a caught
    /// <see cref="PgException"/> leaves the scope usable.
    /// </summary>
    Recoverable = 0,

    /// <summary>
    /// Statements share the scope's single subtransaction, as SPI statements do in pgrx. A PostgreSQL error cannot be
    /// recovered inside the scope: later backend calls in it rethrow that error, and the whole scope rolls back before
    /// the error reaches the caller. Use this for many writes, which then share one subtransaction ID.
    /// </summary>
    Atomic = 1,
}
