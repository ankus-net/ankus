using System.Runtime.CompilerServices;
using Ankus.Postgres;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises call-site state with PostgreSQL owners, native values, cursors, and iterators.
/// </summary>
public static class FunctionStateFunctions
{
    private static int s_created;
    private static int s_disposed;
    private static int s_iteratorCleanup;
    private static PgFunctionContext? s_saved;
    private static readonly List<WeakReference<Counter>> s_values = [];

    /// <summary>
    /// Increments one call site's counter and retains its first native text across row resets.
    /// </summary>
    /// <param name="call">The native call site.</param>
    /// <param name="text">The current row's text.</param>
    /// <param name="failDispose">Whether cleanup should raise an owned error.</param>
    /// <returns>The counter and first input text.</returns>
    [PgFunction]
    public static string StateTick(PgFunctionContext call, string? text, bool failDispose)
    {
        if (call.Arguments[0].Read<string?>() != text)
        {
            throw new InvalidOperationException("Call-site input differs from its managed value.");
        }

        Counter state = call.GetOrCreateState(() => CreateCounter(call, failDispose));
        s_saved = call;
        GarbageCollection.Collect();
        return $"{state.Next()}|{state.Text.Read<string?>() ?? "NULL"}";
    }

    /// <summary>
    /// Counts calls through one call site's state.
    /// </summary>
    /// <param name="call">The native call site.</param>
    /// <returns>The number of calls made through this site.</returns>
    [PgFunction]
    public static int StateSiteCount(PgFunctionContext call) => ++call.GetOrCreateState(static () => new StrongBox<int>()).Value;

    /// <summary>
    /// Calls the counter twice through PostgreSQL's OidFunctionCall0Coll, which builds each FmgrInfo on its own stack frame.
    /// </summary>
    /// <returns>Each call's count; distinct FmgrInfo values have distinct call-site state.</returns>
    [PgFunction]
    public static string StateFreshFunctionInfo()
    {
        uint function = Spi.ExecuteScalar<uint>("SELECT 'datatype.state_site_count()'::pg_catalog.regprocedure::pg_catalog.oid");
        unsafe
        {
            ulong first = NativeMethods.OidFunctionCall0Coll(function, 0);
            ulong second = NativeMethods.OidFunctionCall0Coll(function, 0);
            return FormattableString.Invariant($"{(int)first}|{(int)second}");
        }
    }

    /// <summary>
    /// Reports exact creation, disposal, iterator cleanup, and remaining managed roots.
    /// </summary>
    /// <returns>The cumulative counters for this backend.</returns>
    [PgFunction]
    public static string StateCounts()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        return $"{s_created}|{s_disposed}|{s_iteratorCleanup}|{s_values.Count(IsAlive)}";
    }

    /// <summary>
    /// Verifies a saved call cannot revive its state or owner after the query ends.
    /// </summary>
    /// <returns>The number of rejected operations.</returns>
    [PgFunction]
    public static int StateExpired()
    {
        PgFunctionContext call = s_saved ?? throw new InvalidOperationException("Missing saved call.");
        int rejected = 0;
        try
        {
            call.GetOrCreateState<int>(static () => throw new InvalidOperationException("Expired factory ran."));
        }
        catch (ObjectDisposedException)
        {
            rejected++;
        }

        try
        {
            _ = call.StateMemoryContext;
        }
        catch (ObjectDisposedException)
        {
            rejected++;
        }

        return rejected;
    }

    /// <summary>
    /// Exercises failed factories, recursion, typed values, and thread guards at one real call site.
    /// </summary>
    /// <param name="call">The native call site.</param>
    /// <param name="mode">The initialization case.</param>
    /// <returns>The observations made before query cleanup.</returns>
    [PgFunction]
    public static string StateContracts(PgFunctionContext call, int mode)
    {
        if (mode == 0)
        {
            string? first = call.GetOrCreateState<string?>(static () => null);
            string? next = call.GetOrCreateState<string?>(static () => throw new InvalidOperationException("Null factory repeated."));
            return $"{first is null}|{next is null}";
        }

        if (mode == 1)
        {
            return $"{call.GetOrCreateState(static () => 0)}|{call.GetOrCreateState(static () => 42)}";
        }

        if (mode == 6)
        {
            return call.GetOrCreateState(static () => Spi.ExecuteScalar<string>("SELECT datatype.state_tick('nested', false)"));
        }

        string observed;
        try
        {
            switch (mode)
            {
                case 2:
                    call.GetOrCreateState(static () => Spi.ExecuteScalar<int>("SELECT 1 / 0"));
                    break;
                case 3:
                    call.GetOrCreateState(() => call.GetOrCreateState(static () => 17));
                    break;
                case 4:
                    call.GetOrCreateState(static () => 42);
                    call.GetOrCreateState(static () => "wrong type");
                    break;
                case 5:
                    return Task.Run(() =>
                    {
                        try
                        {
                            call.GetOrCreateState<int>(static () => throw new PgException("P7807", "Off-thread factory ran."));
                            return "accepted";
                        }
                        catch (InvalidOperationException)
                        {
                            return "rejected";
                        }
                    }).GetAwaiter().GetResult();
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }

            throw new InvalidOperationException("Expected state failure did not occur.");
        }
        catch (PgException error) when (mode == 2 && error.SqlState == "22012")
        {
            observed = error.SqlState;
        }
        catch (InvalidOperationException) when (mode == 3)
        {
            observed = "recursive";
        }
        catch (InvalidCastException) when (mode == 4)
        {
            observed = "type";
        }

        return $"{observed}|{call.GetOrCreateState(static () => 42)}|{Spi.ExecuteScalar<int>("SELECT 43")}";
    }

    /// <summary>
    /// Streams a counter without occupying PostgreSQL's iterator state slot.
    /// </summary>
    /// <param name="call">The injected call site.</param>
    /// <param name="count">The number of rows requested.</param>
    /// <param name="fail">Whether to fail on the second row.</param>
    /// <returns>The call site's cumulative counter.</returns>
    [PgFunction(SetMode = PgSetMode.ValuePerCall)]
    public static IEnumerable<int> StateRows(PgFunctionContext call, int count, bool fail)
        => Rows(call, count, fail);

    /// <summary>
    /// Materializes rows while retaining state in the function's parent cache context.
    /// </summary>
    /// <param name="call">The injected call site.</param>
    /// <param name="count">The number of rows requested.</param>
    /// <param name="fail">Whether to fail on the second row.</param>
    /// <returns>The call site's cumulative counter.</returns>
    [PgFunction(SetMode = PgSetMode.Materialize)]
    public static IEnumerable<int> StateMaterialized(PgFunctionContext call, int count, bool fail)
        => Rows(call, count, fail);

    /// <summary>
    /// Creates state and observes iterator cleanup while its parent owner is still live.
    /// </summary>
    /// <param name="call">The captured call site.</param>
    /// <param name="count">The requested row count.</param>
    /// <param name="fail">Whether to raise a managed error.</param>
    /// <returns>One counter increment per yielded row.</returns>
    private static IEnumerable<int> Rows(PgFunctionContext call, int count, bool fail)
    {
        Counter state = call.GetOrCreateState(() => CreateCounter(call, false));
        try
        {
            for (int index = 0; index < count; index++)
            {
                if (index == 1 && fail)
                {
                    throw new PgException("P7808", "state iterator failed");
                }

                yield return state.Next();
            }
        }
        finally
        {
            Counter cached = call.GetOrCreateState(() =>
            {
                s_iteratorCleanup = -100;
                return state;
            });

            if (ReferenceEquals(state, cached))
            {
                _ = state.Value.Read<int>();
                s_iteratorCleanup++;
            }
        }
    }

    /// <summary>
    /// Allocates state payloads directly in the cache owner and records a weak reference for root checks.
    /// </summary>
    /// <param name="call">The captured call site.</param>
    /// <param name="failDispose">Whether disposal raises an error.</param>
    /// <returns>The new owned counter.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Counter CreateCounter(PgFunctionContext call, bool failDispose)
    {
        PgAllocation value = call.StateMemoryContext.Allocate<int>(1);
        value.Write(0);
        var result = new Counter(call, value, call.Arguments[0].CopyTo(call.StateMemoryContext), failDispose);
        s_created++;
        s_values.Add(new WeakReference<Counter>(result));
        return result;
    }

    /// <summary>
    /// Checks root lifetime without keeping a target alive in the collection caller.
    /// </summary>
    /// <param name="reference">The weak reference being checked.</param>
    /// <returns>Whether its target is still alive.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<Counter> reference) => reference.TryGetTarget(out _);

    /// <summary>
    /// Owns native data and verifies cleanup reentry cannot publish another state.
    /// </summary>
    /// <param name="call">The state lookup used by the reentry probe.</param>
    /// <param name="value">The native counter.</param>
    /// <param name="text">The independently copied first argument.</param>
    /// <param name="failDispose">Whether to raise a cleanup diagnostic.</param>
    private sealed class Counter(PgFunctionContext call, PgAllocation value, PgDatum text, bool failDispose) : IDisposable
    {
        /// <summary>
        /// Gets the directly owned native counter.
        /// </summary>
        public PgAllocation Value { get; } = value;

        /// <summary>
        /// Gets the first row's copied native argument.
        /// </summary>
        public PgDatum Text { get; } = text;

        /// <summary>
        /// Increments the counter after checking its owner's lifetime.
        /// </summary>
        /// <returns>The incremented counter.</returns>
        public int Next()
        {
            int next = checked(Value.Read<int>() + 1);
            Value.Write(next);
            return next;
        }

        /// <summary>
        /// Reads live payloads, disposes individual storage, and optionally raises an owned error.
        /// </summary>
        public void Dispose()
        {
            _ = Value.Read<int>();
            _ = Text.DangerousGetBits();
            Value.Dispose();
            s_disposed++;
            try
            {
                call.GetOrCreateState<int>(static () => throw new PgException("P7809", "Cleanup factory ran."));
                throw new PgException("P7809", "Cleanup accepted state access.");
            }
            catch (InvalidOperationException)
            {
                // Cleanup can neither create nor read function state.
            }

            if (failDispose)
            {
                throw new PgException("P7810", "state disposal failed", "owned detail", "owned hint");
            }
        }
    }
}
