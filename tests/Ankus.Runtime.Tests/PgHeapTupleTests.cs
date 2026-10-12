using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks owned tuple metadata, physical slots, strict edits, composite arrays, and pointer-free transport boundaries.
/// </summary>
[TestClass]
public sealed class PgHeapTupleTests
{
    /// <summary>
    /// Copies descriptor inputs and exposes immutable metadata, including domains, collation, typmods, and dropped slots.
    /// </summary>
    [TestMethod]
    public void DescriptorOwnsImmutablePhysicalMetadata()
    {
        var original = new PgTupleAttributeInfo("Name", 8200, 1043, 17, 100, isNotNull: true);
        PgTupleAttributeInfo[] attributes = [original, new("dropped", 0, 0, -1, 0, isDropped: true)];
        var descriptor = new PgTupleDescriptor(9100, -1, attributes);
        attributes[0] = new PgTupleAttributeInfo("replacement", 23, 23, -1, 0);
        Assert.AreEqual(9100U, descriptor.TypeOid);
        Assert.AreEqual(9100U, descriptor.BaseTypeOid);
        Assert.AreEqual(-1, descriptor.TypeModifier);
        Assert.HasCount(2, descriptor.Attributes);
        Assert.AreSame(original, descriptor.Attributes[0]);
        Assert.AreEqual("Name", descriptor.Attributes[0].Name);
        Assert.AreEqual(8200U, descriptor.Attributes[0].TypeOid);
        Assert.AreEqual(1043U, descriptor.Attributes[0].BaseTypeOid);
        Assert.AreEqual(17, descriptor.Attributes[0].TypeModifier);
        Assert.AreEqual(100U, descriptor.Attributes[0].CollationOid);
        Assert.IsTrue(descriptor.Attributes[0].IsNotNull);
        Assert.IsFalse(descriptor.Attributes[0].IsDropped);
        Assert.IsFalse(descriptor.Attributes[0].IsComposite);
        Assert.IsTrue(descriptor.Attributes[1].IsDropped);
        IList<PgTupleAttributeInfo> list = Assert.IsInstanceOfType<IList<PgTupleAttributeInfo>>(descriptor.Attributes);
        Assert.ThrowsExactly<NotSupportedException>(() => list[0] = attributes[0]);
        Assert.AreSame(original, descriptor.Attributes[0]);
    }

    /// <summary>
    /// Relation metadata preserves native identity and fields after transport release, including absent index row types.
    /// </summary>
    /// <param name="typeOid">A named, anonymous or absent row identity.</param>
    [TestMethod]
    [DataRow(0U)]
    [DataRow(9100U)]
    [DataRow(2249U)]
    public void PhysicalDescriptorsRetainIdentityWithoutCreatingUntypedValues(uint typeOid)
    {
        byte[] bytes = IndependentTupleBytes();
        BinaryPrimitives.WriteUInt32BigEndian(bytes, typeOid);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(57), 1);
        NativeValue value = NativeValue.FromBytes(bytes);
        TupleFlag(ref value) = -4;
        PgTupleDescriptor descriptor;
        try
        {
            descriptor = value.ReadRelationDescriptor();
            if (typeOid == 0)
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => value.ReadTuple());
            }
            else
            {
                Assert.AreEqual(typeOid, value.ReadTuple().Descriptor.TypeOid);
            }
        }
        finally
        {
            value.Release();
        }

        Assert.AreEqual(typeOid, descriptor.TypeOid);
        Assert.AreEqual(typeOid, descriptor.BaseTypeOid);
        Assert.AreEqual(-1, descriptor.TypeModifier);
        Assert.HasCount(1, descriptor.Attributes);
        Assert.AreEqual(0, descriptor.GetOrdinal("x"));
        PgTupleAttributeInfo attribute = descriptor.Attributes[0];
        Assert.AreEqual("x", attribute.Name);
        Assert.AreEqual(8200U, attribute.TypeOid);
        Assert.AreEqual(23U, attribute.BaseTypeOid);
        Assert.AreEqual(99, attribute.TypeModifier);
        Assert.AreEqual(100U, attribute.CollationOid);
        Assert.IsTrue(attribute.IsNotNull);
        Assert.IsFalse(attribute.IsDropped);
        Assert.IsFalse(attribute.IsComposite);
        Assert.IsFalse(attribute.IsUnavailable);
        if (typeOid == 0)
        {
            (string Name, Action Create)[] factories =
            [
                ("tuple", () => descriptor.CreateTuple()),
                ("empty array", () => descriptor.CreateArray([])),
                ("shaped array", () => descriptor.CreateArray([null], [1], [-2])),
                ("SPI NULL tuple", () => SpiParameter.Create(null, descriptor)),
                ("SPI NULL array", () => SpiParameter.CreateArray(null, descriptor)),
            ];
            foreach ((string name, Action create) in factories)
            {
                InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(create);
                Assert.AreEqual("The physical descriptor has no PostgreSQL row type.", error.Message, name);
            }
        }
        else
        {
            Assert.IsNull(descriptor.CreateTuple()[0]);
            Assert.AreEqual(typeOid, descriptor.CreateArray([]).ElementTypeOid);
            Assert.AreEqual(typeOid, SpiParameter.Create(null, descriptor).TypeOid);
        }
    }

    /// <summary>
    /// Physical metadata rejects value payloads and inconsistent absent-type identities.
    /// </summary>
    /// <param name="kind">The malformed metadata boundary.</param>
    [TestMethod]
    [DataRow("value")]
    [DataRow("registered")]
    [DataRow("base-type")]
    public void PhysicalDescriptorsRejectInconsistentTransports(string kind)
    {
        byte[] bytes = IndependentTupleBytes();
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(57), kind == "value" ? 0 : 1);
        if (kind == "registered")
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 7);
        }

        NativeValue value = NativeValue.FromBytes(bytes);
        TupleFlag(ref value) = -4;
        value.Integral = kind == "base-type" ? 9100 : 0;
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => value.ReadRelationDescriptor());
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Exact names resolve the first live match, while zero-based ordinals preserve dropped physical slots.
    /// </summary>
    [TestMethod]
    public void NamesAndOrdinalsDistinguishDroppedSlotsAndNullValues()
    {
        var descriptor = new PgTupleDescriptor(9100, -1,
        [
            new("same", 0, 0, -1, 0, isDropped: true), new("same", 23, 23, -1, 0),
            new("same", 25, 25, -1, 0), new("Case", 23, 23, -1, 0),
        ]);
        var tuple = new PgHeapTuple(descriptor, [null, 0, "later", null]);
        Assert.AreEqual(4, tuple.Count);
        Assert.AreEqual(1, descriptor.GetOrdinal("same"));
        Assert.AreEqual(0, tuple.Get<int>("same"));
        Assert.AreEqual("later", tuple.Get<string>(2));
        Assert.IsNull(tuple[0]);
        Assert.IsNull(tuple.Get<int?>(0));
        Assert.IsNull(tuple["Case"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => tuple.Get<int>(0));
        Assert.ThrowsExactly<ArgumentException>(() => tuple.Get<int>("case"));
        Assert.ThrowsExactly<ArgumentNullException>(() => descriptor.GetOrdinal(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tuple.Get<int>(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tuple.Get<int>(4));
        Assert.ThrowsExactly<InvalidOperationException>(() => tuple.Set<int?>(0, null));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tuple.Set(-1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tuple.Set(4, 1));
    }

    /// <summary>
    /// New all-null tuples remain nonnull values, including descriptors with zero columns or not-null metadata.
    /// </summary>
    [TestMethod]
    public void ZeroAndAllNullTuplesAreDistinctFromSqlNull()
    {
        var empty = new PgTupleDescriptor(9100, -1, []);
        PgHeapTuple zero = empty.CreateTuple();
        Assert.AreEqual(0, zero.Count);
        NativeValue encoded = NativeValue.FromTuple(zero);
        try
        {
            Assert.AreEqual((byte)0, encoded.IsNull);
            Assert.AreEqual(0, encoded.ReadTuple().Count);
            Assert.HasCount(12, encoded.ReadBytes());
        }
        finally
        {
            encoded.Release();
        }

        var descriptor = new PgTupleDescriptor(9101, -1, [new("required", 23, 23, -1, 0, isNotNull: true)]);
        PgHeapTuple tuple = descriptor.CreateTuple();
        Assert.AreEqual(1, tuple.Count);
        Assert.IsNull(tuple.Get<int?>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => tuple.Get<int>(0));
        Assert.AreEqual(9101U, SpiParameter.Create<PgHeapTuple?>(tuple).TypeOid);
        Assert.AreEqual(2249U, SpiParameter.Create<PgHeapTuple?>(null).TypeOid);
        SpiParameter typedNull = SpiParameter.Create(null, descriptor);
        Assert.AreEqual(9101U, typedNull.TypeOid);
        Assert.IsNull(typedNull.Value);
    }

    /// <summary>
    /// Cell arrays and shallow clones have independent slots while ordinary nested reference values remain shared.
    /// </summary>
    [TestMethod]
    public void TupleAndCloneOwnTheirCellSlots()
    {
        var descriptor = new PgTupleDescriptor(9100, -1, [new("number", 23, 23, -1, 0), new("bytes", 17, 17, -1, 0)]);
        byte[] bytes = [0, 255];
        object?[] values = [42, bytes];
        var tuple = new PgHeapTuple(descriptor, values);
        values[0] = -1;
        Assert.AreEqual(42, tuple.Get<int>(0));
        PgHeapTuple clone = tuple.Clone();
        clone.Set("number", 7);
        Assert.AreEqual(42, tuple.Get<int>(0));
        Assert.AreEqual(7, clone.Get<int>(0));
        Assert.AreSame(descriptor, clone.Descriptor);
        Assert.AreSame(bytes, clone.Get<byte[]>(1));
        bytes[0] = 3;
        Assert.AreEqual((byte)3, tuple.Get<byte[]>(1)[0]);
        Assert.AreEqual((byte)3, clone.Get<byte[]>(1)[0]);
    }

    /// <summary>
    /// Checked edits preserve declared domains and typmods and fail without changing the original cell.
    /// </summary>
    [TestMethod]
    public void EditsPreserveMetadataAndRejectIncompatibleTypesAtomically()
    {
        var descriptor = new PgTupleDescriptor(9100, -1,
            [new("number", 8200, 23, -1, 0), new("text", 1043, 1043, 12, 100)]);
        PgHeapTuple tuple = descriptor.CreateTuple();
        tuple.Set("number", 42);
        tuple.Set("text", "exact");
        Assert.AreEqual(42, tuple.Get<int>(0));
        Assert.AreEqual("exact", tuple.Get<string>(1));
        Assert.ThrowsExactly<InvalidCastException>(() => tuple.Set(0, 42L));
        Assert.ThrowsExactly<InvalidCastException>(() => tuple.Set<string?>(0, null));
        Assert.ThrowsExactly<InvalidCastException>(() => tuple.Set(0, SpiParameter.Create("wrong")));
        Assert.AreEqual(42, tuple.Get<int>(0));
        tuple.Set<int?>(0, null);
        Assert.IsNull(tuple.Get<int?>(0));
        tuple.Set(0, SpiParameter.Create(7));
        Assert.AreEqual(7, tuple.Get<int>(0));
        Assert.AreEqual(8200U, tuple.Descriptor.Attributes[0].TypeOid);
        Assert.AreEqual(23U, tuple.Descriptor.Attributes[0].BaseTypeOid);
        Assert.AreEqual(12, tuple.Descriptor.Attributes[1].TypeModifier);
        Assert.AreEqual(100U, tuple.Descriptor.Attributes[1].CollationOid);
    }

    /// <summary>
    /// Nested composite replacement validates catalog identity, even when another type has identical fields.
    /// </summary>
    [TestMethod]
    public void NestedEditsRequireTheDeclaredCompositeIdentity()
    {
        var childDescriptor = new PgTupleDescriptor(9101, -1, [new("number", 23, 23, -1, 0)]);
        var otherDescriptor = new PgTupleDescriptor(9102, -1, [new("number", 23, 23, -1, 0)]);
        var parentDescriptor = new PgTupleDescriptor(9100, -1, [new("child", 9101, 9101, -1, 0, isComposite: true)]);
        PgHeapTuple parent = parentDescriptor.CreateTuple();
        PgHeapTuple child = childDescriptor.CreateTuple();
        child.Set(0, 9);
        parent.Set(0, child);
        Assert.AreSame(child, parent.Get<PgHeapTuple>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => parent.Set(0, otherDescriptor.CreateTuple()));
        Assert.AreEqual(9, parent.Get<PgHeapTuple>(0).Get<int>(0));
        parent.Set<PgHeapTuple?>(0, null);
        Assert.IsNull(parent.Get<PgHeapTuple?>(0));
        parent.Set(0, SpiParameter.Create(child, childDescriptor));
        Assert.AreSame(child, parent[0]);
        Assert.ThrowsExactly<InvalidCastException>(() => SpiParameter.Create(child, otherDescriptor));
    }

    /// <summary>
    /// Explicit composite arrays copy their shape and retain named type identity without inspecting the first element.
    /// </summary>
    [TestMethod]
    public void DescriptorArraysPreserveIdentityShapeAndOwnership()
    {
        var descriptor = new PgTupleDescriptor(9100, -1, [new("number", 23, 23, -1, 0)]);
        PgHeapTuple tuple = descriptor.CreateTuple();
        tuple.Set(0, 7);
        PgHeapTuple?[] values = [null, tuple];
        int[] lengths = [1, 2];
        int[] bounds = [-3, 4];
        PgArray<PgHeapTuple?> array = descriptor.CreateArray(values, lengths, bounds);
        values[1] = null;
        lengths[0] = 2;
        bounds[0] = 1;
        Assert.AreEqual(9100U, array.ElementTypeOid);
        Assert.AreSequenceEqual([1, 2], array.Lengths.ToArray());
        Assert.AreSequenceEqual([-3, 4], array.LowerBounds.ToArray());
        Assert.IsNull(array.GetValue(-3, 4));
        Assert.AreSame(tuple, array.GetValue(-3, 5));
        Assert.ThrowsExactly<InvalidOperationException>(array.ToVector);
        Assert.AreEqual(7, array.ToArray()[1]!.Get<int>(0));
        var row = new SpiRow([array], [new("value", 9103)]);
        Assert.AreSame(array, row.Get<PgArray<PgHeapTuple?>>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => row.Get<PgHeapTuple?[]>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => row.Get<int[]>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => SpiParameter.Create(array));
    }

    /// <summary>
    /// Empty and all-null arrays retain explicit element identity, while ordinary vectors use PostgreSQL record[].
    /// </summary>
    [TestMethod]
    public void EmptyAndNullArraysKeepNamedIdentityWithoutFirstElementInference()
    {
        var descriptor = new PgTupleDescriptor(9100, -1, []);
        PgArray<PgHeapTuple?> empty = descriptor.CreateArray([], [2, 0], [-4, 3]);
        PgArray<PgHeapTuple?> allNull = descriptor.CreateArray([null, null]);
        Assert.AreEqual(9100U, empty.ElementTypeOid);
        Assert.AreEqual(0, empty.Rank);
        Assert.IsEmpty(empty);
        Assert.AreEqual(9100U, allNull.ElementTypeOid);
        Assert.AreSequenceEqual([null, null], allNull);
        Assert.AreEqual(2249U, new PgArray<PgHeapTuple?>([descriptor.CreateTuple(), null]).ElementTypeOid);
        Assert.AreEqual(2287U, SpiParameter.Create(new PgHeapTuple?[] { descriptor.CreateTuple() }).TypeOid);
        Assert.AreEqual(2287U, SpiParameter.Create<PgHeapTuple?[]?>(null).TypeOid);
        var recordDescriptor = new PgTupleDescriptor(2249, 7, []);
        SpiParameter recordNull = SpiParameter.CreateArray(null, recordDescriptor);
        Assert.AreEqual(2287U, recordNull.TypeOid);
        Assert.IsNull(recordNull.Value);
        var other = new PgTupleDescriptor(9101, -1, []);
        Assert.ThrowsExactly<InvalidCastException>(() => descriptor.CreateArray([other.CreateTuple()]));
        Assert.ThrowsExactly<InvalidCastException>(() => SpiParameter.CreateArray(empty, other));
        Assert.ThrowsExactly<InvalidCastException>(() => recordDescriptor.CreateArray([new PgTupleDescriptor(2249, 8, []).CreateTuple()]));
    }

    /// <summary>
    /// Local SPI row edits take their concrete tuple identity from the value and use record identities for untyped NULLs and vectors.
    /// </summary>
    [TestMethod]
    public void SpiRowEditsRetainConcreteTupleIdentity()
    {
        var descriptor = new PgTupleDescriptor(9100, -1, [new("number", 23, 23, -1, 0)]);
        var tuple = new PgHeapTuple(descriptor, [42]);
        var row = new SpiRow([7], [new("value", 23)]);
        row.Set("value", tuple);
        Assert.AreEqual(9100U, row.GetTypeOid("value"));
        Assert.AreSame(tuple, row.Get<PgHeapTuple>(0));
        row.Set<PgHeapTuple?>(0, null);
        Assert.AreEqual(2249U, row.GetTypeOid(0));
        Assert.IsNull(row.Get<PgHeapTuple?>(0));
        row.Set(0, new PgHeapTuple?[] { null, tuple });
        Assert.AreEqual(2287U, row.GetTypeOid(0));
        PgArray<PgHeapTuple?> array = row.Get<PgArray<PgHeapTuple?>>(0);
        Assert.AreEqual(2249U, array.ElementTypeOid);
        Assert.IsNull(array[0]);
        Assert.AreSame(tuple, array[1]);
    }

    /// <summary>
    /// Rows of one shape read one after another share a descriptor, while a row of the same type and modifier whose
    /// columns differ gets its own.
    /// </summary>
    [TestMethod]
    public void RowsOfOneShapeShareTheirDescriptor()
    {
        var descriptor = new PgTupleDescriptor(9182, -1, [new("name", 25, 25, -1, 100), new("count", 23, 23, -1, 0)]);
        var renamed = new PgTupleDescriptor(9182, -1, [new("name", 25, 25, -1, 100), new("total", 23, 23, -1, 0)]);
        PgHeapTuple first = RoundTrip(new PgHeapTuple(descriptor, ["a", 1]));
        PgHeapTuple second = RoundTrip(new PgHeapTuple(descriptor, ["b", 2]));
        PgHeapTuple third = RoundTrip(new PgHeapTuple(renamed, ["c", 3]));
        Assert.AreSame(first.Descriptor, second.Descriptor);
        Assert.AreEqual("b", second.Get<string>("name"));
        Assert.AreNotSame(first.Descriptor, third.Descriptor);
        Assert.AreEqual(3, third.Get<int>("total"));

        static PgHeapTuple RoundTrip(PgHeapTuple tuple)
        {
            NativeValue encoded = NativeValue.FromTuple(tuple);
            try
            {
                return encoded.ReadTuple();
            }
            finally
            {
                encoded.Release();
            }
        }
    }

    /// <summary>
    /// Independent wire bytes prove type/domain metadata and values without using the tuple writer as the oracle.
    /// </summary>
    [TestMethod]
    public void NativeReaderAcceptsIndependentTupleEncoding()
    {
        NativeValue value = NativeValue.FromBytes(IndependentTupleBytes());
        TupleFlag(ref value) = -4;
        PgHeapTuple tuple;
        try
        {
            tuple = value.ReadTuple();
        }
        finally
        {
            value.Release();
        }

        Assert.AreEqual(9100U, tuple.Descriptor.TypeOid);
        Assert.AreEqual(-1, tuple.Descriptor.TypeModifier);
        Assert.AreEqual(-7, tuple.Get<int>("x"));
        Assert.AreEqual(8200U, tuple.Descriptor.Attributes[0].TypeOid);
        Assert.AreEqual(23U, tuple.Descriptor.Attributes[0].BaseTypeOid);
        Assert.AreEqual(99, tuple.Descriptor.Attributes[0].TypeModifier);
        Assert.AreEqual(100U, tuple.Descriptor.Attributes[0].CollationOid);
        Assert.IsTrue(tuple.Descriptor.Attributes[0].IsNotNull);
    }

    /// <summary>
    /// The writer emits the independent big-endian envelope with exact integer and attribute metadata fields.
    /// </summary>
    [TestMethod]
    public void NativeWriterMatchesTheIndependentTupleEncoding()
    {
        var descriptor = new PgTupleDescriptor(9100, -1, [new("x", 8200, 23, 99, 100, isNotNull: true)]);
        var tuple = new PgHeapTuple(descriptor, [-7]);
        NativeValue value = NativeValue.FromTuple(tuple);
        try
        {
            Assert.IsTrue(value.IsTuple);
            Assert.AreEqual((byte)0, value.IsNull);
            Assert.AreSequenceEqual(IndependentTupleBytes(), value.ReadBytes());
            Assert.AreEqual(-7, Assert.IsInstanceOfType<PgHeapTuple>(SpiType.FromNative(value, 9100)).Get<int>(0));
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Nested tuples, arrays, Unicode text and byte buffers survive release of every native transport buffer.
    /// </summary>
    [TestMethod]
    public void NestedValuesRemainOwnedAfterNativeTransportRelease()
    {
        var childDescriptor = new PgTupleDescriptor(9101, -1,
            [new("text", 25, 25, -1, 100), new("bytes", 17, 17, -1, 0)]);
        var child = new PgHeapTuple(childDescriptor, ["héllo 😀", new byte[] { 0, 255, 3 }]);
        var parentDescriptor = new PgTupleDescriptor(9100, -1,
        [
            new("child", 9101, 9101, -1, 0, isComposite: true), new("children", 9102, 9102, -1, 0),
            new("removed", 0, 0, -1, 0, isDropped: true), new("number", 23, 23, -1, 0),
        ]);
        var parent = new PgHeapTuple(parentDescriptor,
            [child, childDescriptor.CreateArray([null, child], [2], [-2]), null, int.MaxValue]);
        NativeValue value = NativeValue.FromTuple(parent);
        PgHeapTuple copy;
        try
        {
            copy = value.ReadTuple();
        }
        finally
        {
            value.Release();
        }

        child.Set(0, "changed");
        Assert.AreEqual("héllo 😀", copy.Get<PgHeapTuple>(0).Get<string>(0));
        Assert.AreSequenceEqual(new byte[] { 0, 255, 3 }, copy.Get<PgHeapTuple>(0).Get<byte[]>(1));
        PgArray<PgHeapTuple?> children = copy.Get<PgArray<PgHeapTuple?>>(1);
        Assert.AreEqual(9101U, children.ElementTypeOid);
        Assert.AreSequenceEqual([-2], children.LowerBounds.ToArray());
        Assert.IsNull(children.GetValue(-2));
        Assert.AreEqual("héllo 😀", children.GetValue(-1)!.Get<string>(0));
        Assert.IsNull(copy[2]);
        Assert.IsTrue(copy.Descriptor.Attributes[2].IsDropped);
        Assert.AreEqual(int.MaxValue, copy.Get<int>(3));
    }

    /// <summary>
    /// Composite array transport preserves named identity for first-null, all-null, and empty array states.
    /// </summary>
    [TestMethod]
    public void CompositeArrayTransportRetainsIdentityForEveryNullPartition()
    {
        var descriptor = new PgTupleDescriptor(9100, -1, [new("x", 23, 23, -1, 0)]);
        var tuple = new PgHeapTuple(descriptor, [42]);
        PgArray<PgHeapTuple?>[] arrays = [descriptor.CreateArray([]), descriptor.CreateArray([null]), descriptor.CreateArray([null, tuple])];
        foreach (PgArray<PgHeapTuple?> array in arrays)
        {
            NativeValue value = NativeValue.FromArray(array);
            PgArray<PgHeapTuple?> result;
            try
            {
                Assert.AreEqual(2, ElementKind(ref value));
                Assert.AreEqual(9100U, BinaryPrimitives.ReadUInt32BigEndian(value.ReadBytes().AsSpan(8)));
                result = value.ReadArray<PgHeapTuple?>();
                Assert.ThrowsExactly<InvalidCastException>(() => value.ReadArray<long?>());
            }
            finally
            {
                value.Release();
            }

            Assert.AreEqual(9100U, result.ElementTypeOid);
            Assert.AreEqual(array.Count, result.Count);
            Assert.AreEqual(array.Rank, result.Rank);
            if (result.Count != 0)
            {
                Assert.IsNull(result[0]);
            }

            if (result.Count == 2)
            {
                Assert.AreEqual(42, result[1]!.Get<int>(0));
            }
        }
    }

    /// <summary>
    /// Composite domain declarations retain their OIDs while tuple headers and array elements preserve the base row identity.
    /// </summary>
    [TestMethod]
    public void CompositeDomainsRetainDeclaredAndUnderlyingIdentities()
    {
        PgTupleAttributeInfo[] attributes = [new("number", 23, 23, -1, 0)];
        var baseDescriptor = new PgTupleDescriptor(9100, -1, attributes);
        var domainDescriptor = new PgTupleDescriptor(9200, -1, attributes, 9100);
        var baseTuple = new PgHeapTuple(baseDescriptor, [42]);
        PgHeapTuple domainTuple = domainDescriptor.CreateTuple();
        domainTuple.Set(0, 7);
        Assert.AreEqual(9200U, domainTuple.Descriptor.TypeOid);
        Assert.AreEqual(9100U, domainTuple.Descriptor.BaseTypeOid);
        Assert.AreEqual(9200U, SpiParameter.Create(domainTuple).TypeOid);
        Assert.AreEqual(9200U, SpiParameter.Create(baseTuple, domainDescriptor).TypeOid);
        Assert.AreEqual(9200U, SpiParameter.Create(null, domainDescriptor).TypeOid);
        NativeValue value = NativeValue.FromTuple(domainTuple);
        try
        {
            Assert.AreEqual(9100L, value.Integral);
            PgHeapTuple copy = value.ReadTuple();
            Assert.AreEqual(9200U, copy.Descriptor.TypeOid);
            Assert.AreEqual(9100U, copy.Descriptor.BaseTypeOid);
            Assert.AreEqual(7, copy.Get<int>(0));
        }
        finally
        {
            value.Release();
        }

        var parentDescriptor = new PgTupleDescriptor(9300, -1, [new("child", 9200, 9100, -1, 0, isComposite: true)]);
        PgHeapTuple parent = parentDescriptor.CreateTuple();
        parent.Set(0, domainTuple);
        Assert.AreSame(domainTuple, parent.Get<PgHeapTuple>(0));
        PgArray<PgHeapTuple?>[] arrays =
            [domainDescriptor.CreateArray([]), domainDescriptor.CreateArray([null]), domainDescriptor.CreateArray([null, baseTuple])];
        foreach (PgArray<PgHeapTuple?> array in arrays)
        {
            Assert.AreEqual(9200U, array.ElementTypeOid);
            Assert.AreEqual(9100U, array.ElementBaseTypeOid);
            value = NativeValue.FromArray(array);
            PgArray<PgHeapTuple?> copy;
            try
            {
                Assert.AreEqual(9100L, value.Integral);
                copy = value.ReadArray<PgHeapTuple?>();
            }
            finally
            {
                value.Release();
            }

            Assert.AreEqual(9200U, copy.ElementTypeOid);
            Assert.AreEqual(9100U, copy.ElementBaseTypeOid);
            Assert.AreEqual(array.Count, copy.Count);
            if (copy.Count == 2)
            {
                Assert.IsNull(copy[0]);
                Assert.AreEqual(9100U, copy[1]!.Descriptor.TypeOid);
                Assert.AreEqual(42, copy[1]!.Get<int>(0));
            }

            value = NativeValue.FromArray(copy);
            try
            {
                Assert.AreEqual(9100L, value.Integral);
                Assert.AreEqual(9200U, BinaryPrimitives.ReadUInt32BigEndian(value.ReadBytes().AsSpan(8)));
            }
            finally
            {
                value.Release();
            }
        }
    }

    /// <summary>
    /// Catalog identifiers may expand beyond sixty-three bytes when server text is converted into UTF-8 transport.
    /// </summary>
    [TestMethod]
    public void ExpandedCatalogNamesSurviveUtf8Transport()
    {
        string name = new('é', 63);
        var descriptor = new PgTupleDescriptor(9100, -1, [new(name, 23, 23, -1, 0)]);
        var tuple = new PgHeapTuple(descriptor, [42]);
        NativeValue value = NativeValue.FromTuple(tuple);
        try
        {
            Assert.AreEqual(126, BinaryPrimitives.ReadInt32BigEndian(value.ReadBytes().AsSpan(32)));
            PgHeapTuple copy = value.ReadTuple();
            Assert.AreEqual(name, copy.Descriptor.Attributes[0].Name);
            Assert.AreEqual(42, copy.Get<int>(name));
        }
        finally
        {
            value.Release();
        }

        Assert.ThrowsExactly<ArgumentException>(() => PgHeapTuple.Create((name, SpiParameter.Create(42))));
    }

    /// <summary>
    /// Nested tuples and composite arrays reject a mismatched underlying tuple identity before returning cells.
    /// </summary>
    [TestMethod]
    public void NestedAndArrayTransportsRejectForeignBaseIdentities()
    {
        var descriptor = new PgTupleDescriptor(9100, -1, []);
        PgHeapTuple child = descriptor.CreateTuple();
        var parentDescriptor = new PgTupleDescriptor(9200, -1, [new("child", 9101, 9101, -1, 0, isComposite: true)]);
        var parent = new PgHeapTuple(parentDescriptor, [child]);
        NativeValue value = NativeValue.FromTuple(parent);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => value.ReadTuple());
        }
        finally
        {
            value.Release();
        }

        value = NativeValue.FromArray(descriptor.CreateArray([child]));
        value.Integral = 9101;
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => value.ReadArray<PgHeapTuple?>());
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Exact transport name and attribute-count limits remain accepted while their immediately adjacent values fail.
    /// </summary>
    [TestMethod]
    public void DescriptorAndNameCapacityBoundariesAreExact()
    {
        var attribute = new PgTupleAttributeInfo("x", 23, 23, -1, 0);
        PgTupleAttributeInfo[] attributes = [.. Enumerable.Repeat(attribute, 1664)];
        var descriptor = new PgTupleDescriptor(9100, -1, attributes);
        Assert.AreEqual(1664, descriptor.CreateTuple().Count);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgTupleDescriptor(9100, -1, [.. attributes, attribute]));
        string longestName = new('x', 252);
        var namedDescriptor = new PgTupleDescriptor(9100, -1, [new(longestName, 23, 23, -1, 0)]);
        NativeValue value = NativeValue.FromTuple(namedDescriptor.CreateTuple());
        try
        {
            Assert.AreEqual(longestName, value.ReadTuple().Descriptor.Attributes[0].Name);
        }
        finally
        {
            value.Release();
        }

        var tooLongDescriptor = new PgTupleDescriptor(9100, -1, [new(new string('x', 253), 23, 23, -1, 0)]);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.FromTuple(tooLongDescriptor.CreateTuple()));
        byte[] bytes = [.. IndependentTupleBytes().AsSpan(0, 36), .. new byte[253], .. IndependentTupleBytes().AsSpan(37)];
        bytes.AsSpan(36, 253).Fill((byte)'x');
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(32), 253);
        value = NativeValue.FromBytes(bytes);
        TupleFlag(ref value) = -4;
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => value.ReadTuple());
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// More than sixty-four valid nesting levels remain supported, while mutable reference cycles fail safely.
    /// </summary>
    [TestMethod]
    public void DeepValidTuplesWorkAndCyclicValuesDoNotPoisonLaterConversions()
    {
        var descriptor = new PgTupleDescriptor(9100, -1, [new("next", 9100, 9100, -1, 0, isComposite: true)]);
        PgHeapTuple root = descriptor.CreateTuple();
        for (int index = 0; index < 80; index++)
        {
            root = new PgHeapTuple(descriptor, [root]);
        }

        NativeValue value = NativeValue.FromTuple(root);
        try
        {
            PgHeapTuple current = value.ReadTuple();
            for (int index = 0; index < 80; index++)
            {
                current = current.Get<PgHeapTuple>(0);
            }

            Assert.IsNull(current[0]);
        }
        finally
        {
            value.Release();
        }

        PgHeapTuple cyclic = descriptor.CreateTuple();
        cyclic.Set(0, cyclic);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.FromTuple(cyclic));
        cyclic.Set<PgHeapTuple?>(0, null);
        value = NativeValue.FromTuple(cyclic);
        try
        {
            Assert.IsNull(value.ReadTuple()[0]);
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Rejects malformed envelopes, metadata, NULL flags and payload lengths before reading outside the buffer.
    /// </summary>
    /// <param name="kind">The independent envelope corruption.</param>
    [TestMethod]
    [DataRow("short")]
    [DataRow("unmarked")]
    [DataRow("null")]
    [DataRow("trailing")]
    [DataRow("type-zero")]
    [DataRow("negative-count")]
    [DataRow("too-many")]
    [DataRow("name-length")]
    [DataRow("flags")]
    [DataRow("null-flag")]
    [DataRow("negative-payload")]
    [DataRow("truncated-payload")]
    [DataRow("dropped-value")]
    [DataRow("declared-zero")]
    [DataRow("base-zero")]
    [DataRow("name-zero")]
    [DataRow("composite-flag")]
    public void MalformedTupleTransportsAreRejected(string kind)
    {
        byte[] bytes = kind == "short" ? new byte[11] : kind == "trailing" ? [.. IndependentTupleBytes(), 0] : IndependentTupleBytes();
        (int offset, int replacement) = kind switch
        {
            "type-zero" => (0, 0),
            "negative-count" => (8, -1),
            "too-many" => (8, 1665),
            "name-length" => (32, 64),
            "flags" => (28, 8),
            "null-flag" => (57, 2),
            "negative-payload" => (61, -1),
            "truncated-payload" => (61, 1),
            "dropped-value" => (28, 1),
            "declared-zero" => (12, 0),
            "base-zero" => (16, 0),
            "composite-flag" => (28, 4),
            _ => (-1, 0),
        };
        if (offset >= 0)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), replacement);
        }

        if (kind == "name-zero")
        {
            bytes[36] = 0;
        }

        NativeValue value = NativeValue.FromBytes(bytes);
        TupleFlag(ref value) = kind == "unmarked" ? 0 : -4;
        value.IsNull = kind == "null" ? (byte)1 : (byte)0;
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => value.ReadTuple());
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Invalid UTF-8 names are rejected instead of being replaced during native decoding.
    /// </summary>
    [TestMethod]
    public void InvalidUtf8AttributeNamesAreRejected()
    {
        byte[] bytes = IndependentTupleBytes();
        bytes[36] = 255;
        NativeValue value = NativeValue.FromBytes(bytes);
        TupleFlag(ref value) = -4;
        try
        {
            Assert.ThrowsExactly<DecoderFallbackException>(() => value.ReadTuple());
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Invalid construction inputs fail before backend access or publication of inconsistent owned tuples.
    /// </summary>
    [TestMethod]
    public void InvalidDescriptorsAndCellsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgTupleDescriptor(0, -1, []));
        Assert.ThrowsExactly<ArgumentNullException>(() => new PgTupleDescriptor(9100, -1, null!));
        Assert.ThrowsExactly<ArgumentException>(() => new PgTupleDescriptor(9100, -1, [null!]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgTupleDescriptor(9100, -1, new PgTupleAttributeInfo[1665]));
        var descriptor = new PgTupleDescriptor(9100, -1, [new("x", 23, 23, -1, 0)]);
        Assert.ThrowsExactly<ArgumentException>(() => new PgHeapTuple(descriptor, []));
        Assert.ThrowsExactly<ArgumentNullException>(() => new PgHeapTuple(descriptor, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new PgHeapTuple(null!, []));
        var dropped = new PgTupleDescriptor(9100, -1, [new("removed", 0, 0, -1, 0, isDropped: true)]);
        Assert.ThrowsExactly<ArgumentException>(() => new PgHeapTuple(dropped, [1]));
        Assert.ThrowsExactly<ArgumentException>(() => new PgTupleAttributeInfo("", 23, 23, -1, 0));
        Assert.ThrowsExactly<ArgumentException>(() => new PgTupleAttributeInfo("a\0b", 23, 23, -1, 0));
        Assert.ThrowsExactly<ArgumentException>(() => descriptor.CreateArray([null], [2]));
        Assert.ThrowsExactly<ArgumentException>(() => PgHeapTuple.Create(("", SpiParameter.Create(1))));
        Assert.ThrowsExactly<ArgumentException>(() => PgHeapTuple.Create((new string('x', 64), SpiParameter.Create(1))));
    }

    /// <summary>
    /// Descriptor lookup and anonymous record registration require an active backend after validating their arguments.
    /// </summary>
    [TestMethod]
    public void CatalogOperationsRequireBackendAccess()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => PgTupleDescriptor.Load(null!));
        Assert.ThrowsExactly<ArgumentException>(() => PgTupleDescriptor.Load(""));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgTupleDescriptor.Load(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTupleDescriptor.Load("example"));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTupleDescriptor.Load(9100));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgHeapTuple.Create(("x", SpiParameter.Create(1))));
        Assert.ThrowsExactly<InvalidOperationException>(() => SpiParameter.CreateArray(null, new PgTupleDescriptor(9100, -1, [])));
    }

    /// <summary>
    /// Constructs one domain-typed integer attribute without invoking the production tuple writer.
    /// </summary>
    private static byte[] IndependentTupleBytes()
    {
        byte[] bytes = new byte[65];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 9100);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), -1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 8200);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 23);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), 99);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 100);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(28), 2);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(32), 1);
        bytes[36] = (byte)'x';
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(37), -7);
        return bytes;
    }

    /// <summary>
    /// Changes the independent test envelope's tuple discriminator.
    /// </summary>
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_auxiliary1")]
    private static extern ref int TupleFlag(ref NativeValue value);

    /// <summary>
    /// Reads the independent array element-kind discriminator.
    /// </summary>
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_auxiliary2")]
    private static extern ref int ElementKind(ref NativeValue value);
}
