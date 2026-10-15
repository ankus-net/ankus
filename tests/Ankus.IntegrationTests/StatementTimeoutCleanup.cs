using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Undoes a short statement timeout that a test set to provoke a cancellation.
/// </summary>
/// <remarks>
/// The statement that removes the timeout, or rolls back past it, is itself armed with that timeout. On a busy host it
/// can be descheduled for longer and canceled, which says nothing about the code under test, and the session stays
/// usable with the timeout still in place. The cleanup is therefore tried again.
/// </remarks>
internal static class StatementTimeoutCleanup
{
    private const int Attempts = 20;

    /// <summary>
    /// Runs a cleanup statement until PostgreSQL no longer cancels it for the statement timeout.
    /// </summary>
    /// <param name="statement">Runs the cleanup statement once.</param>
    /// <returns>A task that completes once the statement has run.</returns>
    internal static async Task RetryAsync(Func<Task> statement)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await statement();
                return;
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.QueryCanceled && attempt < Attempts)
            {
                // The timeout expired before the cleanup finished; the next attempt gets a full interval again.
            }
        }
    }
}
