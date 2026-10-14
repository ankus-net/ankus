using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Exercises managed relation contracts against an independently scripted native ABI.
/// </summary>
[TestClass]
public sealed unsafe class RelationTests
{
    [ThreadStatic]
    private static Fixture? s_fixture;

    /// <summary>
    /// Invalid locks, lossy names, detached access and null raw pointers have explicit outcomes.
    /// </summary>
    [TestMethod]
    public void ValidationPrecedesAcquisition()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => PgRelation.Open(42));
        foreach (PgLockMode mode in new[] { PgLockMode.None, (PgLockMode)(-1), (PgLockMode)9 })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgRelation.Open(42, mode));
        }

        Assert.ThrowsExactly<ArgumentNullException>(() => PgRelation.Open(null!));
        Assert.ThrowsExactly<ArgumentException>(() => PgRelation.Open("a\0b"));
        Assert.ThrowsExactly<System.Text.EncoderFallbackException>(() => PgRelation.Open("\ud800"));
        Assert.IsNull(PgRelation.DangerousBorrow(null));
        Assert.IsNull(PgRelation.DangerousAdopt(null));
    }

    /// <summary>
    /// Every lock maps to its native numeric identity and disposal is idempotent.
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    public void OpenPreservesLockAndIdentity(int mode)
    {
        using var fixture = new Fixture();
        using PgRelation relation = PgRelation.Open(uint.MaxValue, (PgLockMode)mode);
        Assert.AreEqual((byte)35, (byte)fixture.Requests[0]._operation);
        Assert.AreEqual(1, fixture.Requests[0]._scalarOperation);
        Assert.AreEqual(mode, fixture.Requests[0]._limit);
        Assert.AreEqual(uint.MaxValue, fixture.Requests[0]._functionOid);
        Assert.AreEqual(uint.MaxValue, relation.Oid);
        Assert.AreEqual("café 🐘", relation.Name);
        Assert.AreEqual(2200U, relation.NamespaceOid);
        Assert.AreEqual("schema 名", relation.NamespaceName);
        Assert.AreEqual(-1F, relation.EstimatedTupleCount);
        fixture.Estimate = 0;
        Assert.IsNull(relation.EstimatedTupleCount);
        fixture.Estimate = 1.25F;
        Assert.AreEqual(1.25F, relation.EstimatedTupleCount);
        relation.Dispose();
        relation.Dispose();
        Assert.AreEqual(1, fixture.Closes);
        Assert.IsEmpty(fixture.Active);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = relation.Oid);
    }

    /// <summary>
    /// Names reach the native resolver unchanged and missing names remain distinguishable from errors.
    /// </summary>
    [TestMethod]
    public void NamesAndMissingResults()
    {
        using var fixture = new Fixture();
        using PgRelation relation = PgRelation.Open("\"schema 名\".\"café 🐘\"");
        Assert.AreEqual("\"schema 名\".\"café 🐘\"", fixture.Name);
        Assert.AreEqual(2, fixture.Requests[0]._scalarOperation);
        Assert.AreEqual(1, fixture.Requests[0]._limit);
        fixture.Missing = true;
        Assert.IsNull(PgRelation.TryOpen("missing"));
        Assert.AreEqual((byte)1, fixture.Requests[^1]._readOnly);
        Assert.AreEqual("42P01", Assert.ThrowsExactly<PgException>(() => PgRelation.Open("missing")).SqlState);
        Assert.AreEqual(3, fixture.Releases);
    }

    /// <summary>
    /// Native kind characters retain exact predicates, including partitioned indexes and future kinds.
    /// </summary>
    [TestMethod]
    [DataRow('r', 0)]
    [DataRow('m', 1)]
    [DataRow('i', 2)]
    [DataRow('v', 3)]
    [DataRow('S', 4)]
    [DataRow('c', 5)]
    [DataRow('f', 6)]
    [DataRow('p', 7)]
    [DataRow('t', 8)]
    [DataRow('I', -1)]
    [DataRow('?', -1)]
    public void KindsRemainExact(char kind, int selected)
    {
        using var fixture = new Fixture { Kind = kind };
        using PgRelation relation = PgRelation.Open(42);
        Assert.AreEqual(kind, relation.Kind);
        bool[] flags = [relation.IsTable, relation.IsMaterializedView, relation.IsIndex, relation.IsView,
            relation.IsSequence, relation.IsCompositeType, relation.IsForeignTable, relation.IsPartitionedTable, relation.IsToast];
        for (int index = 0; index < flags.Length; index++)
        {
            Assert.AreEqual(index == selected, flags[index]);
        }
    }

    /// <summary>
    /// Clone, unsafe opens, raw borrowing and ownership transfer carry distinct native requests.
    /// </summary>
    [TestMethod]
    public void OwnershipOperationsRemainDistinct()
    {
        using var fixture = new Fixture();
        using PgRelation relation = PgRelation.DangerousOpenWithoutLock(42);
        Assert.AreEqual(0, fixture.Requests[0]._limit);
        using PgRelation clone = relation.Clone();
        Assert.AreNotEqual(relation.Identity, clone.Identity);
        Assert.AreEqual(1, fixture.Requests[^1]._limit);
        Assert.AreEqual(42U, clone.Oid);
        Assert.AreEqual<nint>(0x12340, (nint)relation.DangerousGetPointer());
        using PgRelation borrowed = PgRelation.DangerousBorrow((void*)0x12340)!;
        Assert.AreEqual(3, fixture.Requests[^1]._scalarOperation);
        Assert.AreEqual<nint>(0x12340, fixture.Requests[^1]._callback);
        long identity = borrowed.Identity;
        using PgRelation adopted = borrowed.DangerousTakeOwnership();
        Assert.AreEqual(5, fixture.Requests[^1]._scalarOperation);
        Assert.AreEqual(identity, adopted.Identity);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = borrowed.Name);
        Assert.AreSame(adopted, adopted.DangerousTakeOwnership());
        borrowed.Dispose();
        Assert.AreEqual(0, fixture.Closes);
        using PgRelation raw = PgRelation.DangerousAdopt((void*)0x98760)!;
        Assert.AreEqual(4, fixture.Requests[^1]._scalarOperation);
    }

    /// <summary>
    /// Heap and index access open independent references, and a later index failure closes every prior acquisition.
    /// </summary>
    [TestMethod]
    public void IndexAcquisitionCleansPartialResults()
    {
        using var fixture = new Fixture { Indexes = [7, 99, 8] };
        using PgRelation relation = PgRelation.Open(42);
        Assert.IsNull(relation.DangerousOpenHeap());
        fixture.Heap = 9;
        using (PgRelation heap = relation.DangerousOpenHeap()!)
        {
            Assert.AreEqual(0, fixture.Requests[^1]._limit);
            Assert.AreEqual(9U, heap.Oid);
        }

        Assert.AreEqual("42P01", Assert.ThrowsExactly<PgException>(() => relation.GetIndices()).SqlState);
        Assert.AreEqual(2, fixture.Closes);
        Assert.HasCount(1, fixture.Active);
        fixture.Indexes = [7, 8];
        IReadOnlyList<PgRelation> indexes = relation.GetIndices(PgLockMode.Share);
        Assert.AreSequenceEqual<uint>([7, 8], indexes.Select(static item => item.Oid));
        foreach (PgRelation item in indexes)
        {
            item.Dispose();
        }

        Assert.HasCount(1, fixture.Active);
        Assert.AreEqual(42U, relation.Oid);
    }

    /// <summary>
    /// Every statistics method selects its own native macro and preserves a signed 64-bit tuple count.
    /// </summary>
    [TestMethod]
    public void StatisticsPreserveOperationAndSignedCount()
    {
        using var fixture = new Fixture();
        using PgRelation relation = PgRelation.Open(42);
        relation.CountHeapScan();
        relation.CountIndexScan();
        relation.CountHeapGetNext();
        relation.CountHeapFetch();
        relation.CountIndexTuples(long.MinValue);
        relation.CountBufferRead();
        relation.CountBufferHit();
        Assert.AreSequenceEqual(Enumerable.Range(16, 7), fixture.Requests.Skip(1).Select(static request => request._scalarOperation));
        Assert.AreEqual(long.MinValue, fixture.Count);
    }

    /// <summary>
    /// Reads reject a different backend and abort cleanup, while close uses the guarded cleanup-only path.
    /// </summary>
    [TestMethod]
    public void BackendAndAbortBoundaries()
    {
        using var fixture = new Fixture();
        PgRelation relation = PgRelation.Open(42);
        nint previous = NativeBackend.Enter(123);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = relation.Oid);
            Assert.ThrowsExactly<InvalidOperationException>(relation.Dispose);
        }
        finally
        {
            NativeBackend.Exit(previous);
        }

        previous = NativeBackend.Enter(Fixture.Entry, abortCleanup: true);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = relation.Oid);
            relation.Dispose();
            Assert.AreEqual((byte)1, fixture.Requests[^1]._cleanupOnly);
            Assert.AreEqual(0L, fixture.Requests[^1]._sessionId);
        }
        finally
        {
            NativeBackend.Exit(previous, abortCleanup: true);
        }

        Assert.IsEmpty(fixture.Active);
        Assert.AreEqual(1, fixture.Closes);
    }

    /// <summary>
    /// Detached regclass cells keep their exact nominal identity and acquire a reference only for an explicit typed read.
    /// </summary>
    [TestMethod]
    public void RegclassReadsAcquireOnlyOnDemand()
    {
        using var fixture = new Fixture();
        object cell = SpiType.FromNative(new NativeValue { Integral = 42 }, 2205)!;
        Assert.IsEmpty(fixture.Active);
        Assert.ThrowsExactly<InvalidCastException>(() => SpiRow.Convert<uint>(cell));
        Assert.ThrowsExactly<InvalidCastException>(() => SpiRow.Convert<PgRelation>(42U));
        using PgRelation relation = SpiRow.Convert<PgRelation>(cell);
        Assert.AreEqual(42U, relation.Oid);
        Assert.AreEqual(2205U, SpiType.GetOid<PgRelation>());
        Assert.AreEqual(2210U, SpiType.GetOid<PgRelation?[]>());
        Assert.AreEqual(2210U, SpiType.GetOid<PgArray<PgRelation?>>());
        Assert.AreEqual(42L, NativeValue.FromRelation(relation).Integral);
        Assert.IsNull(SpiRow.Convert<PgRelation?>(SpiType.FromNative(new NativeValue { IsNull = 1 }, 2205)));
        Assert.HasCount(1, fixture.Active);
    }

    /// <summary>
    /// Failed relation element conversion and vector shape rejection release acquired references without losing NULLs.
    /// </summary>
    [TestMethod]
    public void RelationArraysCleanUpAndPreserveShape()
    {
        using var fixture = new Fixture();
        var source = new PgArray<PgRelationIdentity?>([new(7), null, new(8), new(9)], [2, 2], [-3, 4]);
        NativeValue native = NativeValue.FromArray(source);
        try
        {
            IPgArray detached = native.ReadArray();
            Assert.IsEmpty(fixture.Active);
            Assert.ThrowsExactly<InvalidOperationException>(() => SpiArray.Convert(detached, typeof(PgRelation[])));
            Assert.IsEmpty(fixture.Active);
            using var scope = new NativeRelationScope();
            PgArray<PgRelation?> converted = scope.Add(native.ReadArray<PgRelation?>());
            Assert.AreEqual(7U, converted[0]!.Oid);
            Assert.IsNull(converted[1]);
            Assert.AreEqual(8U, converted[2]!.Oid);
            Assert.AreEqual(9U, converted[3]!.Oid);
            Assert.AreSequenceEqual<int>([2, 2], converted.Lengths.ToArray());
            Assert.AreSequenceEqual<int>([-3, 4], converted.LowerBounds.ToArray());
        }
        finally
        {
            native.Release();
        }

        Assert.IsEmpty(fixture.Active);
        source = new PgArray<PgRelationIdentity?>([new(7), new(99)]);
        native = NativeValue.FromArray(source);
        try
        {
            Assert.ThrowsExactly<PgException>(() => native.ReadArray<PgRelation>());
        }
        finally
        {
            native.Release();
        }

        Assert.IsEmpty(fixture.Active);
        Assert.AreEqual(4, fixture.Closes);
    }

    /// <summary>
    /// Scope transfer retains input references until iterator disposal, including disposal before the first advance.
    /// </summary>
    [TestMethod]
    public void IteratorOwnsTransferredArguments()
    {
        using var fixture = new Fixture();
        using var scope = new NativeRelationScope();
        PgRelation relation = scope.Add(PgRelation.Open(42));
        nint iterator = NativeSet.Create(Observe(relation), scope.Detach());
        scope.Dispose();
        Assert.AreEqual(42U, relation.Oid);
        Assert.IsTrue(NativeSet.MoveNext(iterator, out uint value));
        Assert.AreEqual(42U, value);
        NativeSet.Dispose(ref iterator);
        Assert.IsEmpty(fixture.Active);
        Assert.AreEqual(1, fixture.Closes);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = relation.Oid);
        using var neverStarted = new NativeRelationScope();
        relation = neverStarted.Add(PgRelation.Open(7));
        iterator = NativeSet.Create(Observe(relation), neverStarted.Detach());
        NativeSet.Dispose(ref iterator);
        Assert.IsEmpty(fixture.Active);
        Assert.AreEqual(2, fixture.Closes);
    }

    /// <summary>
    /// Returning input handles from a set preserves them until iterator cleanup, while fresh output references close per row.
    /// </summary>
    [TestMethod]
    public void YieldedArgumentsRemainOwnedByIterator()
    {
        using var fixture = new Fixture();
        using var input = new NativeRelationScope();
        PgRelation relation = input.Add(PgRelation.Open(42));
        nint iterator = NativeSet.Create(Observe(relation), input.Detach());
        using (NativeRelationScope output = NativeRelationScope.ForIterator(iterator))
        {
            output.Add(new PgRelation?[] { relation, relation.Clone(), null });
            Assert.HasCount(2, fixture.Active);
        }

        Assert.HasCount(1, fixture.Active);
        Assert.AreEqual(42U, relation.Oid);
        NativeSet.Dispose(ref iterator);
        Assert.IsEmpty(fixture.Active);
        Assert.AreEqual(2, fixture.Closes);
    }

    /// <summary>
    /// Multi-column SPI conversion closes earlier scalar and array references when a later column is incompatible.
    /// </summary>
    [TestMethod]
    public void SpiScalarConversionReleasesEarlierColumns()
    {
        using var fixture = new Fixture();
        SpiColumn[] columns = [new("a", 2210), new("r", 2205), new("bad", 25)];
        object?[] cells = [new PgArray<PgRelationIdentity?>([new(7), null, new(8)]), new PgRelationIdentity(9), "wrong"];
        var scalar = new SpiScalarResult(new SpiResult(columns, [new SpiRow(cells, columns)], 1), null);
        Assert.ThrowsExactly<InvalidCastException>(() => scalar.Read(static values =>
            (values.Get<PgRelation?[]>(0), values.Get<PgRelation>(1), values.Get<int>(2))));
        Assert.IsEmpty(fixture.Active);
        Assert.AreEqual(3, fixture.Closes);
        (PgRelation?[] array, PgRelation relation) = scalar.Read(static values =>
            (values.Get<PgRelation?[]>(0), values.Get<PgRelation>(1)));
        using var scope = new NativeRelationScope();
        scope.Add(array);
        scope.Add(relation);
        Assert.AreEqual(7U, array[0]!.Oid);
        Assert.IsNull(array[1]);
        Assert.AreEqual(8U, array[2]!.Oid);
        Assert.AreEqual(9U, relation.Oid);
        Assert.HasCount(3, fixture.Active);
    }

    /// <summary>
    /// Edited rows and tuples keep detached regclass identities after the supplied handles close, including shape and NULLs.
    /// </summary>
    [TestMethod]
    public void EditedCellsCopyRelationIdentities()
    {
        using var fixture = new Fixture();
        using PgRelation original = PgRelation.Open(42);
        SpiColumn[] columns = [new("r", 2205), new("a", 2210), new("v", 2210)];
        var row = new SpiRow(new object?[3], columns);
        var shaped = new PgArray<PgRelation?>([original, null], [1, 2], [-3, 4]);
        PgRelation?[] vector = [original, null];
        row.Set("r", original);
        row.Set("a", shaped);
        row.Set("v", vector);
        var descriptor = new PgTupleDescriptor(2249, -1,
            [new("r", 2205, 2205, -1, 0), new("a", 2210, 2210, -1, 0)]);
        var tuple = new PgHeapTuple(descriptor, [original, shaped]);
        tuple.Set("r", original);
        tuple.Set("a", SpiParameter.Create(shaped));
        original.Dispose();
        vector[0] = null;
        Assert.IsEmpty(fixture.Active);
        Assert.AreEqual(2205U, row.GetTypeOid("r"));
        Assert.AreEqual(2210U, row.GetTypeOid("a"));
        using var ownership = new NativeRelationScope();
        PgRelation first = ownership.Add(row.Get<PgRelation>("r"));
        PgRelation second = ownership.Add(row.Get<PgRelation>("r"));
        Assert.AreNotSame(first, second);
        first.Dispose();
        Assert.AreEqual(42U, second.Oid);
        Assert.AreEqual(42U, ownership.Add(tuple.Get<PgRelation>("r")).Oid);
        PgRelation?[] copiedVector = ownership.Add(row.Get<PgRelation?[]>("v"));
        Assert.AreEqual(42U, copiedVector[0]!.Oid);
        Assert.IsNull(copiedVector[1]);
        foreach (PgArray<PgRelation?> array in new[]
            { ownership.Add(row.Get<PgArray<PgRelation?>>("a")), ownership.Add(tuple.Get<PgArray<PgRelation?>>("a")) })
        {
            Assert.AreEqual(42U, array[0]!.Oid);
            Assert.IsNull(array[1]);
            Assert.AreSequenceEqual<int>([1, 2], array.Lengths.ToArray());
            Assert.AreSequenceEqual<int>([-3, 4], array.LowerBounds.ToArray());
        }

        Assert.ThrowsExactly<ObjectDisposedException>(() => row.Set("r", original));
        Assert.AreEqual(42U, ownership.Add(row.Get<PgRelation>("r")).Oid);
    }

    private static IEnumerable<uint> Observe(PgRelation relation)
    {
        try
        {
            yield return relation.Oid;
        }
        finally
        {
            Assert.AreNotEqual(0U, relation.Oid);
        }
    }

    /// <summary>
    /// Supplies fixed independent metadata and records requests and acquired identities through the actual ABI.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly nint _previous;
        private long _next = 100;

        /// <summary>
        /// Installs the scripted callback.
        /// </summary>
        internal Fixture()
        {
            s_fixture = this;
            _previous = NativeBackend.Enter(Entry);
        }

        /// <summary>
        /// Gets the ABI entry point.
        /// </summary>
        internal static nint Entry => (nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Execute;
        /// <summary>
        /// Gets the copied operation headers.
        /// </summary>
        internal List<NativeSpiRequest> Requests { get; } = [];
        /// <summary>
        /// Gets the currently acquired tokens and their independently supplied OIDs.
        /// </summary>
        internal Dictionary<long, uint> Active { get; } = [];
        /// <summary>
        /// Gets the exact supplied name.
        /// </summary>
        internal string? Name
        {
            get;
            private set;
        }
        /// <summary>
        /// Gets or sets the missing-name response.
        /// </summary>
        internal bool Missing
        {
            get;
            set;
        }
        /// <summary>
        /// Gets or sets the estimate supplied by the backend.
        /// </summary>
        internal float Estimate
        {
            get;
            set;
        } = -1;
        /// <summary>
        /// Gets or sets the native kind.
        /// </summary>
        internal char Kind
        {
            get;
            set;
        } = 'r';
        /// <summary>
        /// Gets or sets the index list.
        /// </summary>
        internal uint[] Indexes
        {
            get;
            set;
        } = [];
        /// <summary>
        /// Gets or sets the heap identity.
        /// </summary>
        internal uint Heap
        {
            get;
            set;
        }
        /// <summary>
        /// Gets the signed statistics operand.
        /// </summary>
        internal long Count
        {
            get;
            private set;
        }
        /// <summary>
        /// Gets the number of native close requests.
        /// </summary>
        internal int Closes
        {
            get;
            private set;
        }
        /// <summary>
        /// Gets the number of allocator-matched result releases.
        /// </summary>
        internal int Releases
        {
            get;
            private set;
        }

        /// <summary>
        /// Restores backend capability after the test.
        /// </summary>
        public void Dispose()
        {
            NativeBackend.Exit(_previous);
            s_fixture = null;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Execute(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
        {
            Fixture fixture = s_fixture!;
            fixture.Requests.Add(*request);
            result->_release = &Release;
            try
            {
                int operation = request->_scalarOperation;
                if (operation == 0)
                {
                    fixture.Closes++;
                    fixture.Active.Remove(request->_cursorId);
                }
                else if (operation is >= 1 and <= 4)
                {
                    if (operation == 2)
                    {
                        fixture.Name = request->_parameters[0]._value.ReadString();
                        if (fixture.Missing)
                        {
                            if (request->_readOnly != 0)
                            {
                                return 0;
                            }

                            throw new PgException("42P01", "missing relation");
                        }
                    }

                    if (request->_functionOid == 99)
                    {
                        throw new PgException("42P01", "missing index");
                    }

                    result->_cursorId = ++fixture._next;
                    fixture.Active.Add(result->_cursorId, request->_functionOid);
                }
                else
                {
                    uint oid = fixture.Active[request->_cursorId];
                    result->_text = operation switch
                    {
                        6 => new NativeValue { Integral = oid },
                        7 => NativeValue.FromString("café 🐘"),
                        8 => new NativeValue { Integral = 2200 },
                        9 => NativeValue.FromString("schema 名"),
                        10 => new NativeValue { Integral = fixture.Kind },
                        11 => new NativeValue { Integral = BitConverter.SingleToInt32Bits(fixture.Estimate), IsNull = fixture.Estimate is 0 ? (byte)1 : (byte)0 },
                        13 => NativeValue.FromArray(new PgArray<uint>(fixture.Indexes)),
                        14 => new NativeValue { Integral = fixture.Heap },
                        15 => new NativeValue { Integral = 0x12340 },
                        _ => default,
                    };
                    if (operation == 20)
                    {
                        fixture.Count = request->_parameters[0]._value.Integral;
                    }
                }

                return 0;
            }
            // codeql[cs/catch-of-all-exceptions] This stands in for native code, so every exception goes back as an error instead of unwinding through it.
            catch (Exception exception)
            {
                NativeError.Write(exception, error);
                return 1;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(NativeSpiResult* result)
        {
            s_fixture!.Releases++;
            result->_text.Release();
        }
    }
}
