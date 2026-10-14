using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies borrowed array validation and transport independently of native array layout.
/// </summary>
[TestClass]
public sealed partial class PgArrayViewTests
{
    /// <summary>
    /// Generated raw readers keep SQL NULL distinct from a present zero word when requesting an owned copy.
    /// </summary>
    /// <param name="absent">Whether the original transport contains SQL NULL.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GeneratedDatumCopiesPreserveNullFlags(bool absent)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new ArrayScript(fixture);
        var value = new NativeValue { Integral = 0, IsNull = absent ? (byte)1 : (byte)0 };
        ArrayType(ref value) = 23;
        PgDatum copied = value.ReadPolymorphic();
        Assert.AreEqual(absent, copied.IsNull);
        Assert.AreEqual(23U, copied.TypeOid);
        Assert.AreEqual((nuint)0, copied.DangerousGetBits());
        Assert.AreSequenceEqual<(uint, bool)>([(23, absent)], script.Copies);
        Assert.AreEqual(1, script.Releases);
    }

    /// <summary>
    /// Generated scalar inputs expire at callback exit while retained inputs use an independently owned snapshot.
    /// </summary>
    [TestMethod]
    public void GeneratedTypedArrayReadersSelectDistinctLifetimes()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new ArrayScript(fixture) { ElementType = 23 };
        PgArrayView<int?> borrowed;
        PgArrayView<int?> retained;
        using (MemoryContextTestFixture.Enter())
        {
            var value = new NativeValue { Integral = 123 };
            ArrayType(ref value) = 1007;
            borrowed = value.ReadBorrowedArray<int?>();
            retained = value.ReadOwnedArrayView<int?>();
            Assert.AreEqual(1007U, borrowed.Datum.TypeOid);
            Assert.AreEqual(1007U, retained.Datum.TypeOid);
        }

        using (borrowed)
        using (retained)
        {
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = borrowed.Datum);
            Assert.AreEqual(1007U, retained.Datum.TypeOid);
            Assert.AreEqual(3, retained.Count);
            Assert.AreSequenceEqual<(uint, bool)>([(1007, false)], script.Copies);
        }
    }

    /// <summary>
    /// Failed SPI conversions release provisional arrays and invalidate their escaped datums.
    /// </summary>
    [TestMethod]
    public void ProvisionalArrayResultsReleaseTheirOwners()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new ArrayScript(fixture);
        using var view = new PgArrayView(PgDatum.DangerousCreate(123, 1009, PgMemoryContext.Current));
        PgDatum escaped = view.Datum;
        using var scope = new SpiConversionScope();
        Assert.AreSame(view, scope.Add(view));
        var primary = new InvalidCastException("Later column failed.");
        scope.ReleaseAfterFailure(primary);
        scope.ReleaseAfterFailure(primary);
        Assert.IsEmpty(primary.Data);
        Assert.AreSequenceEqual<nint>([202], script.Deleted);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = view[0]);
        Assert.ThrowsExactly<ObjectDisposedException>(() => escaped.DangerousGetBits());
    }

    /// <summary>
    /// Resetting only the source rejects native access even when a view's child context remains alive.
    /// </summary>
    /// <param name="nested">Whether an intermediate borrowed array contributes another lifetime dependency.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BorrowedArrayRejectsSourceOnlyGenerationChanges(bool nested)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new ArrayScript(fixture);
        PgDatum source = PgDatum.DangerousCreate(123, 1009, PgMemoryContext.Current);
        using var first = new PgArrayView(source);
        using PgArrayView? second = nested ? new PgArrayView(first.Datum) : null;
        PgArrayView view = second ?? first;
        PgDatum escaped = view.Datum;
        using IEnumerator<PgDatum> cursor = view.GetEnumerator();
        script.Cells.Enqueue((7001, false, true));
        Assert.IsTrue(cursor.MoveNext());
        PgDatum cell = cursor.Current;
        Func<NativeMemoryRequest, NativeMemoryResult> previous = fixture.Handler!;
        fixture.Handler = request => request._operation == NativeMemoryOperation.CaptureGeneration && request._context == 101
            ? new NativeMemoryResult { _value = 902 }
            : previous(request);
        int operations = script.Requests.Count;
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = view[0]);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = view.Datum);
        Assert.ThrowsExactly<ObjectDisposedException>(view.GetEnumerator);
        Assert.ThrowsExactly<ObjectDisposedException>(() => cursor.MoveNext());
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = cursor.Current);
        Assert.ThrowsExactly<ObjectDisposedException>(() => escaped.DangerousGetBits());
        Assert.ThrowsExactly<ObjectDisposedException>(() => cell.DangerousGetBits());
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.DangerousGetSpan<int>());
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.DangerousGetUuidBytes());
        Assert.HasCount(operations, script.Requests);
        Assert.AreEqual(3, view.Count);
        Assert.AreEqual(1009U, view.TypeOid);
        Assert.IsEmpty(script.Deleted);
        cursor.Dispose();
        view.Dispose();
        Assert.AreSequenceEqual<nint>(nested ? [204, 203] : [203, 202], script.Deleted);
    }

    /// <summary>
    /// Null and stale inputs reject before allocating a child context or calling the array bridge.
    /// </summary>
    [TestMethod]
    public void InvalidInputsCannotAllocateBorrowedArrayOwners()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        PgDatum absent = PgDatum.DangerousCreate(0, 1007, PgMemoryContext.Current, isNull: true);
        PgDatum stale = PgDatum.DangerousCreate(123, 1007, PgMemoryContext.Current);
        Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentNullException>(() => new PgArrayView(null!)).ParamName);
        Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentException>(() => new PgArrayView(absent)).ParamName);
        fixture.Handler = static _ => new NativeMemoryResult { _value = 902 };
        Assert.ThrowsExactly<ObjectDisposedException>(() => new PgArrayView(stale));
        Assert.DoesNotContain(static request => request._operation == NativeMemoryOperation.Create, fixture.Requests);
    }

    /// <summary>
    /// Malformed shape, count and type metadata release both response buffers and the new native owner.
    /// </summary>
    /// <param name="mode">The malformed transport partition.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void MalformedMetadataReleasesResponseAndOwner(int mode)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new ArrayScript(fixture) { InvalidMetadata = mode };
        PgDatum source = PgDatum.DangerousCreate(123, 1009, PgMemoryContext.Current);
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgArrayView(source));
        Assert.AreEqual(1, script.Releases);
        Assert.AreSequenceEqual<nint>([202], script.Deleted);
        Assert.HasCount(1, script.Requests);
        Assert.AreEqual(5, script.Requests[0].Operation);
    }

    /// <summary>
    /// Two native cursor handles remain independent; NULL is a typed cell and cells outlive cursor disposal.
    /// </summary>
    [TestMethod]
    public void CursorsKeepSeparateHandlesAndCellsKeepArrayLifetime()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new ArrayScript(fixture);
        using var view = new PgArrayView(PgDatum.DangerousCreate(123, 1009, PgMemoryContext.Current));
        Assert.AreEqual(1009U, view.TypeOid);
        Assert.AreEqual(25U, view.ElementTypeOid);
        Assert.AreEqual(3, view.Count);
        Assert.AreSequenceEqual<int>([3], view.Lengths.ToArray());
        Assert.AreSequenceEqual<int>([-1], view.LowerBounds.ToArray());
        Assert.IsTrue(view.HasNulls);
        using IEnumerator<PgDatum> first = view.GetEnumerator();
        using IEnumerator<PgDatum> second = view.GetEnumerator();
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = first.Current);
        script.Cells.Enqueue((7001, false, true));
        Assert.IsTrue(first.MoveNext());
        PgDatum escaped = first.Current;
        Assert.AreEqual((nuint)7001, escaped.DangerousGetBits());
        script.Cells.Enqueue((0, true, true));
        Assert.IsTrue(first.MoveNext());
        Assert.IsTrue(first.Current.IsNull);
        Assert.AreEqual(25U, first.Current.TypeOid);
        script.Cells.Enqueue((7001, false, true));
        Assert.IsTrue(second.MoveNext());
        Assert.AreEqual((nuint)7001, second.Current.DangerousGetBits());
        first.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => first.MoveNext());
        Assert.AreEqual((nuint)7001, escaped.DangerousGetBits());
        script.Cells.Enqueue((0, false, false));
        Assert.IsFalse(second.MoveNext());
        int requests = script.Requests.Count;
        Assert.IsFalse(second.MoveNext());
        Assert.HasCount(requests, script.Requests);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = second.Current);
        Assert.AreSequenceEqual<nint>([1010, 1010, 2020, 2020],
            script.Requests.Where(static request => request.Operation == 8).Select(static request => request.Iterator));
        Assert.AreEqual(202, escaped.Lifetime.ContextId);
        Assert.AreSequenceEqual<nint>([203], script.Deleted);
        view.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => escaped.DangerousGetBits());
        Assert.AreEqual(3, view.Count);
        Assert.AreEqual(1009U, view.TypeOid);
    }

    /// <summary>
    /// Matching provider tokens on another thread cannot permit any native access or disposal.
    /// </summary>
    [TestMethod]
    public void BorrowedArrayRejectsForeignBackendAccess()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new ArrayScript(fixture);
        using var view = new PgArrayView(PgDatum.DangerousCreate(123, 1009, PgMemoryContext.Current));
        using IEnumerator<PgDatum> iterator = view.GetEnumerator();
        script.Cells.Enqueue((7001, false, true));
        Assert.IsTrue(iterator.MoveNext());
        PgDatum cell = iterator.Current;
        fixture.Requests.Clear();
        using (MemoryContextTestFixture.Enter(29))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => cell.DangerousGetBits());
            Assert.ThrowsExactly<InvalidOperationException>(view.GetEnumerator);
            Assert.ThrowsExactly<InvalidOperationException>(() => view.DangerousGetSpan<int>());
            Assert.ThrowsExactly<InvalidOperationException>(() => view.DangerousGetUuidBytes());
            Assert.ThrowsExactly<InvalidOperationException>(view.Dispose);
        }

        DedicatedThread.Run(() =>
        {
            using var other = new MemoryContextTestFixture();
            using MemoryContextTestFixture.Scope capability = MemoryContextTestFixture.Enter();
            Assert.ThrowsExactly<InvalidOperationException>(() => cell.DangerousGetBits());
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = view[0]);
            Assert.ThrowsExactly<InvalidOperationException>(() => view.GetEnumerator());
            Assert.ThrowsExactly<InvalidOperationException>(() => view.DangerousGetSpan<int>());
            Assert.ThrowsExactly<InvalidOperationException>(() => view.DangerousGetUuidBytes());
            Assert.ThrowsExactly<InvalidOperationException>(() => iterator.MoveNext());
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = iterator.Current);
            Assert.ThrowsExactly<InvalidOperationException>(iterator.Dispose);
            Assert.ThrowsExactly<InvalidOperationException>(view.Dispose);
            Assert.IsEmpty(other.Requests);
        });

        Assert.IsEmpty(fixture.Requests);
        Assert.AreEqual((nuint)7001, cell.DangerousGetBits());
    }

    /// <summary>
    /// Accesses the generated input type field without widening its production visibility.
    /// </summary>
    private static ref int ArrayType(ref NativeValue value) => ref value._auxiliary2;

    /// <summary>
    /// Supplies scripted transport observations without implementing native array traversal.
    /// </summary>
    private sealed unsafe class ArrayScript : IDisposable
    {
        [ThreadStatic]
        private static ArrayScript? s_current;

        /// <summary>
        /// Makes <paramref name="current"/> the ArrayScript this thread's native callbacks use, or restores the enclosing one.
        /// </summary>
        private static void Install(ArrayScript? current) => s_current = current;
        private readonly ArrayScript? _previousScript = s_current;
        private readonly nint _previous = NativeBackend.Enter((nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Invoke);
        private int _nextContext = 201;
        private int _nextIterator;

        /// <summary>
        /// Installs explicit owner and transport responses for the current thread.
        /// </summary>
        internal ArrayScript(MemoryContextTestFixture fixture)
        {
            Install(this);
            fixture.Handler = request =>
            {
                if (request._operation == NativeMemoryOperation.Callback)
                {
                    return new NativeMemoryResult { _context = 101 };
                }

                if (request._operation == NativeMemoryOperation.Create)
                {
                    return new NativeMemoryResult { _context = ++_nextContext };
                }

                if (request._operation == NativeMemoryOperation.Delete)
                {
                    Deleted.Add(request._context);
                }
                else if (Deleted.Contains(request._context))
                {
                    throw new PgException("55000", "Deleted scripted owner.");
                }

                return fixture.Respond(request);
            };
        }

        /// <summary>
        /// Gets or sets the deliberately malformed metadata partition, or minus one for valid metadata.
        /// </summary>
        internal int InvalidMetadata
        {
            get;
            init;
        } = -1;

        /// <summary>
        /// Gets or sets the independently selected source element identity.
        /// </summary>
        internal uint ElementType
        {
            get;
            init;
        } = 25;

        /// <summary>
        /// Gets or sets whether the scripted array contains a NULL cell.
        /// </summary>
        internal bool HasNulls
        {
            get;
            init;
        } = true;

        /// <summary>
        /// Gets the requested element contract and nominal-identity flag.
        /// </summary>
        internal List<(uint Type, long Exact)> Contracts { get; } = [];

        /// <summary>
        /// Gets copied parameter identities and their SQL NULL flags observed at the native boundary.
        /// </summary>
        internal List<(uint Type, bool IsNull)> Copies { get; } = [];

        /// <summary>
        /// Gets prepared scalar conversions independently of the cursor's native words.
        /// </summary>
        internal Queue<NativeValue> Conversions { get; } = [];

        /// <summary>
        /// Gets the independent response sequence for native iteration.
        /// </summary>
        internal Queue<(long Bits, bool IsNull, bool Found)> Cells { get; } = [];

        /// <summary>
        /// Gets observed operation and cursor identities.
        /// </summary>
        internal List<(int Operation, nint Iterator)> Requests { get; } = [];

        /// <summary>
        /// Gets explicit native owner deletion requests.
        /// </summary>
        internal List<nint> Deleted { get; } = [];

        /// <summary>
        /// Gets the number of released response envelopes.
        /// </summary>
        internal int Releases
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the requests for exact scalar identities and physical widths.
        /// </summary>
        internal List<(uint Type, long Width)> Slices { get; } = [];

        /// <summary>
        /// Gets or sets the separately owned payload used by slice responses.
        /// </summary>
        internal nint SliceStorage
        {
            get;
            private set;
        }

        /// <summary>
        /// Allocates an independent aligned payload with known literal bytes.
        /// </summary>
        internal void SetSlice(ReadOnlySpan<byte> bytes)
        {
            SliceStorage = (nint)NativeMemory.Alloc((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>((void*)SliceStorage, bytes.Length));
        }

        /// <summary>
        /// Gets or sets the independently selected response element type.
        /// </summary>
        internal uint SliceType
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets a deliberately invalid slice response partition.
        /// </summary>
        internal int InvalidSlice
        {
            get;
            init;
        } = -1;

        /// <inheritdoc />
        public void Dispose()
        {
            NativeMemory.Free((void*)SliceStorage);
            NativeBackend.Exit(_previous);
            Install(_previousScript);
        }

        /// <summary>
        /// Returns prepared metadata and cells while containing all managed exceptions.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Invoke(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
        {
            try
            {
                ArrayScript script = s_current!;
                script.Requests.Add((request->_scalarOperation, request->_arrayIterator));
                result->_release = &Release;
                result->_resultTypeOid = script.ElementType;
                switch (request->_scalarOperation)
                {
                    case 2:
                        NativeSpiParameter input = request->_parameters[0];
                        script.Copies.Add((input._typeOid, input._value.IsNull != 0));
                        result->_text = new NativeValue { IsNull = input._value.IsNull };
                        break;
                    case 0:
                        result->_text = script.Conversions.Dequeue();
                        result->_rowsAffected = script.ElementType;
                        break;
                    case 12:
                        script.Contracts.Add((request->_scalarResultOid, request->_limit));
                        break;
                    case 5:
                        script.Contracts.Add((request->_scalarResultOid, request->_limit));
                        int[] shape = script.InvalidMetadata == 0 ? [3] : [3, -1];
                        result->_text = NativeValue.FromBytes(MemoryMarshal.AsBytes(shape.AsSpan()));
                        result->_text.Integral = 123;
                        result->_rowCount = script.InvalidMetadata == 1 ? -1 : 3;
                        result->_rowsAffected = script.HasNulls ? 1 : 0;
                        result->_resultTypeOid = script.InvalidMetadata == 2 ? 0U : script.ElementType;
                        break;
                    case 7:
                        result->_text.Integral = ++script._nextIterator * 1010;
                        break;
                    case 8:
                        (long bits, bool isNull, bool found) = script.Cells.Dequeue();
                        result->_text = new NativeValue { Integral = bits, IsNull = isNull ? (byte)1 : (byte)0 };
                        result->_rowsAffected = found ? 1 : 0;
                        break;
                    case 11:
                        script.Slices.Add((request->_scalarResultOid, request->_limit));
                        result->_text.Integral = script.InvalidSlice == 0 ? 0 :
                            (long)script.SliceStorage + (script.InvalidSlice == 1 ? 1 : 0);
                        result->_rowCount = script.InvalidSlice == 2 ? 2 : 3;
                        result->_resultTypeOid = script.InvalidSlice == 3 ? 700U : script.SliceType;
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected array operation.");
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

        /// <summary>
        /// Frees the exact transport buffer and records cleanup on success and failure.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(NativeSpiResult* result)
        {
            s_current!.Releases++;
            result->_text.Release();
        }
    }
}
