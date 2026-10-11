using System.Runtime.CompilerServices;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises managed reset callbacks through PostgreSQL's real memory-context cleanup machinery.
/// </summary>
public static class MemoryCallbackFunctions
{
    private static readonly List<string> s_implicitEvents = [];
    private static PgMemoryContext? s_implicitOwner;
    private static PgAllocation? s_implicitValue;
    private static PgMemoryCallback? s_implicitFirst;
    private static PgMemoryCallback? s_implicitSecond;
    private static int s_payloadCalls;
    private static readonly List<string> s_spiCleanupEvents = [];
    private static PgMemoryContext? s_spiOwner;
    private static SpiPreparedStatement? s_spiPlan;
    private static SpiCursor? s_spiCursor;
    private static PgMemoryCallback? s_spiCallback;
    private static int s_spiCallbackCalls;

    /// <summary>
    /// Records LIFO order, consumption before invocation, repeated reset, and final deletion.
    /// </summary>
    /// <returns>The exact callback sequence and registration states.</returns>
    [PgFunction]
    public static string MemoryCallbackLifo()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("callback lifo");
        var events = new List<string>();
        using PgMemoryCallback first = owner.RegisterResetCallback(() => events.Add("A"));
        using PgMemoryCallback second = owner.RegisterResetCallback(() => events.Add("B"));
        using PgMemoryCallback third = owner.RegisterResetCallback(() => events.Add("C"));
        owner.Reset();
        string firstDrain = string.Concat(events);
        owner.Reset();
        string secondDrain = string.Concat(events);
        using PgMemoryCallback last = owner.RegisterResetCallback(() => events.Add("D"));
        owner.Dispose();
        owner.Dispose();
        return $"{firstDrain}|{secondDrain}|{string.Concat(events)}|{first.IsPending},{second.IsPending},{third.IsPending},{last.IsPending}|{owner.IsAlive}";
    }

    /// <summary>
    /// Registers another callback during drain and optionally cancels an older pending callback.
    /// </summary>
    /// <param name="cancelOlder">Whether the running callback cancels the older callback.</param>
    /// <returns>The drain order, self-consumption state, and final pending states.</returns>
    [PgFunction]
    public static string MemoryCallbackDrain(bool cancelOlder)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("callback drain");
        var events = new List<string>();
        using PgMemoryCallback first = owner.RegisterResetCallback(() => events.Add("A"));
        PgMemoryCallback? second = null;
        PgMemoryCallback? added = null;
        bool consumedBeforeCall = false;
        second = owner.RegisterResetCallback(() =>
        {
            PgMemoryCallback self = second ?? throw new InvalidOperationException("Missing current registration.");
            consumedBeforeCall = !self.IsPending;
            self.Dispose();
            self.Dispose();
            events.Add("B");
            if (cancelOlder)
            {
                first.Dispose();
                first.Dispose();
            }

            added = owner.RegisterResetCallback(() => events.Add("C"));
        });
        owner.Reset();
        owner.Reset();
        return $"{string.Concat(events)}|{consumedBeforeCall}|{first.IsPending},{second.IsPending},{added?.IsPending}";
    }

    /// <summary>
    /// Preserves exact diagnostics and older pending callbacks when reset or deletion fails.
    /// </summary>
    /// <param name="operation">Zero for reset, one for reset-only, or two for deletion.</param>
    /// <returns>The failure diagnostics, partial state, retry order, and restored current context.</returns>
    [PgFunction]
    public static string MemoryCallbackFailure(int operation)
    {
        PgMemoryContext original = PgMemoryContext.Current;
        using PgMemoryContext selected = PgMemoryContext.Create("callback selected context");
        using PgMemoryContext owner = PgMemoryContext.Create("callback failed owner");
        using PgAllocation value = owner.Allocate(sizeof(int));
        value.Write(73);
        var events = new List<string>();
        using PgMemoryCallback first = owner.RegisterResetCallback(() => events.Add("A"));
        using PgMemoryCallback second = owner.RegisterResetCallback(() =>
        {
            events.Add("B");
            throw CallbackError();
        });
        using PgMemoryCallback third = owner.RegisterResetCallback(() => events.Add("C"));
        string failure = selected.Run(() =>
        {
            string diagnostics = CaptureDiagnostic(() => Apply(owner, operation));
            return $"{diagnostics}|{string.Concat(events)}|{first.IsPending},{second.IsPending},{third.IsPending}|{owner.IsAlive}|{value.Read<int>()}|{PgMemoryContext.Current.Id == selected.Id}";
        });
        Apply(owner, operation);
        Apply(owner, operation);
        return $"{failure}|{string.Concat(events)}|{first.IsPending},{second.IsPending},{third.IsPending}|{owner.IsAlive}|{ReadOrStale(value)}|{PgMemoryContext.Current.Id == original.Id}";
    }

    /// <summary>
    /// Reads native payloads before reset invalidation and distinguishes callback traversal in each tree operation.
    /// </summary>
    /// <param name="operation">Reset, reset-only, reset-children, or deletion.</param>
    /// <returns>The exact callback order and surviving context, allocation, and registration state.</returns>
    [PgFunction]
    public static string MemoryCallbackTree(int operation)
    {
        using PgMemoryContext root = PgMemoryContext.Create("callback tree root");
        using PgMemoryContext child = PgMemoryContext.Create("callback tree child", root);
        using PgMemoryContext leaf = PgMemoryContext.Create("callback tree leaf", child);
        using PgAllocation rootValue = root.Allocate(sizeof(int));
        using PgAllocation childValue = child.Allocate(sizeof(int));
        using PgAllocation leafValue = leaf.Allocate(sizeof(int));
        rootValue.Write(11);
        childValue.Write(22);
        leafValue.Write(33);
        var events = new List<string>();
        using PgMemoryCallback rootCallback = root.RegisterResetCallback(() => events.Add($"P{rootValue.Read<int>()}"));
        using PgMemoryCallback childCallback = child.RegisterResetCallback(() => events.Add($"C{childValue.Read<int>()}"));
        using PgMemoryCallback leafCallback = leaf.RegisterResetCallback(() => events.Add($"L{leafValue.Read<int>()}"));
        switch (operation)
        {
            case 0:
                root.Reset();
                break;
            case 1:
                root.ResetOnly();
                break;
            case 2:
                root.ResetChildren();
                break;
            case 3:
                root.Dispose();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return $"{string.Join(',', events)}|{root.IsAlive},{child.IsAlive},{leaf.IsAlive}|{ReadOrStale(rootValue)},{ReadOrStale(childValue)},{ReadOrStale(leafValue)}|{rootCallback.IsPending},{childCallback.IsPending},{leafCallback.IsPending}";
    }

    /// <summary>
    /// Runs an independent nested cleanup while an enclosing context is being retained by reset.
    /// </summary>
    /// <param name="children">Whether the nested operation resets only descendants.</param>
    /// <returns>The nested order, retained identities, stale payloads, and successful reuse.</returns>
    [PgFunction]
    public static string MemoryCallbackNestedReset(bool children)
    {
        using PgMemoryContext outer = PgMemoryContext.Create("outer callback reset");
        using PgMemoryContext independent = PgMemoryContext.Create("independent callback reset");
        using PgMemoryContext child = PgMemoryContext.Create("independent callback child", independent);
        using PgAllocation outerValue = outer.Allocate(sizeof(int));
        using PgAllocation independentValue = independent.Allocate(sizeof(int));
        using PgAllocation childValue = child.Allocate(sizeof(int));
        outerValue.Write(11);
        independentValue.Write(22);
        childValue.Write(33);
        var events = new List<string>();
        using PgMemoryCallback nested = (children ? child : independent).RegisterResetCallback(() => events.Add("B"));
        using PgMemoryCallback callback = outer.RegisterResetCallback(() =>
        {
            events.Add("A");
            if (children)
            {
                independent.ResetChildren();
            }
            else
            {
                independent.ResetOnly();
            }

            events.Add(outerValue.Read<int>().ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        outer.ResetOnly();
        using PgAllocation replacement = outer.Allocate(sizeof(int));
        replacement.Write(44);
        using PgMemoryCallback repeated = outer.RegisterResetCallback(() => events.Add($"R{replacement.Read<int>()}"));
        outer.ResetOnly();
        return $"{string.Join(',', events)}|{outer.IsAlive},{independent.IsAlive},{child.IsAlive}|{ReadOrStale(outerValue)},{ReadOrStale(independentValue)},{ReadOrStale(childValue)}|{ReadOrStale(replacement)}|{outer.Name}";
    }

    /// <summary>
    /// Rejects destructive reentry and child creation while allowing live payload access and independent allocation.
    /// </summary>
    /// <param name="delete">Whether the outer native cleanup deletes or resets its context.</param>
    /// <returns>The exact protected-operation errors, payload value, and masked SQL and log capabilities.</returns>
    [PgFunction]
    public static string MemoryCallbackProtectedOwner(bool delete)
    {
        using PgMemoryContext root = PgMemoryContext.Create("callback protected parent");
        using PgMemoryContext owner = PgMemoryContext.Create("callback protected owner", root);
        using PgMemoryContext independent = PgMemoryContext.Create("callback independent storage");
        using PgAllocation value = owner.Allocate(sizeof(int));
        value.Write(77);
        var states = new List<string>();
        int copied = 0;
        int independentValue = 0;
        int deniedCapabilities = 0;
        using PgMemoryCallback callback = owner.RegisterResetCallback(() =>
        {
            foreach (Action action in new Action[]
            {
                owner.Reset,
                owner.ResetOnly,
                owner.Dispose,
                root.Reset,
                root.ResetChildren,
                root.Dispose,
                () => owner.Run(static () =>
                {
                }),
                () =>
                {
                    using PgMemoryContext forbidden = PgMemoryContext.Create("forbidden callback child", owner);
                },
                () =>
                {
                    using PgMemoryContext forbidden = PgMemoryContext.Create("forbidden callback sibling", root);
                },
            })
            {
                states.Add(CaptureState(action));
            }

            copied = value.Read<int>();
            using PgAllocation allocation = independent.Allocate(sizeof(int));
            allocation.Write(91);
            independentValue = allocation.Read<int>();
            foreach (Action action in new Action[]
            {
                static () => Spi.ExecuteScalar<int>("SELECT 99"),
                static () => PgLog.Write(PgLogLevel.Notice, "A reset callback must not log this message."),
            })
            {
                try
                {
                    action();
                }
                catch (InvalidOperationException)
                {
                    deniedCapabilities++;
                }
            }
        });
        if (delete)
        {
            owner.Dispose();
        }
        else
        {
            owner.Reset();
        }

        PgLog.Write(PgLogLevel.Debug1, "Memory callback restored logging.");
        long leaked = Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident LIKE 'forbidden callback %'");
        return $"{string.Join(',', states)}|{copied}|{independentValue}|{deniedCapabilities}|{ReadOrStale(value)}|{callback.IsPending}|{leaked}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    /// <summary>
    /// Proves native pending ownership and managed-root release after cancellation, success, or failure.
    /// </summary>
    /// <param name="operation">Cancel, reset, throwing reset, or reset without retaining the wrapper.</param>
    /// <returns>The captured object's reachability before and after cleanup and the exact callback count.</returns>
    [PgFunction]
    public static string MemoryCallbackRoots(int operation)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("callback managed root");
        s_payloadCalls = 0;
        (PgMemoryCallback? registration, WeakReference<CallbackPayload> weak) = RegisterPayload(owner, operation);
        CollectPayload();
        bool before = PayloadIsAlive(weak);
        CompletePayload(owner, registration, operation);
        CollectPayload();
        bool after = PayloadIsAlive(weak);
        owner.Reset();
        GC.KeepAlive(registration);
        return $"{before}|{after}|{s_payloadCalls}|{registration?.IsPending ?? false}";
    }

    /// <summary>
    /// Leaves two registrations owned solely by a query, transaction, or subtransaction context.
    /// </summary>
    /// <param name="kind">Zero for the query context, one for the top transaction, or two for the current subtransaction.</param>
    /// <param name="throws">Whether the newer callback reports an owned PostgreSQL error.</param>
    /// <returns>A value returned before PostgreSQL initiates the implicit cleanup.</returns>
    [PgFunction]
    public static int MemoryCallbackPrepare(int kind, bool throws)
    {
        s_implicitFirst?.Dispose();
        s_implicitSecond?.Dispose();
        s_implicitValue?.Dispose();
        s_implicitOwner?.Dispose();
        s_implicitEvents.Clear();
        PgMemoryContext parent = kind switch
        {
            0 => PgMemoryContext.Current,
            1 => PgMemoryContext.Get(PgMemoryContextKind.TopTransaction) ?? throw new InvalidOperationException("No top transaction."),
            2 => PgMemoryContext.Get(PgMemoryContextKind.CurTransaction) ?? throw new InvalidOperationException("No current transaction."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        s_implicitOwner = PgMemoryContext.Create("implicit memory callback", parent);
        PgAllocation value = s_implicitOwner.Allocate(sizeof(int));
        s_implicitValue = value;
        value.Write(73);
        s_implicitFirst = s_implicitOwner.RegisterResetCallback(() => s_implicitEvents.Add($"A{value.Read<int>()}"));
        s_implicitSecond = s_implicitOwner.RegisterResetCallback(() =>
        {
            s_implicitEvents.Add($"B{value.Read<int>()}");
            if (throws)
            {
                throw CallbackError();
            }
        });
        return 42;
    }

    /// <summary>
    /// Registers cancellation during cleanup after retaining an older callback and its native payload.
    /// </summary>
    /// <param name="kind">The current statement, top transaction, or current subtransaction owner.</param>
    /// <returns>The ordinary preparation result before native cleanup starts.</returns>
    [PgFunction]
    public static int MemoryCallbackPrepareCancellation(int kind)
    {
        int result = MemoryCallbackPrepare(kind, false);
        PgMemoryContext owner = s_implicitOwner ?? throw new InvalidOperationException("Missing callback owner.");
        PgAllocation value = s_implicitValue ?? throw new InvalidOperationException("Missing callback payload.");
        s_implicitSecond?.Dispose();
        s_implicitSecond = owner.RegisterResetCallback(() =>
        {
            s_implicitEvents.Add($"B{value.Read<int>()}");
            throw new PgQueryCanceledException("cleanup cancellation");
        });
        return result;
    }

    /// <summary>
    /// Leaves an unrepresentable secondary diagnostic for transaction or subtransaction cleanup.
    /// </summary>
    /// <param name="kind">The top transaction or current subtransaction owner.</param>
    /// <returns>The preparation result before PostgreSQL starts callback drain.</returns>
    [PgFunction]
    public static int MemoryCallbackPrepareEncoding(int kind)
    {
        int result = MemoryCallbackPrepare(kind, false);
        PgMemoryContext owner = s_implicitOwner ?? throw new InvalidOperationException("Missing callback owner.");
        PgAllocation value = s_implicitValue ?? throw new InvalidOperationException("Missing callback payload.");
        s_implicitSecond?.Dispose();
        s_implicitSecond = owner.RegisterResetCallback(() =>
        {
            s_implicitEvents.Add($"B{value.Read<int>()}");
            throw new PgException("22023", "callback €", "secondary detail", "secondary hint")
            {
                Context = "cleanup context",
                SchemaName = "schéma",
                TableName = "callback_table",
                ColumnName = "callback_column",
                DataTypeName = "callback_type",
                ConstraintName = "callback_constraint",
                Position = 17,
                InternalPosition = 3,
                InternalQuery = "SELECT callback",
                File = "MemoryCallbackFunctions.cs",
                Line = 911,
                Routine = nameof(MemoryCallbackPrepareEncoding),
                DetailLog = "server detail €",
                Backtrace = "frame one\nframe two",
            };
        });
        return result;
    }

    /// <summary>
    /// Retains terminal reporting intent even when user cleanup catches the managed exception before commit completes.
    /// </summary>
    /// <param name="level">FATAL or PANIC represented by its logging enum value.</param>
    /// <returns>The ordinary preparation result before transaction completion starts.</returns>
    [PgFunction]
    public static int MemoryCallbackPrepareTerminal(int level)
    {
        if (level != (int)PgLogLevel.Fatal && level != (int)PgLogLevel.Panic)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        int result = MemoryCallbackPrepare(1, false);
        PgMemoryContext owner = s_implicitOwner ?? throw new InvalidOperationException("Missing callback owner.");
        s_implicitSecond?.Dispose();
        s_implicitSecond = owner.RegisterResetCallback(() =>
        {
            CallbackRecoveryFunctions.ReportCaughtTerminal(level);
            s_implicitEvents.Add("caught terminal report");
        });
        return result;
    }

    /// <summary>
    /// Registers the same caught terminal cleanup report in whichever process runs this parallel-safe call, so a forced
    /// parallel plan makes a parallel worker report it while its transaction commits.
    /// </summary>
    /// <returns>A value returned before the worker's commit starts the cleanup.</returns>
    [PgFunction(ParallelSafety = PgParallelSafety.Safe)]
    public static int MemoryCallbackPrepareTerminalInWorker() => MemoryCallbackPrepareTerminal((int)PgLogLevel.Fatal);

    /// <summary>
    /// Inspects the backend-local aftermath of implicit cleanup from a later SQL call.
    /// </summary>
    /// <returns>The callback order, pending states, owner lifetime, and checked payload state.</returns>
    [PgFunction]
    public static string MemoryCallbackImplicitState()
        => $"{string.Join(',', s_implicitEvents)}|{s_implicitFirst?.IsPending},{s_implicitSecond?.IsPending}|{s_implicitOwner?.IsAlive}|{(s_implicitValue is null ? "missing" : ReadOrStale(s_implicitValue))}";

    /// <summary>
    /// Retains a prepared plan and an owned cursor for a transaction-context cleanup callback.
    /// </summary>
    /// <param name="subtransaction">Whether the callback belongs to the current subtransaction.</param>
    /// <param name="adoptParentCursor">Whether to adopt the named cursor created in the parent transaction.</param>
    /// <returns>The prepared value and first cursor row, proving both resources were live before cleanup.</returns>
    [PgFunction]
    public static string MemoryCallbackSpiPrepare(bool subtransaction, bool adoptParentCursor)
    {
        s_spiCallback?.Dispose();
        s_spiCursor?.Dispose();
        s_spiPlan?.Dispose();
        s_spiOwner?.Dispose();
        s_spiCleanupEvents.Clear();
        s_spiCallbackCalls = 0;
        PgMemoryContext parent = PgMemoryContext.Get(subtransaction ? PgMemoryContextKind.CurTransaction : PgMemoryContextKind.TopTransaction)
            ?? throw new InvalidOperationException("No transaction context for owned SPI resources.");
        s_spiOwner = PgMemoryContext.Create("memory callback SPI owner", parent);
        SpiPreparedStatement plan = Spi.Prepare("SELECT generate_series(41, 43) AS value /* Ankus memory callback resources */");
        s_spiPlan = plan;
        SpiCursor cursor = adoptParentCursor ? Spi.FindCursor("memory_callback_parent_cursor") : plan.OpenCursor();
        s_spiCursor = cursor;
        int preparedValue = plan.ExecuteScalar<int>();
        int cursorValue = cursor.Fetch(1)[0].Get<int>(0);
        s_spiCallback = s_spiOwner.RegisterResetCallback(() =>
        {
            s_spiCallbackCalls++;
            foreach ((string name, Action action) in new (string, Action)[]
            {
                ("query", static () => Spi.ExecuteScalar<int>("SELECT 99")),
                ("plan", () => plan.ExecuteScalar<int>()),
                ("cursor", () => cursor.Fetch(1)),
                ("log", static () => PgLog.Write(PgLogLevel.Notice, "A resource cleanup callback must not log this message.")),
            })
            {
                try
                {
                    action();
                    s_spiCleanupEvents.Add(name + ":allowed");
                }
                catch (ObjectDisposedException)
                {
                    s_spiCleanupEvents.Add(name + ":prematurely disposed");
                }
                catch (InvalidOperationException)
                {
                    s_spiCleanupEvents.Add(name + ":denied");
                }
            }

            cursor.Dispose();
            cursor.Dispose();
            s_spiCleanupEvents.Add("cursor:disposed");
            plan.Dispose();
            plan.Dispose();
            s_spiCleanupEvents.Add("plan:disposed");
        });
        return $"{preparedValue}|{cursorValue}";
    }

    /// <summary>
    /// Explicitly resets a callback owner twice and proves its owned SPI resources were disposed once.
    /// </summary>
    /// <returns>The cleanup state while the reset owner remains live, followed by a recovered SQL result.</returns>
    [PgFunction]
    public static string MemoryCallbackSpiReset()
    {
        string prepared = MemoryCallbackSpiPrepare(subtransaction: false, adoptParentCursor: false);
        using PgMemoryContext owner = s_spiOwner ?? throw new InvalidOperationException("Missing SPI callback owner.");
        owner.Reset();
        owner.Reset();
        string state = MemoryCallbackSpiState();
        PgLog.Write(PgLogLevel.Debug1, "SPI callback cleanup restored logging.");
        return $"{prepared}|{state}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    /// <summary>
    /// Checks consumed registration, exact cleanup order, and disposed plan and cursor handles from a later callback.
    /// </summary>
    /// <returns>The callback count, event order, pending state, disposed resources, and native owner state.</returns>
    [PgFunction]
    public static string MemoryCallbackSpiState()
    {
        SpiPreparedStatement plan = s_spiPlan ?? throw new InvalidOperationException("Missing saved callback plan.");
        SpiCursor cursor = s_spiCursor ?? throw new InvalidOperationException("Missing saved callback cursor.");
        string planState = DisposedResourceState(() => plan.ExecuteScalar<int>());
        string cursorState = DisposedResourceState(() => cursor.Fetch(1));
        return $"{s_spiCallbackCalls}|{string.Join(',', s_spiCleanupEvents)}|{s_spiCallback?.IsPending}|{planState},{cursorState}|{s_spiOwner?.IsAlive}";
    }

    /// <summary>
    /// Exercises guarded allocation and callback errors while PostgreSQL's ErrorContext is selected.
    /// </summary>
    /// <returns>The exact diagnostics, surviving independent payload, and restored original context.</returns>
    [PgFunction]
    public static string MemoryCallbackErrorContext()
    {
        PgMemoryContext original = PgMemoryContext.Current;
        using PgMemoryContext owner = PgMemoryContext.Create("ErrorContext callback owner");
        using PgAllocation value = owner.Allocate(sizeof(int));
        value.Write(91);
        PgMemoryContext errorContext = PgMemoryContext.Get(PgMemoryContextKind.Error)
            ?? throw new InvalidOperationException("No ErrorContext.");
        string allocationFailure = errorContext.Run(() =>
        {
            string state = CaptureState(() =>
            {
                PgTransaction.RunInSubtransaction(() =>
                {
                    using PgAllocation invalid = owner.Allocate(0x40000000);
                });
            });
            return $"{state}|{PgMemoryContext.Current.Name}";
        });
        using PgMemoryCallback callback = owner.RegisterResetCallback(static () => throw CallbackError());
        PgMemoryContext reboundError = PgMemoryContext.Get(PgMemoryContextKind.Error)
            ?? throw new InvalidOperationException("No ErrorContext after recovery.");
        string callbackFailure = reboundError.Run(() => $"{CaptureDiagnostic(owner.Reset)}|{PgMemoryContext.Current.Name}");
        int retained = value.Read<int>();
        owner.Reset();
        return $"{allocationFailure}|{callbackFailure}|{retained}|{callback.IsPending}|{ReadOrStale(value)}|{PgMemoryContext.Current.Id == original.Id}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    /// <summary>
    /// Transports owned callback diagnostics through the database encoding and retries any remaining cleanup.
    /// </summary>
    /// <param name="unrepresentable">Whether the message contains an elephant outside LATIN1.</param>
    /// <returns>The observed diagnostics, one-shot state, and recovered SQL result.</returns>
    [PgFunction]
    public static string MemoryCallbackEncoding(bool unrepresentable)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("callback encoded diagnostics");
        var events = new List<string>();
        using PgMemoryCallback first = owner.RegisterResetCallback(() => events.Add("A"));
        using PgMemoryCallback second = owner.RegisterResetCallback(() =>
        {
            events.Add("B");
            throw new PgException("22023", unrepresentable ? "callback 🐘" : "callback café", "detail naïve", "hint déjà");
        });
        string diagnostics = unrepresentable
            ? CaptureState(() => PgTransaction.RunInSubtransaction(owner.Reset))
            : CaptureDiagnostic(owner.Reset);
        string partial = $"{string.Concat(events)}|{first.IsPending},{second.IsPending}";
        owner.Reset();
        owner.Reset();
        return $"{diagnostics}|{partial}|{string.Concat(events)}|{first.IsPending},{second.IsPending}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    private static PgException CallbackError() => new("22023", "callback café", "detail naïve", "hint déjà");

    private static string DisposedResourceState(Action action)
    {
        try
        {
            action();
            return "live";
        }
        catch (ObjectDisposedException)
        {
            return "disposed";
        }
    }

    private static string CaptureDiagnostic(Action action)
    {
        try
        {
            PgTransaction.RunInSubtransaction(action);
            return "no error";
        }
        catch (PgException error)
        {
            return $"{error.SqlState}|{error.Message}|{error.Detail}|{error.Hint}";
        }
    }

    private static string CaptureState(Action action)
    {
        try
        {
            action();
            return "no error";
        }
        catch (PgException error)
        {
            return error.SqlState;
        }
    }

    private static void Apply(PgMemoryContext owner, int operation)
    {
        switch (operation)
        {
            case 0:
                owner.Reset();
                break;
            case 1:
                owner.ResetOnly();
                break;
            case 2:
                owner.Dispose();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static string ReadOrStale(PgAllocation allocation)
    {
        try
        {
            return allocation.Read<int>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ObjectDisposedException)
        {
            return "stale";
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PgMemoryCallback? Registration, WeakReference<CallbackPayload> Weak) RegisterPayload(PgMemoryContext owner, int operation)
    {
        var payload = new CallbackPayload(operation == 2);
        var weak = new WeakReference<CallbackPayload>(payload);
        PgMemoryCallback registration = owner.RegisterResetCallback(payload.Invoke);
        return (operation == 3 ? null : registration, weak);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool PayloadIsAlive(WeakReference<CallbackPayload> weak) => weak.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompletePayload(PgMemoryContext owner, PgMemoryCallback? registration, int operation)
    {
        if (operation == 0)
        {
            (registration ?? throw new InvalidOperationException("Missing pending registration.")).Dispose();
        }
        else
        {
            try
            {
                PgTransaction.RunInSubtransaction(owner.Reset);
            }
            catch (PgException error) when (operation == 2 && error.SqlState == "22023")
            {
            }
        }
    }

    private static void CollectPayload()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class CallbackPayload(bool throws)
    {
        /// <summary>
        /// Counts dispatch and optionally fails after the callback has consumed its root.
        /// </summary>
        public void Invoke()
        {
            s_payloadCalls++;
            if (throws)
            {
                throw CallbackError();
            }
        }
    }
}
