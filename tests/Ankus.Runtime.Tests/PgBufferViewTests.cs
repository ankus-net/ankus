using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks buffer byte access and native ownership independently of PostgreSQL varlena layout.
/// </summary>
[TestClass]
public sealed partial class PgBufferViewTests
{
    /// <summary>
    /// Callback exit cleans forgotten buffers in reverse order without expiring views owned by an outer callback.
    /// </summary>
    [TestMethod]
    public void BorrowedBuffersFollowTheirCallbackScope()
    {
        using var fixture = new MemoryContextTestFixture();
        using var script = new BufferScript(fixture, [65]);
        PgByteaView binary;
        PgTextView text;
        PgByteaView nested;
        PgDatum escaped;
        using (MemoryContextTestFixture.Enter())
        {
            var lifetime = new PgDatumLifetime(PgMemoryContext.Current, NativeMemoryContext.BorrowScope);
            binary = new PgByteaView(new PgDatum(123, 17, false, lifetime));
            text = new PgTextView(new PgDatum(124, 25, false, lifetime));
            escaped = text.Datum;
            using (MemoryContextTestFixture.Enter())
            {
                var child = new PgDatumLifetime(PgMemoryContext.Current, NativeMemoryContext.BorrowScope);
                nested = new PgByteaView(new PgDatum(125, 17, false, child));
                Assert.AreEqual((byte)65, nested[0]);
                Assert.AreEqual((byte)65, binary[0]);
                Assert.AreEqual("A", text.ToString());
            }

            Assert.AreSequenceEqual<nint>([204], script.Deleted);
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = nested[0]);
            Assert.AreEqual((byte)65, binary[0]);
            Assert.AreEqual("A", text.ToString());
        }

        Assert.AreSequenceEqual<nint>([204, 203, 202], script.Deleted);
        Assert.AreEqual(3, script.Releases);
        using (MemoryContextTestFixture.Enter())
        {
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = binary[0]);
            Assert.ThrowsExactly<ObjectDisposedException>(() => text.ToString());
            Assert.ThrowsExactly<ObjectDisposedException>(() => escaped.DangerousGetBits());
            Assert.AreEqual(1, binary.Count);
            Assert.AreEqual(1, text.Utf8Length);
        }

        Assert.AreSequenceEqual<nint>([204, 203, 202], script.Deleted);
        Assert.AreSequenceEqual<int>([9, 10, 9], script.Operations);
    }

    /// <summary>
    /// Null references, SQL NULL and stale datums reject before allocating private native storage.
    /// </summary>
    /// <param name="text">Whether the text constructor or bytea constructor is used.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InvalidBufferInputsCannotAllocateOwners(bool text)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new BufferScript(fixture, []);
        Func<PgDatum, IDisposable> create = text ? static value => new PgTextView(value) : static value => new PgByteaView(value);
        PgDatum absent = PgDatum.DangerousCreate(0, text ? 25U : 17U, PgMemoryContext.Current, isNull: true);
        PgDatum stale = PgDatum.DangerousCreate(123, text ? 25U : 17U, PgMemoryContext.Current);
        Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentNullException>(() => create(null!)).ParamName);
        Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentException>(() => create(absent)).ParamName);
        fixture.Handler = static _ => new NativeMemoryResult { _value = 902 };
        Assert.ThrowsExactly<ObjectDisposedException>(() => create(stale));
        Assert.DoesNotContain(static request => request._operation == NativeMemoryOperation.Create, fixture.Requests);
        Assert.IsEmpty(script.Operations);
        Assert.IsEmpty(script.Deleted);
        Assert.AreEqual(0, script.Releases);
    }

    /// <summary>
    /// Present empty text retains its datum and cannot address either adjacent byte boundary.
    /// </summary>
    [TestMethod]
    public void EmptyBorrowedTextRemainsPresent()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new BufferScript(fixture, []);
        using var view = new PgTextView(PgDatum.DangerousCreate(123, 25, PgMemoryContext.Current));
        Assert.AreEqual(0, view.Utf8Length);
        Assert.AreEqual(string.Empty, view.ToString());
        Assert.IsFalse(view.Datum.IsNull);
        Assert.AreEqual((nuint)9011, view.Datum.DangerousGetBits());
        Assert.IsTrue(view.DangerousGetUtf8Span().IsEmpty);
        view.CopyUtf8To([]);
        byte[] destination = [9];
        view.CopyUtf8To(destination);
        Assert.AreSequenceEqual<byte>([9], destination);
        Assert.AreEqual("index", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => view.GetUtf8Byte(-1)).ParamName);
        Assert.AreEqual("index", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => view.GetUtf8Byte(0)).ParamName);
    }

    /// <summary>
    /// Malformed UTF-8 returned by a broken native provider is never replaced with a different character.
    /// </summary>
    [TestMethod]
    public void BorrowedTextRejectsLossyStringDecoding()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new BufferScript(fixture, [0xc3]);
        using var view = new PgTextView(PgDatum.DangerousCreate(123, 25, PgMemoryContext.Current));
        Assert.ThrowsExactly<DecoderFallbackException>(view.ToString);
        Assert.AreEqual((byte)0xc3, view.GetUtf8Byte(0));
        Assert.AreEqual(1, view.Utf8Length);
        Assert.AreEqual(1, script.Releases);
        view.Dispose();
        Assert.AreSequenceEqual<nint>([202], script.Deleted);
    }

    /// <summary>
    /// A source-only reset expires direct and nested binary aliases before reading their still-allocated payload.
    /// </summary>
    /// <param name="nested">Whether an intermediate view contributes another source dependency.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BorrowedByteaRetainsOriginalSourceLifetime(bool nested)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new BufferScript(fixture, [7, 0]);
        using var first = new PgByteaView(PgDatum.DangerousCreate(123, 40001, PgMemoryContext.Current));
        using PgByteaView? second = nested ? new PgByteaView(first.Datum) : null;
        PgByteaView view = second ?? first;
        PgDatum escaped = view.Datum;
        using IEnumerator<byte> cursor = view.GetEnumerator();
        Assert.IsTrue(cursor.MoveNext());
        Assert.AreEqual((byte)7, cursor.Current);
        Func<byte[]> snapshot = view.ToArray;
        byte[] copy = snapshot();
        Func<NativeMemoryRequest, NativeMemoryResult> previous = fixture.Handler!;
        fixture.Handler = request => request._operation == NativeMemoryOperation.CaptureGeneration && request._context == 101
            ? new NativeMemoryResult { _value = 902 }
            : previous(request);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = view[0]);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = view.Datum);
        Assert.ThrowsExactly<ObjectDisposedException>(view.GetEnumerator);
        Assert.ThrowsExactly<ObjectDisposedException>(() => cursor.MoveNext());
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = cursor.Current);
        Assert.ThrowsExactly<ObjectDisposedException>(() => escaped.DangerousGetBits());
        Assert.ThrowsExactly<ObjectDisposedException>(() => snapshot());
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.DangerousGetSpan().ToArray());
        byte[] destination = [9, 9];
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.CopyTo(destination));
        Assert.AreSequenceEqual<byte>([9, 9], destination);
        Assert.AreSequenceEqual<byte>([7, 0], copy);
        Assert.AreEqual(2, view.Count);
        Assert.AreEqual(40001U, view.TypeOid);
        Assert.IsEmpty(script.Deleted);
        view.Dispose();
        Assert.AreSequenceEqual<nint>(nested ? [203] : [202], script.Deleted);
        Assert.AreSequenceEqual<int>(nested ? [9, 9] : [9], script.Operations);
    }

    /// <summary>
    /// A source-only reset expires direct and nested text aliases while copied strings and metadata survive.
    /// </summary>
    /// <param name="nested">Whether an intermediate view contributes another source dependency.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BorrowedTextRetainsOriginalSourceLifetime(bool nested)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new BufferScript(fixture, [0xc3, 0xa9]);
        using var first = new PgTextView(PgDatum.DangerousCreate(123, 40002, PgMemoryContext.Current));
        using PgTextView? second = nested ? new PgTextView(first.Datum) : null;
        PgTextView view = second ?? first;
        PgDatum escaped = view.Datum;
        string copy = view.ToString();
        Func<NativeMemoryRequest, NativeMemoryResult> previous = fixture.Handler!;
        fixture.Handler = request => request._operation == NativeMemoryOperation.CaptureGeneration && request._context == 101
            ? new NativeMemoryResult { _value = 902 }
            : previous(request);
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.GetUtf8Byte(0));
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = view.Datum);
        Assert.ThrowsExactly<ObjectDisposedException>(() => escaped.DangerousGetBits());
        Assert.ThrowsExactly<ObjectDisposedException>(view.ToString);
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.DangerousGetUtf8Span().ToArray());
        byte[] destination = [9, 9];
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.CopyUtf8To(destination));
        Assert.AreSequenceEqual<byte>([9, 9], destination);
        Assert.AreEqual("é", copy);
        Assert.AreEqual(2, view.Utf8Length);
        Assert.AreEqual(40002U, view.TypeOid);
        Assert.IsEmpty(script.Deleted);
        view.Dispose();
        Assert.AreSequenceEqual<nint>(nested ? [203] : [202], script.Deleted);
        Assert.AreSequenceEqual<int>(nested ? [10, 10] : [10], script.Operations);
    }

    /// <summary>
    /// Binary payloads retain every byte, validate adjacent bounds, and make independent managed copies.
    /// </summary>
    [TestMethod]
    public void BorrowedByteaReadsExactBytesAndCopies()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new BufferScript(fixture, [0, 127, 128, 255, 0]);
        using var view = new PgByteaView(PgDatum.DangerousCreate(123, 40001, PgMemoryContext.Current));
        Assert.AreEqual(40001U, view.TypeOid);
        Assert.AreEqual(5, view.Count);
        Assert.AreEqual((nuint)9011, view.Datum.DangerousGetBits());
        Assert.AreEqual((byte)0, view[0]);
        Assert.AreEqual((byte)128, view[2]);
        Assert.AreEqual((byte)0, view[4]);
        Assert.AreEqual("index", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = view[-1]).ParamName);
        Assert.AreEqual("index", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = view[5]).ParamName);
        byte[] shortDestination = [9, 9, 9, 9];
        Assert.ThrowsExactly<ArgumentException>(() => view.CopyTo(shortDestination));
        Assert.AreSequenceEqual<byte>([9, 9, 9, 9], shortDestination);
        byte[] destination = [9, 9, 9, 9, 9, 9];
        view.CopyTo(destination);
        Assert.AreSequenceEqual<byte>([0, 127, 128, 255, 0, 9], destination);
        Func<byte[]> snapshot = view.ToArray;
        byte[] copy = snapshot();
        Assert.AreSequenceEqual<byte>([0, 127, 128, 255, 0], view.DangerousGetSpan().ToArray());
        Assert.AreSequenceEqual<int>([9], script.Operations);
        Assert.AreEqual(1, script.Releases);
        view.Dispose();
        Assert.AreEqual(5, view.Count);
        Assert.AreEqual(40001U, view.TypeOid);
        Assert.AreSequenceEqual<byte>([0, 127, 128, 255, 0], copy);
        Assert.ThrowsExactly<ObjectDisposedException>(() => snapshot());
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.CopyTo(destination));
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.DangerousGetSpan().ToArray());
        Assert.AreSequenceEqual<nint>([202], script.Deleted);
    }

    /// <summary>
    /// Text exposes exact UTF-8 byte offsets and decodes an independent string without replacing characters.
    /// </summary>
    [TestMethod]
    public void BorrowedTextKeepsUtf8BytesAndOriginalType()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new BufferScript(fixture, [0x63, 0x61, 0x66, 0xc3, 0xa9]);
        using var view = new PgTextView(PgDatum.DangerousCreate(123, 40002, PgMemoryContext.Current));
        Assert.AreEqual(40002U, view.TypeOid);
        Assert.AreEqual(5, view.Utf8Length);
        Assert.AreEqual((nuint)9011, view.Datum.DangerousGetBits());
        Assert.AreEqual((byte)0x63, view.GetUtf8Byte(0));
        Assert.AreEqual((byte)0xc3, view.GetUtf8Byte(3));
        Assert.AreEqual((byte)0xa9, view.GetUtf8Byte(4));
        Assert.AreEqual("index", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => view.GetUtf8Byte(-1)).ParamName);
        Assert.AreEqual("index", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => view.GetUtf8Byte(5)).ParamName);
        byte[] shortDestination = [9, 9, 9, 9];
        Assert.ThrowsExactly<ArgumentException>(() => view.CopyUtf8To(shortDestination));
        Assert.AreSequenceEqual<byte>([9, 9, 9, 9], shortDestination);
        byte[] destination = [9, 9, 9, 9, 9, 9];
        view.CopyUtf8To(destination);
        Assert.AreSequenceEqual<byte>([0x63, 0x61, 0x66, 0xc3, 0xa9, 9], destination);
        Assert.AreSequenceEqual<byte>([0x63, 0x61, 0x66, 0xc3, 0xa9], view.DangerousGetUtf8Span().ToArray());
        string copy = view.ToString();
        Assert.AreSequenceEqual<int>([10], script.Operations);
        view.Dispose();
        Assert.AreEqual("café", copy);
        Assert.AreEqual(5, view.Utf8Length);
        Assert.AreEqual(40002U, view.TypeOid);
        Assert.ThrowsExactly<ObjectDisposedException>(view.ToString);
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.CopyUtf8To(destination));
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.DangerousGetUtf8Span().ToArray());
        Assert.AreSequenceEqual<nint>([202], script.Deleted);
    }

    /// <summary>
    /// Empty bytea is present and enumerators retain distinct positions, terminal state and disposal.
    /// </summary>
    /// <param name="empty">Whether the payload contains no bytes.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BorrowedByteaEnumeratorsKeepIndependentState(bool empty)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new BufferScript(fixture, empty ? [] : [7, 0]);
        using var view = new PgByteaView(PgDatum.DangerousCreate(123, 17, PgMemoryContext.Current));
        using IEnumerator<byte> first = view.GetEnumerator();
        using IEnumerator<byte> second = view.GetEnumerator();
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = first.Current);
        Assert.AreEqual(!empty, first.MoveNext());
        Assert.AreEqual(!empty, second.MoveNext());
        if (!empty)
        {
            Assert.AreEqual((byte)7, first.Current);
            Assert.IsTrue(first.MoveNext());
            Assert.AreEqual((byte)0, first.Current);
            Assert.AreEqual((byte)7, second.Current);
            Assert.AreEqual((byte)7, ((IEnumerator)second).Current);
        }

        Assert.IsFalse(first.MoveNext());
        Assert.IsFalse(first.MoveNext());
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = first.Current);
        Assert.ThrowsExactly<NotSupportedException>(first.Reset);
        first.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => first.MoveNext());
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = first.Current);
        view.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => second.MoveNext());
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = second.Current);
    }

    /// <summary>
    /// Invalid native metadata releases the response and deletes the provisional owner.
    /// </summary>
    /// <param name="text">Whether UTF-8 text is requested.</param>
    /// <param name="invalid">The missing datum, missing payload or negative-length partition.</param>
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 0)]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    public void MalformedBorrowedBuffersReleaseTheirOwners(bool text, int invalid)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new BufferScript(fixture, [7]) { InvalidMetadata = invalid };
        PgDatum source = PgDatum.DangerousCreate(123, text ? 25U : 17U, PgMemoryContext.Current);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            using IDisposable view = text ? new PgTextView(source) : new PgByteaView(source);
        });
        Assert.AreEqual(1, script.Releases);
        Assert.AreSequenceEqual<nint>([202], script.Deleted);
        Assert.AreSequenceEqual<int>([text ? 10 : 9], script.Operations);
    }

    /// <summary>
    /// Supplies explicit response buffers without reproducing PostgreSQL detoasting or encoding conversion.
    /// </summary>
    private sealed unsafe class BufferScript : IDisposable
    {
        [ThreadStatic]
        private static BufferScript? s_current;

        /// <summary>
        /// Makes <paramref name="current"/> the BufferScript this thread's native callbacks use, or restores the enclosing one.
        /// </summary>
        private static void Install(BufferScript? current) => s_current = current;
        private readonly BufferScript? _previousScript = s_current;
        private readonly nint _previous = NativeBackend.Enter((nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Invoke);
        private readonly byte* _data;
        private readonly int _length;
        private int _nextContext = 201;

        /// <summary>
        /// Owns one independent native test allocation and observes context cleanup.
        /// </summary>
        internal BufferScript(MemoryContextTestFixture fixture, byte[] bytes)
        {
            _length = bytes.Length;
            _data = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
            bytes.CopyTo(new Span<byte>(_data, bytes.Length));
            _data[bytes.Length] = 0;
            Install(this);
            fixture.Handler = request =>
            {
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
        /// Gets or sets a malformed response partition, or minus one for ordinary metadata.
        /// </summary>
        internal int InvalidMetadata
        {
            get;
            init;
        } = -1;

        /// <summary>
        /// Gets the observed bytea/text bridge operations.
        /// </summary>
        internal List<int> Operations { get; } = [];

        /// <summary>
        /// Gets the private owner deletion requests.
        /// </summary>
        internal List<nint> Deleted { get; } = [];

        /// <summary>
        /// Gets the number of released native response envelopes.
        /// </summary>
        internal int Releases
        {
            get;
            private set;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            NativeBackend.Exit(_previous);
            NativeMemory.Free(_data);
            Install(_previousScript);
        }

        /// <summary>
        /// Returns independent native addresses and lengths while containing all managed exceptions.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Invoke(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
        {
            try
            {
                BufferScript script = s_current!;
                script.Operations.Add(request->_scalarOperation);
                result->_release = &Release;
                result->_text.Integral = script.InvalidMetadata == 0 ? 0 : 9011;
                result->_rowsAffected = script.InvalidMetadata == 1 ? 0 : (long)script._data;
                result->_rowCount = script.InvalidMetadata == 2 ? -1 : script._length;
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
        /// Records envelope release without freeing borrowed payload storage.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(NativeSpiResult* result) => s_current!.Releases++;
    }
}
