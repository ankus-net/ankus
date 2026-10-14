using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies transaction callback validation, lifetime, ordering, cancellation, and native capability scopes.
/// </summary>
[TestClass]
public sealed unsafe class PgTransactionTests
{
    [ThreadStatic]
    private static TransactionFixture? s_fixture;

    /// <summary>
    /// Rejects invalid registrations before retaining callbacks or entering native code.
    /// </summary>
    [TestMethod]
    public void RegistrationValidatesEventsCallbacksAndBackendAccess()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => PgTransaction.RegisterCallback(PgTransactionEvent.Commit, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Commit, null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PgTransaction.RegisterCallback((PgTransactionEvent)8, static () =>
            {
            }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PgTransaction.RegisterSubtransactionCallback((PgSubtransactionEvent)4, static (_, _) =>
            {
            }));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            PgTransaction.RegisterCallback(PgTransactionEvent.Commit, static () =>
            {
            }));
    }

    /// <summary>
    /// Installs each native dispatcher once per managed transaction registry and preserves its stable callback pointer.
    /// </summary>
    [TestMethod]
    public void RegistrationInstallsStableNativeDispatchers()
    {
        using var fixture = new TransactionFixture();
        using PgTransactionCallback first = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, static () =>
        {
        });
        using PgTransactionCallback second = PgTransaction.RegisterCallback(PgTransactionEvent.Commit, static () =>
        {
        });
        using PgSubtransactionCallback sub = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Start, static (_, _) =>
        {
        });
        using PgSubtransactionCallback anotherSub = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Commit, static (_, _) =>
        {
        });
        Assert.AreSequenceEqual([1, 2], fixture.DispatcherRequests);
        Assert.AreNotEqual(nint.Zero, fixture.Callback);
        Assert.IsTrue(first.IsPending);
        Assert.IsTrue(second.IsPending);
        Assert.IsTrue(sub.IsPending);
        Assert.IsTrue(anotherSub.IsPending);
    }

    /// <summary>
    /// Runs one-shot callbacks in registration order while allowing an earlier callback to cancel a later callback.
    /// </summary>
    [TestMethod]
    public void TransactionCallbacksRunOnceInOrderAndSupportCancellation()
    {
        using var fixture = new TransactionFixture();
        var events = new List<string>();
        using PgTransactionCallback first = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, () => events.Add("A"));
        PgTransactionCallback? third = null;
        using PgTransactionCallback second = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, () =>
        {
            events.Add("B");
            third!.Dispose();
        });
        third = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, () => events.Add("C"));
        Assert.IsNull(fixture.DispatchTransaction(PgTransactionEvent.PreCommit, BackendPointer));
        Assert.IsNull(fixture.DispatchTransaction(PgTransactionEvent.PreCommit, BackendPointer));
        Assert.AreSequenceEqual(["A", "B"], events);
        Assert.IsFalse(first.IsPending);
        Assert.IsFalse(second.IsPending);
        Assert.IsFalse(third.IsPending);
    }

    /// <summary>
    /// Lets a pre-commit callback register a commit callback while deferring same-event registrations until cleanup.
    /// </summary>
    [TestMethod]
    public void RegistrationDuringDispatchUsesTheRemainingTransactionPhases()
    {
        using var fixture = new TransactionFixture();
        var events = new List<string>();
        PgTransactionCallback? deferred = null;
        using PgTransactionCallback preCommit = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, () =>
        {
            events.Add("pre");
            deferred = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, () => events.Add("deferred"));
            _ = PgTransaction.RegisterCallback(PgTransactionEvent.Commit, () => events.Add("commit"));
        });
        Assert.IsNull(fixture.DispatchTransaction(PgTransactionEvent.PreCommit, BackendPointer));
        Assert.AreSequenceEqual(["pre"], events);
        Assert.IsTrue(deferred!.IsPending);
        Assert.IsNull(fixture.DispatchTransaction(PgTransactionEvent.Commit, 0));
        Assert.AreSequenceEqual(["pre", "commit"], events);
        Assert.IsFalse(deferred.IsPending);
    }

    /// <summary>
    /// Clears callbacks for mutually exclusive events and all subtransaction callbacks when the outer transaction ends.
    /// </summary>
    /// <param name="terminal">The terminal event dispatched by PostgreSQL.</param>
    [TestMethod]
    [DataRow(PgTransactionEvent.Commit)]
    [DataRow(PgTransactionEvent.Abort)]
    [DataRow(PgTransactionEvent.Prepare)]
    [DataRow(PgTransactionEvent.ParallelCommit)]
    [DataRow(PgTransactionEvent.ParallelAbort)]
    public void TerminalEventReleasesEveryUnusedRegistration(PgTransactionEvent terminal)
    {
        using var fixture = new TransactionFixture();
        var events = new List<string>();
        PgTransactionCallback[] receipts = [.. Enum.GetValues<PgTransactionEvent>().Select(@event =>
            PgTransaction.RegisterCallback(@event, () => events.Add(@event.ToString())))];
        using PgTransactionCallback second = PgTransaction.RegisterCallback(terminal, () => events.Add("second"));
        using PgSubtransactionCallback sub = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Abort,
            (_, _) => events.Add("sub"));
        Assert.IsNull(fixture.DispatchTransaction(terminal, 0));
        Assert.AreSequenceEqual([terminal.ToString(), "second"], events);
        foreach (PgTransactionCallback receipt in receipts)
        {
            Assert.IsFalse(receipt.IsPending);
            receipt.Dispose();
        }

        Assert.IsFalse(second.IsPending);
        Assert.IsFalse(sub.IsPending);
        Assert.IsNull(fixture.DispatchTransaction(terminal, 0));
        Assert.IsNull(fixture.DispatchSubtransaction(PgSubtransactionEvent.Abort, 17, 9, 0));
        Assert.AreSequenceEqual([terminal.ToString(), "second"], events);
    }

    /// <summary>
    /// Repeats subtransaction callbacks with exact IDs, excludes new registrations from the active snapshot, and honors cancellation.
    /// </summary>
    [TestMethod]
    public void SubtransactionCallbacksRepeatWithSnapshotOrderingAndExactIds()
    {
        using var fixture = new TransactionFixture();
        var events = new List<string>();
        PgSubtransactionCallback? second = null;
        PgSubtransactionCallback? added = null;
        PgSubtransactionId firstId = default;
        PgSubtransactionId firstParent = default;
        using PgSubtransactionCallback first = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Start, (id, parent) =>
        {
            firstId = id;
            firstParent = parent;
            events.Add($"A{id}:{parent}");
            second!.Dispose();
            added ??= PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Start,
                (nestedId, nestedParent) => events.Add($"C{nestedId}:{nestedParent}"));
        });
        second = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Start,
            (id, parent) => events.Add($"B{id}:{parent}"));
        Assert.IsNull(fixture.DispatchSubtransaction(PgSubtransactionEvent.Start, 17, 9, BackendPointer));
        Assert.IsNull(fixture.DispatchSubtransaction(PgSubtransactionEvent.Start, 18, 9, BackendPointer));
        Assert.AreSequenceEqual(["A17:9", "A18:9", "C18:9"], events);
        Assert.AreEqual(new PgSubtransactionId(18), firstId);
        Assert.AreEqual(new PgSubtransactionId(9), firstParent);
        Assert.IsTrue(first.IsPending);
        Assert.IsFalse(second.IsPending);
        Assert.IsTrue(added!.IsPending);
    }

    /// <summary>
    /// Converts a managed callback failure into an owned diagnostic after consuming the complete one-shot event.
    /// </summary>
    [TestMethod]
    public void TransactionCallbackFailureReleasesRemainingEventCallbacks()
    {
        using var fixture = new TransactionFixture();
        using PgTransactionCallback failing = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit,
            static () => throw new PgException("22023", "callback café", "detail", "hint"));
        using PgTransactionCallback later = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, static () => Assert.Fail("must not run"));
        PgException error = Assert.IsInstanceOfType<PgException>(fixture.DispatchTransaction(PgTransactionEvent.PreCommit, BackendPointer));
        Assert.AreEqual("22023", error.SqlState);
        Assert.AreEqual("callback café", error.Message);
        Assert.AreEqual("detail", error.Detail);
        Assert.AreEqual("hint", error.Hint);
        Assert.IsFalse(failing.IsPending);
        Assert.IsFalse(later.IsPending);
    }

    /// <summary>
    /// Keeps repeating subtransaction callbacks after a failed event until outer-transaction cleanup.
    /// </summary>
    [TestMethod]
    public void SubtransactionCallbackFailurePreservesRegistrationUntilTransactionEnd()
    {
        using var fixture = new TransactionFixture();
        int calls = 0;
        using PgSubtransactionCallback failing = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.PreCommit, (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("subtransaction failure");
        });
        Assert.IsInstanceOfType<PgException>(fixture.DispatchSubtransaction(PgSubtransactionEvent.PreCommit, 3, 1, BackendPointer));
        Assert.IsTrue(failing.IsPending);
        Assert.IsInstanceOfType<PgException>(fixture.DispatchSubtransaction(PgSubtransactionEvent.PreCommit, 4, 1, BackendPointer));
        Assert.AreEqual(2, calls);
        Assert.IsNull(fixture.DispatchTransaction(PgTransactionEvent.Abort, 0));
        Assert.IsFalse(failing.IsPending);
    }

    /// <summary>
    /// Enables guarded SQL only when the native phase supplies it and restores the enclosing backend binding.
    /// </summary>
    [TestMethod]
    public void DispatcherScopesSqlCapabilityToTheNativePhase()
    {
        using var fixture = new TransactionFixture();
        string? reversible = null;
        using PgTransactionCallback preCommit = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, () =>
        {
            Assert.AreEqual(41L, Spi.Execute("SELECT 1"));
            reversible = "sql";
        });
        Assert.IsNull(fixture.DispatchTransaction(PgTransactionEvent.PreCommit, BackendPointer));
        Assert.AreEqual("sql", reversible);
        Assert.Contains(SpiOperation.Execute, fixture.Operations);

        string? terminal = null;
        using PgTransactionCallback commit = PgTransaction.RegisterCallback(PgTransactionEvent.Commit, () =>
        {
            terminal = Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 1")).Message;
        });
        Assert.IsNull(fixture.DispatchTransaction(PgTransactionEvent.Commit, 0));
        Assert.IsNotNull(terminal);
        Assert.Contains("active PostgreSQL backend thread", terminal);
        using PgTransactionCallback next = PgTransaction.RegisterCallback(PgTransactionEvent.Abort, static () =>
        {
        });
        Assert.IsTrue(next.IsPending);
    }

    /// <summary>
    /// Retains captured objects without retaining the receipt and releases them after the selected event.
    /// </summary>
    [TestMethod]
    public void RegistryRootsDroppedReceiptsUntilDispatch()
    {
        using var fixture = new TransactionFixture();
        WeakReference payload = RegisterRootedCallback(fixture);
        Collect();
        Assert.IsTrue(payload.IsAlive);
        Assert.IsNull(fixture.DispatchTransaction(PgTransactionEvent.PreCommit, BackendPointer));
        Assert.AreEqual(1, fixture.RootedCallbackCalls);
        Collect();
        Assert.IsFalse(payload.IsAlive);
    }

    /// <summary>
    /// Rejects cancellation from another managed thread while retaining the owning backend registration.
    /// </summary>
    [TestMethod]
    public void CancellationRequiresTheOwningBackendThread()
    {
        using var fixture = new TransactionFixture();
        using PgTransactionCallback registration = PgTransaction.RegisterCallback(PgTransactionEvent.Commit, static () =>
        {
        });
        Assert.Throws<InvalidOperationException>(() => DedicatedThread.Run(registration.Dispose));
        Assert.IsTrue(registration.IsPending);
    }

    /// <summary>
    /// Rolls back managed registry creation when native dispatcher registration fails.
    /// </summary>
    [TestMethod]
    public void FailedNativeRegistrationDoesNotRetainManagedState()
    {
        using var fixture = new TransactionFixture { FailRegistration = true };
        PgException error = Assert.ThrowsExactly<PgException>(() =>
            PgTransaction.RegisterCallback(PgTransactionEvent.Commit, static () =>
            {
            }));
        Assert.AreEqual("55000", error.SqlState);
        fixture.FailRegistration = false;
        using PgTransactionCallback registration = PgTransaction.RegisterCallback(PgTransactionEvent.Commit, static () =>
        {
        });
        Assert.IsTrue(registration.IsPending);
        Assert.AreSequenceEqual([1, 1], fixture.DispatcherRequests);
    }

    /// <summary>
    /// Returns managed diagnostics for malformed native event discriminators without corrupting later dispatch.
    /// </summary>
    [TestMethod]
    public void DispatcherRejectsMalformedNativeEventsAndRecovers()
    {
        using var fixture = new TransactionFixture();
        using PgTransactionCallback registration = PgTransaction.RegisterCallback(PgTransactionEvent.Commit, static () =>
        {
        });
        PgException kind = Assert.IsInstanceOfType<PgException>(fixture.Dispatch(2, 0, 0, 0, BackendPointer));
        Assert.Contains("kind is invalid", kind.Message);
        PgException @event = Assert.IsInstanceOfType<PgException>(fixture.Dispatch(0, 8, 0, 0, BackendPointer));
        Assert.Contains("Specified argument was out of the range", @event.Message);
        Assert.IsNull(fixture.DispatchTransaction(PgTransactionEvent.Commit, 0));
        Assert.IsFalse(registration.IsPending);
    }

    private static WeakReference RegisterRootedCallback(TransactionFixture fixture)
    {
        object payload = new();
        var reference = new WeakReference(payload);
        _ = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, () =>
        {
            fixture.RootedCallbackCalls++;
            GC.KeepAlive(payload);
        });
        return reference;
    }

    private static void Collect()
    {
        GarbageCollection.Collect();
    }

    /// <summary>
    /// Gets the controlled backend transport.
    /// </summary>
    private static nint BackendPointer
        => (nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Backend;

    /// <summary>
    /// Gets the controlled callback logger.
    /// </summary>
    private static nint LogPointer
        => (nint)(delegate* unmanaged[Cdecl]<int, int, NativeCallError*, NativeCallError*, int*, int>)&Log;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Backend(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
    {
        TransactionFixture fixture = s_fixture!;
        fixture.Operations.Add(request->_operation);
        if (request->_operation == SpiOperation.TransactionCallbacks)
        {
            fixture.DispatcherRequests.Add(request->_scalarOperation);
            fixture.Callback = request->_callback;
            if (fixture.FailRegistration)
            {
                NativeError.Write(new PgException("55000", "registration failed"), error);
                return 1;
            }
        }

        result->_rowsAffected = 41;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Log(int operation, int level, NativeCallError* report, NativeCallError* error, int* enabled)
    {
        _ = level;
        _ = report;
        _ = error;
        *enabled = operation is 0 or 1 ? 1 : 0;
        return 0;
    }

    /// <summary>
    /// Owns the controlled backend binding and transaction dispatcher for one test.
    /// </summary>
    private sealed class TransactionFixture : IDisposable
    {
        private readonly TransactionFixture? _previousFixture = s_fixture;
        private readonly nint _previousBackend = NativeBackend.Enter(BackendPointer);

        /// <summary>
        /// Installs the fixture on the current managed backend thread.
        /// </summary>
        internal TransactionFixture() => s_fixture = this;

        /// <summary>
        /// Gets the registered managed callback pointer.
        /// </summary>
        internal nint Callback
        {
            get;
            set;
        }

        /// <summary>
        /// Gets the native registration requests.
        /// </summary>
        internal List<int> DispatcherRequests { get; } = [];

        /// <summary>
        /// Gets every backend operation invoked by the managed API.
        /// </summary>
        internal List<SpiOperation> Operations { get; } = [];

        /// <summary>
        /// Gets or sets whether dispatcher registration returns an owned failure.
        /// </summary>
        internal bool FailRegistration
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the dropped-receipt callback count.
        /// </summary>
        internal int RootedCallbackCalls
        {
            get;
            set;
        }

        /// <summary>
        /// Dispatches one outer-transaction event.
        /// </summary>
        /// <param name="event">The stable managed event.</param>
        /// <param name="execute">The callback-scoped backend capability.</param>
        /// <returns>The managed failure, or null.</returns>
        internal PgException? DispatchTransaction(PgTransactionEvent @event, nint execute)
            => Dispatch(0, (int)@event, 0, 0, execute);

        /// <summary>
        /// Dispatches one subtransaction event.
        /// </summary>
        /// <param name="event">The stable managed event.</param>
        /// <param name="id">The current subtransaction ID.</param>
        /// <param name="parent">The parent subtransaction ID.</param>
        /// <param name="execute">The callback-scoped backend capability.</param>
        /// <returns>The managed failure, or null.</returns>
        internal PgException? DispatchSubtransaction(PgSubtransactionEvent @event, uint id, uint parent, nint execute)
            => Dispatch(1, (int)@event, id, parent, execute);

        /// <summary>
        /// Invokes the captured Native AOT dispatcher and copies any owned failure.
        /// </summary>
        /// <param name="kind">Zero for an outer transaction or one for a subtransaction.</param>
        /// <param name="eventCode">The stable managed event value.</param>
        /// <param name="id">The current subtransaction ID.</param>
        /// <param name="parent">The parent subtransaction ID.</param>
        /// <param name="execute">The callback-scoped backend capability.</param>
        /// <returns>The managed failure, or null.</returns>
        internal PgException? Dispatch(int kind, int eventCode, uint id, uint parent, nint execute)
        {
            Assert.AreNotEqual(nint.Zero, Callback);
            var callback = (delegate* unmanaged[Cdecl]<int, int, uint, uint, NativeCallError*, nint, nint, nint, int>)Callback;
            NativeCallError error = default;
            try
            {
                return callback(kind, eventCode, id, parent, &error, execute, LogPointer, 1) == 0
                    ? null : error.ToException();
            }
            finally
            {
                error.Release();
            }
        }

        /// <summary>
        /// Clears any pending registry before restoring enclosing native state.
        /// </summary>
        public void Dispose()
        {
            if (Callback != 0)
            {
                _ = DispatchTransaction(PgTransactionEvent.Abort, 0);
            }

            s_fixture = _previousFixture;
            NativeBackend.Exit(_previousBackend);
        }
    }
}
