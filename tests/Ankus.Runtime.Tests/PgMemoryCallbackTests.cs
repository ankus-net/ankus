using System.Runtime.CompilerServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies managed callback rooting, cancellation, guarded dispatch, and capability restoration.
/// </summary>
[TestClass]
public sealed unsafe partial class PgMemoryCallbackTests
{
    /// <summary>
    /// A missing action is rejected before context liveness or native registration is queried.
    /// </summary>
    [TestMethod]
    public void NullActionIsRejectedBeforeNativeAccess()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Requests.Clear();
        ArgumentNullException error = Assert.ThrowsExactly<ArgumentNullException>(() => context.RegisterResetCallback(null!));
        Assert.AreEqual("callback", error.ParamName);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Native liveness validation prevents a stale context from acquiring a managed callback root.
    /// </summary>
    [TestMethod]
    public void StaleContextRejectsRegistrationBeforeNativeCallbackRequest()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Requests.Clear();
        fixture.Handler = static _ => throw new PgException("55000", "deleted context");
        Assert.ThrowsExactly<ObjectDisposedException>(() => context.RegisterResetCallback(static () =>
        {
        }));
        NativeMemoryRequest request = Assert.ContainsSingle(fixture.Requests);
        Assert.AreEqual(NativeMemoryOperation.Name, request._operation);
        Assert.AreEqual(1, fixture.ErrorReleases);
    }

    /// <summary>
    /// Each pending action has a distinct stable identity and a dispatcher tied to its owning context.
    /// </summary>
    [TestMethod]
    public void RegistrationCarriesDistinctRootsAndOwningContext()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        using PgMemoryCallback first = context.RegisterResetCallback(static () =>
        {
        });
        using PgMemoryCallback second = context.RegisterResetCallback(static () =>
        {
        });
        NativeMemoryRequest[] registrations = [.. fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.RegisterCallback)];
        Assert.HasCount(2, registrations);
        Assert.IsTrue(first.IsPending);
        Assert.IsTrue(second.IsPending);
        Assert.AreEqual(101, registrations[0]._context);
        Assert.AreEqual(101, registrations[1]._context);
        Assert.AreNotEqual(nint.Zero, registrations[0]._other);
        Assert.AreNotEqual(nint.Zero, registrations[1]._other);
        Assert.AreNotEqual(registrations[0]._other, registrations[1]._other);
        Assert.AreNotEqual(nint.Zero, registrations[0]._pointer);
        Assert.AreEqual(registrations[0]._pointer, registrations[1]._pointer);
    }

    /// <summary>
    /// Cancellation releases the managed action without executing it and tolerates repeated disposal.
    /// </summary>
    [TestMethod]
    public void CancellationIsIdempotentAndMakesSavedNativeRootStale()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int calls = 0;
        using PgMemoryCallback registration = PgMemoryContext.Current.RegisterResetCallback(() => calls++);
        NativeMemoryRequest registered = GetRegistration(fixture);
        fixture.Requests.Clear();
        registration.Dispose();
        registration.Dispose();
        Assert.IsFalse(registration.IsPending);
        Assert.AreEqual(0, calls);
        NativeMemoryRequest cancelled = Assert.ContainsSingle(fixture.Requests);
        Assert.AreEqual(NativeMemoryOperation.CancelCallback, cancelled._operation);
        Assert.AreEqual(registered._context, cancelled._context);
        Assert.AreEqual(registered._other, cancelled._other);
        PgException stale = Assert.IsInstanceOfType<PgException>(Dispatch(registered, scope.Address));
        Assert.AreEqual("38000", stale.SqlState);
        Assert.Contains("stale", stale.Message);
        Assert.AreEqual(0, calls);
    }

    /// <summary>
    /// A failed native cancellation retains the pending action until a successful retry.
    /// </summary>
    [TestMethod]
    public void FailedCancellationPreservesPendingActionAndReleasesDiagnostics()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int calls = 0;
        using PgMemoryCallback registration = PgMemoryContext.Current.RegisterResetCallback(() => calls++);
        NativeMemoryRequest registered = GetRegistration(fixture);
        fixture.Handler = static _ => throw new PgException("22023", "cancel café", "cancel detail", "retry hint");
        PgException error = Assert.ThrowsExactly<PgException>(registration.Dispose);
        Assert.AreEqual("22023", error.SqlState);
        Assert.AreEqual("cancel café", error.Message);
        Assert.AreEqual("cancel detail", error.Detail);
        Assert.AreEqual("retry hint", error.Hint);
        Assert.AreEqual(3, fixture.ErrorReleases);
        Assert.IsTrue(registration.IsPending);
        Assert.AreEqual(0, calls);
        fixture.Handler = null;
        registration.Dispose();
        Assert.IsFalse(registration.IsPending);
        Assert.IsNotNull(Dispatch(registered, scope.Address));
        Assert.AreEqual(0, calls);
        Assert.HasCount(2, fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.CancelCallback));
    }

    /// <summary>
    /// Dispatch consumes its root before entering user code, including reentrant self-disposal.
    /// </summary>
    [TestMethod]
    public void CallbackIsConsumedBeforeUserActionAndRunsExactlyOnce()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int calls = 0;
        PgMemoryCallback? registration = null;
        NativeMemoryRequest registered = default;
        registration = PgMemoryContext.Current.RegisterResetCallback(() =>
        {
            Assert.IsNotNull(registration);
            Assert.IsFalse(registration.IsPending);
            registration.Dispose();
            PgException reentrant = Assert.IsInstanceOfType<PgException>(Dispatch(registered, scope.Address));
            Assert.AreEqual("38000", reentrant.SqlState);
            Assert.Contains("stale", reentrant.Message);
            calls++;
        });
        using (registration)
        {
            registered = GetRegistration(fixture);
            fixture.Requests.Clear();
            Assert.IsNull(Dispatch(registered, scope.Address));
            Assert.AreEqual(1, calls);
            Assert.IsFalse(registration.IsPending);
            Assert.IsEmpty(fixture.Requests);
            PgException stale = Assert.IsInstanceOfType<PgException>(Dispatch(registered, scope.Address));
            Assert.AreEqual("38000", stale.SqlState);
            Assert.AreEqual(1, calls);
        }
    }

    /// <summary>
    /// Managed failures preserve structured PostgreSQL diagnostics while permanently consuming the action.
    /// </summary>
    [TestMethod]
    public void CallbackFailureReturnsOwnedDiagnosticsAndCannotRunAgain()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int calls = 0;
        using PgMemoryCallback registration = PgMemoryContext.Current.RegisterResetCallback(() =>
        {
            calls++;
            throw new PgException("PZ123", "cleanup café 🐘", "owned detail", "owned hint")
            {
                SchemaName = "cleanup_schema",
                Position = 17,
            };
        });
        NativeMemoryRequest registered = GetRegistration(fixture);
        PgException error = Assert.IsInstanceOfType<PgException>(Dispatch(registered, scope.Address));
        Assert.AreEqual("PZ123", error.SqlState);
        Assert.AreEqual("cleanup café 🐘", error.Message);
        Assert.AreEqual("owned detail", error.Detail);
        Assert.AreEqual("owned hint", error.Hint);
        Assert.AreEqual("cleanup_schema", error.SchemaName);
        Assert.AreEqual(17, error.Position);
        Assert.IsFalse(registration.IsPending);
        Assert.AreEqual(1, calls);
        PgException stale = Assert.IsInstanceOfType<PgException>(Dispatch(registered, scope.Address));
        Assert.AreEqual("38000", stale.SqlState);
        Assert.AreEqual(1, calls);
        Assert.AreEqual("cleanup café 🐘", error.Message);
        Assert.AreEqual(101, PgMemoryContext.Current.Id);
    }

    /// <summary>
    /// SQL and logging bindings are masked during cleanup and restored after success or failure.
    /// </summary>
    /// <param name="fail">Whether user cleanup throws after checking the masked capabilities.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CallbackMasksInheritedBackendAndLogThenRestoresEveryBinding(bool fail)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        nint previousBackend = NativeBackend.Enter(71);
        nint previousLog = NativeLog.Enter(83);
        nint previousRead = NativeGuc.Enter(97);
        PgAggregateContext aggregate = NativeAggregate.Enter([new NativeValue { Integral = 1 }, default, default, default], 101, 103);
        try
        {
            using PgMemoryCallback registration = PgMemoryContext.Current.RegisterResetCallback(() =>
            {
                NativeBackend.CheckDisposalAccess(71);
                Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 42"));
                Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.IsEnabled(PgLogLevel.Notice));
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadInt32("test.setting"));
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Write(new PgAggregateState<int>(42)));
                Assert.AreEqual(101, PgMemoryContext.Current.Id);
                if (fail)
                {
                    throw new InvalidOperationException("cleanup failed");
                }
            });
            NativeMemoryRequest registered = GetRegistration(fixture);
            PgException? error = Dispatch(registered, scope.Address);
            if (fail)
            {
                Assert.IsNotNull(error);
                Assert.AreEqual("cleanup failed", error.Message);
            }
            else
            {
                Assert.IsNull(error);
            }

            Assert.IsFalse(registration.IsPending);
            NativeBackend.CheckAccess(71);
            NativeLog.CheckAccess();
            Assert.AreEqual(101, PgMemoryContext.Current.Id);
            nint observedBackend = NativeBackend.Enter(0);
            NativeBackend.Exit(observedBackend);
            nint observedLog = NativeLog.Enter(0);
            NativeLog.Exit(observedLog);
            nint observedRead = NativeGuc.Enter(0);
            NativeGuc.Exit(observedRead);
            Assert.AreEqual(71, observedBackend);
            Assert.AreEqual(83, observedLog);
            Assert.AreEqual(97, observedRead);
        }
        finally
        {
            NativeAggregate.Exit(aggregate);
            NativeGuc.Exit(previousRead);
            NativeLog.Exit(previousLog);
            NativeBackend.Exit(previousBackend);
        }
    }

    /// <summary>
    /// Native cleanup can provide a fresh memory envelope while restoring an explicitly disabled outer scope.
    /// </summary>
    [TestMethod]
    public void CallbackProvidesMemoryAndRestoresMaskedOuterScope()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        nint observedProvider = 0;
        using PgMemoryCallback registration = PgMemoryContext.Current.RegisterResetCallback(() => observedProvider = NativeMemoryContext.Provider);
        NativeMemoryRequest registered = GetRegistration(fixture);
        nint previous = NativeMemoryContext.Enter(0);
        try
        {
            Assert.IsNull(Dispatch(registered, scope.Address));
            Assert.AreEqual(17, observedProvider);
            Assert.ThrowsExactly<InvalidOperationException>(() => PgMemoryContext.Current);
        }
        finally
        {
            NativeMemoryContext.Exit(previous);
        }

        Assert.AreEqual(101, PgMemoryContext.Current.Id);
    }

    /// <summary>
    /// Cleanup retains its resource-release binding after the registering backend scope ends.
    /// </summary>
    [TestMethod]
    public void CallbackRetainsOwnedResourceCleanupBindingAfterRegistrationScopeEnds()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int calls = 0;
        PgMemoryCallback registration;
        nint previous = NativeBackend.Enter(71);
        try
        {
            registration = PgMemoryContext.Current.RegisterResetCallback(() =>
            {
                NativeBackend.CheckDisposalAccess(71);
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeBackend.CheckAccess(71));
                Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.IsEnabled(PgLogLevel.Notice));
                calls++;
            });
        }
        finally
        {
            NativeBackend.Exit(previous);
        }

        using (registration)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeBackend.CheckDisposalAccess(71));
            Assert.IsNull(Dispatch(GetRegistration(fixture), scope.Address));
            Assert.AreEqual(1, calls);
            Assert.IsFalse(registration.IsPending);
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeBackend.CheckDisposalAccess(71));
        }
    }

    /// <summary>
    /// Foreign providers cannot cancel or consume a pending callback, and the owning provider remains usable.
    /// </summary>
    [TestMethod]
    public void ForeignProviderRejectsCancellationAndDispatchWithoutConsumingRoot()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int calls = 0;
        using PgMemoryCallback registration = PgMemoryContext.Current.RegisterResetCallback(() => calls++);
        NativeMemoryRequest registered = GetRegistration(fixture);
        fixture.Requests.Clear();
        using (MemoryContextTestFixture.Scope foreign = MemoryContextTestFixture.Enter(29))
        {
            Assert.ThrowsExactly<InvalidOperationException>(registration.Dispose);
            PgException error = Assert.IsInstanceOfType<PgException>(Dispatch(registered, foreign.Address));
            Assert.AreEqual("38000", error.SqlState);
            Assert.Contains("provider", error.Message);
            Assert.IsTrue(registration.IsPending);
            Assert.AreEqual(0, calls);
            Assert.IsEmpty(fixture.Requests);
        }

        Assert.IsNull(Dispatch(registered, scope.Address));
        Assert.AreEqual(1, calls);
        Assert.IsFalse(registration.IsPending);
    }

    /// <summary>
    /// Retained registrations reject detached cancellation and accept a later callback from their provider.
    /// </summary>
    [TestMethod]
    public void CancellationRequiresLiveCapabilityUntilActionIsConsumed()
    {
        using var fixture = new MemoryContextTestFixture();
        using PgMemoryCallback registration = RegisterInCallback();
        Assert.ThrowsExactly<InvalidOperationException>(registration.Dispose);
        Assert.IsTrue(registration.IsPending);
        using (MemoryContextTestFixture.Enter())
        {
            registration.Dispose();
        }

        Assert.IsFalse(registration.IsPending);
        registration.Dispose();

        static PgMemoryCallback RegisterInCallback()
        {
            using (MemoryContextTestFixture.Enter())
            {
                return PgMemoryContext.Current.RegisterResetCallback(static () =>
                {
                });
            }
        }
    }

    /// <summary>
    /// A worker cannot cancel or dispatch another thread's action even with the same provider identity.
    /// </summary>
    [TestMethod]
    public void CallbackRootsAndCancellationRemainOnTheirOwningThread()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int calls = 0;
        using PgMemoryCallback registration = PgMemoryContext.Current.RegisterResetCallback(() => calls++);
        NativeMemoryRequest registered = GetRegistration(fixture);
        DedicatedThread.Run(() =>
        {
            using var workerFixture = new MemoryContextTestFixture();
            Assert.ThrowsExactly<InvalidOperationException>(registration.Dispose);
            using MemoryContextTestFixture.Scope workerScope = MemoryContextTestFixture.Enter();
            Assert.ThrowsExactly<InvalidOperationException>(registration.Dispose);
            PgException error = Assert.IsInstanceOfType<PgException>(Dispatch(registered, workerScope.Address));
            Assert.AreEqual("38000", error.SqlState);
            Assert.Contains("thread", error.Message);
            Assert.IsEmpty(workerFixture.Requests);
        });
        Assert.IsTrue(registration.IsPending);
        Assert.AreEqual(0, calls);
        Assert.IsNull(Dispatch(registered, scope.Address));
        Assert.AreEqual(1, calls);
    }

    /// <summary>
    /// Native ownership roots both the registration and captured state without any application reference.
    /// </summary>
    [TestMethod]
    public void NativeRegistrationRootsWrapperAndCaptureUntilConsumption()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        List<int> calls = [];
        (WeakReference<PgMemoryCallback> registration, WeakReference<RetainedAction> capture) = RegisterUnreferenced(PgMemoryContext.Current, calls);
        NativeMemoryRequest registered = GetRegistration(fixture);
        CollectUnreferencedValues();
        Assert.IsTrue(IsAlive(registration));
        Assert.IsTrue(IsAlive(capture));
        Assert.IsEmpty(calls);
        Assert.IsNull(Dispatch(registered, scope.Address));
        Assert.AreSequenceEqual([42], calls);
        CollectUnreferencedValues();
        Assert.IsFalse(IsAlive(registration));
        Assert.IsFalse(IsAlive(capture));
    }

    /// <summary>
    /// Successful cancellation releases captured objects while the registration wrapper remains referenced.
    /// </summary>
    [TestMethod]
    public void CancellationReleasesCaptureWhileWrapperRemainsAlive()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        List<int> calls = [];
        (PgMemoryCallback registration, WeakReference<RetainedAction> capture) = RegisterCapture(PgMemoryContext.Current, calls, false);
        using (registration)
        {
            CollectUnreferencedValues();
            Assert.IsTrue(IsAlive(capture));
            registration.Dispose();
            CollectUnreferencedValues();
            Assert.IsFalse(IsAlive(capture));
            Assert.IsFalse(registration.IsPending);
            Assert.IsEmpty(calls);
            GC.KeepAlive(registration);
        }
    }

    /// <summary>
    /// Both successful and throwing actions release their capture while the consumed wrapper remains referenced.
    /// </summary>
    /// <param name="fail">Whether the captured action throws after recording its call.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConsumptionReleasesCaptureEvenWhenActionFails(bool fail)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        List<int> calls = [];
        (PgMemoryCallback registration, WeakReference<RetainedAction> capture) = RegisterCapture(PgMemoryContext.Current, calls, fail);
        using (registration)
        {
            PgException? error = Dispatch(GetRegistration(fixture), scope.Address);
            if (fail)
            {
                Assert.IsNotNull(error);
                Assert.AreEqual("retained cleanup failed", error.Message);
            }
            else
            {
                Assert.IsNull(error);
            }

            CollectUnreferencedValues();
            Assert.IsFalse(IsAlive(capture));
            Assert.IsFalse(registration.IsPending);
            Assert.AreSequenceEqual([42], calls);
            GC.KeepAlive(registration);
        }
    }

    /// <summary>
    /// A registration failure unwinds the managed root and exposes a stale dispatcher identity afterward.
    /// </summary>
    [TestMethod]
    public void FailedRegistrationReleasesCaptureAndRejectsSavedRoot()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Handler = request => request._operation == NativeMemoryOperation.RegisterCallback
            ? throw new PgException("53200", "registration failed", "allocation detail", "retry hint")
            : fixture.Respond(request);
        List<int> calls = [];
        WeakReference<RetainedAction> capture = RegisterFailedCapture(context, calls);
        NativeMemoryRequest registered = GetRegistration(fixture);
        Assert.AreEqual(3, fixture.ErrorReleases);
        CollectUnreferencedValues();
        Assert.IsFalse(IsAlive(capture));
        PgException stale = Assert.IsInstanceOfType<PgException>(Dispatch(registered, scope.Address));
        Assert.AreEqual("38000", stale.SqlState);
        Assert.IsEmpty(calls);
        fixture.Handler = null;
        using PgMemoryCallback retry = context.RegisterResetCallback(() => calls.Add(7));
        NativeMemoryRequest retried = fixture.Requests.Last(static request => request._operation == NativeMemoryOperation.RegisterCallback);
        Assert.AreNotEqual(registered._other, retried._other);
        Assert.IsNull(Dispatch(retried, scope.Address));
        Assert.AreSequenceEqual([7], calls);
    }

    /// <summary>
    /// Finds the one registration request belonging to a single-action test.
    /// </summary>
    private static NativeMemoryRequest GetRegistration(MemoryContextTestFixture fixture)
        => Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.RegisterCallback, fixture.Requests);

    /// <summary>
    /// Executes native callback ABI and copies diagnostics before releasing every owned native field.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PgException? Dispatch(NativeMemoryRequest registration, nint memory)
    {
        var invoke = (delegate* unmanaged[Cdecl]<nint, nint, NativeCallError*, int>)registration._pointer;
        NativeCallError error = default;
        try
        {
            int status = invoke(registration._other, memory, &error);
            Assert.IsInRange(0, 1, status);
            return status == 0 ? null : error.ToException();
        }
        finally
        {
            error.Release();
        }
    }

    /// <summary>
    /// Separates capture creation from the collecting caller's stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PgMemoryCallback Registration, WeakReference<RetainedAction> Capture) RegisterCapture(PgMemoryContext context, List<int> calls, bool fail)
    {
        var action = new RetainedAction(calls, fail);
        return (context.RegisterResetCallback(action.Invoke), new WeakReference<RetainedAction>(action));
    }

    /// <summary>
    /// Discards the caller's strong reference while preserving weak observations of native-owned state.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<PgMemoryCallback> Registration, WeakReference<RetainedAction> Capture) RegisterUnreferenced(PgMemoryContext context, List<int> calls)
    {
        (PgMemoryCallback registration, WeakReference<RetainedAction> capture) = RegisterCapture(context, calls, false);
        return (new WeakReference<PgMemoryCallback>(registration), capture);
    }

    /// <summary>
    /// Observes the capture from a failed registration after its constructing stack has unwound.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<RetainedAction> RegisterFailedCapture(PgMemoryContext context, List<int> calls)
    {
        var action = new RetainedAction(calls, false);
        PgException error = Assert.ThrowsExactly<PgException>(() => context.RegisterResetCallback(action.Invoke));
        Assert.AreEqual("53200", error.SqlState);
        Assert.AreEqual("registration failed", error.Message);
        Assert.AreEqual("allocation detail", error.Detail);
        Assert.AreEqual("retry hint", error.Hint);
        return new WeakReference<RetainedAction>(action);
    }

    /// <summary>
    /// Reads liveness without extending the target's lifetime into a collecting test frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference)
        where T : class => reference.TryGetTarget(out _);

    /// <summary>
    /// Completes managed collection before testing native-root ownership.
    /// </summary>
    private static void CollectUnreferencedValues()
    {
        GarbageCollection.Collect();
    }

    /// <summary>
    /// Supplies a captured object whose lifetime and invocation are independently observable.
    /// </summary>
    private sealed class RetainedAction(List<int> calls, bool fail)
    {
        /// <summary>
        /// Records exact execution and optionally fails after the observable effect.
        /// </summary>
        internal void Invoke()
        {
            calls.Add(42);
            if (fail)
            {
                throw new InvalidOperationException("retained cleanup failed");
            }
        }
    }
}
