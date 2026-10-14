namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies guarded interrupt dispatch and callback-scoped flag lifetimes.
/// </summary>
[TestClass]
public sealed class PgInterruptsTests
{
    /// <summary>
    /// Even idle flag reads respect the prohibition on backend access while a spinlock is held.
    /// </summary>
    [TestMethod]
    public unsafe void HeldSpinlockRejectsPollingBeforeReadingFlags()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var storage = new PgSpinLock<int>(17);
        using PgSpinLockGuard<int> guard = storage.Lock();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int idle = 0;
        NativeMemoryApi* api = (NativeMemoryApi*)scope.Address;
        api->_interruptPending = &idle;
        api->_failedFrames = &idle;
        int before = fixture.Memory.Requests.Count;

        Assert.ThrowsExactly<InvalidOperationException>(PgInterrupts.Check);

        Assert.HasCount(before, fixture.Memory.Requests);
        Assert.AreEqual(17, guard.Value);
    }

    /// <summary>
    /// Only pending interrupts, retained failures or unblocked Windows signals enter native code.
    /// </summary>
    /// <param name="pending">The PostgreSQL interrupt flag.</param>
    /// <param name="failures">The active failed-frame count.</param>
    /// <param name="queue">The Windows queued signals.</param>
    /// <param name="mask">The Windows blocked signals.</param>
    /// <param name="calls">The required number of guarded transitions.</param>
    [TestMethod]
    [DataRow(0, 0, 0, 0, 0)]
    [DataRow(1, 0, 0, 0, 1)]
    [DataRow(0, 1, 0, 0, 1)]
    [DataRow(0, 2, 0, 0, 1)]
    [DataRow(0, 0, 4, 0, 1)]
    [DataRow(0, 0, 4, 4, 0)]
    [DataRow(0, 0, 12, 4, 1)]
    [DataRow(1, 1, 4, 4, 1)]
    public unsafe void FlagsSelectGuardedDispatch(int pending, int failures, int queue, int mask, int calls)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        NativeMemoryApi* api = (NativeMemoryApi*)scope.Address;
        api->_interruptPending = &pending;
        api->_failedFrames = &failures;
        api->_signalQueue = &queue;
        api->_signalMask = &mask;

        PgInterrupts.Check();

        Assert.HasCount(calls, fixture.Requests);
        if (calls != 0)
        {
            Assert.AreEqual(NativeMemoryOperation.CheckInterrupts, fixture.Requests[0]._operation);
        }
    }

    /// <summary>
    /// Every call reads fresh borrowed state, including the Unix envelope without signal pointers.
    /// </summary>
    [TestMethod]
    public unsafe void ChangedFlagsReturnToIdleWithoutAnotherTransition()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        // The runtime reads these flags through the capability, as it reads the backend's own.
        int* pending = stackalloc int[] { 0 };
        int* failures = stackalloc int[] { 0 };
        NativeMemoryApi* api = (NativeMemoryApi*)scope.Address;
        api->_interruptPending = pending;
        api->_failedFrames = failures;

        PgInterrupts.Check();
        Assert.IsEmpty(fixture.Requests);
        *pending = 1;
        PgInterrupts.Check();
        Assert.HasCount(1, fixture.Requests);
        *pending = 0;
        PgInterrupts.Check();
        Assert.HasCount(1, fixture.Requests);
        *failures = 1;
        PgInterrupts.Check();
        Assert.HasCount(2, fixture.Requests);
        *failures = 0;
        PgInterrupts.Check();
        Assert.HasCount(2, fixture.Requests);
    }

    /// <summary>
    /// A capability without fast flags still executes the checked native operation.
    /// </summary>
    [TestMethod]
    public void MissingFlagsUseNativeGuard()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgInterrupts.Check();
        Assert.AreEqual(NativeMemoryOperation.CheckInterrupts, Assert.ContainsSingle(fixture.Requests)._operation);
    }

    /// <summary>
    /// Cancellation diagnostics cross the native boundary as owned managed data and release their buffers.
    /// </summary>
    [TestMethod]
    public void GuardedCancellationRetainsDiagnosticsAndReleasesBuffers()
    {
        using var fixture = new MemoryContextTestFixture
        {
            Handler = _ => throw new PgQueryCanceledException("owned cancellation café"),
        };
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgQueryCanceledException error = Assert.ThrowsExactly<PgQueryCanceledException>(PgInterrupts.Check);
        Assert.AreEqual(PgSqlStates.QueryCanceled, error.Diagnostic.SqlState);
        Assert.AreEqual("owned cancellation café", error.Message);
        Assert.AreEqual(1, fixture.ErrorReleases);
        Assert.AreEqual(NativeMemoryOperation.CheckInterrupts, Assert.ContainsSingle(fixture.Requests)._operation);
    }

    /// <summary>
    /// Masks, nested callbacks and callback exit select the live envelope rather than retaining native addresses.
    /// </summary>
    [TestMethod]
    public unsafe void NestedCapabilitiesRestoreAndExpireFlagAccess()
    {
        using var fixture = new MemoryContextTestFixture();
        Assert.ThrowsExactly<InvalidOperationException>(PgInterrupts.Check);
        using (MemoryContextTestFixture.Scope outer = MemoryContextTestFixture.Enter())
        {
            int idle = 0;
            NativeMemoryApi* api = (NativeMemoryApi*)outer.Address;
            api->_interruptPending = &idle;
            api->_failedFrames = &idle;
            nint previous = NativeMemoryContext.Enter(0);
            try
            {
                Assert.ThrowsExactly<InvalidOperationException>(PgInterrupts.Check);
                using (MemoryContextTestFixture.Enter(29))
                {
                    PgInterrupts.Check();
                }

                Assert.ThrowsExactly<InvalidOperationException>(PgInterrupts.Check);
            }
            finally
            {
                NativeMemoryContext.Exit(previous);
            }

            PgInterrupts.Check();
            Assert.HasCount(1, fixture.Requests);
        }

        Assert.ThrowsExactly<InvalidOperationException>(PgInterrupts.Check);
        Assert.HasCount(1, fixture.Requests);
    }

    /// <summary>
    /// A managed worker cannot use native flag addresses from the backend thread.
    /// </summary>
    [TestMethod]
    public void ForeignThreadsCannotPollBackendFlags()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        DedicatedThread.Run(() => Assert.ThrowsExactly<InvalidOperationException>(PgInterrupts.Check));

        Assert.IsEmpty(fixture.Requests);
    }
}
