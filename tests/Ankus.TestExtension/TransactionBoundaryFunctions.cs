using System.Globalization;
using System.Runtime.CompilerServices;

namespace Ankus.TestExtension;

/// <summary>
/// Observes prepared and actual parallel-worker transaction callbacks across backend lifetime boundaries.
/// </summary>
public static class TransactionBoundaryFunctions
{
    private static readonly List<string> s_events = [];
    private static readonly List<PgTransactionCallback> s_receipts = [];
    private static PgSubtransactionCallback? s_subtransaction;
    private static WeakReference? s_parallelRoot;
    private static bool s_parallelRegistered;

    /// <summary>
    /// Registers preparation callbacks, cancellation, later-phase registration and controlled errors.
    /// </summary>
    /// <param name="mode">Zero for success, one for managed rejection, two or three for native errors, four for post-prepare failure.</param>
    [PgFunction]
    public static void PreparedCallbackRegister(int mode)
    {
        foreach (PgTransactionCallback receipt in s_receipts)
        {
            receipt.Dispose();
        }

        s_receipts.Clear();
        s_subtransaction?.Dispose();
        s_events.Clear();
        s_receipts.Add(PgTransaction.RegisterCallback(PgTransactionEvent.Commit, static () => s_events.Add("commit")));
        s_receipts.Add(PgTransaction.RegisterCallback(PgTransactionEvent.Abort, static () => s_events.Add("abort")));
        s_receipts.Add(PgTransaction.RegisterCallback(PgTransactionEvent.PrePrepare, () =>
        {
            try
            {
                s_events.Add("pre:" + Spi.Execute("INSERT INTO callback_prepared VALUES (43)"));
                s_receipts.Add(PgTransaction.RegisterCallback(PgTransactionEvent.Prepare, static () => s_events.Add("late-prepare")));
                s_receipts.Add(PgTransaction.RegisterCallback(PgTransactionEvent.PrePrepare, static () => s_events.Add("deferred-pre")));
                if (mode == 1)
                {
                    throw new PgException("P7501", "managed pre-prepare failure", "prepare detail", "prepare hint");
                }

                if (mode is 2 or 3)
                {
                    try
                    {
                        _ = Spi.Execute("SELECT 1 / 0");
                    }
                    catch (PgException) when (mode == 3)
                    {
                        s_events.Add("caught-native");
                    }
                }
            }
            finally
            {
                s_events.Add("pre-finally");
            }
        }));
        using PgTransactionCallback cancelled = PgTransaction.RegisterCallback(PgTransactionEvent.PrePrepare,
            static () => s_events.Add("cancelled-pre"));
        s_receipts.Add(cancelled);
        cancelled.Dispose();
        s_receipts.Add(PgTransaction.RegisterCallback(PgTransactionEvent.PrePrepare, static () => s_events.Add("pre-second")));
        s_receipts.Add(PgTransaction.RegisterCallback(PgTransactionEvent.Prepare, () =>
        {
            s_events.Add("prepare");
            ObserveUnavailableSql();
            if (mode == 4)
            {
                try
                {
                    throw new InvalidOperationException("managed post-prepare failure");
                }
                finally
                {
                    PgLog.Write(PgLogLevel.Warning, "managed post-prepare finally");
                }
            }
        }));
        s_subtransaction = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Abort,
            static (_, _) => s_events.Add("sub-abort"));
        TransactionCallbackFunctions.TransactionCallbackRegisterRoot();
    }

    /// <summary>
    /// Returns preparation observations and the number of retained pending receipts.
    /// </summary>
    /// <returns>The ordered event log, pending outer count and subtransaction state.</returns>
    [PgFunction]
    public static string PreparedCallbackState()
        => string.Join(',', s_events) + "|" + s_receipts.Count(static receipt => receipt.IsPending).ToString(CultureInfo.InvariantCulture) +
            "|" + (s_subtransaction?.IsPending ?? false);

    /// <summary>
    /// Registers one callback set per real parallel worker and returns the process evaluating each row.
    /// </summary>
    /// <param name="value">A scanned value used to require row evaluation.</param>
    /// <param name="identity">A unique test-owned witness name.</param>
    /// <param name="mode">Zero for success, one for a row error, two for a parallel pre-commit error.</param>
    /// <returns>The worker's operating-system process identity.</returns>
    [PgFunction(ParallelSafety = PgParallelSafety.Safe, Volatility = PgVolatility.Stable)]
    public static int ParallelCallbackValue(int value, string identity, int mode)
    {
        if (!s_parallelRegistered)
        {
            _ = Guid.ParseExact(identity, "N");
            s_parallelRegistered = true;
            s_receipts.Clear();
            s_events.Clear();
            s_events.Add("registered");
            string path = "ankus-parallel-callback-" + identity + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            s_parallelRoot = RegisterParallelRoot();
            s_receipts.Add(PgTransaction.RegisterCallback(PgTransactionEvent.ParallelPreCommit, () =>
            {
                try
                {
                    s_events.Add("ParallelPreCommit");
                    ObserveUnavailableSql();

                    if (mode == 2)
                    {
                        throw new PgException("P7503", "managed parallel pre-commit failure", "parallel detail", "parallel hint");
                    }
                }
                finally
                {
                    s_events.Add("pre-finally");
                }
            }));
            PgTransactionEvent[] terminals =
            [
                PgTransactionEvent.ParallelCommit, PgTransactionEvent.ParallelAbort, PgTransactionEvent.Commit, PgTransactionEvent.Abort,
            ];
            foreach (PgTransactionEvent terminal in terminals)
            {
                s_receipts.Add(PgTransaction.RegisterCallback(terminal, () =>
                {
                    s_events.Add(terminal.ToString());
                    ObserveUnavailableSql();
                    string report = string.Join(',', s_events) + "|" +
                        s_receipts.Count(static receipt => receipt.IsPending).ToString(CultureInfo.InvariantCulture) + "|" +
                        ParallelRootAlive();
                    File.WriteAllText(path + ".pending", report);
                    File.Move(path + ".pending", path + ".done");
                }));
            }

            File.WriteAllText(path + ".started", identity + "|" + ParallelRootAlive());
        }

        if (mode == 1 && value > 0)
        {
            try
            {
                throw new PgException("P7502", "managed parallel row failure", "parallel detail", "parallel hint");
            }
            finally
            {
                s_events.Add("body-finally");
            }
        }

        return Environment.ProcessId;
    }

    /// <summary>
    /// Keeps a managed payload alive solely through a mutually exclusive regular-commit callback.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterParallelRoot()
    {
        object payload = new();
        s_receipts.Add(PgTransaction.RegisterCallback(PgTransactionEvent.Commit, () => GC.KeepAlive(payload)));
        return new(payload);
    }

    /// <summary>
    /// Forces collection to distinguish live callback ownership from an object that has not yet been collected.
    /// </summary>
    private static bool ParallelRootAlive()
    {
        GarbageCollection.Collect();
        return (s_parallelRoot ?? throw new InvalidOperationException("Missing parallel callback root.")).IsAlive;
    }

    /// <summary>
    /// Distinguishes denied SQL capability from an unrelated managed or PostgreSQL failure.
    /// </summary>
    private static void ObserveUnavailableSql()
    {
        try
        {
            _ = Spi.Execute("SELECT 1");
            s_events.Add("unexpected-spi");
        }
        catch (InvalidOperationException error) when
            (error.Message == "PostgreSQL APIs can only be used on the active PostgreSQL backend thread.")
        {
            s_events.Add("spi-blocked");
        }
    }
}
