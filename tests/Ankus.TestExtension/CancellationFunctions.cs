namespace Ankus.TestExtension;

/// <summary>
/// Attempts to swallow native cancellation through ordinary managed exception handlers.
/// </summary>
public static class CancellationFunctions
{
    [ThreadStatic]
    private static int s_observed;

    /// <summary>
    /// Runs a cancellable server operation and deliberately catches its exception.
    /// </summary>
    /// <param name="mode">Zero for SPI, one for recursive dispatch, two for explicit recovery, three/four for one/two SPI sessions, or five/six for direct/nested managed polling.</param>
    /// <param name="replace">Whether to replace the caught error with an unrelated exception.</param>
    /// <returns>A value that cancellation must prevent from reaching SQL.</returns>
    [PgFunction]
    public static int CancelCatch(int mode, bool replace)
    {
        try
        {
            switch (mode)
            {
                case 0:
                    Spi.Execute("SELECT pg_sleep(30)");
                    break;
                case 1:
                    Spi.Execute("SELECT datatype.cancel_catch(0, false)");
                    break;
                case 2:
                    PgTransaction.RunInSubtransaction(() => CancelCatch(0, false));
                    break;
                case 3:
                    Spi.Connect(session => session.Execute("SELECT pg_sleep(30)"));
                    break;
                case 4:
                    Spi.Connect(_ => Spi.Connect(session => session.Execute("SELECT pg_sleep(30)")));
                    break;
                case 5:
                    PollUntilCanceled();
                    break;
                case 6:
                    PgTransaction.RunInSubtransaction(PollUntilCanceled);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }
        catch (PgQueryCanceledException exception)
        {
            if (exception is { Diagnostic.SqlState: PgSqlStates.QueryCanceled })
            {
                s_observed |= 1;
            }

            try
            {
                if (mode >= 5)
                {
                    PgInterrupts.Check();
                }
                else
                {
                    Spi.Execute("SELECT 1");
                }
            }
            catch (PgQueryCanceledException)
            {
                s_observed |= 2;
            }

            if (replace)
            {
                throw new InvalidOperationException("replacement must not hide cancellation");
            }
        }
        finally
        {
            s_observed |= 4;
        }

        return 42;
    }

    /// <summary>
    /// Runs managed work with no PostgreSQL calls except the explicit interrupt poll.
    /// </summary>
    private static void PollUntilCanceled()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
        {
            PgInterrupts.Check();
        }

        throw new InvalidOperationException("The managed loop did not observe PostgreSQL cancellation.");
    }

    /// <summary>
    /// Verifies an explicit rollback releases an ordinary retained failure before polling resumes.
    /// </summary>
    /// <returns>The number of successful polls after independent nested rollbacks.</returns>
    [PgFunction]
    public static int PollAfterRollback()
    {
        int completed = 0;
        for (int index = 0; index < 3; index++)
        {
            try
            {
                PgTransaction.RunInSubtransaction(() =>
                {
                    using PgMemoryContext owner = PgMemoryContext.Create("poll rollback");
                    using PgAllocation allocation = owner.Allocate(0x40000000);
                });
            }
            catch (PgException exception) when (exception.SqlState == PgSqlStates.InternalError)
            {
                PgInterrupts.Check();
                completed++;
            }
        }

        return completed;
    }

    /// <summary>
    /// Reads and clears the managed cancellation and cleanup observations in the same backend.
    /// </summary>
    /// <returns>The bit mask for typed cancellation, rejected subsequent work and managed finally execution.</returns>
    [PgFunction]
    public static int CancelObservations()
    {
        PgInterrupts.Check();
        int result = s_observed;
        s_observed = 0;
        return result;
    }
}
