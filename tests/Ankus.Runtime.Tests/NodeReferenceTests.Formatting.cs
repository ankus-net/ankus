using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.Runtime.Tests;

public sealed unsafe partial class NodeReferenceTests
{
    [ThreadStatic]
    private static int s_outputReleases;

    /// <summary>
    /// Clears the count of output buffers the static native callback released.
    /// </summary>
    private static void ResetOutputReleases() => s_outputReleases = 0;

    /// <summary>
    /// Allocation uses measured alignment, zero initialization and only the exact four-byte caller tag.
    /// </summary>
    /// <param name="tag">The exact tag, including values outside the sample cast predicate.</param>
    [TestMethod]
    [DataRow(7U)]
    [DataRow(uint.MaxValue)]
    public void NodeAllocationPreservesZeroPolicyTagAndOwnership(uint tag)
    {
        using var contract = new Contract();
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] storage = new byte[8];
        fixture.Handler = request => request._operation == NativeMemoryOperation.NativeBinding
            ? Respond(fixture, request) : fixture.RespondWithStorage(request, storage);
        PgMemoryContext owner = PgMemoryContext.Current;
        fixture.Requests.Clear();
        using PgNativeBox<SampleNode> box = PgNodes.DangerousAllocate<SampleNode>(tag, owner);
        Assert.AreEqual(NativeMemoryOperation.NativeBinding, fixture.Requests[0]._operation);
        NativeMemoryRequest allocation = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Allocate, fixture.Requests);
        Assert.AreEqual(101, allocation._context);
        Assert.AreEqual((nuint)8, allocation._length);
        Assert.AreEqual((nuint)4, allocation._alignment);
        Assert.AreEqual(1, allocation._flags);
        NativeMemoryRequest write = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Write, fixture.Requests);
        Assert.AreEqual((nuint)4, write._length);
        Assert.AreEqual(501, write._context);
        Assert.AreEqual(0, write._value);
        Assert.AreEqual(tag, box.Value._tag);
        Assert.AreEqual(0, box.Value._number);
        Assert.AreEqual(PgAllocationOptions.Zeroed, box.Options);
        PgContextValue<SampleNode> transferred = box.ReleaseToContext();
        box.Dispose();
        Assert.AreEqual(tag, transferred.Value._tag);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Free));
    }

    /// <summary>
    /// Incompatible metadata or the active native ABI prevents allocation and preserves its diagnostic.
    /// </summary>
    /// <param name="nativeMismatch">Whether the active native ABI rather than local metadata is invalid.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NodeAllocationValidatesBeforeAllocating(bool nativeMismatch)
    {
        using var contract = new Contract();
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Respond(fixture, request);
        if (nativeMismatch)
        {
            contract.Identity = new string('B', 64);
            PgException error = Assert.ThrowsExactly<PgException>(() => PgNodes.DangerousAllocate<SampleNode>(7));
            Assert.AreEqual("0A000", error.SqlState);
        }
        else
        {
            contract.Size = 4;
            Assert.ThrowsExactly<PlatformNotSupportedException>(() => PgNodes.DangerousAllocate<SampleNode>(7));
        }

        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Allocate));
    }

    /// <summary>
    /// Tag-write failure releases the new allocation, retaining both errors when native cleanup also fails.
    /// </summary>
    /// <param name="cleanupFails">Whether individual native release also reports an error.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NodeAllocationFailurePreservesCleanupErrors(bool cleanupFails)
    {
        using var contract = new Contract();
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => request._operation switch
        {
            NativeMemoryOperation.Write => throw new PgException("XX001", "tag write failed"),
            NativeMemoryOperation.Free when cleanupFails => throw new PgException("XX002", "release failed"),
            _ => Respond(fixture, request),
        };
        if (cleanupFails)
        {
            AggregateException error = Assert.ThrowsExactly<AggregateException>(() => PgNodes.DangerousAllocate<SampleNode>(7));
            Assert.HasCount(2, error.InnerExceptions);
            Assert.AreEqual("XX001", Assert.IsInstanceOfType<PgException>(error.InnerExceptions[0]).SqlState);
            Assert.AreEqual("XX002", Assert.IsInstanceOfType<PgException>(error.InnerExceptions[1]).SqlState);
        }
        else
        {
            PgException error = Assert.ThrowsExactly<PgException>(() => PgNodes.DangerousAllocate<SampleNode>(7));
            Assert.AreEqual("tag write failed", error.Message);
        }

        Assert.AreEqual(501, Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Free, fixture.Requests)._context);
    }

    /// <summary>
    /// Native formatting retains allocation identity, byte offset and resized extent; disposal prevents dispatch.
    /// </summary>
    [TestMethod]
    public void NodeFormattingRetainsAllocationOffsetAndCurrentExtent()
    {
        using var contract = new Contract();
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => request._operation switch
        {
            NativeMemoryOperation.FormatNode => Output(request),
            NativeMemoryOperation.Read => new NativeMemoryResult { _pointer = 4096 },
            _ => Respond(fixture, request),
        };
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(32);
        PgNodeReference<SampleNode> node = PgNodes.Borrow(allocation.Borrow<SampleNode>(8));
        allocation.Reallocate(24);
        fixture.Requests.Clear();
        ResetOutputReleases();
        Assert.AreEqual("native café 🐘", node.DangerousToNativeString());
        NativeMemoryRequest format = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.FormatNode, fixture.Requests);
        Assert.AreEqual(502, format._context);
        Assert.AreEqual(8, format._value);
        Assert.AreEqual((nuint)16, format._length);
        Assert.AreEqual(0, format._pointer);
        Assert.AreEqual(0, format._other);
        Assert.AreEqual(1, s_outputReleases);
        allocation.Reallocate(15);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(node.DangerousToNativeString);
        allocation.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(node.DangerousToNativeString);
        Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.FormatNode, fixture.Requests);
    }

    /// <summary>
    /// Native formatting retains a raw anchor's original generation and releases output on success and both error paths.
    /// </summary>
    /// <param name="failure">Zero for success, one for invalid UTF8, two for a native error after output allocation.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void NodeFormattingRetainsRawGenerationAndReleasesOutput(int failure)
    {
        using var contract = new Contract();
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request =>
        {
            if (request._operation == NativeMemoryOperation.FormatNode)
            {
                _ = Output(request, failure == 1);
                if (failure == 2)
                {
                    throw new PgException("54001", "native traversal failed");
                }

                return default;
            }

            return Respond(fixture, request);
        };
        SampleNode value = new()
        {
            _tag = 7,
            _number = 99
        };
        PgNodeReference<SampleNode> node = PgNodes.Borrow(PgMemoryContext.Current.DangerousBorrow<SampleNode>(&value)!);
        fixture.Requests.Clear();
        ResetOutputReleases();
        if (failure == 1)
        {
            Assert.ThrowsExactly<DecoderFallbackException>(node.DangerousToNativeString);
        }
        else if (failure == 2)
        {
            PgException error = Assert.ThrowsExactly<PgException>(node.DangerousToNativeString);
            Assert.AreEqual("54001", error.SqlState);
            Assert.AreEqual("native traversal failed", error.Message);
        }
        else
        {
            Assert.AreEqual("native café 🐘", node.DangerousToNativeString());
        }

        NativeMemoryRequest format = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.FormatNode, fixture.Requests);
        Assert.AreEqual(101, format._context);
        Assert.AreEqual(901, format._other);
        Assert.AreEqual((nint)(&value), format._pointer);
        Assert.AreEqual((nuint)8, format._length);
        Assert.AreEqual(1, s_outputReleases);
        Assert.AreEqual(99, value._number);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.CaptureGeneration));
    }

    private static NativeMemoryResult Output(NativeMemoryRequest request, bool invalidUtf8 = false)
    {
        NativeValue* output = (NativeValue*)request._data;
        *output = invalidUtf8 ? NativeValue.FromBytes([255]) : NativeValue.FromString("native café 🐘");
        OutputRelease(ref *output) = &ReleaseOutput;
        return default;
    }

    /// <summary>
    /// Accesses the real transport release callback to observe allocator-matched cleanup.
    /// </summary>
    private static ref delegate* unmanaged[Cdecl]<void*, void> OutputRelease(ref NativeValue value) => ref value._release;

    /// <summary>
    /// Counts released output buffers and uses the transport's original allocator.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseOutput(void* data)
    {
        s_outputReleases++;
        NativeMemory.Free(data);
    }
}
