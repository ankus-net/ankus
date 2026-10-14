using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks aggregate metadata, checked state identities, native adoption, cleanup, and guarded ordering without PostgreSQL.
/// </summary>
[TestClass]
public sealed class PgAggregateTests
{
    [ThreadStatic]
    private static ApiFixture? s_fixture;

    /// <summary>
    /// Resolves the native aggregate owner only through the exact active callback and owning thread.
    /// </summary>
    [TestMethod]
    public void AggregateMemoryContextRequiresActiveInnermostScope()
    {
        using var fixture = new ApiFixture();
        using var memory = new MemoryContextTestFixture
        {
            Handler = static request => request._operation == NativeMemoryOperation.Callback
                ? new NativeMemoryResult { _context = 999 } : default,
        };
        using MemoryContextTestFixture.Scope binding = MemoryContextTestFixture.Enter();
        PgAggregateContext saved;
        using (var scope = new AggregateScope())
        {
            saved = scope.Context;
            Assert.AreEqual(601, saved.MemoryContext.Id);
            DedicatedThread.Run(() => Assert.ThrowsExactly<InvalidOperationException>(() => saved.MemoryContext));
            using (var nested = new AggregateScope(owner: 202))
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => saved.MemoryContext);
                Assert.AreEqual(702, nested.Context.MemoryContext.Id);
            }

            Assert.AreEqual(601, saved.MemoryContext.Id);
            fixture.MemoryIdentityMissing = true;
            Assert.ThrowsExactly<InvalidOperationException>(() => saved.MemoryContext);
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => saved.MemoryContext);
    }

    /// <summary>
    /// New wrappers retain exact payloads for ordinary managed use and reject null payloads independently of SQL NULL wrappers.
    /// </summary>
    [TestMethod]
    public void UnattachedStateRetainsPayloadAndSqlNullRemainsDistinct()
    {
        var payload = new Probe("value");
        var state = new PgAggregateState<Probe>(payload);
        Assert.AreSame(payload, state.Value);
        Assert.AreEqual(0, new PgAggregateState<int>(0).Value);
        Assert.ThrowsExactly<ArgumentNullException>(() => new PgAggregateState<string>(null!));
        NativeValue nullState = NativeAggregate.Write<Probe>(null);
        Assert.AreEqual((byte)1, nullState.IsNull);
        Assert.AreEqual(0L, nullState.Integral);
        Assert.IsNull(NativeAggregate.Read<Probe>(nullState));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Write(state));
        Assert.AreSame(payload, state.Value);
        DedicatedThread.Run(() => Assert.AreSame(payload, state.Value));
        Assert.AreEqual(0, payload.DisposeCount);
    }

    /// <summary>
    /// Context metadata copies multiple sort keys and preserves OIDs, resjunk positions, sharedness and NULL ordering independently.
    /// </summary>
    [TestMethod]
    public void ContextOwnsExactMetadataAndImmutableSortKeys()
    {
        using var fixture = new ApiFixture();
        NativeValue[] metadata = Metadata();
        PgAggregateContext context = NativeAggregate.Enter(metadata, 101, ApiPointer);
        try
        {
            metadata[2].Integral = 0;
            metadata[4].Integral = 99;
            Assert.AreEqual(PgAggregateContextKind.Aggregate, context.Kind);
            Assert.IsTrue(context.IsStateShared);
            Assert.AreEqual(100U, context.CollationOid);
            Assert.AreEqual(8100U, context.AggregateOid);
            Assert.HasCount(2, context.SortKeys);
            Assert.AreEqual(0, context.SortKeys[0].ArgumentIndex);
            Assert.AreEqual(25U, context.SortKeys[0].TypeOid);
            Assert.AreEqual(664U, context.SortKeys[0].OperatorOid);
            Assert.AreEqual(101U, context.SortKeys[0].CollationOid);
            Assert.IsTrue(context.SortKeys[0].NullsFirst);
            Assert.AreEqual(7, context.SortKeys[1].ArgumentIndex);
            Assert.AreEqual(23U, context.SortKeys[1].TypeOid);
            Assert.AreEqual(97U, context.SortKeys[1].OperatorOid);
            Assert.AreEqual(0U, context.SortKeys[1].CollationOid);
            Assert.IsFalse(context.SortKeys[1].NullsFirst);
            IList<PgAggregateSortKey> keys = Assert.IsInstanceOfType<IList<PgAggregateSortKey>>(context.SortKeys);
            Assert.ThrowsExactly<NotSupportedException>(() => keys[0] = keys[1]);
        }
        finally
        {
            NativeAggregate.Exit(context);
        }

        Assert.AreEqual(8100U, context.AggregateOid);
        Assert.AreEqual(664U, context.SortKeys[0].OperatorOid);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Compare("a", "b"));
    }

    /// <summary>
    /// Window and unshared contexts preserve absent aggregate identity and support empty ordering metadata.
    /// </summary>
    [TestMethod]
    [DataRow(1L)]
    [DataRow(2L)]
    public void EmptyOrderingAndAbsentAggregateIdentityArePreserved(long kind)
    {
        using var fixture = new ApiFixture();
        using var scope = new AggregateScope(metadata: [Scalar(kind), Scalar(0), Scalar(uint.MaxValue), Scalar(0)]);
        Assert.AreEqual((PgAggregateContextKind)kind, scope.Context.Kind);
        Assert.IsFalse(scope.Context.IsStateShared);
        Assert.AreEqual(uint.MaxValue, scope.Context.CollationOid);
        Assert.IsNull(scope.Context.AggregateOid);
        Assert.IsEmpty(scope.Context.SortKeys);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => scope.Context.Compare(1, 2));
        Assert.AreEqual(0, fixture.CompareCalls);
    }

    /// <summary>
    /// The largest valid catalog identities and executor-only argument positions remain exact at the scalar boundaries.
    /// </summary>
    [TestMethod]
    public void MetadataPreservesUnsignedAndArgumentIndexBoundaries()
    {
        using var fixture = new ApiFixture();
        using var scope = new AggregateScope(metadata:
        [
            Scalar(1), Scalar(0), Scalar(uint.MaxValue), Scalar(uint.MaxValue),
            Scalar(int.MaxValue), Scalar(uint.MaxValue), Scalar(uint.MaxValue), Scalar(uint.MaxValue), Scalar(0),
        ]);
        Assert.AreEqual(uint.MaxValue, scope.Context.AggregateOid);
        Assert.AreEqual(uint.MaxValue, scope.Context.CollationOid);
        PgAggregateSortKey key = Assert.ContainsSingle(scope.Context.SortKeys);
        Assert.AreEqual(int.MaxValue, key.ArgumentIndex);
        Assert.AreEqual(uint.MaxValue, key.TypeOid);
        Assert.AreEqual(uint.MaxValue, key.OperatorOid);
        Assert.AreEqual(uint.MaxValue, key.CollationOid);
        Assert.IsFalse(key.NullsFirst);
    }

    /// <summary>
    /// Malformed scalar partitions cannot create contexts or change the currently active parent.
    /// </summary>
    [TestMethod]
    [DataRow(0, 0L)]
    [DataRow(0, 3L)]
    [DataRow(1, -1L)]
    [DataRow(1, 2L)]
    [DataRow(2, -1L)]
    [DataRow(2, 4294967296L)]
    [DataRow(3, -1L)]
    [DataRow(3, 4294967296L)]
    [DataRow(4, -1L)]
    [DataRow(4, 2147483648L)]
    [DataRow(5, 0L)]
    [DataRow(5, 4294967296L)]
    [DataRow(6, 0L)]
    [DataRow(6, -1L)]
    [DataRow(7, 4294967296L)]
    [DataRow(8, 2L)]
    public void InvalidMetadataScalarsLeaveTheParentActive(int slot, long value)
    {
        using var fixture = new ApiFixture();
        using var parent = new AggregateScope();
        NativeValue[] metadata = Metadata();
        metadata[slot].Integral = value;
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Enter(metadata, 202, ApiPointer));
        fixture.Comparison = -1;
        Assert.AreEqual(-1, parent.Context.Compare("z", "a"));
        Assert.AreEqual(1, fixture.CompareCalls);
    }

    /// <summary>
    /// The context envelope requires complete scalar groups and nonzero native owner and API bindings.
    /// </summary>
    [TestMethod]
    [DataRow(0, 101L, 1L)]
    [DataRow(3, 101L, 1L)]
    [DataRow(5, 101L, 1L)]
    [DataRow(8, 101L, 1L)]
    [DataRow(4, 0L, 1L)]
    [DataRow(4, 101L, 0L)]
    public void InvalidEnvelopeShapeAndBindingsAreRejected(int count, long owner, long api)
    {
        NativeValue[] metadata = [.. Metadata().AsSpan(0, count)];
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Enter(metadata, (nint)owner, (nint)api));
    }

    /// <summary>
    /// Context metadata rejects NULL, byte buffers and auxiliary tags instead of reading their integral union field.
    /// </summary>
    [TestMethod]
    [DataRow("null")]
    [DataRow("buffer")]
    [DataRow("auxiliary1")]
    [DataRow("auxiliary2")]
    [DataRow("infinity")]
    [DataRow("length")]
    public void MetadataHeadersRejectConflictingValues(string defect)
    {
        NativeValue[] metadata = Metadata();
        switch (defect)
        {
            case "null":
                metadata[2] = new NativeValue { IsNull = 1 };
                break;
            case "buffer":
                metadata[2] = NativeValue.FromString("text");
                metadata[2].Integral = 100;
                Length(ref metadata[2]) = 0;
                break;
            case "auxiliary1":
                Auxiliary1(ref metadata[2]) = 1;
                break;
            case "auxiliary2":
                Auxiliary2(ref metadata[2]) = 1;
                break;
            case "infinity":
                Infinity(ref metadata[2]) = 1;
                break;
            case "length":
                Length(ref metadata[2]) = 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }

        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Enter(metadata, 101, ApiPointer));
        }
        finally
        {
            metadata[2].Release();
        }
    }

    /// <summary>
    /// New state is visible by its checked ID before native adoption and later writes reuse the exact adopted pointer.
    /// </summary>
    [TestMethod]
    public void StateIsRootedBeforeAdoptionAndReusesItsNativeIdentity()
    {
        using var fixture = new ApiFixture();
        using var scope = new AggregateScope();
        var payload = new Probe("payload");
        var state = new PgAggregateState<Probe>(payload);
        fixture.DuringAdoption = id => Assert.AreSame(state, NativeAggregate.Read<Probe>(Scalar(id)));
        NativeValue first = NativeAggregate.Write(state);
        Registration registration = fixture.Last!;
        Assert.AreEqual((byte)0, first.IsNull);
        Assert.AreEqual(registration.Pointer, first.Integral);
        Assert.AreNotEqual(registration.Id, first.Integral);
        Assert.AreSame(state, NativeAggregate.Read<Probe>(Scalar(registration.Id)));
        Assert.AreSame(payload, state.Value);
        NativeValue second = NativeAggregate.Write(state);
        Assert.AreEqual(first.Integral, second.Integral);
        Assert.AreEqual(1, fixture.AdoptionCalls);
        Assert.AreEqual(0, payload.DisposeCount);
        Assert.IsNull(Release(registration));
        Assert.AreEqual(1, payload.DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => state.Value);
        AssertInvalidState(() => NativeAggregate.Read<Probe>(Scalar(registration.Id)));
        Assert.ThrowsExactly<ObjectDisposedException>(() => NativeAggregate.Write(state));
        Assert.IsNotNull(Release(registration));
        Assert.AreEqual(1, payload.DisposeCount);
    }

    /// <summary>
    /// A state ID retains its exact managed payload type and cannot be confused with SQL NULL, a native pointer, or a stale ID.
    /// </summary>
    [TestMethod]
    public void StateReadsRejectWrongTypesAndUnregisteredIdentities()
    {
        using var fixture = new ApiFixture();
        using var scope = new AggregateScope();
        var state = new PgAggregateState<int>(42);
        NativeValue pointer = NativeAggregate.Write(state);
        Registration registration = fixture.Last!;
        Assert.ThrowsExactly<InvalidCastException>(() => NativeAggregate.Read<long>(Scalar(registration.Id)));
        AssertInvalidState(() => NativeAggregate.Read<int>(Scalar(0)));
        AssertInvalidState(() => NativeAggregate.Read<int>(Scalar(-1)));
        AssertInvalidState(() => NativeAggregate.Read<int>(Scalar(long.MaxValue)));
        AssertInvalidState(() => NativeAggregate.Read<int>(pointer));
        Assert.AreEqual(42, NativeAggregate.Read<int>(Scalar(registration.Id))!.Value);
        NativeValue badNull = new()
        {
            IsNull = 1,
            Integral = registration.Id
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Read<int>(badNull));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Read<int>(new NativeValue { IsNull = 2 }));
    }

    /// <summary>
    /// The last valid identity is returned once and exhaustion never wraps or permits reuse, without mutating the process counter.
    /// </summary>
    [TestMethod]
    public void StateIdentityExhaustionRemainsPermanentlySaturated()
    {
        long counter = (long)nint.MaxValue - 1;
        Assert.AreEqual(nint.MaxValue, NativeAggregate.AllocateStateId(ref counter));
        Assert.AreEqual(nint.MaxValue, counter);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.AllocateStateId(ref counter));
        Assert.AreEqual(nint.MaxValue, counter);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.AllocateStateId(ref counter));
        Assert.AreEqual(nint.MaxValue, counter);
    }

    /// <summary>
    /// Combine can read a live right-hand state but must return a copied wrapper instead of adopting another owner's state.
    /// </summary>
    [TestMethod]
    public void BorrowedStateRequiresACopyBeforeCrossOwnerReturn()
    {
        using var fixture = new ApiFixture();
        using var outer = new AggregateScope(101);
        var original = new PgAggregateState<int>(42);
        NativeValue originalPointer = NativeAggregate.Write(original);
        Registration originalRegistration = fixture.Last!;
        using (new AggregateScope(202))
        {
            PgAggregateState<int> borrowed = NativeAggregate.Read<int>(Scalar(originalRegistration.Id))!;
            Assert.AreSame(original, borrowed);
            Assert.AreEqual(42, borrowed.Value);
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Write(borrowed));
            var copy = new PgAggregateState<int>(borrowed.Value + 1);
            NativeValue copiedPointer = NativeAggregate.Write(copy);
            Assert.AreNotEqual(originalPointer.Integral, copiedPointer.Integral);
            Assert.AreEqual(43, copy.Value);
            Assert.AreEqual(42, original.Value);
        }

        Assert.AreEqual(originalPointer.Integral, NativeAggregate.Write(original).Integral);
        Assert.AreEqual(2, fixture.AdoptionCalls);
    }

    /// <summary>
    /// Failed native adoption invalidates and disposes the root, preserving native diagnostics and allowing a later registration.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AdoptionFailureReleasesRootAndRecovers(bool missingPointer)
    {
        using var fixture = new ApiFixture { FailAdoption = !missingPointer, MissingPointer = missingPointer };
        using var scope = new AggregateScope();
        var payload = new Probe("failure", () => Assert.IsNotNull(Release(fixture.Last!)));
        var state = new PgAggregateState<Probe>(payload);
        if (missingPointer)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Write(state));
        }
        else
        {
            PgException error = Assert.ThrowsExactly<PgException>(() => NativeAggregate.Write(state));
            Assert.AreEqual("53200", error.SqlState);
            Assert.AreEqual("adoption failed", error.Message);
        }

        Registration failed = fixture.Last!;
        Assert.AreEqual(1, payload.DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => state.Value);
        AssertInvalidState(() => NativeAggregate.Read<Probe>(Scalar(failed.Id)));
        Assert.IsNotNull(Release(failed));
        Assert.AreEqual(1, payload.DisposeCount);
        fixture.FailAdoption = false;
        fixture.MissingPointer = false;
        var replacement = new PgAggregateState<int>(17);
        NativeAggregate.Write(replacement);
        Assert.IsGreaterThan(failed.Id, fixture.Last!.Id);
        Assert.AreEqual(17, replacement.Value);
    }

    /// <summary>
    /// Registration and payload cleanup failures retain both diagnostics without keeping the failed root alive.
    /// </summary>
    [TestMethod]
    public void AdoptionAndCleanupFailuresPreserveBothErrors()
    {
        using var fixture = new ApiFixture { FailAdoption = true };
        using var scope = new AggregateScope();
        var payload = new Probe("failure", () => throw new ArgumentException("cleanup failed"));
        var state = new PgAggregateState<Probe>(payload);
        AggregateException error = Assert.ThrowsExactly<AggregateException>(() => NativeAggregate.Write(state));
        Assert.HasCount(2, error.InnerExceptions);
        PgException adoption = Assert.IsInstanceOfType<PgException>(error.InnerExceptions[0]);
        Assert.AreEqual("53200", adoption.SqlState);
        Assert.AreEqual("cleanup failed", error.InnerExceptions[1].Message);
        Assert.AreEqual(1, payload.DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => state.Value);
        AssertInvalidState(() => NativeAggregate.Read<Probe>(Scalar(fixture.Last!.Id)));
    }

    /// <summary>
    /// Reset invalidates state before disposal and suspends aggregate/native query access, then restores the enclosing bindings.
    /// </summary>
    [TestMethod]
    public void ReleaseInvalidatesBeforeCleanupAndRestoresScopes()
    {
        using var fixture = new ApiFixture();
        using var backend = new BackendScope();
        using var scope = new AggregateScope();
        PgAggregateState<Probe>? state = null;
        var payload = new Probe("cleanup", () =>
        {
            Assert.ThrowsExactly<ObjectDisposedException>(() => state!.Value);
            PgException? duplicate = Release(fixture.Last!);
            Assert.IsNotNull(duplicate);
            Assert.AreEqual("38000", duplicate.SqlState);
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Read<Probe>(Scalar(fixture.Last!.Id)));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Write(new PgAggregateState<int>(0)));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Enter(Metadata(), 202, ApiPointer));
            Assert.ThrowsExactly<InvalidOperationException>(() => scope.Context.Compare("a", "b"));
            Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 1"));
        });

        state = new PgAggregateState<Probe>(payload);
        NativeAggregate.Write(state);
        Registration registration = fixture.Last!;
        Assert.IsNull(Release(registration));
        Assert.AreEqual(1, payload.DisposeCount);
        Assert.AreEqual(0, fixture.BackendCalls);
        Assert.AreEqual(0, scope.Context.Compare("same", "same"));
        PgException error = Assert.ThrowsExactly<PgException>(() => Spi.Execute("SELECT 1"));
        Assert.AreEqual("P0001", error.SqlState);
        Assert.AreEqual(1, fixture.BackendCalls);
    }

    /// <summary>
    /// A throwing cleanup callback still invalidates once and restores the active child context after releasing a parent's state.
    /// </summary>
    [TestMethod]
    public void CleanupFailureDoesNotRetainStateOrLoseNestedContext()
    {
        using var fixture = new ApiFixture();
        using var parent = new AggregateScope(101);
        var payload = new Probe("error", () => throw new PgException("22003", "cleanup overflow"));
        var state = new PgAggregateState<Probe>(payload);
        NativeAggregate.Write(state);
        Registration registration = fixture.Last!;
        using (var child = new AggregateScope(202))
        {
            PgException? error = Release(registration);
            Assert.IsNotNull(error);
            Assert.AreEqual("22003", error.SqlState);
            Assert.AreEqual("cleanup overflow", error.Message);
            Assert.AreEqual(1, payload.DisposeCount);
            Assert.ThrowsExactly<ObjectDisposedException>(() => state.Value);
            AssertInvalidState(() => NativeAggregate.Read<Probe>(Scalar(registration.Id)));
            Assert.AreEqual(0, child.Context.Compare(1, 1, 1));
            Assert.ThrowsExactly<InvalidOperationException>(() => parent.Context.Compare(1, 1, 1));
            NativeAggregate.Write(new PgAggregateState<int>(9));
            Assert.IsGreaterThan(registration.Id, fixture.Last!.Id);
        }

        Assert.AreEqual(0, parent.Context.Compare(1, 1, 1));
        Assert.IsNotNull(Release(registration));
        Assert.AreEqual(1, payload.DisposeCount);
    }

    /// <summary>
    /// The checked registry alone roots an unreferenced state and releases its payload after native reset.
    /// </summary>
    [TestMethod]
    public void NativeOwnershipRootsPayloadAcrossGarbageCollections()
    {
        using var fixture = new ApiFixture();
        using var scope = new AggregateScope();
        WeakReference<Probe> weak = CreateRootedPayload();
        Registration registration = fixture.Last!;
        Collect();
        Assert.AreEqual("rooted", ReadWeakPayload(weak));
        Assert.IsNull(Release(registration));
        Collect();
        Assert.IsFalse(weak.TryGetTarget(out _));
        AssertInvalidState(() => NativeAggregate.Read<Probe>(Scalar(registration.Id)));
    }

    /// <summary>
    /// Attached wrappers and root IDs stay on their owner thread, while process-wide IDs prevent collisions with a worker registry.
    /// </summary>
    [TestMethod]
    public void WorkerCannotReadReturnOrReleaseAnotherThreadsState()
    {
        using var fixture = new ApiFixture();
        using var scope = new AggregateScope();
        var payload = new Probe("owner");
        var state = new PgAggregateState<Probe>(payload);
        NativeAggregate.Write(state);
        Registration ownerRegistration = fixture.Last!;
        DedicatedThread.Run(() =>
        {
            using var workerFixture = new ApiFixture();
            using var workerScope = new AggregateScope(202);
            Assert.AreEqual(8100U, scope.Context.AggregateOid);
            Assert.ThrowsExactly<InvalidOperationException>(() => state.Value);
            AssertInvalidState(() => NativeAggregate.Read<Probe>(Scalar(ownerRegistration.Id)));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Write(state));
            Assert.ThrowsExactly<InvalidOperationException>(() => scope.Context.Compare("a", "b"));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Exit(scope.Context));
            Assert.IsNotNull(Release(ownerRegistration));
            NativeAggregate.Write(new PgAggregateState<int>(7));
            Assert.IsGreaterThan(ownerRegistration.Id, workerFixture.Last!.Id);
            Assert.AreEqual(7, NativeAggregate.Read<int>(Scalar(workerFixture.Last.Id))!.Value);
        });

        Assert.AreEqual("owner", state.Value.Text);
        Assert.AreEqual(0, payload.DisposeCount);
        Assert.IsNull(Release(ownerRegistration));
        Assert.AreEqual(1, payload.DisposeCount);
    }

    /// <summary>
    /// Nested callbacks require their innermost context and restore metadata/ordering after normal, invalid and exceptional exits.
    /// </summary>
    [TestMethod]
    public void NestedContextsRestoreParentsAndRejectStaleOperations()
    {
        using var fixture = new ApiFixture();
        PgAggregateContext parent = NativeAggregate.Enter(Metadata(), 101, ApiPointer);
        try
        {
            PgAggregateContext child = NativeAggregate.Enter(Metadata(), 202, ApiPointer);
            try
            {
                Assert.AreSame(parent, child.Parent);
                Assert.ThrowsExactly<InvalidOperationException>(() => parent.Compare("a", "b"));
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Exit(parent));
                Assert.AreSame(parent, child.Parent);
                Assert.AreEqual(0, child.Compare("a", "b"));
            }
            finally
            {
                NativeAggregate.Exit(child);
            }

            Assert.IsNull(child.Parent);
            Assert.ThrowsExactly<InvalidOperationException>(() => child.Compare("a", "b"));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Exit(child));
            Assert.AreEqual(0, parent.Compare("a", "b"));
            Assert.ThrowsExactly<ArgumentException>(ThrowFromChild);
            Assert.AreEqual(0, parent.Compare("a", "b"));
        }
        finally
        {
            NativeAggregate.Exit(parent);
        }

        Assert.IsNull(parent.Parent);
        Assert.ThrowsExactly<InvalidOperationException>(() => parent.Compare("a", "b"));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeAggregate.Exit(parent));
    }

    /// <summary>
    /// Ordering delegates its exact key and typed values to native code instead of applying managed comparison semantics.
    /// </summary>
    [TestMethod]
    [DataRow(-7)]
    [DataRow(0)]
    [DataRow(9)]
    [DataRow(int.MinValue)]
    [DataRow(int.MaxValue)]
    public void ComparePreservesNativeSignAndTypedOperands(int result)
    {
        using var fixture = new ApiFixture { Comparison = result };
        using var scope = new AggregateScope();
        Assert.AreEqual(result, scope.Context.Compare(9, 2, 1));
        Assert.AreEqual(1, fixture.CompareCalls);
        Assert.AreEqual(1, fixture.SortKey);
        Assert.AreEqual(23U, fixture.LeftType);
        Assert.AreEqual(23U, fixture.RightType);
        Assert.AreEqual(9, fixture.Left);
        Assert.AreEqual(2, fixture.Right);
    }

    /// <summary>
    /// Text and SQL NULL parameters preserve exact values and type identity for native collation and NULL-order handling.
    /// </summary>
    [TestMethod]
    public void ComparePreservesUnicodeAndSqlNullOperands()
    {
        using var fixture = new ApiFixture { Comparison = 1 };
        using var scope = new AggregateScope();
        Assert.AreEqual(1, scope.Context.Compare<string?>(null, "é 😀"));
        Assert.AreEqual(25U, fixture.LeftType);
        Assert.AreEqual(25U, fixture.RightType);
        Assert.IsNull(fixture.Left);
        Assert.AreEqual("é 😀", fixture.Right);
        Assert.AreEqual(0, fixture.SortKey);
        fixture.Comparison = -1;
        Assert.AreEqual(-1, scope.Context.Compare<string?>("é 😀", null));
        Assert.AreEqual("é 😀", fixture.Left);
        Assert.IsNull(fixture.Right);
        fixture.Comparison = 0;
        Assert.AreEqual(0, scope.Context.Compare<string?>(null, null));
        Assert.IsNull(fixture.Left);
        Assert.IsNull(fixture.Right);
    }

    /// <summary>
    /// Invalid key selection fails before native dispatch; native comparison errors retain SQL diagnostics and permit later calls.
    /// </summary>
    [TestMethod]
    public void CompareRejectsInvalidKeysAndRecoversAfterNativeError()
    {
        using var fixture = new ApiFixture();
        using var scope = new AggregateScope();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => scope.Context.Compare(1, 2, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => scope.Context.Compare(1, 2, 2));
        Assert.ThrowsExactly<ArgumentException>(() => scope.Context.Compare("left", "bad\0text"));
        Assert.AreEqual(0, fixture.CompareCalls);
        fixture.FailComparison = true;
        PgException error = Assert.ThrowsExactly<PgException>(() => scope.Context.Compare("é", "a"));
        Assert.AreEqual("22003", error.SqlState);
        Assert.AreEqual("comparison failed", error.Message);
        Assert.AreEqual("controlled native failure", error.Detail);
        fixture.FailComparison = false;
        fixture.Comparison = -1;
        Assert.AreEqual(-1, scope.Context.Compare("é", "a"));
        Assert.AreEqual(2, fixture.CompareCalls);
    }

    /// <summary>
    /// A native pointer-width comparison result cannot be silently narrowed outside the public Int32 return contract.
    /// </summary>
    [TestMethod]
    public void CompareValidatesTheNativeResultWidth()
    {
        using var fixture = new ApiFixture { Comparison = nint.MaxValue };
        using var scope = new AggregateScope();
        if (nint.Size > sizeof(int))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => scope.Context.Compare(1, 2, 1));
        }
        else
        {
            Assert.AreEqual(int.MaxValue, scope.Context.Compare(1, 2, 1));
        }

        fixture.Comparison = 0;
        Assert.AreEqual(0, scope.Context.Compare(1, 1, 1));
        Assert.AreEqual(2, fixture.CompareCalls);
    }

    /// <summary>
    /// Explicit parameters retain a declared composite domain identity independently of SQL NULL or a base tuple's physical descriptor.
    /// </summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ExplicitParametersPreserveCompositeIdentityAndNulls(bool leftNull, bool rightNull)
    {
        using var fixture = new ApiFixture { Comparison = -1 };
        NativeValue[] metadata = Metadata();
        metadata[5] = Scalar(9200);
        using var scope = new AggregateScope(metadata: metadata);
        PgTupleAttributeInfo[] attributes = [new("number", 23, 23, -1, 0)];
        var descriptor = new PgTupleDescriptor(9100, -1, attributes);
        var domain = new PgTupleDescriptor(9200, -1, attributes, 9100);
        PgHeapTuple? left = leftNull ? null : new PgHeapTuple(descriptor, [37]);
        PgHeapTuple? right = rightNull ? null : new PgHeapTuple(descriptor, [62]);
        Assert.AreEqual(-1, scope.Context.Compare(SpiParameter.Create(left, domain), SpiParameter.Create(right, domain)));
        Assert.AreEqual(9200U, fixture.LeftType);
        Assert.AreEqual(9200U, fixture.RightType);
        Assert.AreEqual(1, fixture.CompareCalls);
        if (leftNull)
        {
            Assert.IsNull(fixture.Left);
        }
        else
        {
            PgHeapTuple actual = Assert.IsInstanceOfType<PgHeapTuple>(fixture.Left);
            Assert.AreEqual(9100U, actual.Descriptor.TypeOid);
            Assert.AreEqual(37, actual.Get<int>(0));
            Assert.AreNotSame(left, actual);
        }

        if (rightNull)
        {
            Assert.IsNull(fixture.Right);
        }
        else
        {
            PgHeapTuple actual = Assert.IsInstanceOfType<PgHeapTuple>(fixture.Right);
            Assert.AreEqual(9100U, actual.Descriptor.TypeOid);
            Assert.AreEqual(62, actual.Get<int>(0));
            Assert.AreNotSame(right, actual);
        }
    }

    /// <summary>
    /// Explicit array descriptors retain named type identity for SQL NULL, empty arrays and nonempty arrays containing only NULLs.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void ExplicitParametersPreserveCompositeArrayIdentityAndShape(int state)
    {
        using var fixture = new ApiFixture { TupleArrayTypeOid = 9101, Comparison = 1 };
        using var backend = new BackendScope();
        NativeValue[] metadata = Metadata();
        metadata[5] = Scalar(9101);
        using var scope = new AggregateScope(metadata: metadata);
        var descriptor = new PgTupleDescriptor(9100, -1, [new("number", 23, 23, -1, 0)]);
        PgArray<PgHeapTuple?>? left = state switch
        {
            0 => null,
            1 => descriptor.CreateArray([]),
            _ => descriptor.CreateArray([null, null], [2], [-3]),
        };
        PgArray<PgHeapTuple?> right = descriptor.CreateArray([new PgHeapTuple(descriptor, [19])]);
        Assert.AreEqual(1, scope.Context.Compare(SpiParameter.CreateArray(left, descriptor), SpiParameter.CreateArray(right, descriptor)));
        Assert.AreEqual(9101U, fixture.LeftType);
        Assert.AreEqual(9101U, fixture.RightType);
        Assert.AreEqual(9100U, fixture.ArrayElementTypeOid);
        Assert.AreEqual(2, fixture.BackendCalls);
        if (state == 0)
        {
            Assert.IsNull(fixture.Left);
        }
        else
        {
            PgArray<PgHeapTuple?> actual = Assert.IsInstanceOfType<PgArray<PgHeapTuple?>>(fixture.Left);
            Assert.AreEqual(9100U, actual.ElementTypeOid);
            Assert.AreEqual(state == 1 ? 0 : 2, actual.Count);
            Assert.AreEqual(state == 1 ? 0 : 1, actual.Rank);
            if (state == 2)
            {
                Assert.AreEqual(2, actual.Lengths[0]);
                Assert.AreEqual(-3, actual.LowerBounds[0]);
                Assert.IsNull(actual[0]);
                Assert.IsNull(actual[1]);
            }
        }

        PgArray<PgHeapTuple?> actualRight = Assert.IsInstanceOfType<PgArray<PgHeapTuple?>>(fixture.Right);
        Assert.AreEqual(9100U, actualRight.ElementTypeOid);
        Assert.AreEqual(19, actualRight[0]!.Get<int>(0));
    }

    /// <summary>
    /// Explicit operands share exact-scope/key validation and recover from malformed parameters, conversion failures and native errors.
    /// </summary>
    [TestMethod]
    public void ExplicitParametersValidateBindingsAndRecover()
    {
        using var fixture = new ApiFixture();
        PgAggregateContext context = NativeAggregate.Enter(Metadata(), 101, ApiPointer);
        SpiParameter left = SpiParameter.Create("left");
        SpiParameter right = SpiParameter.Create("right");
        try
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.Compare(default(SpiParameter), right));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.Compare(left, default(SpiParameter)));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.Compare(left, right, -1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.Compare(left, right, 2));
            Assert.ThrowsExactly<ArgumentException>(() => context.Compare(left, SpiParameter.Create("bad\0text")));
            DedicatedThread.Run(() => Assert.ThrowsExactly<InvalidOperationException>(() => context.Compare(left, right)));
            using (new AggregateScope(202))
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => context.Compare(left, right));
            }

            Assert.AreEqual(0, fixture.CompareCalls);
            fixture.FailComparison = true;
            PgException error = Assert.ThrowsExactly<PgException>(() => context.Compare(left, right));
            Assert.AreEqual("22003", error.SqlState);
            Assert.AreEqual("controlled native failure", error.Detail);
            fixture.FailComparison = false;
            fixture.Comparison = -1;
            Assert.AreEqual(-1, context.Compare(left, right));
            Assert.AreEqual(2, fixture.CompareCalls);
            Assert.AreEqual("left", fixture.Left);
            Assert.AreEqual("right", fixture.Right);
        }
        finally
        {
            NativeAggregate.Exit(context);
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => context.Compare(left, right));
        Assert.AreEqual(2, fixture.CompareCalls);
    }

    /// <summary>
    /// Creates a plain scalar aggregate metadata slot.
    /// </summary>
    private static NativeValue Scalar(long value) => new() { Integral = value };

    /// <summary>
    /// Verifies rejection of an internal value that does not identify a live aggregate state.
    /// </summary>
    private static void AssertInvalidState(Action action)
    {
        PgException error = Assert.ThrowsExactly<PgException>(action);
        Assert.AreEqual("55000", error.SqlState);
        Assert.AreEqual("Invalid or expired Ankus aggregate state", error.Message);
    }

    /// <summary>
    /// Creates independent aggregate context metadata with distinct collation, operator, type and argument identities.
    /// </summary>
    private static NativeValue[] Metadata() =>
    [
        Scalar(1), Scalar(1), Scalar(100), Scalar(8100),
        Scalar(0), Scalar(25), Scalar(664), Scalar(101), Scalar(1),
        Scalar(7), Scalar(23), Scalar(97), Scalar(0), Scalar(0),
    ];

    /// <summary>
    /// Gets the controlled guarded aggregate API pointer.
    /// </summary>
    private static unsafe nint ApiPointer => (nint)(delegate* unmanaged[Cdecl]<int, void*, void*, void**, NativeSpiParameter*, int, NativeCallError*, int>)&Api;

    /// <summary>
    /// Gets the controlled guarded SPI pointer used to prove cleanup access and backend restoration.
    /// </summary>
    private static unsafe nint BackendPointer => (nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Backend;

    /// <summary>
    /// Simulates native adoption, comparison, and owner lookup while returning managed test failures as owned diagnostics.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Api(int operation, void* handle, void* release, void** output,
        NativeSpiParameter* values, int sortKey, NativeCallError* error)
    {
        try
        {
            ApiFixture fixture = s_fixture ?? throw new InvalidOperationException("No native aggregate fixture is active.");
            if (operation == 0)
            {
                fixture.AdoptionCalls++;
                nint id = (nint)handle;
                var registration = new Registration(id, (nint)release, id + 0x1000000, Environment.CurrentManagedThreadId);
                fixture.Last = registration;
                fixture.DuringAdoption?.Invoke(id);
                if (fixture.FailAdoption)
                {
                    throw new PgException("53200", "adoption failed");
                }

                if (!fixture.MissingPointer)
                {
                    fixture.Registrations.Add(registration);
                    *output = (void*)registration.Pointer;
                }

                return 0;
            }

            if (operation == 2 && handle != null && release == null && values == null)
            {
                *output = fixture.MemoryIdentityMissing ? null : (void*)((nint)handle + 500);
                return 0;
            }

            if (operation != 1 || values == null || handle != null || release != null)
            {
                throw new InvalidOperationException("Unexpected native aggregate operation contract.");
            }

            fixture.CompareCalls++;
            fixture.SortKey = sortKey;
            fixture.LeftType = values[0]._typeOid;
            fixture.RightType = values[1]._typeOid;
            fixture.Left = SpiType.FromNative(values[0]._value, values[0]._typeOid);
            fixture.Right = SpiType.FromNative(values[1]._value, values[1]._typeOid);
            if (fixture.FailComparison)
            {
                throw new PgException("22003", "comparison failed", "controlled native failure");
            }

            *output = (void*)fixture.Comparison;
            return 0;
        }
        // codeql[cs/catch-of-all-exceptions] This stands in for native code, so every exception goes back as an error instead of unwinding through it.
        catch (Exception exception)
        {
            NativeError.Write(exception, error);
            return 1;
        }
    }

    /// <summary>
    /// Resolves a controlled composite array identity or returns a sentinel when ordinary SPI dispatch reaches the backend binding.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Backend(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
    {
        try
        {
            ApiFixture fixture = s_fixture!;
            fixture.BackendCalls++;
            if (fixture.TupleArrayTypeOid != 0 && request->_operation == SpiOperation.Tuple && request->_scalarOperation == 2)
            {
                if (request->_parameterCount != 1 || request->_parameters[0]._typeOid != 26)
                {
                    throw new InvalidOperationException("Invalid composite array identity lookup.");
                }

                fixture.ArrayElementTypeOid = (uint)request->_parameters[0]._value.Integral;
                result->_text = Scalar(fixture.TupleArrayTypeOid);
                return 0;
            }

            throw new PgException("P0001", "backend reached");
        }
        // codeql[cs/catch-of-all-exceptions] This stands in for native code, so every exception goes back as an error instead of unwinding through it.
        catch (Exception exception)
        {
            NativeError.Write(exception, error);
            return 1;
        }
    }

    /// <summary>
    /// Invokes the native reset callback and copies/releases its owned diagnostics without reusing released state IDs.
    /// </summary>
    private static unsafe PgException? Release(Registration registration)
    {
        if (registration.ThreadId == Environment.CurrentManagedThreadId)
        {
            registration.Released = true;
        }

        var release = (delegate* unmanaged[Cdecl]<void*, NativeCallError*, nint, nint, int>)registration.Callback;
        NativeCallError error = default;
        try
        {
            return release((void*)registration.Id, &error, BackendPointer, 0) == 0 ? null : error.ToException();
        }
        finally
        {
            error.Release();
        }
    }

    /// <summary>
    /// Simulates generated callback unwinding after a user exception.
    /// </summary>
    private static void ThrowFromChild()
    {
        using var child = new AggregateScope(202);
        throw new ArgumentException("child callback failed");
    }

    /// <summary>
    /// Creates a payload whose only strong owner after return is the aggregate root registry.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Probe> CreateRootedPayload()
    {
        var payload = new Probe("rooted");
        NativeAggregate.Write(new PgAggregateState<Probe>(payload));
        return new WeakReference<Probe>(payload);
    }

    /// <summary>
    /// Observes a rooted payload without extending its strong lifetime into the caller's subsequent collection.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? ReadWeakPayload(WeakReference<Probe> weak) => weak.TryGetTarget(out Probe? payload) ? payload.Text : null;

    /// <summary>
    /// Completes managed collection cycles while ownership tests hold no unintentional strong references.
    /// </summary>
    private static void Collect()
    {
        GarbageCollection.Collect();
    }

    /// <summary>
    /// Accesses the first scalar discriminator for malformed transport fixtures.
    /// </summary>
    private static ref int Auxiliary1(ref NativeValue value) => ref value._auxiliary1;

    /// <summary>
    /// Accesses the second scalar discriminator for malformed transport fixtures.
    /// </summary>
    private static ref int Auxiliary2(ref NativeValue value) => ref value._auxiliary2;

    /// <summary>
    /// Accesses the temporal discriminator for malformed transport fixtures.
    /// </summary>
    private static ref int Infinity(ref NativeValue value) => ref value._temporalInfinity;

    /// <summary>
    /// Accesses the buffer length for malformed transport fixtures.
    /// </summary>
    private static ref int Length(ref NativeValue value) => ref value._length;

    /// <summary>
    /// Enters and exits a controlled aggregate callback scope.
    /// </summary>
    private sealed class AggregateScope(nint owner = 101, NativeValue[]? metadata = null) : IDisposable
    {
        /// <summary>
        /// Gets the active aggregate context.
        /// </summary>
        internal PgAggregateContext Context { get; } = NativeAggregate.Enter(metadata ?? Metadata(), owner, ApiPointer);

        /// <summary>
        /// Restores the enclosing context.
        /// </summary>
        public void Dispose() => NativeAggregate.Exit(Context);
    }

    /// <summary>
    /// Binds controlled SPI dispatch and restores the enclosing native binding.
    /// </summary>
    private sealed class BackendScope : IDisposable
    {
        private readonly nint _previous = NativeBackend.Enter(BackendPointer);

        /// <summary>
        /// Restores the previous backend callback.
        /// </summary>
        public void Dispose() => NativeBackend.Exit(_previous);
    }

    /// <summary>
    /// Tracks payload disposal and optionally runs assertions or throws from user cleanup.
    /// </summary>
    private sealed class Probe(string text, Action? cleanup = null) : IDisposable
    {
        /// <summary>
        /// Gets the exact payload value.
        /// </summary>
        internal string Text { get; } = text;

        /// <summary>
        /// Gets the number of times payload cleanup ran.
        /// </summary>
        internal int DisposeCount
        {
            get;
            private set;
        }

        /// <summary>
        /// Records cleanup before invoking any user action.
        /// </summary>
        public void Dispose()
        {
            DisposeCount++;
            cleanup?.Invoke();
        }
    }

    /// <summary>
    /// Represents a controlled native header and the root/reset callback captured during adoption.
    /// </summary>
    private sealed class Registration(nint id, nint callback, nint pointer, int threadId)
    {
        /// <summary>
        /// Gets the managed registry ID.
        /// </summary>
        internal nint Id { get; } = id;

        /// <summary>
        /// Gets the unmanaged release callback.
        /// </summary>
        internal nint Callback { get; } = callback;

        /// <summary>
        /// Gets the simulated native header pointer.
        /// </summary>
        internal nint Pointer { get; } = pointer;

        /// <summary>
        /// Gets the thread that owns the native header.
        /// </summary>
        internal int ThreadId { get; } = threadId;

        /// <summary>
        /// Gets or sets whether the owning native reset was invoked.
        /// </summary>
        internal bool Released
        {
            get;
            set;
        }
    }

    /// <summary>
    /// Owns controlled native registrations so all tests reset their states even after assertion failures.
    /// </summary>
    private sealed class ApiFixture : IDisposable
    {
        private readonly ApiFixture? _previous = s_fixture;

        /// <summary>
        /// Installs this thread's controlled native API behavior.
        /// </summary>
        internal ApiFixture() => s_fixture = this;

        /// <summary>
        /// Gets the successfully adopted native registrations.
        /// </summary>
        internal List<Registration> Registrations { get; } = [];

        /// <summary>
        /// Gets or sets the most recent registration attempt, including failures.
        /// </summary>
        internal Registration? Last
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets an action invoked while the managed root must already be registered.
        /// </summary>
        internal Action<nint>? DuringAdoption
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether native adoption reports a controlled PostgreSQL error.
        /// </summary>
        internal bool FailAdoption
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether native adoption incorrectly succeeds without a header pointer.
        /// </summary>
        internal bool MissingPointer
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether resolving aggregate storage returns an invalid zero identity.
        /// </summary>
        internal bool MemoryIdentityMissing
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether native comparison reports a controlled PostgreSQL error.
        /// </summary>
        internal bool FailComparison
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the comparison result returned by the native ordering API.
        /// </summary>
        internal nint Comparison
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the number of native adoption calls.
        /// </summary>
        internal int AdoptionCalls
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the number of native comparisons.
        /// </summary>
        internal int CompareCalls
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the number of SPI calls that reached the native guard.
        /// </summary>
        internal int BackendCalls
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets a named composite array OID returned by the controlled catalog lookup, or zero to reject lookup.
        /// </summary>
        internal uint TupleArrayTypeOid
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the composite element OID supplied to the controlled catalog lookup.
        /// </summary>
        internal uint ArrayElementTypeOid
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the selected ordering key passed to native code.
        /// </summary>
        internal int SortKey
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the left operand's declared PostgreSQL type.
        /// </summary>
        internal uint LeftType
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the right operand's declared PostgreSQL type.
        /// </summary>
        internal uint RightType
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the copied left native comparison operand.
        /// </summary>
        internal object? Left
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the copied right native comparison operand.
        /// </summary>
        internal object? Right
        {
            get;
            set;
        }

        /// <summary>
        /// Resets every adopted state and restores the previous thread fixture.
        /// </summary>
        public void Dispose()
        {
            try
            {
                foreach (Registration registration in Registrations.Where(static registration => !registration.Released))
                {
                    if (Release(registration) is PgException error)
                    {
                        throw error;
                    }
                }
            }
            finally
            {
                s_fixture = _previous;
            }
        }
    }
}
