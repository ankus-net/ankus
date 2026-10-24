using System.Diagnostics;
using System.Text;
using Ankus.Postgres;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises generated native calls and global accesses at their managed boundary, including the accesses that reuse a
/// checked binding and a cached global address.
/// </summary>
[PgSchema("raw_call_boundary")]
public static unsafe class RawCallBoundaryFunctions
{
    private const string WorkMem = "SELECT setting::integer FROM pg_settings WHERE name = 'work_mem'";
    private static string? s_failureObservations;

    /// <summary>
    /// Repeats one raw operation and returns the mean time of one repetition.
    /// </summary>
    /// <param name="kind">
    /// The operation: 0 an empty loop, 1 <c>GetCurrentTransactionNestLevel</c>, 2 a read and write of
    /// <c>CurrentMemoryContext</c>, 3 <c>palloc</c> and <c>pfree</c>, 4 a read of <c>CurrentMemoryContext</c>, or
    /// 5 a binding validation alone.
    /// </param>
    /// <param name="iterations">The number of repetitions.</param>
    /// <returns>The mean nanoseconds per repetition.</returns>
    [PgFunction]
    public static double Measure(int kind, int iterations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
        byte[] identity = Encoding.UTF8.GetBytes(NativeBinding.Identity);
        int major = Spi.ExecuteScalar<int>("SELECT current_setting('server_version_num')::integer / 10000");
        long sink = 0;
        long start = Stopwatch.GetTimestamp();
        for (int index = 0; index < iterations; index++)
        {
            switch (kind)
            {
                case 0:
                    sink += index;
                    break;
                case 1:
                    sink += NativeMethods.GetCurrentTransactionNestLevel();
                    break;
                case 2:
                {
                    MemoryContextData* current = NativeGlobals.CurrentMemoryContext;
                    NativeGlobals.CurrentMemoryContext = current;
                    break;
                }
                case 3:
                    NativeMethods.pfree(NativeMethods.palloc(16));
                    break;
                case 4:
                    sink += (nint)NativeGlobals.CurrentMemoryContext;
                    break;
                case 5:
                    NativeRawCall.ValidateBinding(identity, major);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        double elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
        GC.KeepAlive(sink);
        return elapsed / iterations;
    }

    /// <summary>
    /// Reads and writes pointer and integer globals in place and compares them with their original addresses and with
    /// the setting PostgreSQL reports.
    /// </summary>
    /// <returns>Whether each read, write and restore saw the native storage.</returns>
    [PgFunction]
    public static string GlobalsReadAndWriteInPlace()
    {
        MemoryContextData* current = NativeGlobals.CurrentMemoryContext;
        bool read = current == *NativeGlobals.DangerousAddressOf_CurrentMemoryContext();
        MemoryContextData* top = NativeGlobals.TopMemoryContext;
        bool written;
        NativeGlobals.CurrentMemoryContext = top;
        try
        {
            written = *NativeGlobals.DangerousAddressOf_CurrentMemoryContext() == top && NativeGlobals.CurrentMemoryContext == top;
        }
        finally
        {
            NativeGlobals.CurrentMemoryContext = current;
        }

        bool restored = *NativeGlobals.DangerousAddressOf_CurrentMemoryContext() == current;
        int workMem = NativeGlobals.work_mem;
        bool setting = workMem == Spi.ExecuteScalar<int>(WorkMem);
        int changed;
        NativeGlobals.work_mem = workMem + 1;
        try
        {
            changed = Spi.ExecuteScalar<int>(WorkMem);
        }
        finally
        {
            NativeGlobals.work_mem = workMem;
        }

        return $"{read}|{written}|{restored}|{setting}|{changed == workMem + 1}|{NativeGlobals.work_mem == workMem}";
    }

    /// <summary>
    /// Makes generated calls and global accesses from a thread with no backend callback after the backend thread has
    /// cached the binding and the global's address.
    /// </summary>
    /// <returns>The rejection of each access, and whether the backend thread still reads the same values afterward.</returns>
    [PgFunction]
    public static string WorkerThreadsAreRejected()
    {
        int level = NativeMethods.GetCurrentTransactionNestLevel();
        int workMem = NativeGlobals.work_mem;
        string[] rejections = Task.Factory.StartNew(static () => new[]
            {
                Attempt(static () => _ = NativeMethods.GetCurrentTransactionNestLevel()),
                Attempt(static () => _ = NativeGlobals.work_mem),
                Attempt(static () => NativeGlobals.work_mem = 1),
                Attempt(static () => _ = NativeGlobals.DangerousAddressOf_work_mem()),
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
        bool unchanged = level == NativeMethods.GetCurrentTransactionNestLevel() && workMem == NativeGlobals.work_mem &&
            workMem == Spi.ExecuteScalar<int>(WorkMem);
        return string.Join("|", rejections) + "|" + unchanged;
    }

    /// <summary>
    /// Raises an ERROR from a generated call inside a subtransaction, then keeps using generated calls and globals.
    /// </summary>
    /// <returns>The ERROR, and whether later calls and global accesses work at the same transaction depth.</returns>
    [PgFunction]
    public static string RaisingCallsRecover()
    {
        int level = NativeMethods.GetCurrentTransactionNestLevel();
        int workMem = NativeGlobals.work_mem;
        PgException? failure = null;
        try
        {
            PgTransaction.RunInSubtransaction(static () => _ = Parse("x"u8));
        }
        catch (PgException exception)
        {
            failure = exception;
        }

        if (failure is null)
        {
            throw new InvalidOperationException("The deliberate native error did not occur.");
        }

        bool recovered = level == NativeMethods.GetCurrentTransactionNestLevel() && workMem == NativeGlobals.work_mem &&
            Parse("42"u8) == 42;
        return $"{failure.SqlState}|{failure.Message}|{recovered}";
    }

    /// <summary>
    /// Catches an ERROR raised by a generated call without a subtransaction, then tries a generated call and global
    /// accesses whose binding and address are already cached. PostgreSQL still fails the statement afterward.
    /// </summary>
    /// <returns>Zero, which PostgreSQL never receives because the caught ERROR fails the statement.</returns>
    [PgFunction]
    public static int UnrecoveredFailureBlocksAccess()
    {
        _ = NativeMethods.GetCurrentTransactionNestLevel();
        _ = NativeGlobals.work_mem;
        string failure;
        try
        {
            _ = Parse("x"u8);
            failure = "accepted";
        }
        catch (PgException exception)
        {
            failure = exception.SqlState;
        }

        s_failureObservations = string.Join("|", failure,
            Observe(static () => _ = NativeMethods.GetCurrentTransactionNestLevel()),
            Observe(static () => _ = NativeGlobals.work_mem),
            Observe(static () => NativeGlobals.work_mem = 1),
            Observe(static () => _ = NativeGlobals.DangerousAddressOf_work_mem()));
        return 0;
    }

    /// <summary>
    /// Returns and clears what <see cref="UnrecoveredFailureBlocksAccess"/> observed.
    /// </summary>
    /// <returns>The caught ERROR's SQLSTATE and the SQLSTATE each later access failed with.</returns>
    [PgFunction]
    public static string? FailureObservations()
    {
        string? observations = s_failureObservations;
        s_failureObservations = null;
        return observations;
    }

    private static int Parse(ReadOnlySpan<byte> text)
    {
        Span<byte> terminated = stackalloc byte[text.Length + 1];
        text.CopyTo(terminated);
        terminated[^1] = 0;
        fixed (byte* value = terminated)
        {
            return NativeMethods.pg_strtoint32((sbyte*)value);
        }
    }

    private static string Attempt(Action access)
    {
        try
        {
            access();
            return "accepted";
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
    }

    private static string Observe(Action access)
    {
        try
        {
            access();
            return "accepted";
        }
        catch (PgException exception)
        {
            return exception.SqlState;
        }
    }
}
