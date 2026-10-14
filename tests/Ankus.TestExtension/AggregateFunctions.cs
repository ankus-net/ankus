using System.Globalization;

[assembly: PgSql("aggregate-support", "CREATE SCHEMA aggregate_values;", Requires = ["sql-first"])]

namespace Ankus.TestExtension;

/// <summary>
/// Makes aggregate values, callback dispatch, native state ownership, and cleanup observable.
/// </summary>
[PgSchema("aggregate_values", Create = false)]
public static class AggregateFunctions
{
    private static readonly List<string> s_trace = [];
    private static string s_mode = "normal";
    private static int s_created;
    private static int s_disposed;
    private static int s_live;
    private static int s_duplicateDispose;
    private static int s_transition;
    private static int s_final;
    private static int s_inverse;
    private static int s_restart;
    private static int s_cleanupDenied;
    private static PgAggregateState<TrackedState>? s_retained;
    private static PgAggregateContext? s_context;

    /// <summary>
    /// Resets backend-local observations and selects a controlled callback action.
    /// </summary>
    /// <param name="mode">The action applied by the managed-state probes.</param>
    [PgFunction(Requires = ["aggregate-support"])]
    public static void AggregateReset(string mode)
    {
        s_mode = mode;
        s_created = 0;
        s_disposed = 0;
        s_live = 0;
        s_duplicateDispose = 0;
        s_transition = 0;
        s_final = 0;
        s_inverse = 0;
        s_restart = 0;
        s_cleanupDenied = 0;
        s_retained = null;
        s_context = null;
        s_trace.Clear();
    }

    /// <summary>
    /// Reports created, disposed, live, duplicate disposal, transition, final, inverse, restart, and restricted cleanup counts.
    /// </summary>
    /// <returns>The counters in documented order.</returns>
    [PgFunction(Requires = ["aggregate-support"])]
    public static int[] AggregateStatus() =>
        [s_created, s_disposed, s_live, s_duplicateDispose, s_transition, s_final, s_inverse, s_restart, s_cleanupDenied];

    /// <summary>
    /// Returns ordered support-function observations after the aggregate statement has finished.
    /// </summary>
    /// <returns>The immutable observation strings copied into an array.</returns>
    [PgFunction(Requires = ["aggregate-support"])]
    public static string[] AggregateTrace() => [.. s_trace];

    /// <summary>
    /// Distinguishes owned metadata from expired native state and comparison access.
    /// </summary>
    /// <returns>The owned context kind and the two stale-access diagnostics.</returns>
    [PgFunction(Requires = ["aggregate-support"])]
    public static string AggregateRetained()
    {
        PgAggregateContext context = s_context ?? throw new InvalidOperationException("No retained aggregate context.");
        string state;
        try
        {
            state = s_retained!.Value.Sum.ToString(CultureInfo.InvariantCulture);
        }
        catch (ObjectDisposedException)
        {
            state = "disposed";
        }

        string comparison = "unexpected comparison";
        try
        {
            context.Compare(1, 2);
        }
        catch (InvalidOperationException)
        {
            comparison = "inactive";
        }

        return context.Kind + ":" + (context.AggregateOid.HasValue ? "oid" : "no oid") + ":" + state + ":" + comparison;
    }

    /// <summary>
    /// Sums integers in a custom type declared in another schema, as pgrx's <c>demo_sum_state</c> keeps its state type
    /// in a <c>pg_schema</c> module.
    /// </summary>
    [PgAggregate(Name = "demo_sum_state", Requires = ["aggregate-support", "custom.number"])]
    public sealed class DemoSumState : IPgAggregate<CustomTypeFunctions.Number?, int>,
        IPgFinalizingAggregate<CustomTypeFunctions.Number?, ValueTuple, int>
    {
        /// <summary>
        /// Adds one value to the custom state, starting from SQL NULL.
        /// </summary>
        /// <param name="context">The aggregate call.</param>
        /// <param name="state">The running sum, or SQL NULL before the first row.</param>
        /// <param name="value">The value to add.</param>
        /// <returns>The new running sum.</returns>
        public static CustomTypeFunctions.Number? Transition(PgAggregateContext context, CustomTypeFunctions.Number? state, int value)
            => new CustomTypeFunctions.Number(checked((state is { } current ? current.Value : 0) + value));

        /// <summary>
        /// Reads the sum, or zero when no row was aggregated.
        /// </summary>
        /// <param name="context">The aggregate call.</param>
        /// <param name="state">The running sum, or SQL NULL.</param>
        /// <param name="arguments">No direct arguments.</param>
        /// <returns>The sum.</returns>
        public static int Final(PgAggregateContext context, CustomTypeFunctions.Number? state, ValueTuple arguments)
            => checked((int)(state is { } current ? current.Value : 0));
    }

    /// <summary>
    /// Adds nullable inputs to an explicit zero state.
    /// </summary>
    [PgAggregate(Name = "sum_values", InitialCondition = "0", Requires = ["aggregate-support"])]
    public sealed class SumValues : IPgAggregate<int, int?>
    {
        /// <summary>
        /// Adds a value while recording actual non-strict transition calls.
        /// </summary>
        public static int Transition(PgAggregateContext context, int state, int? value)
        {
            s_transition++;
            return checked(state + (value ?? 0));
        }
    }

    /// <summary>
    /// Keeps the first non-NULL json value through a strict, immutable, parallel-safe transition, as pgrx's FirstJson does.
    /// </summary>
    [PgAggregate(Name = "first_json", Requires = ["aggregate-support"])]
    public sealed class FirstJson : IPgAggregate<PgJson, PgJson>
    {
        /// <summary>
        /// Returns the state PostgreSQL seeded from the first non-NULL input.
        /// </summary>
        [PgFunction(Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
        public static PgJson Transition(PgAggregateContext context, PgJson state, PgJson value) => state;
    }

    /// <summary>
    /// Keeps the first non-NULL jsonb value through a strict, immutable, parallel-safe transition, as pgrx's FirstJsonB does.
    /// </summary>
    [PgAggregate(Name = "first_jsonb", Requires = ["aggregate-support"])]
    public sealed class FirstJsonb : IPgAggregate<PgJsonb, PgJsonb>
    {
        /// <summary>
        /// Returns the state PostgreSQL seeded from the first non-NULL input.
        /// </summary>
        [PgFunction(Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
        public static PgJsonb Transition(PgAggregateContext context, PgJsonb state, PgJsonb value) => state;
    }

    /// <summary>
    /// Lets PostgreSQL seed strict state from the first nonnull input without invoking the transition.
    /// </summary>
    [PgAggregate(Name = "strict_sum", Requires = ["aggregate-support"])]
    public sealed class StrictSum : IPgAggregate<int, int>
    {
        /// <summary>
        /// Adds two required values and records every actual invocation.
        /// </summary>
        public static int Transition(PgAggregateContext context, int state, int value)
        {
            s_transition++;
            return checked(state + value);
        }
    }

    /// <summary>
    /// Distinguishes nullable state recovery and a non-strict empty final result.
    /// </summary>
    [PgAggregate(Name = "nullable_sum", Requires = ["aggregate-support"])]
    public sealed class NullableSum : IPgAggregate<int?, int?>, IPgFinalizingAggregate<int?, ValueTuple, int>
    {
        /// <summary>
        /// Returns NULL on a sentinel and otherwise starts again from zero.
        /// </summary>
        public static int? Transition(PgAggregateContext context, int? state, int? value)
        {
            s_transition++;
            return value == -999 ? null : checked((state ?? 0) + (value ?? 0));
        }

        /// <summary>
        /// Makes a NULL final state observable without an exception.
        /// </summary>
        public static int Final(PgAggregateContext context, int? state, ValueTuple arguments)
        {
            s_final++;
            return state ?? 42;
        }
    }

    /// <summary>
    /// Keeps NULL state sticky after a strict transition returns NULL.
    /// </summary>
    [PgAggregate(Name = "sticky_sum", InitialCondition = "0", Requires = ["aggregate-support"])]
    public sealed class StickySum : IPgAggregate<int?, int>, IPgFinalizingAggregate<int, ValueTuple, int>
    {
        /// <summary>
        /// Produces a NULL state deliberately on the sentinel.
        /// </summary>
        [PgFunction(NullInput = PgNullInput.Strict)]
        public static int? Transition(PgAggregateContext context, int? state, int value)
        {
            s_transition++;
            return value == -999 ? null : checked(state!.Value + value);
        }

        /// <summary>
        /// Is skipped by PostgreSQL when the transition state became NULL.
        /// </summary>
        public static int Final(PgAggregateContext context, int state, ValueTuple arguments)
        {
            s_final++;
            return state;
        }
    }

    /// <summary>
    /// Counts rows through PostgreSQL's zero-argument aggregate signature.
    /// </summary>
    [PgAggregate(Name = "count_rows", InitialCondition = "0", Requires = ["aggregate-support"])]
    public sealed class CountRows : IPgAggregate<long, ValueTuple>
    {
        /// <summary>
        /// Counts a row without receiving any SQL input argument.
        /// </summary>
        public static long Transition(PgAggregateContext context, long state, ValueTuple arguments) => checked(state + 1);
    }

    /// <summary>
    /// Skips rows where either ordinary input is NULL through strict PostgreSQL dispatch.
    /// </summary>
    [PgAggregate(Name = "dot_values", InitialCondition = "0", Requires = ["aggregate-support"])]
    public sealed class DotValues : IPgAggregate<int, (int left, int right)>
    {
        /// <summary>
        /// Adds the product of two required row inputs.
        /// </summary>
        public static int Transition(PgAggregateContext context, int state, (int left, int right) arguments)
        {
            s_transition++;
            return checked(state + arguments.left * arguments.right);
        }
    }

    /// <summary>
    /// Preserves a quoted initial condition and ordered input text through a no-final aggregate.
    /// </summary>
    [PgAggregate(Name = "text_values", InitialCondition = "a'b\\café:", Requires = ["aggregate-support"])]
    public sealed class TextValues : IPgAggregate<string, string?>
    {
        /// <summary>
        /// Appends text and represents SQL NULL separately from an empty value.
        /// </summary>
        public static string Transition(PgAggregateContext context, string state, string? value) => state + (value ?? "<NULL>");
    }

    /// <summary>
    /// Treats an explicitly empty initial state as a real value.
    /// </summary>
    [PgAggregate(Name = "empty_text", InitialCondition = "", Requires = ["aggregate-support"])]
    public sealed class EmptyText : IPgAggregate<string, string>
    {
        /// <summary>
        /// Appends the next text value.
        /// </summary>
        public static string Transition(PgAggregateContext context, string state, string value) => state + value;
    }

    /// <summary>
    /// Receives PostgreSQL's variadic array value for each input row.
    /// </summary>
    [PgAggregate(Name = "variadic_sum", InitialCondition = "0", Requires = ["aggregate-support"])]
    public sealed class VariadicSum : IPgAggregate<long, int?[]?>
    {
        /// <summary>
        /// Sums all nonnull array elements while preserving a NULL array's distinct path.
        /// </summary>
        public static long Transition(PgAggregateContext context, long state, params int?[]? values)
            => values is null ? state + 1000 : checked(state + values.Sum(static value => (long)(value ?? 0)));
    }

    /// <summary>
    /// Proves the distinct regular and moving callback paths with nullable input and deterministic restart.
    /// </summary>
    [PgAggregate(Name = "moving_sum", InitialCondition = "0", MovingInitialCondition = "0", Requires = ["aggregate-support"])]
    public sealed class MovingSum : IPgAggregate<int, int?>, IPgMovingAggregate<int?, int?>
    {
        /// <summary>
        /// Records regular aggregate transitions.
        /// </summary>
        public static int Transition(PgAggregateContext context, int state, int? value)
        {
            s_trace.Add("T:" + Text(value));
            return checked(state + (value ?? 0));
        }

        /// <summary>
        /// Records moving transitions and can deliberately violate the nonnull result contract.
        /// </summary>
        public static int? MovingTransition(PgAggregateContext context, int? state, int? value)
        {
            s_context = context;
            s_trace.Add("M:" + Text(value));
            return s_mode == "moving_null" && value == 20 ? null : checked((state ?? 0) + (value ?? 0));
        }

        /// <summary>
        /// Removes the oldest row or asks PostgreSQL to recompute the frame.
        /// </summary>
        public static int? MovingInverse(PgAggregateContext context, int? state, int? value)
        {
            s_inverse++;
            s_trace.Add("I:" + Text(value));
            if (s_mode == "restart" && value == 20)
            {
                s_restart++;
                return null;
            }

            return checked((state ?? 0) - (value ?? 0));
        }
    }

    /// <summary>
    /// Owns managed state through ordinary and moving aggregate memory contexts.
    /// </summary>
    [PgAggregate(Name = "managed_sum", Requires = ["aggregate-support"])]
    public sealed class ManagedSum : IPgAggregate<PgAggregateState<TrackedState>?, int?>,
        IPgFinalizingAggregate<PgAggregateState<TrackedState>?, ValueTuple, long>,
        IPgMovingAggregate<PgAggregateState<TrackedState>?, int?>,
        IPgMovingFinalizingAggregate<PgAggregateState<TrackedState>?, ValueTuple, long>
    {
        /// <summary>
        /// Retains exact managed values and creates native ownership only on callback return.
        /// </summary>
        public static PgAggregateState<TrackedState> Transition(PgAggregateContext context, PgAggregateState<TrackedState>? state, int? value)
            => Advance(context, state, value);

        /// <summary>
        /// Returns a scalar result without consuming or disposing the state.
        /// </summary>
        public static long Final(PgAggregateContext context, PgAggregateState<TrackedState>? state, ValueTuple arguments)
            => Finish(context, state);

        /// <summary>
        /// Uses independently owned managed moving state.
        /// </summary>
        public static PgAggregateState<TrackedState> MovingTransition(PgAggregateContext context, PgAggregateState<TrackedState>? state, int? value)
            => Advance(context, state, value);

        /// <summary>
        /// Removes a row or restarts just this aggregate's memory context.
        /// </summary>
        public static PgAggregateState<TrackedState>? MovingInverse(PgAggregateContext context, PgAggregateState<TrackedState>? state, int? value)
        {
            s_inverse++;
            if (s_mode == "restart" && value == 20)
            {
                s_restart++;
                return null;
            }

            TrackedState payload = state!.Value;
            payload.Sum -= value ?? 0;
            return state;
        }

        /// <summary>
        /// Reads state without damaging later transitions in a window.
        /// </summary>
        public static long MovingFinal(PgAggregateContext context, PgAggregateState<TrackedState>? state, ValueTuple arguments)
            => Finish(context, state);
    }

    /// <summary>
    /// A disposable payload whose lifetime is controlled by PostgreSQL rather than final invocation.
    /// </summary>
    /// <param name="mode">The behavior copied when the state is allocated.</param>
    public sealed class TrackedState(string mode) : IDisposable
    {
        private readonly int _identity = Created();
        private readonly string _mode = mode;
        private bool _disposed;

        /// <summary>
        /// Gets or sets the sum accumulated in the current group or frame.
        /// </summary>
        public long Sum
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the owned query plan used by the cleanup probe.
        /// </summary>
        public SpiPreparedStatement? Plan
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the owned cursor used by the cleanup probe.
        /// </summary>
        public SpiCursor? Cursor
        {
            get;
            set;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                s_duplicateDispose++;
                return;
            }

            _disposed = true;
            s_disposed++;
            s_live--;
            if (_mode is "resources_native" or "resources_native_transition")
            {
                ProbeNativeCleanupFailure();
            }

            Cursor?.Dispose();
            Plan?.Dispose();
            if (_mode is "cleanup_native" or "cleanup_native_transition")
            {
                ProbeNativeCleanupFailure();
            }

            if (_mode is "cleanup_terminal_fatal" or "cleanup_terminal_panic")
            {
                CallbackRecoveryFunctions.ReportCaughtTerminal((int)(_mode == "cleanup_terminal_fatal" ? PgLogLevel.Fatal : PgLogLevel.Panic));
            }

            if (_mode is "cleanup_sql" or "dispose_error" or "dispose_error_transition")
            {
                try
                {
                    Spi.Execute("SELECT 42");
                    s_trace.Add("unexpected cleanup SPI");
                }
                catch (InvalidOperationException)
                {
                    s_cleanupDenied++;
                }
            }

            if (_mode is "dispose_error" or "dispose_error_transition")
            {
                throw new PgException("P7804", "aggregate state Dispose failure " + _identity);
            }
        }
    }

    private static unsafe void ProbeNativeCleanupFailure()
    {
        try
        {
            fixed (byte* name = "ankus_missing_aggregate_cleanup\0"u8)
            {
                _ = Ankus.Postgres.NativeMethods.GetCustomScanMethods((sbyte*)name, false);
            }
        }
        catch (PgException native)
        {
            s_trace.Add(native.SqlState);
        }
    }

    private static PgAggregateState<TrackedState> Advance(PgAggregateContext context, PgAggregateState<TrackedState>? state, int? value)
    {
        s_context = context;
        s_transition++;
        if (state is null || s_mode == "replace")
        {
            long previous = state?.Value.Sum ?? 0;
            state = new PgAggregateState<TrackedState>(new TrackedState(s_mode) { Sum = previous });
            if (s_mode is "resources" or "resources_error" or "resources_native" or "resources_native_transition")
            {
                state.Value.Plan = Spi.Prepare("SELECT 42").Keep();
                state.Value.Cursor = Spi.OpenCursor("SELECT generate_series(1,100)");
                state.Value.Cursor.Fetch(1);
            }
        }

        s_retained = state;
        if (value == 2 && s_mode is "transition_error" or "dispose_error_transition" or "resources_error" or "cleanup_native_transition" or "resources_native_transition" or
            "cleanup_terminal_fatal" or "cleanup_terminal_panic")
        {
            throw new PgException("P7801", "aggregate transition failed", "owned aggregate detail", "retry valid inputs");
        }

        if (value == 2 && s_mode == "wait")
        {
            Spi.Execute("SELECT pg_sleep(30)");
        }

        if (s_mode == "worker")
        {
            string outcome = Task.Run(() =>
            {
                try
                {
                    _ = state.Value;
                    return "accessible";
                }
                catch (InvalidOperationException)
                {
                    return "thread protected";
                }
            }).GetAwaiter().GetResult();
            s_trace.Add(outcome);
        }

        state.Value.Sum = checked(state.Value.Sum + (value ?? 0));
        return state;
    }

    private static long Finish(PgAggregateContext context, PgAggregateState<TrackedState>? state)
    {
        s_context = context;
        s_final++;
        if (s_mode == "final_error")
        {
            throw new PgException("P7802", "aggregate final failed");
        }

        return state?.Value.Sum ?? 0;
    }

    private static int Created()
    {
        s_live++;
        return ++s_created;
    }

    private static string Text(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
}
