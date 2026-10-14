using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks node view ABI validation, exact tag decisions and failure ordering before raw storage access.
/// </summary>
[TestClass]
public sealed unsafe partial class NodeReferenceTests
{
    private const string ExpectedIdentity = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    /// <summary>
    /// A tag-preserving roundtrip shares the original bytes and address without taking ownership or recapturing a generation.
    /// </summary>
    [TestMethod]
    public void NodeCastsShareValuesTagsAndOriginalRawAddress()
    {
        using var contract = new Contract();
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Respond(fixture, request);
        SampleNode value = new()
        {
            _tag = 7,
            _number = 9
        };
        PgNodeReference<SampleNode> leaf = PgNodes.Borrow(PgMemoryContext.Current.DangerousBorrow<SampleNode>(&value)!);
        fixture.Requests.Clear();
        PgNodeReference<NodeHeader>? root = leaf.TryCast<NodeHeader>();
        Assert.IsNotNull(root);
        PgNodeReference<SampleNode>? roundtrip = root.TryCast<SampleNode>();
        Assert.IsNotNull(roundtrip);
        Assert.AreEqual(7U, roundtrip.Tag);
        Assert.IsTrue(root.IsA(7));
        Assert.IsFalse(root.IsA(8));
        Assert.AreEqual(9, roundtrip.Value._number);
        Assert.AreEqual((nint)(&value), (nint)root.DangerousGetPointer());
        roundtrip.Value = new SampleNode { _tag = 7, _number = -51 };
        Assert.AreEqual(-51, value._number);
        Assert.AreEqual(-51, leaf.Value._number);
        Assert.AreEqual(101, root.LifetimeContext.Id);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation is
            NativeMemoryOperation.Allocate or NativeMemoryOperation.Adopt or NativeMemoryOperation.CaptureGeneration));
    }

    /// <summary>
    /// A rejected tag is distinct from a recognized tag whose complete target storage was never promised.
    /// </summary>
    [TestMethod]
    public void TagRejectionAndIncompleteTargetStorageRemainDistinct()
    {
        using var contract = new Contract();
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Respond(fixture, request);
        NodeHeader value = new()
        {
            _tag = uint.MaxValue
        };
        PgNodeReference<NodeHeader> root = PgNodes.Borrow(PgMemoryContext.Current.DangerousBorrow<NodeHeader>(&value)!);
        Assert.AreEqual(uint.MaxValue, root.Tag);
        Assert.IsNull(root.TryCast<SampleNode>());
        root.Value = new NodeHeader { _tag = 7 };
        Assert.ThrowsExactly<InvalidCastException>(root.TryCast<SampleNode>);
        Assert.AreEqual(7U, value._tag);
    }

    /// <summary>
    /// An untagged union may be a source view even though its generated predicate never accepts a downcast target.
    /// </summary>
    [TestMethod]
    public void SourceOnlyUnionCanUpcastWithoutAcceptingDowncasts()
    {
        using var contract = new Contract();
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Respond(fixture, request);
        SourceOnlyUnion value = new()
        {
            _value = new SampleNode { _tag = 7, _number = 42 }
        };
        PgNodeReference<SourceOnlyUnion> union = PgNodes.Borrow(PgMemoryContext.Current.DangerousBorrow<SourceOnlyUnion>(&value)!);
        PgNodeReference<NodeHeader>? root = union.TryCast<NodeHeader>();
        Assert.IsNotNull(root);
        PgNodeReference<SampleNode>? leaf = root.TryCast<SampleNode>();
        Assert.IsNotNull(leaf);
        Assert.AreEqual(42, leaf.Value._number);
        Assert.AreEqual(7U, root.Tag);
        Assert.AreEqual((nint)(&value), (nint)leaf.DangerousGetPointer());
        Assert.IsNull(root.TryCast<SourceOnlyUnion>());
        Assert.IsNull(union.TryCast<SourceOnlyUnion>());
    }

    /// <summary>
    /// Previously borrowed nodes revalidate the active ABI before each observable operation and retain the original bytes on failure.
    /// </summary>
    /// <param name="operation">The public operation to perform on the retained view.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    public void RetainedNodeViewsRevalidateAbiBeforeEveryAccess(int operation)
    {
        using var contract = new Contract();
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Respond(fixture, request);
        SampleNode value = new()
        {
            _tag = 7,
            _number = 9
        };
        PgNodeReference<SampleNode> node = PgNodes.Borrow(PgMemoryContext.Current.DangerousBorrow<SampleNode>(&value)!);
        fixture.Handler = request => request._operation == NativeMemoryOperation.NativeBinding
            ? throw new PgException("0A000", "changed active ABI")
            : Respond(fixture, request);
        fixture.Requests.Clear();
        Action access = operation switch
        {
            0 => () =>
            {
                _ = node.Value;
            }
            ,
            1 => () => node.Value = default,
            2 => () =>
            {
                _ = node.Tag;
            }
            ,
            3 => () =>
            {
                _ = node.LifetimeContext;
            }
            ,
            4 => () =>
            {
                _ = node.TryCast<NodeHeader>();
            }
            ,
            5 => () =>
            {
                _ = node.DangerousGetPointer();
            }
            ,
            6 => () =>
            {
                _ = node.DangerousToNativeString();
            }
            ,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        PgException error = Assert.ThrowsExactly<PgException>(access);
        Assert.AreEqual("0A000", error.SqlState);
        Assert.AreEqual("changed active ABI", error.Message);
        Assert.AreEqual(NativeMemoryOperation.NativeBinding, Assert.ContainsSingle(fixture.Requests)._operation);
        Assert.AreEqual(7U, value._tag);
        Assert.AreEqual(9, value._number);
        fixture.Handler = request => Respond(fixture, request);
        Assert.AreEqual(9, node.Value._number);
    }

    /// <summary>
    /// Invalid size, alignment, runtime and identity declarations fail before the native binding query or raw memory validation.
    /// </summary>
    /// <param name="invalid">The invalid metadata partition.</param>
    [TestMethod]
    [DataRow("size")]
    [DataRow("zero-size")]
    [DataRow("zero-alignment")]
    [DataRow("non-power-alignment")]
    [DataRow("oversized-alignment")]
    [DataRow("runtime")]
    [DataRow("identity")]
    [DataRow("null-identity")]
    public void InvalidNodeMetadataFailsBeforeNativeAccess(string invalid)
    {
        using var contract = new Contract();
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        SampleNode value = new()
        {
            _tag = 7,
            _number = 9
        };
        PgNativeReference<SampleNode> reference = PgMemoryContext.Current.DangerousBorrow<SampleNode>(&value)!;
        switch (invalid)
        {
            case "size":
                contract.Size = 4;
                break;
            case "zero-size":
                contract.Size = 0;
                break;
            case "zero-alignment":
                contract.Alignment = 0;
                break;
            case "non-power-alignment":
                contract.Alignment = 3;
                break;
            case "oversized-alignment":
                contract.Alignment = 16;
                break;
            case "runtime":
                contract.Runtime = "incompatible-runtime";
                break;
            case "identity":
                contract.Identity = "short";
                break;
            case "null-identity":
                contract.Identity = null!;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalid));
        }

        fixture.Requests.Clear();
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => PgNodes.Borrow(reference));
        Assert.IsEmpty(fixture.Requests);
        Assert.AreEqual(9, value._number);
    }

    /// <summary>
    /// The exact binding identity and PostgreSQL major reach the native capability before any node dereference.
    /// </summary>
    /// <param name="majorMismatch">Whether the major differs instead of the ABI hash.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActiveBindingMismatchPreservesDiagnosticAndPrecedesStorageRead(bool majorMismatch)
    {
        using var contract = new Contract();
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        SampleNode value = new()
        {
            _tag = 7,
            _number = 9
        };
        fixture.Handler = request => Respond(fixture, request);
        PgNativeReference<SampleNode> reference = PgMemoryContext.Current.DangerousBorrow<SampleNode>(&value)!;
        contract.Identity = majorMismatch ? ExpectedIdentity : new string('B', 64);
        contract.Major = majorMismatch ? 17 : 18;
        fixture.Requests.Clear();
        PgException error = Assert.ThrowsExactly<PgException>(() => PgNodes.Borrow(reference));
        Assert.AreEqual("0A000", error.SqlState);
        Assert.AreEqual("different native ABI", error.Message);
        NativeMemoryRequest query = Assert.ContainsSingle(fixture.Requests);
        Assert.AreEqual(NativeMemoryOperation.NativeBinding, query._operation);
        Assert.AreEqual(majorMismatch ? 17 : 18, query._value);
        Assert.AreEqual((nuint)64, query._length);
        Assert.AreEqual(1, fixture.ErrorReleases);
        Assert.AreEqual(9, value._number);
    }

    /// <summary>
    /// A misaligned address fails before any node bytes are read or written.
    /// </summary>
    [TestMethod]
    public void NodeBorrowRejectsMisalignmentBeforeDereferencing()
    {
        using var contract = new Contract();
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Respond(fixture, request);
        long storage = 91;
        PgNativeReference<NodeHeader> reference = PgMemoryContext.Current.DangerousBorrow<NodeHeader>((byte*)&storage + 1)!;
        fixture.Requests.Clear();
        Assert.ThrowsExactly<InvalidOperationException>(() => PgNodes.Borrow(reference));
        Assert.AreEqual(91, storage);
        Assert.IsTrue(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.ReadReference)
            .All(static request => request._length == 0));
    }

    /// <summary>
    /// Null input fails independently of a backend; a detached native view cannot bypass the active capability requirement.
    /// </summary>
    [TestMethod]
    public void NodeBorrowRequiresReferenceAndActiveCapability()
    {
        using var contract = new Contract();
        ArgumentNullException error = Assert.ThrowsExactly<ArgumentNullException>(() => PgNodes.Borrow<SampleNode>(null!));
        Assert.AreEqual("reference", error.ParamName);
        using var fixture = new MemoryContextTestFixture();
        SampleNode value = new()
        {
            _tag = 7,
            _number = 9
        };
        PgNativeReference<SampleNode> reference;
        using (MemoryContextTestFixture.Enter())
        {
            reference = PgMemoryContext.Current.DangerousBorrow<SampleNode>(&value)!;
        }

        fixture.Requests.Clear();
        Assert.ThrowsExactly<InvalidOperationException>(() => PgNodes.Borrow(reference));
        Assert.IsEmpty(fixture.Requests);
    }

    private static NativeMemoryResult Respond(MemoryContextTestFixture fixture, NativeMemoryRequest request)
    {
        if (request._operation == NativeMemoryOperation.NativeBinding)
        {
            string identity = Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)request._data, checked((int)request._length)));
            if (identity != ExpectedIdentity || request._value != 18)
            {
                throw new PgException("0A000", "different native ABI");
            }

            return default;
        }

        if (request._operation is NativeMemoryOperation.ReadReference or NativeMemoryOperation.WriteReference)
        {
            int length = checked((int)request._length);
            if (request._operation == NativeMemoryOperation.ReadReference)
            {
                new ReadOnlySpan<byte>((void*)request._pointer, length).CopyTo(new Span<byte>((void*)request._data, length));
            }
            else
            {
                new ReadOnlySpan<byte>((void*)request._data, length).CopyTo(new Span<byte>((void*)request._pointer, length));
            }

            return new NativeMemoryResult { _pointer = request._pointer };
        }

        return fixture.Respond(request);
    }

    /// <summary>
    /// Supplies independent mutable metadata on the test's thread while restoring any enclosing fixture.
    /// </summary>
    private sealed class Contract : IDisposable
    {
        [ThreadStatic]
        private static Contract? s_current;

        /// <summary>
        /// Makes <paramref name="current"/> the Contract this thread's native callbacks use, or restores the enclosing one.
        /// </summary>
        private static void Install(Contract? current) => s_current = current;

        private readonly Contract? _previous = s_current;

        /// <summary>
        /// Installs metadata without exposing shared state across parallel test threads.
        /// </summary>
        internal Contract() => Install(this);

        /// <summary>
        /// Gets this thread's explicit test contract.
        /// </summary>
        internal static Contract Current => s_current!;

        /// <summary>
        /// Gets or sets the claimed representation size.
        /// </summary>
        internal int Size
        {
            get;
            set;
        } = 8;

        /// <summary>
        /// Gets or sets the claimed representation alignment.
        /// </summary>
        internal int Alignment
        {
            get;
            set;
        } = 4;

        /// <summary>
        /// Gets or sets the selected server major.
        /// </summary>
        internal int Major
        {
            get;
            set;
        } = 18;

        /// <summary>
        /// Gets or sets the claimed full binding identity.
        /// </summary>
        internal string Identity
        {
            get;
            set;
        } = ExpectedIdentity;

        /// <summary>
        /// Gets or sets the claimed runtime ABI.
        /// </summary>
        internal string Runtime
        {
            get;
            set;
        } = RuntimeInformation.RuntimeIdentifier;

        /// <summary>
        /// Restores the enclosing test contract.
        /// </summary>
        public void Dispose() => Install(_previous);
    }

    /// <summary>
    /// Supplies a concrete two-field node independent of the generated declaration implementation.
    /// </summary>
    private struct SampleNode : IPgNativeNode
    {
        /// <summary>
        /// Carries the actual tag prefix.
        /// </summary>
        internal uint _tag;

        /// <summary>
        /// Carries the payload that a prefix cast must retain.
        /// </summary>
        internal int _number;

        static int IPgNativeType.PostgresMajor => Contract.Current.Major;
        static string IPgNativeType.AbiIdentity => Contract.Current.Identity;
        static string IPgNativeType.RuntimeIdentifier => Contract.Current.Runtime;
        static int IPgNativeType.NativeSize => Contract.Current.Size;
        static int IPgNativeType.NativeAlignment => Contract.Current.Alignment;
        static bool IPgNativeNode.AcceptsTag(uint tag) => tag == 7;
    }

    /// <summary>
    /// Supplies a universal four-byte node prefix for independent cast and extent tests.
    /// </summary>
    private struct NodeHeader : IPgNativeNode
    {
        /// <summary>
        /// Carries the exact tag bits.
        /// </summary>
        internal uint _tag;

        static int IPgNativeType.PostgresMajor => 18;
        static string IPgNativeType.AbiIdentity => ExpectedIdentity;
        static string IPgNativeType.RuntimeIdentifier => RuntimeInformation.RuntimeIdentifier;
        static int IPgNativeType.NativeSize => 4;
        static int IPgNativeType.NativeAlignment => 4;
        static bool IPgNativeNode.AcceptsTag(uint tag) => true;
    }

    /// <summary>
    /// Models the source-only cast semantics of the generated PostgreSQL 15 and later ValUnion.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct SourceOnlyUnion : IPgNativeNode
    {
        /// <summary>
        /// Overlaps the concrete node with the union's tag prefix.
        /// </summary>
        [FieldOffset(0)]
        internal SampleNode _value;

        static int IPgNativeType.PostgresMajor => 18;
        static string IPgNativeType.AbiIdentity => ExpectedIdentity;
        static string IPgNativeType.RuntimeIdentifier => RuntimeInformation.RuntimeIdentifier;
        static int IPgNativeType.NativeSize => 8;
        static int IPgNativeType.NativeAlignment => 4;
        static bool IPgNativeNode.AcceptsTag(uint tag) => false;
    }
}
