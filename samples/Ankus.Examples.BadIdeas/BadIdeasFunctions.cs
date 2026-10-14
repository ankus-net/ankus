// Containment chosen by the extension author: only superusers, or roles explicitly granted EXECUTE, may write files.
[assembly: PgSql("write_file_privileges", "REVOKE EXECUTE ON FUNCTION write_file(text, bytea) FROM PUBLIC;",
    Requires = ["write_file"])]

namespace Ankus.Examples.BadIdeas;

/// <summary>
/// Ports pgrx's <c>bad_ideas</c> example. Each function does something an extension should not do; the sample's
/// README describes how Ankus rejects it, converts it to a PostgreSQL error, or documents it.
/// </summary>
public static class BadIdeasFunctions
{
    /// <summary>
    /// Tries to discard a PANIC report, which PostgreSQL still raises after managed frames unwind.
    /// </summary>
    /// <param name="s">The report message.</param>
    /// <returns>Never returns to SQL: PostgreSQL restarts every session.</returns>
    [PgFunction]
    public static bool Panic(string s)
    {
        try
        {
            PgLog.Panic(s);
        }
        // codeql[cs/catch-of-all-exceptions] The sample shows that catching every exception still cannot discard the report.
        catch (Exception)
        {
            // Like catch_unwind around PANIC!, catching the exception does not discard the report.
        }

        return true;
    }

    /// <summary>
    /// Tries to discard a FATAL report, which PostgreSQL still raises after managed frames unwind.
    /// </summary>
    /// <param name="s">The report message.</param>
    /// <returns>Never returns to SQL: PostgreSQL ends this session.</returns>
    [PgFunction]
    public static bool Fatal(string s)
    {
        try
        {
            PgLog.Fatal(s);
        }
        // codeql[cs/catch-of-all-exceptions] The sample shows that catching every exception still cannot discard the report.
        catch (Exception)
        {
            // Like catch_unwind around FATAL!, catching the exception does not discard the report.
        }

        return true;
    }

    /// <summary>
    /// Swallows an ERROR raised by managed code, as pgrx's <c>catch_unwind</c> swallows <c>error!</c>.
    /// </summary>
    /// <param name="s">The discarded error message.</param>
    /// <returns><see langword="true"/>, because the error never reaches PostgreSQL.</returns>
    [PgFunction]
    public static bool Error(string s)
    {
        try
        {
            PgLog.Error(s);
        }
        catch (PgException)
        {
            // A managed PgException is an ordinary exception; nothing in PostgreSQL needs to be rolled back.
        }

        return true;
    }

    /// <summary>
    /// Reports a warning and continues.
    /// </summary>
    /// <param name="s">The warning message.</param>
    /// <returns><see langword="true"/>.</returns>
    [PgFunction]
    public static bool Warning(string s)
    {
        PgLog.Warning(s);
        return true;
    }

    /// <summary>
    /// Writes arbitrary bytes to a server file with the PostgreSQL server's operating-system permissions.
    /// </summary>
    /// <param name="filename">The file to create or replace.</param>
    /// <param name="bytes">The file contents.</param>
    /// <returns>The number of bytes written.</returns>
    [PgFunction(Id = "write_file")]
    public static long WriteFile(string filename, byte[] bytes)
    {
        File.WriteAllBytes(filename, bytes);
        return bytes.LongLength;
    }

    /// <summary>
    /// Loops until PostgreSQL cancels the statement or terminates the session.
    /// </summary>
    [PgFunction]
    public static void LoopForever()
    {
        while (true)
        {
            PgInterrupts.Check();
        }
    }

    /// <summary>
    /// Registers a pre-commit callback that aborts about half of the transactions that call this function.
    /// </summary>
    [PgFunction]
    public static void RandomAbort()
        => _ = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, static () =>
        {
            PgLog.Info("in xact callback pre-commit");
            if (Random.Shared.Next(2) == 0)
            {
                throw new InvalidOperationException("aborting transaction");
            }
        });

    /// <summary>
    /// Throws where pgrx's unguarded function would unwind into PostgreSQL. Ankus has no unguarded entry points.
    /// </summary>
    [PgFunction]
    public static void CrashPostgres() => throw new InvalidOperationException("oh no!");

    /// <summary>
    /// Throws on a thread the extension started. No boundary can contain this: .NET ends the process.
    /// </summary>
    [PgFunction]
    public static void CrashPostgresFromThread()
    {
        var thread = new Thread(static () => throw new InvalidOperationException("oh no, from a thread!"));
        thread.Start();
        thread.Join();
    }

    /// <summary>
    /// Throws inside a task; waiting for it rethrows the exception on the backend thread.
    /// </summary>
    [PgFunction]
    public static void TaskPanic()
        => Task.Run(static () => throw new InvalidOperationException("oh no, from a task!")).GetAwaiter().GetResult();

    /// <summary>
    /// Shows that disposal runs while an exception unwinds, before PostgreSQL reports the error.
    /// </summary>
    [PgFunction]
    public static void DropStruct()
    {
        PgLog.Info("before foo drop");
        {
            using var foo = new Foo();
            using PgRelation relation = PgRelation.TryOpen("table_does_not_exist")
                ?? throw new InvalidOperationException("unable to open table: no such relation");
        }

        PgLog.Info("after foo drop");
    }

    /// <summary>
    /// The counterpart of pgrx's <c>Foo</c>, whose <c>Drop</c> implementation reports that it ran.
    /// </summary>
    private sealed class Foo : IDisposable
    {
        /// <summary>
        /// Reports disposal.
        /// </summary>
        public void Dispose() => PgLog.Info("Foo was dropped");
    }
}
