using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies initialization's transaction-disabled backend binding and restoration of enclosing callback state.
/// </summary>
[TestClass]
public sealed class InitializationScopeTests
{
    [ThreadStatic]
    private static int s_calls;

    [ThreadStatic]
    private static int s_openedSessions;

    [ThreadStatic]
    private static int s_closedSessions;

    /// <summary>
    /// Clears the counts the static native callbacks record, before a test drives them.
    /// </summary>
    private static void ResetCounts()
    {
        s_calls = 0;
        s_openedSessions = 0;
        s_closedSessions = 0;
    }

    /// <summary>
    /// Blocks transaction-only API families before any native call when initialization has no transaction.
    /// </summary>
    [TestMethod]
    public void NontransactionalInitializationBlocksBackendEntryPoints()
    {
        ResetCounts();
        using var scope = new BackendScope(0);
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 1"));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Connect(static session => session.Execute("SELECT 1")));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Prepare("SELECT 1"));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.OpenCursor("SELECT 1"));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDate.CurrentDate);
        Assert.ThrowsExactly<InvalidOperationException>(() => PgNumeric.Parse("1.5"));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.IsEnabled(PgLogLevel.Notice));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.Write(PgLogLevel.Notice, "initializing"));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeBackend.CheckDisposalAccess());
        Assert.AreEqual(0, s_calls);
    }

    /// <summary>
    /// Nested disabled bindings restore the exact outer native callback after both ordinary and exceptional exits.
    /// </summary>
    /// <param name="fail">Whether the simulated initialization callback throws.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NestedDisabledInitializationRestoresOuterBinding(bool fail)
    {
        ResetCounts();
        using (new BackendScope(BackendPointer))
        {
            Assert.AreEqual(37L, Spi.Execute("SELECT 1"));
            nint previous = NativeBackend.Enter(0);
            Assert.AreEqual(BackendPointer, previous);
            PgException? caught = null;
            try
            {
                try
                {
                    Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 2"));
                    nint disabled = NativeBackend.Enter(0);
                    Assert.AreEqual(0, disabled);
                    try
                    {
                        Assert.ThrowsExactly<InvalidOperationException>(() => PgDate.CurrentDate);
                    }
                    finally
                    {
                        NativeBackend.Exit(disabled);
                    }

                    Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 3"));
                    if (fail)
                    {
                        throw new PgException("PZ001", "initialization failed");
                    }
                }
                finally
                {
                    NativeBackend.Exit(previous);
                }
            }
            catch (PgException exception) when (fail)
            {
                caught = exception;
            }

            if (fail)
            {
                Assert.IsNotNull(caught);
                Assert.AreEqual("PZ001", caught.SqlState);
                Assert.AreEqual("initialization failed", caught.Message);
            }
            else
            {
                Assert.IsNull(caught);
            }

            Assert.AreEqual(1, s_calls);
            Assert.AreEqual(37L, Spi.Execute("SELECT 4"));
            Assert.AreEqual(2, s_calls);
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 5"));
        Assert.AreEqual(2, s_calls);
    }

    /// <summary>
    /// Backend permission remains thread-local during initialization and worker scopes do not alter the parent binding.
    /// </summary>
    [TestMethod]
    public void InitializationBindingDoesNotFlowToWorkerThreads()
    {
        ResetCounts();
        using var outer = new BackendScope(BackendPointer);
        Assert.AreEqual(37L, Spi.Execute("SELECT 1"));
        DedicatedThread.Run(static () =>
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 2"));
            using (new BackendScope(0))
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => PgDate.CurrentDate);
            }

            Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.IsEnabled(PgLogLevel.Notice));
            Assert.AreEqual(0, s_calls);
        });
        Assert.AreEqual(37L, Spi.Execute("SELECT 3"));
        Assert.AreEqual(2, s_calls);
    }

    /// <summary>
    /// A suspended outer SPI session remains unavailable during initialization and regains its original callback depth afterward.
    /// </summary>
    [TestMethod]
    public void InitializationRestoresTheOwningSpiSession()
    {
        ResetCounts();
        using var outer = new BackendScope(BackendPointer);
        long value = Spi.Connect(session =>
        {
            Assert.AreEqual(37L, session.Execute("SELECT 1"));
            using (new BackendScope(0))
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => session.Execute("SELECT 2"));
                Assert.ThrowsExactly<InvalidOperationException>(() => session.Prepare("SELECT 3"));
            }

            return session.Execute("SELECT 4");
        });
        Assert.AreEqual(37L, value);
        Assert.AreEqual(1, s_openedSessions);
        Assert.AreEqual(1, s_closedSessions);
        Assert.AreEqual(4, s_calls);
        Assert.AreEqual(37L, Spi.Execute("SELECT 5"));
        Assert.AreEqual(5, s_calls);
    }

    /// <summary>
    /// Leaving a disabled nested scope preserves an enclosing abort-cleanup restriction and its disposal permission.
    /// </summary>
    [TestMethod]
    public void InitializationPreservesAbortCleanupRestrictions()
    {
        ResetCounts();
        using var outer = new BackendScope(BackendPointer);
        using (new BackendScope(BackendPointer, abortCleanup: true))
        {
            using (new BackendScope(0))
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeBackend.CheckDisposalAccess(BackendPointer));
                Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 1"));
            }

            NativeBackend.CheckDisposalAccess(BackendPointer);
            InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 2"));
            Assert.AreEqual("PostgreSQL queries are unavailable during aborted iterator cleanup.", error.Message);
            Assert.AreEqual(0, s_calls);
        }

        Assert.AreEqual(37L, Spi.Execute("SELECT 3"));
        Assert.AreEqual(1, s_calls);
    }

    /// <summary>
    /// Owned event metadata remains readable while its native query methods honor the disabled backend binding.
    /// </summary>
    [TestMethod]
    public void EventQueriesHonorDisabledInitializationAndRecover()
    {
        ResetCounts();
        NativeValue[] arguments = [NativeValue.FromString("ddl_command_end"), NativeValue.FromString("CREATE TABLE")];
        using var outer = new BackendScope(BackendPointer);
        PgEventTriggerContext? context = null;
        try
        {
            context = NativeEventTrigger.Enter(arguments);
            Assert.IsEmpty(context.GetDdlCommands());
            Assert.AreEqual(1, s_calls);
            using (new BackendScope(0))
            {
                Assert.AreEqual("CREATE TABLE", context.CommandTag);
                Assert.AreEqual(PgEventTriggerKind.DdlCommandEnd, context.Kind);
                Assert.ThrowsExactly<InvalidOperationException>(() => context.GetDdlCommands());
                Assert.AreEqual(1, s_calls);
            }

            Assert.IsEmpty(context.GetDdlCommands());
            Assert.AreEqual(2, s_calls);
        }
        finally
        {
            if (context is not null)
            {
                NativeEventTrigger.Exit(context);
            }

            foreach (NativeValue argument in arguments)
            {
                argument.Release();
            }
        }
    }

    /// <summary>
    /// Gets the controlled backend callback used to distinguish denied access from an actual native invocation.
    /// </summary>
    private static unsafe nint BackendPointer
        => (nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Backend;

    /// <summary>
    /// Returns a distinct processed-row value and counts session operations without allocating native buffers.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Backend(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
    {
        s_calls++;
        result->_rowsAffected = 37;
        if (request->_operation == SpiOperation.OpenSession)
        {
            request->_sessionId = 71;
            s_openedSessions++;
        }
        else if (request->_operation == SpiOperation.CloseSession)
        {
            s_closedSessions++;
        }

        return 0;
    }

    /// <summary>
    /// Balances the backend binding even when an assertion or simulated user callback throws.
    /// </summary>
    private sealed class BackendScope(nint execute, bool abortCleanup = false) : IDisposable
    {
        private readonly nint _previous = NativeBackend.Enter(execute, abortCleanup);

        /// <summary>
        /// Restores the enclosing callback and its cleanup restriction.
        /// </summary>
        public void Dispose() => NativeBackend.Exit(_previous, abortCleanup);
    }
}
