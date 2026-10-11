namespace Ankus;

/// <summary>
/// Cooperates with PostgreSQL cancellation and shutdown during managed work.
/// </summary>
public static class PgInterrupts
{
    /// <summary>
    /// Processes pending PostgreSQL interrupts on the active backend thread.
    /// </summary>
    /// <remarks>
    /// Call periodically in long loops that do not otherwise enter PostgreSQL.
    /// An idle check reads callback-scoped native flags without a native call.
    /// PostgreSQL interrupt holdoffs still apply. Within a SQL callback, catching
    /// cancellation does not clear it: subsequent checks and callback completion retain the error.
    /// Ordinary shared and exclusive lightweight-lock guards defer pending
    /// interrupts until release. Checks inside scoped shared-memory Read or
    /// Mutate callbacks, or while holding a spinlock, are rejected to preserve
    /// the borrowed reference and short critical-section contracts.
    /// A worker transaction retains cancellation until rollback; its caller can then handle the exception
    /// and continue through <see cref="PgBackgroundWorker.RunTransaction(Action)"/>.
    /// An idle worker outside a transaction can handle cancellation from this check and continue;
    /// the completed check owns no transaction resources requiring rollback. Other failures remain unrecovered.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No backend callback is active, or a spinlock or scoped shared-memory borrow forbids backend access.</exception>
    /// <exception cref="PgQueryCanceledException">PostgreSQL has canceled the current query.</exception>
    public static void Check() => NativeMemoryContext.CheckInterrupts();

    /// <summary>
    /// Gets whether PostgreSQL has a pending interrupt, such as a query cancellation or a shutdown request, without
    /// processing it. Call <see cref="Check"/> to process it. Reading the flag does not enter PostgreSQL, so it is
    /// allowed while a spinlock or scoped shared-memory borrow is held.
    /// </summary>
    /// <exception cref="InvalidOperationException">No backend callback is active.</exception>
    public static bool IsPending => NativeMemoryContext.IsInterruptPending();
}
