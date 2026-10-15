namespace Ankus.TestExtension;

/// <summary>
/// Probes destructive reentry while native iterator and aggregate storage is being reclaimed.
/// </summary>
public static class MemoryCleanupOwnerFunctions
{
    private static readonly List<string> s_cleanup = [];

    /// <summary>
    /// Reads or clears the backend-local cleanup observations.
    /// </summary>
    /// <param name="clear">Whether to clear observations after reading them.</param>
    /// <returns>The ordered cleanup observations.</returns>
    [PgFunction]
    public static string MemoryCleanupStatus(bool clear)
    {
        string result = string.Join(';', s_cleanup);
        if (clear)
        {
            s_cleanup.Clear();
        }

        return result;
    }

    /// <summary>
    /// Rejects resetting a PostgreSQL infrastructure owner while retaining normal child ownership.
    /// </summary>
    /// <param name="kind">The predefined infrastructure context.</param>
    /// <returns>The exact native errors and a value from independently owned storage.</returns>
    [PgFunction]
    public static string MemoryInfrastructureProtection(int kind)
    {
        var states = new List<string>();
        for (int operation = 0; operation < 3; operation++)
        {
            PgMemoryContext owner = PgMemoryContext.Get((PgMemoryContextKind)kind)
                ?? throw new InvalidOperationException("The infrastructure context is unavailable.");
            try
            {
                switch (operation)
                {
                    case 0:
                        owner.Reset();
                        break;
                    case 1:
                        owner.ResetOnly();
                        break;
                    default:
                        owner.ResetChildren();
                        break;
                }

                states.Add("unexpected success");
            }
            catch (PgException error)
            {
                states.Add(error.SqlState);
            }
        }

        using PgMemoryContext child = PgMemoryContext.Create("infrastructure guard child");
        using PgAllocation value = child.Allocate(sizeof(int));
        value.Write(42);
        int copied = value.Read<int>();
        child.ResetOnly();
        return $"{string.Join(',', states)}|{copied}|{child.IsAlive}";
    }

    /// <summary>
    /// Recovers a guarded SPI error when PostgreSQL's error context was deliberately selected by managed code.
    /// </summary>
    /// <returns>The owned diagnostic, restored context, and subsequent query result.</returns>
    [PgFunction]
    public static string MemoryErrorContextSpi()
    {
        PgMemoryContext original = PgMemoryContext.Current;
        PgMemoryContext error = PgMemoryContext.Get(PgMemoryContextKind.Error)
            ?? throw new InvalidOperationException("No error context.");
        string state = error.Run(() =>
        {
            try
            {
                Spi.ExecuteScalar<int>("SELECT 1 / 0");
                return "unexpected success";
            }
            catch (PgException exception)
            {
                return $"{exception.SqlState}|{exception.Message}|{PgMemoryContext.Current.Name}";
            }
        });
        return $"{state}|{PgMemoryContext.Current.Id == original.Id}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    /// <summary>
    /// Preserves a live current context after PostgreSQL error flushing deletes the selected ErrorContext child.
    /// </summary>
    /// <param name="spi">Whether the guarded error originates in SPI rather than the native allocator.</param>
    /// <returns>The error, selected-context expiration, safe recovery context, and restored caller.</returns>
    [PgFunction]
    public static string MemoryErrorContextChild(bool spi)
    {
        PgMemoryContext original = PgMemoryContext.Current;
        PgMemoryContext error = PgMemoryContext.Get(PgMemoryContextKind.Error)
            ?? throw new InvalidOperationException("No error context.");
        using PgMemoryContext child = PgMemoryContext.Create("error context child", error);
        string state = child.Run(() =>
        {
            try
            {
                if (spi)
                {
                    Spi.ExecuteScalar<int>("SELECT 1 / 0");
                }
                else
                {
                    PgTransaction.RunInSubtransaction(() =>
                    {
                        using PgAllocation allocation = child.Allocate(nuint.MaxValue);
                    });
                }

                return "unexpected success";
            }
            catch (PgException exception)
            {
                return $"{exception.SqlState}|{child.IsAlive}|{PgMemoryContext.Current.Name}";
            }
        });
        return $"{state}|{PgMemoryContext.Current.Id == original.Id}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    /// <summary>
    /// Reports a direct or SPI-emitted notice while an ErrorContext child is current and checks the live native caller immediately afterward.
    /// </summary>
    /// <param name="viaSpi">Whether an executed SQL block emits the notice instead of the managed reporting API.</param>
    /// <returns>The filter decision, restored current context, retained child payload, and outer caller recovery.</returns>
    [PgFunction]
    public static string MemoryErrorContextChildNotice(bool viaSpi)
    {
        PgMemoryContext original = PgMemoryContext.Current;
        PgMemoryContext error = PgMemoryContext.Get(PgMemoryContextKind.Error)
            ?? throw new InvalidOperationException("No error context.");
        using PgMemoryContext child = PgMemoryContext.Create("error context notice child", error);
        nint childId = child.Id;
        using PgAllocation retained = child.Allocate(sizeof(int));
        retained.Write(73);
        int resets = 0;
        using PgMemoryCallback cleanup = child.RegisterResetCallback(() => resets++);
        string state = child.Run(() =>
        {
            bool enabled = PgLog.IsEnabled(PgLogLevel.Notice);
            if (viaSpi)
            {
                Spi.Execute("DO $notice$ BEGIN RAISE NOTICE USING ERRCODE = '01000', MESSAGE = 'error context SPI notice café 100%', DETAIL = 'SPI détail conservé', HINT = 'SPI hint café'; END; $notice$");
            }
            else
            {
                PgLog.Write(PgLogLevel.Notice, new PgDiagnostic("error context notice café 100%")
                {
                    SqlState = "01000",
                    Detail = "détail conservé",
                    Hint = "hint café",
                    Context = "ErrorContext child notice",
                    File = "error-notice.cs",
                    Line = 317,
                    Routine = "MemoryErrorContextChildNotice",
                });
            }

            PgMemoryContext current = PgMemoryContext.Current;
            string name = current.Name;
            bool same = current.Id == childId;
            bool live = current.IsAlive;
            int copied;
            using (PgAllocation probe = current.Allocate(sizeof(int)))
            {
                probe.Write(91);
                copied = probe.Read<int>();
            }

            // Testing a stale child itself can flush ErrorContext. Observe and
            // use the native current context before making that stale request.
            bool alive = child.IsAlive;
            int preserved = alive ? retained.Read<int>() : 0;
            return $"{enabled}|{name}|{same}|{live}|{copied}|{alive}|{preserved}|{resets}";
        });
        return $"{state}|{PgMemoryContext.Current.Id == original.Id}|{Spi.ExecuteScalar<int>("SELECT 42")}|{DistinctSmallContexts()}";
    }

    /// <summary>
    /// Creates three small contexts at once. A context deleted twice enters PostgreSQL's small-context free list twice,
    /// and from then on that list hands the same storage to more than one live context, which Ankus registers by address.
    /// </summary>
    /// <returns>Whether the three contexts are distinct.</returns>
    private static bool DistinctSmallContexts()
    {
        using PgMemoryContext first = PgMemoryContext.Create("free list first", options: PgMemoryContextOptions.Small);
        using PgMemoryContext second = PgMemoryContext.Create("free list second", options: PgMemoryContextOptions.Small);
        using PgMemoryContext third = PgMemoryContext.Create("free list third", options: PgMemoryContextOptions.Small);
        return first.Id != second.Id && second.Id != third.Id && first.Id != third.Id;
    }

    /// <summary>
    /// Keeps error-handler cleanup from recursively consuming PostgreSQL's active error stack or memory owner.
    /// </summary>
    /// <param name="operation">Explicit child reset, guarded-error cleanup of ErrorContext, or its child.</param>
    /// <returns>The exact phase rejections, original error, retained payload, and subsequent SPI result.</returns>
    [PgFunction]
    public static string MemoryErrorCleanupPhase(int operation)
    {
        PgMemoryContext error = PgMemoryContext.Get(PgMemoryContextKind.Error)
            ?? throw new InvalidOperationException("No error context.");
        using PgMemoryContext owner = operation == 1 ? error : PgMemoryContext.Create("error-handler callback", error);
        using PgMemoryContext independent = PgMemoryContext.Create("independent error-handler payload");
        using PgAllocation value = independent.Allocate(sizeof(int));
        value.Write(73);
        using SpiPreparedStatement plan = Spi.Prepare("SELECT 42");
        var observations = new List<string>();
        PgMemoryCallback? callback = null;
        callback = owner.RegisterResetCallback(() =>
        {
            observations.Add($"pending:{callback?.IsPending}");
            try
            {
                observations.Add($"unexpected memory access:{value.Read<int>()}");
            }
            catch (PgException exception)
            {
                observations.Add($"{exception.SqlState}:{exception.Message}");
            }

            try
            {
                plan.Dispose();
                observations.Add("unexpected plan disposal");
            }
            catch (PgException exception)
            {
                observations.Add($"{exception.SqlState}:{exception.Message}");
            }
        });
        using (callback)
        {
            string trigger = "reset";
            if (operation == 0)
            {
                owner.Reset();
                owner.Reset();
            }
            else
            {
                try
                {
                    PgTransaction.RunInSubtransaction(() =>
                    {
                        using PgAllocation invalid = independent.Allocate(nuint.MaxValue);
                    });
                    trigger = "unexpected allocation";
                }
                catch (PgException exception)
                {
                    trigger = exception.SqlState;
                }
            }

            return $"{trigger}|{string.Join('|', observations)}|{callback.IsPending}|{owner.IsAlive}|{value.Read<int>()}|{plan.ExecuteScalar<int>()}|{Spi.ExecuteScalar<int>("SELECT 42")}";
        }
    }

    /// <summary>
    /// Suspends an iterator whose finally block attempts to mutate its owning executor tree.
    /// </summary>
    /// <returns>Two rows whose native consumer can fail after the first yield.</returns>
    [PgFunction]
    public static IEnumerable<int> MemoryCleanupSequence()
    {
        PgMemoryContext executor = FindExecutor();
        try
        {
            yield return 1;
            yield return 2;
        }
        finally
        {
            Probe(executor);
        }
    }

    /// <summary>
    /// Owns an aggregate payload that attempts destructive memory access from native reset cleanup.
    /// </summary>
    [PgAggregate(Name = "memory_cleanup_sum")]
    public sealed class CleanupSum : IPgAggregate<PgAggregateState<CleanupPayload>?, int>,
        IPgFinalizingAggregate<PgAggregateState<CleanupPayload>?, ValueTuple, int>
    {
        /// <summary>
        /// Retains the executor handle while accumulating an ordinary sum.
        /// </summary>
        /// <param name="context">The aggregate invocation and owner.</param>
        /// <param name="state">The existing owned payload.</param>
        /// <param name="value">The next input.</param>
        /// <returns>The attached aggregate state.</returns>
        public static PgAggregateState<CleanupPayload> Transition(PgAggregateContext context, PgAggregateState<CleanupPayload>? state, int value)
        {
            state ??= new PgAggregateState<CleanupPayload>(new CleanupPayload(FindExecutor()));
            state.Value.Sum += value;
            return state;
        }

        /// <summary>
        /// Returns the sum before PostgreSQL releases the owned payload.
        /// </summary>
        /// <param name="context">The aggregate invocation and owner.</param>
        /// <param name="state">The live aggregate state.</param>
        /// <param name="arguments">The empty direct argument group.</param>
        /// <returns>The accumulated sum.</returns>
        public static int Final(PgAggregateContext context, PgAggregateState<CleanupPayload>? state, ValueTuple arguments) => state?.Value.Sum ?? 0;
    }

    /// <summary>
    /// Retains an executor ancestor for the native cleanup reentry probe.
    /// </summary>
    /// <param name="executor">The ancestor of the native aggregate state.</param>
    public sealed class CleanupPayload(PgMemoryContext executor) : IDisposable
    {
        /// <summary>
        /// Gets or sets the accumulated value.
        /// </summary>
        public int Sum
        {
            get;
            set;
        }

        /// <summary>
        /// Probes the native owner's protection before its allocation is freed.
        /// </summary>
        public void Dispose() => Probe(executor);
    }

    private static PgMemoryContext FindExecutor()
    {
        for (PgMemoryContext? current = PgMemoryContext.Current; current is not null; current = current.Parent)
        {
            if (current.Name == "ExecutorState")
            {
                return current;
            }
        }

        throw new InvalidOperationException("No executor context in the active callback ancestry.");
    }

    private static void Probe(PgMemoryContext executor)
    {
        bool currentInsideExecutor = false;
        for (PgMemoryContext? current = PgMemoryContext.Current; current is not null; current = current.Parent)
        {
            if (current.Id == executor.Id)
            {
                currentInsideExecutor = true;
            }
        }

        PgMemoryContext transaction = PgMemoryContext.Get(PgMemoryContextKind.TopTransaction)
            ?? throw new InvalidOperationException("No transaction context.");
        var states = new List<string>();
        foreach (Action operation in new Action[] { executor.ResetOnly, executor.ResetChildren, transaction.ResetOnly, transaction.ResetChildren })
        {
            try
            {
                operation();
                states.Add("unexpected success");
            }
            catch (PgException exception)
            {
                states.Add(exception.SqlState);
            }
        }

        s_cleanup.Add($"{currentInsideExecutor}:{string.Join(',', states)}");
    }
}
