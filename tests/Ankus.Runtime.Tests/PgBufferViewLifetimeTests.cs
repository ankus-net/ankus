namespace Ankus.Runtime.Tests;

public sealed partial class PgBufferViewTests
{
    /// <summary>
    /// Provisional SPI ownership attempts every close and retains native cleanup diagnostics on the original failure.
    /// </summary>
    /// <param name="cleanupFails">Whether deleting the newest native view fails once.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProvisionalBufferResultsReleaseEveryOwner(bool cleanupFails)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new BufferScript(fixture, [65]);
        using var binary = new PgByteaView(PgDatum.DangerousCreate(123, 17, PgMemoryContext.Current));
        using var text = new PgTextView(PgDatum.DangerousCreate(124, 25, PgMemoryContext.Current));
        using var scope = new SpiConversionScope();
        Assert.AreSame(binary, scope.Add(binary));
        Assert.AreSame(text, scope.Add(text));
        var primary = new InvalidCastException("Third column conversion failed.");
        Func<NativeMemoryRequest, NativeMemoryResult> previous = fixture.Handler!;
        fixture.Handler = request => cleanupFails && request._operation == NativeMemoryOperation.Delete && request._context == 203
            ? throw new PgException("XX000", "Scripted view cleanup failed.")
            : previous(request);
        try
        {
            scope.ReleaseAfterFailure(primary);
            scope.ReleaseAfterFailure(primary);
            Assert.AreSequenceEqual<nint>([203, 202], fixture.Requests
                .Where(static request => request._operation == NativeMemoryOperation.Delete)
                .Select(static request => request._context));
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = binary[0]);
            if (cleanupFails)
            {
                AggregateException cleanup = Assert.IsInstanceOfType<AggregateException>(primary.Data["Ankus.SpiViewCleanup"]);
                Assert.HasCount(1, cleanup.InnerExceptions);
                PgException native = Assert.IsInstanceOfType<PgException>(cleanup.InnerExceptions[0]);
                Assert.AreEqual("XX000", native.SqlState);
                Assert.AreEqual("Scripted view cleanup failed.", native.Message);
                Assert.AreEqual("A", text.ToString());
                Assert.AreSequenceEqual<nint>([202], script.Deleted);
            }
            else
            {
                Assert.IsEmpty(primary.Data);
                Assert.ThrowsExactly<ObjectDisposedException>(text.ToString);
                Assert.AreSequenceEqual<nint>([203, 202], script.Deleted);
            }
        }
        finally
        {
            fixture.Handler = previous;
        }
    }

    /// <summary>
    /// Successful conversions transfer views to the caller and ignore detached values and SQL NULL.
    /// </summary>
    [TestMethod]
    public void ProvisionalBufferResultsTransferSuccessfulOwnership()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new BufferScript(fixture, [65]);
        using var binary = new PgByteaView(PgDatum.DangerousCreate(123, 17, PgMemoryContext.Current));
        using var text = new PgTextView(PgDatum.DangerousCreate(124, 25, PgMemoryContext.Current));
        using var scope = new SpiConversionScope();
        Assert.AreSame(binary, scope.Add(binary));
        Assert.AreSame(text, scope.Add(text));
        Assert.IsNull(scope.Add<PgTextView?>(null));
        Assert.AreEqual(42, scope.Add(42));
        scope.Relinquish();
        var later = new InvalidOperationException("Later caller operation failed.");
        scope.ReleaseAfterFailure(later);
        Assert.IsEmpty(later.Data);
        Assert.IsEmpty(script.Deleted);
        Assert.AreEqual((byte)65, binary[0]);
        Assert.AreEqual("A", text.ToString());
    }

    /// <summary>
    /// Foreign providers and threads cannot read or dispose views even when the native capability token matches.
    /// </summary>
    [TestMethod]
    public void BorrowedBuffersRejectForeignBackendAccess()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new BufferScript(fixture, [65]);
        using var binary = new PgByteaView(PgDatum.DangerousCreate(123, 17, PgMemoryContext.Current));
        using var text = new PgTextView(PgDatum.DangerousCreate(124, 25, PgMemoryContext.Current));
        using IEnumerator<byte> cursor = binary.GetEnumerator();
        using var cstring = new PgCStringView(PgDatum.DangerousCreate(125, 2275, PgMemoryContext.Current));
        Assert.IsTrue(cursor.MoveNext());
        PgDatum escaped = text.Datum;
        fixture.Requests.Clear();
        using (MemoryContextTestFixture.Enter(29))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = binary[0]);
            Assert.ThrowsExactly<InvalidOperationException>(text.ToString);
            Assert.ThrowsExactly<InvalidOperationException>(binary.Dispose);
            Assert.ThrowsExactly<InvalidOperationException>(text.Dispose);
            Assert.ThrowsExactly<InvalidOperationException>(cstring.ToOwned);
            Assert.ThrowsExactly<InvalidOperationException>(cstring.Dispose);
        }

        DedicatedThread.Run(() =>
        {
            using var other = new MemoryContextTestFixture();
            using MemoryContextTestFixture.Scope capability = MemoryContextTestFixture.Enter();
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = binary[0]);
            Assert.ThrowsExactly<InvalidOperationException>(() => text.ToString());
            Assert.ThrowsExactly<InvalidOperationException>(() => escaped.DangerousGetBits());
            Assert.ThrowsExactly<InvalidOperationException>(() => cursor.MoveNext());
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = cursor.Current);
            Assert.ThrowsExactly<InvalidOperationException>(binary.Dispose);
            Assert.ThrowsExactly<InvalidOperationException>(text.Dispose);
            Assert.ThrowsExactly<InvalidOperationException>(() => cstring.ToUtf8String());
            Assert.ThrowsExactly<InvalidOperationException>(() => cstring.DangerousGetNullTerminatedSpan().ToArray());
            Assert.ThrowsExactly<InvalidOperationException>(cstring.Dispose);
            Assert.IsEmpty(other.Requests);
        });

        Assert.IsEmpty(fixture.Requests);
        Assert.IsEmpty(script.Deleted);
        Assert.AreEqual((byte)65, binary[0]);
        Assert.AreEqual("A", text.ToString());
        Assert.AreEqual("A", cstring.ToUtf8String());
    }
}
