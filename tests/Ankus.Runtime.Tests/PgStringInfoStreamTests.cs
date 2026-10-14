using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies StringInfo's managed byte, capability, and ownership contracts with scripted native responses.
/// </summary>
[TestClass]
public sealed unsafe class PgStringInfoStreamTests
{
    /// <summary>
    /// Creation and every append transport exact bytes instead of applying database encoding or C-string truncation.
    /// </summary>
    [TestMethod]
    public void CreationAndUnicodeWritesPreserveExactByteEnvelopes()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        var writes = new List<byte[]>();
        fixture.Handler = request =>
        {
            if (request._operation == NativeMemoryOperation.StringInfo &&
                request._flags is (int)NativeStringInfoOperation.Create or (int)NativeStringInfoOperation.Append)
            {
                writes.Add(new ReadOnlySpan<byte>((void*)request._data, checked((int)request._length)).ToArray());
            }

            return Respond(fixture, request);
        };
        using PgMemoryContext owner = PgMemoryContext.Create("owner");
        using PgStringInfoStream buffer = PgStringInfoStream.Create([0, 128, 255], owner);
        buffer.Write("café\0");
        buffer.Write(new Rune(0x1F418));
        buffer.Write('é');
        buffer.WriteByte(0);
        buffer.Write([9, 1, 255, 8], 1, 2);
        buffer.DangerousAppend(null, 0);
        Assert.HasCount(7, writes);
        Assert.AreSequenceEqual<byte>([0, 128, 255], writes[0]);
        Assert.AreSequenceEqual<byte>([99, 97, 102, 195, 169, 0], writes[1]);
        Assert.AreSequenceEqual<byte>([240, 159, 144, 152], writes[2]);
        Assert.AreSequenceEqual<byte>([195, 169], writes[3]);
        Assert.AreSequenceEqual<byte>([0], writes[4]);
        Assert.AreSequenceEqual<byte>([1, 255], writes[5]);
        Assert.IsEmpty(writes[6]);
        NativeMemoryRequest creation = fixture.Requests.Single(request => request._operation == NativeMemoryOperation.StringInfo && request._flags == 1);
        Assert.AreEqual(202, creation._context);
        Assert.AreEqual(3, creation._value);
        foreach (NativeMemoryRequest request in fixture.Requests.Where(request => request._operation == NativeMemoryOperation.StringInfo && request._flags == 3))
        {
            Assert.AreEqual(701, request._context);
            Assert.AreEqual(0, request._pointer);
            Assert.AreEqual(0, request._other);
        }
    }

    /// <summary>
    /// Checked copies and replacement pass exact native ranges, and strict decoding never replaces malformed bytes.
    /// </summary>
    [TestMethod]
    public void CopiesAreIndependentAndStrictUtf8HasAnExplicitLossyAlternative()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] payload = [97, 0, 255, 195, 169];
        byte[]? replaced = null;
        fixture.Handler = request =>
        {
            if (request._operation != NativeMemoryOperation.StringInfo)
            {
                return fixture.Respond(request);
            }

            if (request._flags == (int)NativeStringInfoOperation.Read)
            {
                payload.AsSpan((int)request._value, (int)request._length).CopyTo(new Span<byte>((void*)request._data, (int)request._length));
            }
            else if (request._flags == (int)NativeStringInfoOperation.Write)
            {
                Assert.AreEqual(2, request._value);
                replaced = new ReadOnlySpan<byte>((void*)request._data, (int)request._length).ToArray();
            }

            NativeMemoryResult result = Respond(fixture, request);
            result._length = 5;
            return result;
        };
        using PgStringInfoStream buffer = PgStringInfoStream.Create();
        byte[] copy = buffer.ToArray();
        Assert.AreSequenceEqual<byte>([97, 0, 255, 195, 169], copy);
        copy[0] = 0;
        Assert.AreEqual((byte)97, buffer.ToArray()[0]);
        byte[] middle = new byte[3];
        buffer.CopyTo(middle, 1);
        Assert.AreSequenceEqual<byte>([0, 255, 195], middle);
        Assert.ThrowsExactly<DecoderFallbackException>(buffer.ToString);
        Assert.AreEqual("a\0�é", buffer.ToStringLossy());
        buffer.WriteAt(2, [17, 128]);
        Assert.AreSequenceEqual<byte>([17, 128], replaced!);
        buffer.CopyTo([], 5);
        Assert.AreEqual(5, fixture.Requests[^1]._value);
        Assert.AreEqual((nuint)0, fixture.Requests[^1]._length);
    }

    /// <summary>
    /// Managed invalid inputs fail before any guarded native operation or ownership change.
    /// </summary>
    [TestMethod]
    public void InvalidSizesPointersAndUnicodeDoNotReachNativeStorage()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Respond(fixture, request);
        using PgStringInfoStream buffer = PgStringInfoStream.Create();
        fixture.Requests.Clear();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgStringInfoStream.Create(-1));
        Assert.ThrowsExactly<EncoderFallbackException>(() => PgStringInfoStream.Create("\uD800"));
        Assert.ThrowsExactly<EncoderFallbackException>(() => buffer.Write("\uDC00"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.Write('\uD800'));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.Enlarge(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.EnsureCapacity(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.CopyTo([], -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.WriteAt(-1, []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.DangerousAppend(null, -1));
        Assert.ThrowsExactly<ArgumentNullException>(() => buffer.DangerousAppend(null, 1));
        Assert.ThrowsExactly<ArgumentNullException>(() => buffer.Write(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => buffer.Write(null!, 0, 0));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// An externally borrowed value transports its exact anchor generation and never requests release rights.
    /// </summary>
    /// <param name="readOnly">Whether native metadata uses the read-only capacity sentinel.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BorrowingPreservesAnchorAndReadOnlyCapabilities(bool readOnly)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request =>
        {
            NativeMemoryResult result = Respond(fixture, request);
            if (request._operation == NativeMemoryOperation.StringInfo)
            {
                result._context = 101;
                result._length = 7;
                result._value = readOnly ? 0 : 1024;
            }

            return result;
        };
        using PgStringInfoStream buffer = PgStringInfoStream.DangerousBorrow((void*)303, PgMemoryContext.Current)!;
        Assert.AreEqual(readOnly, buffer.IsReadOnly);
        Assert.AreEqual(!readOnly, buffer.CanWrite);
        Assert.AreEqual(readOnly ? 7 : 1023, buffer.Capacity);
        Assert.AreEqual(101, buffer.LifetimeContext.Id);
        if (readOnly)
        {
            Assert.ThrowsExactly<NotSupportedException>(() => buffer.WriteByte(1));
            Assert.ThrowsExactly<NotSupportedException>(() => buffer.WriteAt(0, []));
            Assert.ThrowsExactly<NotSupportedException>(buffer.Reset);
            Assert.ThrowsExactly<NotSupportedException>(() => buffer.Enlarge(0));
        }
        else
        {
            buffer.WriteByte(1);
        }

        foreach (NativeMemoryRequest request in fixture.Requests.Where(request => request._operation == NativeMemoryOperation.StringInfo))
        {
            Assert.AreEqual(101, request._context);
            Assert.AreEqual(901, request._other);
            Assert.AreEqual(303, request._pointer);
        }

        fixture.Requests.Clear();
        buffer.Dispose();
        Assert.IsEmpty(fixture.Requests);
        Assert.IsFalse(buffer.CanWrite);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = buffer.Length);
    }

    /// <summary>
    /// Native stale-context diagnostics invalidate even zero-byte operations and release owned error strings.
    /// </summary>
    [TestMethod]
    public void StaleGenerationErrorsRejectEmptyCopiesAndReleaseDiagnostics()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Respond(fixture, request);
        using PgStringInfoStream buffer = PgStringInfoStream.DangerousBorrow((void*)303, PgMemoryContext.Current)!;
        fixture.Handler = _ => throw new PgException("55000", "stale anchor", detail: "reset generation");
        Assert.ThrowsExactly<ObjectDisposedException>(() => buffer.CopyTo([]));
        Assert.AreEqual(2, fixture.ErrorReleases);
    }

    /// <summary>
    /// Provider and thread capabilities cannot be bypassed by empty writes or transferred addresses.
    /// </summary>
    [TestMethod]
    public void MissingForeignAndOtherThreadCapabilitiesRejectAccess()
    {
        using var fixture = new MemoryContextTestFixture();
        fixture.Handler = request => Respond(fixture, request);
        using PgStringInfoStream buffer = CreateInCallback();
        Assert.ThrowsExactly<InvalidOperationException>(() => buffer.CopyTo([]));
        Assert.IsNull(PgStringInfoStream.DangerousBorrow(null, null!));
        using MemoryContextTestFixture.Scope replacement = MemoryContextTestFixture.Enter();
        buffer.Dispose();

        static PgStringInfoStream CreateInCallback()
        {
            using (MemoryContextTestFixture.Enter())
            {
                PgStringInfoStream created = PgStringInfoStream.Create();
                using (MemoryContextTestFixture.Enter(99))
                {
                    Assert.ThrowsExactly<InvalidOperationException>(() => created.Write([]));
                    Assert.ThrowsExactly<InvalidOperationException>(() => created.DangerousDetach());
                }

                Assert.Throws<InvalidOperationException>(() => DedicatedThread.Run(created.Flush));
                return created;
            }
        }
    }

    /// <summary>
    /// Failed disposal or C-string validation preserves ownership so a later operation or retry is possible.
    /// </summary>
    /// <param name="transfer">Whether the failed request attempts C-string transfer instead of disposal.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeFailurePreservesOwnershipUntilSuccessfulRetry(bool transfer)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        bool fail = true;
        fixture.Handler = request =>
        {
            if (request._operation == NativeMemoryOperation.StringInfo &&
                request._flags == (int)(transfer ? NativeStringInfoOperation.DetachCString : NativeStringInfoOperation.Dispose) && fail)
            {
                throw new PgException("22023", "native failure", detail: "owned detail", hint: "retry");
            }

            return Respond(fixture, request);
        };
        using PgStringInfoStream buffer = PgStringInfoStream.Create();
        PgException exception = Assert.ThrowsExactly<PgException>(() =>
        {
            if (transfer)
            {
                buffer.DangerousDetachCString();
            }
            else
            {
                buffer.Dispose();
            }
        });
        Assert.AreEqual("22023", exception.SqlState);
        Assert.AreEqual("native failure", exception.Message);
        Assert.AreEqual("owned detail", exception.Detail);
        Assert.AreEqual("retry", exception.Hint);
        Assert.AreEqual(3, fixture.ErrorReleases);
        buffer.WriteByte(42);
        fail = false;
        if (transfer)
        {
            Assert.AreEqual(701, (nint)buffer.DangerousDetachCString());
        }
        else
        {
            buffer.Dispose();
        }

        Assert.IsFalse(buffer.CanWrite);
        int requests = fixture.Requests.Count;
        buffer.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => buffer.DangerousDetachData());
        Assert.HasCount(requests, fixture.Requests);
    }

    /// <summary>
    /// Capacity and stream operations preserve semantics while asynchronous writes stay on the invoking backend thread.
    /// </summary>
    [TestMethod]
    public void StreamOperationsRemainSynchronousAndHonorCancellationBeforeNativeAccess()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int thread = Environment.CurrentManagedThreadId;
        fixture.Handler = request =>
        {
            Assert.AreEqual(thread, Environment.CurrentManagedThreadId);
            return Respond(fixture, request);
        };
        using PgStringInfoStream buffer = PgStringInfoStream.Create(4096);
        Assert.AreEqual(4096, fixture.Requests[^1]._value);
        Assert.IsTrue(buffer.IsEmpty);
        Assert.IsFalse(buffer.CanRead);
        Assert.IsFalse(buffer.CanSeek);
        Assert.ThrowsExactly<NotSupportedException>(() => buffer.Position = 1);
        Assert.ThrowsExactly<NotSupportedException>(() => _ = buffer.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => buffer.Read([], 0, 0));
        Assert.ThrowsExactly<NotSupportedException>(() => buffer.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => buffer.SetLength(0));
        buffer.Enlarge(57);
        Assert.AreEqual((int)NativeStringInfoOperation.Enlarge, fixture.Requests[^1]._flags);
        Assert.AreEqual(57, fixture.Requests[^1]._value);
        Assert.AreEqual(1023, buffer.EnsureCapacity(901));
        Assert.AreEqual((int)NativeStringInfoOperation.EnsureCapacity, fixture.Requests[^1]._flags);
        Assert.AreEqual(901, fixture.Requests[^1]._value);
        buffer.Reset();
        Assert.AreEqual((int)NativeStringInfoOperation.Reset, fixture.Requests[^1]._flags);
        Assert.IsTrue(buffer.WriteAsync(new byte[] { 1, 2 }.AsMemory(), CancellationToken.None).AsTask().IsCompletedSuccessfully);
        Assert.IsTrue(buffer.WriteAsync([3], 0, 1, CancellationToken.None).IsCompletedSuccessfully);
        Assert.IsTrue(buffer.FlushAsync(CancellationToken.None).IsCompletedSuccessfully);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        int requests = fixture.Requests.Count;
        Assert.ThrowsExactly<OperationCanceledException>(() => buffer.WriteAsync(ReadOnlyMemory<byte>.Empty, canceled.Token).AsTask());
        Assert.ThrowsExactly<OperationCanceledException>(() => buffer.FlushAsync(canceled.Token));
        Assert.HasCount(requests, fixture.Requests);
        Assert.IsTrue(buffer.DisposeAsync().AsTask().IsCompletedSuccessfully);
        Assert.AreEqual((int)NativeStringInfoOperation.Dispose, fixture.Requests[^1]._flags);
    }

    /// <summary>
    /// Supplies fixed boundary values without simulating allocation, growth, or lifetime algorithms.
    /// </summary>
    private static NativeMemoryResult Respond(MemoryContextTestFixture fixture, NativeMemoryRequest request)
        => request._operation == NativeMemoryOperation.StringInfo
            ? new NativeMemoryResult { _pointer = 701, _context = 202, _value = 1024 }
            : fixture.Respond(request);
}
