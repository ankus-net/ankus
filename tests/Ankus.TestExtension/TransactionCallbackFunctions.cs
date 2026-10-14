using System.Runtime.CompilerServices;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises managed transaction and subtransaction callbacks through PostgreSQL's native hook machinery.
/// </summary>
public static class TransactionCallbackFunctions
{
    private static readonly List<string> s_events = [];
    private static PgTransactionCallback? s_preCommitFirst;
    private static PgTransactionCallback? s_preCommitSecond;
    private static PgTransactionCallback? s_commit;
    private static PgTransactionCallback? s_abort;
    private static PgTransactionCallback? s_deferred;
    private static PgTransactionCallback? s_saved;
    private static PgSubtransactionCallback? s_subStart;
    private static PgSubtransactionCallback? s_subPreCommit;
    private static PgSubtransactionCallback? s_subCommit;
    private static PgSubtransactionCallback? s_subAbort;
    private static WeakReference? s_root;
    private static string? s_caughtAbortError;

    /// <summary>
    /// Registers a savepoint commit or abort callback that throws after the savepoint completes.
    /// </summary>
    /// <param name="abort">Whether the abort callback throws instead of the commit callback.</param>
    [PgFunction]
    public static void TransactionCallbackRegisterSavepointFailure(bool abort)
        => s_subCommit = PgTransaction.RegisterSubtransactionCallback(abort ? PgSubtransactionEvent.Abort : PgSubtransactionEvent.Commit,
            static (_, _) => throw new PgException("P7840", "savepoint completion callback failure"));

    /// <summary>
    /// Registers a savepoint abort callback that catches a backend error of its own, as cleanup code may: a warning
    /// that the client's encoding cannot represent.
    /// </summary>
    [PgFunction]
    public static void TransactionCallbackRegisterCaughtAbortError()
    {
        s_caughtAbortError = null;
        s_subAbort = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Abort, static (_, _) =>
        {
            try
            {
                PgLog.Write(PgLogLevel.Warning, "savepoint cleanup costs 5 \u20AC");
                s_caughtAbortError = "logged";
            }
            catch (PgException error)
            {
                s_caughtAbortError = error.SqlState;
            }
        });
    }

    /// <summary>
    /// Returns the SQLSTATE that the savepoint abort callback caught.
    /// </summary>
    /// <returns>The caught SQLSTATE, or null when the callback has not run.</returns>
    [PgFunction]
    public static string? TransactionCallbackCaughtAbortError() => s_caughtAbortError;

    /// <summary>
    /// Registers a pre-commit callback that runs SQL after PostgreSQL has fired the transaction's deferred triggers.
    /// </summary>
    /// <param name="sql">The command to run before commit.</param>
    [PgFunction]
    public static void TransactionCallbackRegisterPreCommitSql(string sql)
        => s_preCommitFirst = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, () => _ = Spi.Execute(sql));

    /// <summary>
    /// Registers a commit callback whose server-only report needs encoding conversion after transaction state ends.
    /// </summary>
    [PgFunction]
    public static void TransactionCallbackRegisterEncodedReport()
        => _ = PgTransaction.RegisterCallback(PgTransactionEvent.Commit,
            static () => PgLog.Write(PgLogLevel.ServerOnly, "commit report café"));

    /// <summary>
    /// Clears observations after cancelling any callback still pending in the current transaction.
    /// </summary>
    [PgFunction]
    public static void TransactionCallbackReset()
    {
        Cancel(s_preCommitFirst);
        Cancel(s_preCommitSecond);
        Cancel(s_commit);
        Cancel(s_abort);
        Cancel(s_deferred);
        Cancel(s_saved);
        Cancel(s_subStart);
        Cancel(s_subPreCommit);
        Cancel(s_subCommit);
        Cancel(s_subAbort);
        s_preCommitFirst = null;
        s_preCommitSecond = null;
        s_commit = null;
        s_abort = null;
        s_deferred = null;
        s_saved = null;
        s_subStart = null;
        s_subPreCommit = null;
        s_subCommit = null;
        s_subAbort = null;
        s_events.Clear();
        s_root = null;
    }

    /// <summary>
    /// Registers ordered outer-transaction callbacks with optional cancellation, failure, and nested registration.
    /// </summary>
    /// <param name="cancelSecond">Whether to cancel the second pre-commit callback.</param>
    /// <param name="failPreCommit">Whether the first pre-commit callback raises an ordinary PostgreSQL error.</param>
    /// <param name="failCommit">Whether the post-commit callback fails and requires PostgreSQL recovery.</param>
    /// <param name="registerDuringPreCommit">Whether pre-commit registers commit and pre-commit callbacks.</param>
    /// <returns>Whether every initial callback is pending after registration.</returns>
    [PgFunction]
    public static bool TransactionCallbackRegisterOuter(
        bool cancelSecond,
        bool failPreCommit,
        bool failCommit,
        bool registerDuringPreCommit)
    {
        s_preCommitFirst = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, () =>
        {
            s_events.Add("pre1:" + Spi.Execute("SELECT 1"));
            PgLog.Write(PgLogLevel.Notice, "Ankus pre-commit callback");
            if (registerDuringPreCommit)
            {
                s_deferred = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit,
                    static () => s_events.Add("deferred-pre"));
                _ = PgTransaction.RegisterCallback(PgTransactionEvent.Commit,
                    static () => s_events.Add("late-commit"));
            }

            if (failPreCommit)
            {
                throw new PgException("P0001", "managed pre-commit failure", "callback detail", "callback hint");
            }
        });
        s_preCommitSecond = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit,
            static () => s_events.Add("pre2"));
        s_commit = PgTransaction.RegisterCallback(PgTransactionEvent.Commit, () =>
        {
            s_events.Add("commit");
            if (failCommit)
            {
                try
                {
                    throw new InvalidOperationException("managed post-commit failure");
                }
                finally
                {
                    PgLog.Write(PgLogLevel.Warning, "managed post-commit finally");
                }
            }
        });
        s_abort = PgTransaction.RegisterCallback(PgTransactionEvent.Abort,
            static () => s_events.Add("abort"));
        if (cancelSecond)
        {
            Cancel(s_preCommitSecond);
        }

        return s_preCommitFirst.IsPending && s_preCommitSecond.IsPending == !cancelSecond &&
            s_commit.IsPending && s_abort.IsPending;
    }

    /// <summary>
    /// Registers all four subtransaction phases and performs guarded SQL in both reversible phases.
    /// </summary>
    /// <param name="failPreCommit">Whether the subtransaction pre-commit callback rejects release.</param>
    [PgFunction]
    public static void TransactionCallbackRegisterSubtransactions(bool failPreCommit)
    {
        s_subStart = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Start, static (id, parent) =>
        {
            s_events.Add($"start:{id}:{parent}:{Spi.Execute("SELECT 1")}");
        });
        s_subPreCommit = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.PreCommit, (id, parent) =>
        {
            s_events.Add($"pre-sub:{id}:{parent}:{Spi.Execute("SELECT 1")}");
            if (failPreCommit)
            {
                throw new PgException("P0002", "managed subtransaction pre-commit failure");
            }
        });
        s_subCommit = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Commit,
            static (id, parent) => s_events.Add($"commit-sub:{id}:{parent}"));
        s_subAbort = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Abort,
            static (id, parent) => s_events.Add($"abort-sub:{id}:{parent}"));
    }

    /// <summary>
    /// Registers a subtransaction start callback that runs the given SQL, such as a call to a PL/pgSQL function whose
    /// EXCEPTION block opens a subtransaction while the savepoint is still starting.
    /// </summary>
    /// <param name="sql">The SQL the callback runs.</param>
    [PgFunction]
    public static void TransactionCallbackRegisterStartSql(string sql)
        => s_subStart = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Start, (_, _) => Spi.Execute(sql));

    /// <summary>
    /// Recovers a native numeric error through explicit rollback before performing another pure operation.
    /// </summary>
    /// <returns>The recovered SQLSTATE and subsequent result.</returns>
    [PgFunction]
    public static string TransactionCallbackRecoverPureValueError()
    {
        string failure;
        try
        {
            _ = PgTransaction.RunInSubtransaction(static () => PgNumeric.Parse("1") / default(PgNumeric));
            failure = "no error";
        }
        catch (PgException error)
        {
            failure = error.SqlState;
        }

        return failure + ":" + (PgNumeric.Parse("1") + PgNumeric.Parse("2")).Text;
    }

    /// <summary>
    /// Registers a subtransaction callback that catches a guarded SPI error to verify native rethrow after managed unwind.
    /// </summary>
    [PgFunction]
    public static void TransactionCallbackRegisterSubtransactionSpiFailure()
    {
        s_subPreCommit = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.PreCommit,
            static (_, _) =>
            {
                try
                {
                    _ = Spi.Execute("SELECT * FROM ankus_missing_callback_relation");
                }
                catch (PgException)
                {
                    s_events.Add("caught-spi-error");
                }
            });
    }

    /// <summary>
    /// Registers a callback that creates one nested PostgreSQL subtransaction through SPI.
    /// </summary>
    [PgFunction]
    public static void TransactionCallbackRegisterNestedSubtransaction()
    {
        s_subStart = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Start,
            static (id, parent) => s_events.Add($"nested-start:{id}:{parent}"));
        s_subPreCommit = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.PreCommit,
            static (id, parent) => s_events.Add($"nested-pre:{id}:{parent}"));
        s_subCommit = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Commit,
            static (id, parent) => s_events.Add($"nested-commit:{id}:{parent}"));
        s_preCommitFirst = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit,
            static () => _ = Spi.Execute("DO $$ BEGIN BEGIN PERFORM 1; EXCEPTION WHEN OTHERS THEN NULL; END; END $$"));
    }

    /// <summary>
    /// Registers a commit callback retained in static state for cancellation from a later SQL call.
    /// </summary>
    [PgFunction]
    public static void TransactionCallbackRegisterSaved()
        => s_saved = PgTransaction.RegisterCallback(PgTransactionEvent.Commit,
            static () => s_events.Add("saved-commit"));

    /// <summary>
    /// Cancels the saved callback in a later generated function call in the same transaction.
    /// </summary>
    /// <returns>Whether the callback is no longer pending.</returns>
    [PgFunction]
    public static bool TransactionCallbackCancelSaved()
    {
        s_saved?.Dispose();
        return s_saved is not null && !s_saved.IsPending;
    }

    /// <summary>
    /// Registers an unmatched abort callback that is the only strong owner of a managed payload.
    /// </summary>
    [PgFunction]
    public static void TransactionCallbackRegisterRoot() => RegisterRoot();

    /// <summary>
    /// Forces managed collection and reports whether the captured payload remains rooted.
    /// </summary>
    /// <returns>Whether the callback payload is alive.</returns>
    [PgFunction]
    public static bool TransactionCallbackRootAlive()
    {
        GarbageCollection.Collect();
        return s_root?.IsAlive == true;
    }

    /// <summary>
    /// Returns callback observations and pending states from the current backend.
    /// </summary>
    /// <returns>The ordered event log followed by all retained receipt states.</returns>
    [PgFunction]
    public static string TransactionCallbackState()
        => string.Join(',', s_events) + "|" + string.Join(',', new[]
        {
            Pending(s_preCommitFirst), Pending(s_preCommitSecond), Pending(s_commit), Pending(s_abort), Pending(s_deferred),
            Pending(s_saved), Pending(s_subStart), Pending(s_subPreCommit), Pending(s_subCommit), Pending(s_subAbort),
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterRoot()
    {
        object payload = new();
        s_root = new WeakReference(payload);
        _ = PgTransaction.RegisterCallback(PgTransactionEvent.Abort, () => GC.KeepAlive(payload));
    }

    private static string Pending(PgTransactionCallback? callback) => callback?.IsPending.ToString() ?? "null";

    private static string Pending(PgSubtransactionCallback? callback) => callback?.IsPending.ToString() ?? "null";

    private static void Cancel(IDisposable? callback) => callback?.Dispose();
}
