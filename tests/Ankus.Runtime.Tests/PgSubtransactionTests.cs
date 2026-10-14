using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies managed callback containment and recovery-scope state without modeling PostgreSQL rollback.
/// </summary>
[TestClass]
public sealed unsafe class PgSubtransactionTests
{
    [ThreadStatic]
    private static List<int>? s_statuses;

    [ThreadStatic]
    private static int s_failStage;

    /// <summary>
    /// Makes the static native callback fail at <paramref name="stage"/>, or at none when it is zero.
    /// </summary>
    private static void FailAtStage(int stage) => s_failStage = stage;

    /// <summary>
    /// Rejects null work and offline calls before executing user code.
    /// </summary>
    [TestMethod]
    public void RecoveryScopesRequireCallbackAndBackend()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => PgTransaction.RunInSubtransaction(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => PgTransaction.RunInSubtransaction<int>(null!));
        bool invoked = false;
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTransaction.RunInSubtransaction(() => invoked = true));
        Assert.IsFalse(invoked);
    }

    /// <summary>
    /// Returns results and rethrows the original managed exception only after the native callback has returned failure.
    /// </summary>
    [TestMethod]
    public void RecoveryScopesPreserveResultsAndExceptionIdentity()
    {
        nint previous = Enter();
        try
        {
            object expected = new();
            Assert.AreSame(expected, PgTransaction.RunInSubtransaction(() => expected));
            var failure = new InvalidOperationException("original managed failure");
            bool unwound = false;
            InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(() =>
                PgTransaction.RunInSubtransaction(() =>
                {
                    try
                    {
                        throw failure;
                    }
                    finally
                    {
                        unwound = true;
                    }
                }));
            Assert.AreSame(failure, actual);
            Assert.IsTrue(unwound);
            Assert.AreSequenceEqual([0, 1], s_statuses!);
            Assert.AreEqual(42, PgTransaction.RunInSubtransaction(static () => 42));
            Assert.AreSequenceEqual([0, 1, 0], s_statuses!);
        }
        finally
        {
            Exit(previous);
        }
    }

    /// <summary>
    /// Cleanup aggregates retain the original native failure and other causes after rollback.
    /// </summary>
    [TestMethod]
    public void RecoveryPreservesCombinedCleanupFailures()
    {
        using var memory = new MemoryContextTestFixture
        {
            Handler = static _ => throw new PgException("22023", "native cleanup failure")
            {
                NativeFlags = NativeErrorFlags.Unrecovered,
            },
        };
        using MemoryContextTestFixture.Scope memoryScope = MemoryContextTestFixture.Enter();
        nint previous = Enter();
        try
        {
            var primary = new InvalidOperationException("action failure");
            AggregateException? combined = null;
            PgException? cleanup = null;
            AggregateException actual = Assert.ThrowsExactly<AggregateException>(() => PgTransaction.RunInSubtransaction(() =>
            {
                cleanup = Assert.ThrowsExactly<PgException>(() => _ = PgMemoryContext.Current);
                combined = new AggregateException(primary, cleanup);
                throw combined;
            }));
            Assert.AreSame(combined, actual);
            Assert.AreSequenceEqual<Exception>([primary, cleanup!], actual.InnerExceptions);
            Assert.IsFalse(NativeSubtransaction.HasFailure);
            Assert.AreEqual(42, PgTransaction.RunInSubtransaction(static () => 42));
            Assert.AreSequenceEqual([1, 0], s_statuses!);
        }
        finally
        {
            Exit(previous);
        }
    }

    /// <summary>
    /// A swallowed raw error blocks additional native work and rolls back only the inner recovery scope.
    /// </summary>
    [TestMethod]
    public void RawFailureCannotBeSwallowedOrPoisonRecoveredParent()
    {
        using var memory = new MemoryContextTestFixture
        {
            Handler = static _ => throw new PgException("22023", "native failure", "owned detail", "retry outside"),
        };
        using MemoryContextTestFixture.Scope memoryScope = MemoryContextTestFixture.Enter();
        nint previous = Enter();
        try
        {
            int result = PgTransaction.RunInSubtransaction(() =>
            {
                PgException? original = null;
                PgException failure = Assert.ThrowsExactly<PgException>(() => PgTransaction.RunInSubtransaction(() =>
                {
                    original = Assert.ThrowsExactly<PgException>(() => NativeRawCall.Invoke(1, [], 0, 0));
                    Assert.AreSame(original, Assert.ThrowsExactly<PgException>(() => NativeRawCall.Invoke(2, [], 0, 0)));
                    Assert.AreSame(original, Assert.ThrowsExactly<PgException>(() => Spi.Execute("SELECT 1")));
                    Assert.AreSame(original, Assert.ThrowsExactly<PgException>(() => PgTransaction.RunInSubtransaction(static () => 9)));
                }));
                Assert.AreSame(original, failure);
                Assert.AreEqual("owned detail", failure.Detail);
                Assert.ContainsSingle(memory.Requests);
                memory.Handler = null;
                NativeRawCall.Invoke(3, [], 0, 0);
                return PgTransaction.RunInSubtransaction(static () => 73);
            });
            Assert.AreEqual(73, result);
            Assert.AreSequenceEqual([1, 0, 0], s_statuses!);
            Assert.HasCount(2, memory.Requests);
            Assert.AreEqual(3, memory.ErrorReleases);
        }
        finally
        {
            Exit(previous);
        }
    }

    /// <summary>
    /// Native memory transport marks a recovery scope only when the native diagnostic still requires rollback.
    /// </summary>
    /// <param name="unrecovered">Whether PostgreSQL has not yet rolled back the failed operation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MemoryFailureTransportPreservesRecoveryRequirement(bool unrecovered)
    {
        using var memory = new MemoryContextTestFixture
        {
            Handler = _ => throw new PgException("22023", "memory failure", "owned detail", "retry outside")
            {
                NativeFlags = unrecovered ? NativeErrorFlags.Unrecovered : 0,
            },
        };
        using MemoryContextTestFixture.Scope memoryScope = MemoryContextTestFixture.Enter();
        nint previous = Enter();
        try
        {
            int result = PgTransaction.RunInSubtransaction(() =>
            {
                PgException? original = null;
                int Inner()
                {
                    original = Assert.ThrowsExactly<PgException>(() => _ = PgMemoryContext.Current);
                    Assert.AreEqual(unrecovered, NativeSubtransaction.HasFailure);
                    if (unrecovered)
                    {
                        Assert.AreSame(original, Assert.ThrowsExactly<PgException>(() => Spi.Execute("SELECT 1")));
                    }

                    return 73;
                }

                if (unrecovered)
                {
                    PgException failure = Assert.ThrowsExactly<PgException>(() => PgTransaction.RunInSubtransaction(Inner));
                    Assert.AreSame(original, failure);
                    Assert.AreEqual("owned detail", failure.Detail);
                    Assert.AreEqual("retry outside", failure.Hint);
                }
                else
                {
                    Assert.AreEqual(73, PgTransaction.RunInSubtransaction(Inner));
                }

                Assert.IsFalse(NativeSubtransaction.HasFailure);
                memory.Handler = null;
                Assert.AreEqual(101, PgMemoryContext.Current.Id);
                return 42;
            });

            Assert.AreEqual(42, result);
            Assert.AreSequenceEqual(unrecovered ? [1, 0] : [0, 0], s_statuses!);
            Assert.HasCount(2, memory.Requests);
            Assert.AreEqual(3, memory.ErrorReleases);
        }
        finally
        {
            Exit(previous);
        }
    }

    /// <summary>
    /// Raw cancellation marks its managed recovery scope so caught failures cannot resume native work before rollback.
    /// </summary>
    [TestMethod]
    public void RawCancellationRemainsPendingUntilRollback()
    {
        using var memory = new MemoryContextTestFixture
        {
            Handler = static _ => throw new PgException(PgSqlStates.QueryCanceled, "raw cancellation", "owned cancellation detail"),
        };
        using MemoryContextTestFixture.Scope memoryScope = MemoryContextTestFixture.Enter();
        nint previous = Enter();
        try
        {
            PgQueryCanceledException? original = null;
            PgQueryCanceledException failure = Assert.ThrowsExactly<PgQueryCanceledException>(() => PgTransaction.RunInSubtransaction(() =>
            {
                original = Assert.ThrowsExactly<PgQueryCanceledException>(() => NativeRawCall.Invoke(1, [], 0, 0));
                Assert.IsTrue(NativeSubtransaction.HasFailure);
                Assert.AreSame(original, Assert.ThrowsExactly<PgQueryCanceledException>(() => NativeRawCall.Invoke(2, [], 0, 0)));
                Assert.AreSame(original, Assert.ThrowsExactly<PgQueryCanceledException>(() => Spi.Execute("SELECT 1")));
            }));
            Assert.AreSame(original, failure);
            Assert.AreEqual("owned cancellation detail", failure.Diagnostic.Detail);
            Assert.ContainsSingle(memory.Requests);
            Assert.AreSequenceEqual([1], s_statuses!);
            Assert.IsFalse(NativeSubtransaction.HasFailure);
        }
        finally
        {
            Exit(previous);
        }
    }

    /// <summary>
    /// Native failures before callback entry or after success retain their own diagnostics and permit another scope.
    /// </summary>
    /// <param name="stage">One fails before entry; two fails after observing callback success.</param>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void RecoveryScopesPreserveNativeBoundaryFailures(int stage)
    {
        nint previous = Enter();
        try
        {
            FailAtStage(stage);
            bool invoked = false;
            PgException failure = Assert.ThrowsExactly<PgException>(() => PgTransaction.RunInSubtransaction(() => invoked = true));
            Assert.AreEqual("55000", failure.SqlState);
            Assert.AreEqual("native scope failure", failure.Message);
            Assert.AreEqual("native boundary detail", failure.Detail);
            Assert.AreEqual(stage == 2, invoked);
            Assert.HasCount(stage - 1, s_statuses!);
            FailAtStage(0);
            Assert.AreEqual(42, PgTransaction.RunInSubtransaction(static () => 42));
        }
        finally
        {
            Exit(previous);
        }
    }

    /// <summary>
    /// Installs a synchronous native callback fixture on the current test thread.
    /// </summary>
    /// <returns>The enclosing backend capability.</returns>
    private static nint Enter()
    {
        s_statuses = [];
        return NativeBackend.Enter((nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Execute);
    }

    /// <summary>
    /// Native cancellation during recovery takes precedence over the callback's earlier managed exception.
    /// </summary>
    [TestMethod]
    public void RecoveryCancellationCannotBeReplacedByManagedFailure()
    {
        nint previous = Enter();
        try
        {
            FailAtStage(3);
            PgQueryCanceledException failure = Assert.ThrowsExactly<PgQueryCanceledException>(() =>
                PgTransaction.RunInSubtransaction(static () => throw new InvalidOperationException("earlier managed failure")));
            Assert.AreEqual("native cancellation", failure.Message);
            Assert.AreEqual(PgSqlStates.QueryCanceled, failure.Diagnostic.SqlState);
            Assert.AreEqual("recovery detail", failure.Diagnostic.Detail);
            Assert.AreSequenceEqual([1], s_statuses!);
        }
        finally
        {
            Exit(previous);
        }
    }

    /// <summary>
    /// Restores the enclosing backend capability and releases recorded fixture state.
    /// </summary>
    /// <param name="previous">The capability captured on entry.</param>
    private static void Exit(nint previous)
    {
        NativeBackend.Exit(previous);
        s_statuses = null;
        FailAtStage(0);
    }

    /// <summary>
    /// Observes the unmanaged callback status before returning a distinct native error to its managed caller.
    /// </summary>
    /// <param name="request">The borrowed recovery callback and state.</param>
    /// <param name="result">The unused result transport.</param>
    /// <param name="error">The independently generated native diagnostic.</param>
    /// <returns>Zero on success or one after observing callback failure.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Execute(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
    {
        try
        {
            Assert.AreEqual(SpiOperation.Subtransaction, request->_operation);
            if (s_failStage == 1)
            {
                NativeError.Write(new PgException("55000", "native scope failure", "native boundary detail"), error);
                return 1;
            }

            var callback = (delegate* unmanaged[Cdecl]<nint, int>)request->_callback;
            int status = callback(request->_callbackState);
            s_statuses!.Add(status);
            if (s_failStage == 3)
            {
                NativeError.Write(new PgException(PgSqlStates.QueryCanceled, "native cancellation", "recovery detail"), error);
                return 1;
            }

            if (s_failStage == 2)
            {
                NativeError.Write(new PgException("55000", "native scope failure", "native boundary detail"), error);
                return 1;
            }

            if (status != 0)
            {
                NativeError.Write(new PgException("38000", "native rollback completed"), error);
            }

            return status;
        }
        // codeql[cs/catch-of-all-exceptions] This stands in for native code, so every exception goes back as an error instead of unwinding through it.
        catch (Exception exception)
        {
            NativeError.Write(exception, error);
            return 1;
        }
    }
}
