using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks detached trigger contexts, native invocation validation, and unavailable generated column values.
/// </summary>
[TestClass]
public sealed class PgTriggerTests
{
    /// <summary>
    /// Decodes each supported event from independent PostgreSQL bit values and keeps OLD and NEW distinct.
    /// </summary>
    [TestMethod]
    [DataRow(4U, PgTriggerOperation.Insert, PgTriggerTiming.After, PgTriggerLevel.Row, false, true)]
    [DataRow(5U, PgTriggerOperation.Delete, PgTriggerTiming.After, PgTriggerLevel.Row, true, false)]
    [DataRow(6U, PgTriggerOperation.Update, PgTriggerTiming.After, PgTriggerLevel.Row, true, true)]
    [DataRow(12U, PgTriggerOperation.Insert, PgTriggerTiming.Before, PgTriggerLevel.Row, false, true)]
    [DataRow(13U, PgTriggerOperation.Delete, PgTriggerTiming.Before, PgTriggerLevel.Row, true, false)]
    [DataRow(14U, PgTriggerOperation.Update, PgTriggerTiming.Before, PgTriggerLevel.Row, true, true)]
    [DataRow(20U, PgTriggerOperation.Insert, PgTriggerTiming.InsteadOf, PgTriggerLevel.Row, false, true)]
    [DataRow(21U, PgTriggerOperation.Delete, PgTriggerTiming.InsteadOf, PgTriggerLevel.Row, true, false)]
    [DataRow(22U, PgTriggerOperation.Update, PgTriggerTiming.InsteadOf, PgTriggerLevel.Row, true, true)]
    [DataRow(0U, PgTriggerOperation.Insert, PgTriggerTiming.After, PgTriggerLevel.Statement, false, false)]
    [DataRow(1U, PgTriggerOperation.Delete, PgTriggerTiming.After, PgTriggerLevel.Statement, false, false)]
    [DataRow(2U, PgTriggerOperation.Update, PgTriggerTiming.After, PgTriggerLevel.Statement, false, false)]
    [DataRow(3U, PgTriggerOperation.Truncate, PgTriggerTiming.After, PgTriggerLevel.Statement, false, false)]
    [DataRow(8U, PgTriggerOperation.Insert, PgTriggerTiming.Before, PgTriggerLevel.Statement, false, false)]
    [DataRow(9U, PgTriggerOperation.Delete, PgTriggerTiming.Before, PgTriggerLevel.Statement, false, false)]
    [DataRow(10U, PgTriggerOperation.Update, PgTriggerTiming.Before, PgTriggerLevel.Statement, false, false)]
    [DataRow(11U, PgTriggerOperation.Truncate, PgTriggerTiming.Before, PgTriggerLevel.Statement, false, false)]
    [DataRow(38U, PgTriggerOperation.Update, PgTriggerTiming.After, PgTriggerLevel.Row, true, true)]
    [DataRow(70U, PgTriggerOperation.Update, PgTriggerTiming.After, PgTriggerLevel.Row, true, true)]
    [DataRow(102U, PgTriggerOperation.Update, PgTriggerTiming.After, PgTriggerLevel.Row, true, true)]
    public void EventsDecodeExactOperationTimingAndLevel(uint eventBits, PgTriggerOperation operation,
        PgTriggerTiming timing, PgTriggerLevel level, bool hasOld, bool hasNew)
    {
        using NativeEnvelope envelope = CreateEnvelope(eventBits, hasOld, hasNew);
        PgTriggerContext context = NativeValue.ReadTriggerContext(envelope.Values);
        Assert.AreEqual(eventBits, context.Event);
        Assert.AreEqual(operation, context.Operation);
        Assert.AreEqual(timing, context.Timing);
        Assert.AreEqual(level, context.Level);
        if (hasOld)
        {
            Assert.IsNotNull(context.Old);
            Assert.AreEqual(-7, context.Old.Get<int>(0));
        }
        else
        {
            Assert.IsNull(context.Old);
        }

        if (hasNew)
        {
            Assert.IsNotNull(context.New);
            Assert.AreEqual(42, context.New.Get<int>(0));
        }
        else
        {
            Assert.IsNull(context.New);
        }
    }

    /// <summary>
    /// Owned names, ordered arguments, row cells, and descriptor metadata survive freeing every native buffer.
    /// </summary>
    [TestMethod]
    public void ContextOwnsMetadataAndRowsAfterTransportRelease()
    {
        PgTriggerContext context;
        using (NativeEnvelope envelope = CreateEnvelope(6, true, true))
        {
            envelope.Replace(6, NativeValue.FromString("old rows"));
            envelope.Replace(7, NativeValue.FromString("new rows"));
            context = NativeValue.ReadTriggerContext(envelope.Values);
        }

        Assert.AreEqual("audit \"é\" 😀", context.Name);
        Assert.AreEqual("table é", context.TableName);
        Assert.AreEqual("schema space", context.TableSchema);
        Assert.AreEqual(8100U, context.RelationOid);
        Assert.AreEqual(8200U, context.TriggerOid);
        Assert.AreEqual(9100U, context.Descriptor.TypeOid);
        Assert.AreEqual("old rows", context.OldTransitionTableName);
        Assert.AreEqual("new rows", context.NewTransitionTableName);
        Assert.AreSequenceEqual(["", "a,b", "é 😀", "quote\"slash\\"], context.Arguments);
        IList<string> arguments = Assert.IsInstanceOfType<IList<string>>(context.Arguments);
        Assert.ThrowsExactly<NotSupportedException>(() => arguments[0] = "changed");
        Assert.AreEqual("", context.Arguments[0]);
        Assert.AreEqual("number", context.Descriptor.Attributes[0].Name);
        Assert.IsNotNull(context.Old);
        Assert.IsNotNull(context.New);
        context.New.Set(0, 99);
        Assert.AreEqual(-7, context.Old.Get<int>(0));
        Assert.AreEqual(99, context.New.Get<int>(0));
        Assert.IsNull(context.Descriptor.CreateTuple().Get<int?>(0));
    }

    /// <summary>
    /// Empty arguments and absent transition relations remain distinct from nonempty declarations, and input arrays are copied.
    /// </summary>
    [TestMethod]
    public void ArgumentsAreCopiedAndOptionalNamesStayNull()
    {
        string[] arguments = ["first", "second"];
        var descriptor = new PgTupleDescriptor(9100, -1, []);
        var context = new PgTriggerContext(3, 8100, 8200, "trigger", "table", "schema", null, null,
            arguments, null, null, descriptor);
        arguments[0] = "changed";
        Assert.AreSequenceEqual(["first", "second"], context.Arguments);
        Assert.IsNull(context.OldTransitionTableName);
        Assert.IsNull(context.NewTransitionTableName);
        using NativeEnvelope envelope = CreateEnvelope(3);
        envelope.Replace(8, NativeValue.FromArray(new PgArray<string>([])));
        PgTriggerContext empty = NativeValue.ReadTriggerContext(envelope.Values);
        Assert.IsEmpty(empty.Arguments);
        Assert.IsNull(empty.OldTransitionTableName);
        Assert.IsNull(empty.NewTransitionTableName);
    }

    /// <summary>
    /// Rejects unknown bits, mutually exclusive timing flags, row TRUNCATE, and statement INSTEAD OF events.
    /// </summary>
    [TestMethod]
    [DataRow(128U)]
    [DataRow(0x80000000U)]
    [DataRow(24U)]
    [DataRow(28U)]
    [DataRow(7U)]
    [DataRow(15U)]
    [DataRow(23U)]
    [DataRow(16U)]
    [DataRow(17U)]
    [DataRow(18U)]
    [DataRow(19U)]
    public void InvalidEventCombinationsAreRejected(uint eventBits)
    {
        using NativeEnvelope envelope = CreateEnvelope(eventBits);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// Rejects missing required rows and extraneous rows instead of guessing which native tuple slot was intended.
    /// </summary>
    [TestMethod]
    [DataRow(4U, false, false)]
    [DataRow(4U, true, true)]
    [DataRow(5U, false, false)]
    [DataRow(5U, true, true)]
    [DataRow(6U, false, true)]
    [DataRow(6U, true, false)]
    [DataRow(0U, false, true)]
    [DataRow(1U, true, false)]
    [DataRow(2U, true, true)]
    [DataRow(3U, false, true)]
    public void RowPresenceMustMatchTheEvent(uint eventBits, bool hasOld, bool hasNew)
    {
        using NativeEnvelope envelope = CreateEnvelope(eventBits, hasOld, hasNew);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// Calls from one native call site share what was parsed from its unchanging slots, and their rows reuse its
    /// descriptor. The same identity with a different buffer, or an unidentified call, is read in full.
    /// </summary>
    [TestMethod]
    public void CallSiteMetadataIsParsedOnce()
    {
        using NativeEnvelope envelope = CreateEnvelope(4, hasNew: true, site: 7001);
        PgTriggerContext first = NativeValue.ReadTriggerContext(envelope.Values);
        envelope.Replace(10, NativeValue.FromTuple(new PgHeapTuple(CreateDescriptor(), [43])));
        PgTriggerContext second = NativeValue.ReadTriggerContext(envelope.Values);
        Assert.AreSame(first.Descriptor, second.Descriptor);
        Assert.AreSame(first.Descriptor, second.New!.Descriptor);
        Assert.AreSame(first.Arguments, second.Arguments);
        Assert.AreSame(first.Name, second.Name);
        Assert.AreEqual(42, first.New!.Get<int>(0));
        Assert.AreEqual(43, second.New.Get<int>(0));

        envelope.Replace(8, NativeValue.FromArray(new PgArray<string>(["other"])));
        PgTriggerContext changed = NativeValue.ReadTriggerContext(envelope.Values);
        Assert.AreSequenceEqual(["other"], changed.Arguments);
        Assert.AreNotSame(first.Descriptor, changed.Descriptor);

        using NativeEnvelope unidentified = CreateEnvelope(4, hasNew: true);
        Assert.AreNotSame(NativeValue.ReadTriggerContext(unidentified.Values).Descriptor,
            NativeValue.ReadTriggerContext(unidentified.Values).Descriptor);
    }

    /// <summary>
    /// The generated invocation shape has exactly sixteen slots, with no ignored extra metadata.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(15)]
    [DataRow(17)]
    public void InvocationRequiresExactlySixteenSlots(int count)
    {
        var values = new NativeValue[count];
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(values));
    }

    /// <summary>
    /// Event, catalog and call site identities must be nonnull, unsigned, scalar values, and catalog OIDs cannot be invalid zero.
    /// </summary>
    [TestMethod]
    [DataRow(0, -1L)]
    [DataRow(0, 4294967296L)]
    [DataRow(1, -1L)]
    [DataRow(1, 4294967296L)]
    [DataRow(1, 0L)]
    [DataRow(2, -1L)]
    [DataRow(2, 4294967296L)]
    [DataRow(2, 0L)]
    [DataRow(15, -1L)]
    public void NumericMetadataRejectsOutOfRangeValues(int slot, long value)
    {
        using NativeEnvelope envelope = CreateEnvelope(3);
        envelope.Replace(slot, new NativeValue { Integral = value });
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// Column lists and constraint flags arrive as copies, and an UPDATE without a reported set still lists no columns.
    /// </summary>
    [TestMethod]
    public void ColumnsAndConstraintFlagsArriveAsCopies()
    {
        using NativeEnvelope update = CreateEnvelope(14, hasOld: true, hasNew: true);
        update.Replace(12, NativeValue.FromArray(new PgArray<int>([0])));
        update.Values[14].Integral = 7300;
        TransportKind(ref update.Values[14]) = 3;
        PgTriggerContext context = NativeValue.ReadTriggerContext(update.Values);
        Assert.AreSequenceEqual([0], context.UpdateOfColumns);
        Assert.AreSequenceEqual([0], context.UpdatedColumns!);
        Assert.AreEqual(7300U, context.ConstraintOid);
        Assert.IsTrue(context.IsDeferrable);
        Assert.IsTrue(context.IsInitiallyDeferred);

        using NativeEnvelope insert = CreateEnvelope(4, hasNew: true);
        PgTriggerContext inserted = NativeValue.ReadTriggerContext(insert.Values);
        Assert.IsEmpty(inserted.UpdateOfColumns);
        Assert.IsNull(inserted.UpdatedColumns);
        Assert.AreEqual(0U, inserted.ConstraintOid);
        Assert.IsFalse(inserted.IsDeferrable);
        Assert.IsEmpty(new PgTriggerContext(14, 8100, 8200, "trigger", "table", "schema", null, null, [],
            new PgHeapTuple(CreateDescriptor(), [1]), new PgHeapTuple(CreateDescriptor(), [2]), CreateDescriptor()).UpdatedColumns!);
    }

    /// <summary>
    /// Column ordinals outside the relation, updated columns outside an UPDATE, and an initially deferred trigger that is
    /// not deferrable are rejected.
    /// </summary>
    /// <param name="slot">The slot to replace.</param>
    /// <param name="eventBits">The trigger event.</param>
    [TestMethod]
    [DataRow(12, 14U)]
    [DataRow(13, 14U)]
    [DataRow(13, 4U)]
    [DataRow(14, 14U)]
    public void InvalidColumnsAndConstraintFlagsAreRejected(int slot, uint eventBits)
    {
        bool update = (eventBits & 3) == 2;
        using NativeEnvelope envelope = CreateEnvelope(eventBits, hasOld: update, hasNew: true);
        if (slot == 14)
        {
            TransportKind(ref envelope.Values[14]) = 2;
        }
        else
        {
            envelope.Replace(slot, NativeValue.FromArray(new PgArray<int>([update ? 1 : 0])));
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// Catalog OIDs preserve their full unsigned range independently of the relation's composite identity.
    /// </summary>
    [TestMethod]
    public void OidsPreserveUnsignedValues()
    {
        using NativeEnvelope envelope = CreateEnvelope(3);
        envelope.Values[1].Integral = uint.MaxValue;
        envelope.Values[2].Integral = uint.MaxValue - 1;
        PgTriggerContext context = NativeValue.ReadTriggerContext(envelope.Values);
        Assert.AreEqual(uint.MaxValue, context.RelationOid);
        Assert.AreEqual(uint.MaxValue - 1, context.TriggerOid);
        Assert.AreEqual(9100U, context.Descriptor.TypeOid);
    }

    /// <summary>
    /// Trigger names require nonempty, zero-free strings and permit UTF-8 expansion of server catalog identifiers.
    /// </summary>
    [TestMethod]
    [DataRow(3, "")]
    [DataRow(4, "")]
    [DataRow(5, "")]
    [DataRow(6, "")]
    [DataRow(7, "")]
    [DataRow(3, "bad\0name")]
    [DataRow(4, "bad\0name")]
    [DataRow(5, "bad\0name")]
    [DataRow(6, "bad\0name")]
    [DataRow(7, "bad\0name")]
    public void NamesRejectEmptyAndZeroCharacters(int slot, string name)
    {
        using NativeEnvelope envelope = CreateEnvelope(3);
        envelope.Replace(slot, NativeValue.FromBytes(Encoding.UTF8.GetBytes(name)));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// Strict decoding accepts the catalog expansion limit and rejects oversized or invalid UTF-8 names.
    /// </summary>
    [TestMethod]
    public void NameEncodingIsStrictAndAllowsExpandedCatalogIdentifiers()
    {
        using NativeEnvelope envelope = CreateEnvelope(3);
        string expanded = string.Concat(Enumerable.Repeat("😀", 63));
        envelope.Replace(3, NativeValue.FromString(expanded));
        Assert.AreEqual(expanded, NativeValue.ReadTriggerContext(envelope.Values).Name);
        envelope.Replace(3, NativeValue.FromString(expanded + "a"));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
        envelope.Replace(3, NativeValue.FromBytes([0xc3, 0x28]));
        Assert.ThrowsExactly<DecoderFallbackException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// Scalar and nullable headers reject conflicting payloads rather than silently discarding them.
    /// </summary>
    [TestMethod]
    [DataRow(0, "null")]
    [DataRow(1, "buffer")]
    [DataRow(2, "marker")]
    [DataRow(3, "null")]
    [DataRow(4, "integer")]
    [DataRow(5, "marker")]
    [DataRow(6, "null-integer")]
    [DataRow(7, "invalid-null")]
    [DataRow(9, "null-buffer")]
    [DataRow(10, "invalid-null")]
    public void HeadersRejectConflictingNullAndPayloadStates(int slot, string defect)
    {
        using NativeEnvelope envelope = CreateEnvelope(3);
        NativeValue replacement = defect switch
        {
            "null" => new NativeValue { IsNull = 1 },
            "buffer" or "integer" => NativeValue.FromString("name"),
            "marker" => slot <= 2 ? new NativeValue { Integral = 3 } : NativeValue.FromString("name"),
            "null-integer" => new NativeValue { IsNull = 1, Integral = 1 },
            "invalid-null" => new NativeValue { IsNull = 2 },
            "null-buffer" => NativeValue.FromString("ignored"),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        if (defect == "null-buffer")
        {
            replacement.IsNull = 1;
        }

        if (defect is "buffer" or "integer")
        {
            replacement.Integral = defect == "buffer" ? 8100 : 1;
        }

        if (defect == "marker")
        {
            TransportKind(ref replacement) = 1;
        }

        envelope.Replace(slot, replacement);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// Trigger arguments reject SQL NULL elements, zero characters, and shapes that would lose indexing information.
    /// </summary>
    [TestMethod]
    [DataRow("null-array")]
    [DataRow("null-element")]
    [DataRow("multidimensional")]
    [DataRow("lower-bound")]
    public void ArgumentsRejectInvalidContentsAndShapes(string defect)
    {
        using NativeEnvelope envelope = CreateEnvelope(3);
        NativeValue replacement = defect switch
        {
            "null-array" => new NativeValue { IsNull = 1 },
            "null-element" => NativeValue.FromArray(new PgArray<string?>([null])),
            "multidimensional" => NativeValue.FromArray(new PgArray<string>(["one"], [1, 1])),
            "lower-bound" => NativeValue.FromArray(new PgArray<string>(["one"], [1], [0])),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        envelope.Replace(8, replacement);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// An array of a different PostgreSQL element type cannot masquerade as the trigger argument vector.
    /// </summary>
    [TestMethod]
    public void ArgumentsRequireTextElementIdentity()
    {
        using NativeEnvelope envelope = CreateEnvelope(3);
        envelope.Replace(8, NativeValue.FromArray(new PgArray<int>([42])));
        Assert.ThrowsExactly<InvalidCastException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// Native trigger arguments reject embedded zero bytes independently of the managed text writer's validation.
    /// </summary>
    [TestMethod]
    public void ArgumentsRejectZeroCharactersInNativeText()
    {
        using NativeEnvelope envelope = CreateEnvelope(3);
        envelope.Replace(8, NativeValue.FromArray(new PgArray<string>(["bad-value"])));
        byte[] bytes = envelope.Values[8].ReadBytes();
        bytes[51] = 0;
        NativeValue malformed = NativeValue.FromBytes(bytes);
        TransportKind(ref malformed) = -1;
        envelope.Replace(8, malformed);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// A row descriptor must retain the target's physical layout and metadata, not just the same number of cells.
    /// </summary>
    [TestMethod]
    [DataRow("type")]
    [DataRow("modifier")]
    [DataRow("count")]
    [DataRow("name")]
    [DataRow("field-type")]
    [DataRow("field-base")]
    [DataRow("field-modifier")]
    [DataRow("collation")]
    [DataRow("dropped")]
    [DataRow("not-null")]
    [DataRow("composite")]
    public void RowDescriptorsRequireMatchingPhysicalMetadata(string defect)
    {
        using NativeEnvelope envelope = CreateEnvelope(4, hasNew: true);
        var attribute = new PgTupleAttributeInfo(defect == "name" ? "other" : "number",
            defect == "field-type" ? 8201U : 23U, defect == "field-base" ? 20U : 23U,
            defect == "field-modifier" ? 3 : -1, defect == "collation" ? 100U : 0U,
            isDropped: defect == "dropped", isNotNull: defect == "not-null", isComposite: defect == "composite");
        var descriptor = new PgTupleDescriptor(defect == "type" ? 9101U : 9100U,
            defect == "modifier" ? 1 : -1, defect == "count" ? [] : [attribute]);
        envelope.Replace(10, NativeValue.FromTuple(descriptor.CreateTuple()));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// The descriptor slot carries metadata only and cannot conceal a populated row in its cells.
    /// </summary>
    [TestMethod]
    public void DescriptorEnvelopeMustContainOnlyNullCells()
    {
        using NativeEnvelope envelope = CreateEnvelope(3);
        envelope.Replace(11, NativeValue.FromTuple(new PgHeapTuple(CreateDescriptor(), [17])));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// OLD has its own identity validation even when NEW has the correct target descriptor.
    /// </summary>
    [TestMethod]
    public void OldDescriptorIsCheckedIndependentlyOfNew()
    {
        using NativeEnvelope envelope = CreateEnvelope(6, true, true);
        var other = new PgTupleDescriptor(9101, -1, [new("number", 23, 23, -1, 0)]);
        envelope.Replace(9, NativeValue.FromTuple(new PgHeapTuple(other, [-7])));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
    }

    /// <summary>
    /// A generated column's availability can differ between a relation descriptor and OLD or NEW without losing that distinction.
    /// </summary>
    [TestMethod]
    public void RowAvailabilityCanDifferFromTheRelationDescriptor()
    {
        using NativeEnvelope envelope = CreateEnvelope(14, true, true);
        var unavailable = new PgTupleDescriptor(9100, -1,
            [new("number", 23, 23, -1, 0, isUnavailable: true)]);
        envelope.Replace(10, NativeValue.FromTuple(unavailable.CreateTuple()));
        PgTriggerContext context = NativeValue.ReadTriggerContext(envelope.Values);
        Assert.IsFalse(context.Descriptor.Attributes[0].IsUnavailable);
        Assert.IsNotNull(context.Old);
        Assert.IsNotNull(context.New);
        Assert.IsFalse(context.Old.Descriptor.Attributes[0].IsUnavailable);
        Assert.IsTrue(context.New.Descriptor.Attributes[0].IsUnavailable);
        Assert.AreEqual(-7, context.Old.Get<int>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => context.New.Get<int?>(0));
    }

    /// <summary>
    /// Every getter and setter rejects an unavailable generated column even when the requested value is nullable.
    /// </summary>
    [TestMethod]
    public void UnavailableColumnsCannotBeReadOrReplaced()
    {
        var descriptor = new PgTupleDescriptor(9100, -1,
        [
            new("generated", 23, 23, -1, 0, isUnavailable: true),
            new("nullable", 23, 23, -1, 0), new("dropped", 0, 0, -1, 0, isDropped: true),
        ]);
        PgHeapTuple tuple = descriptor.CreateTuple();
        Assert.ThrowsExactly<InvalidOperationException>(() => tuple[0]);
        Assert.ThrowsExactly<InvalidOperationException>(() => tuple["generated"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => tuple.Get<int>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => tuple.Get<int?>("generated"));
        Assert.ThrowsExactly<InvalidOperationException>(() => tuple.Get<object?>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => tuple.Set(0, 42));
        Assert.ThrowsExactly<InvalidOperationException>(() => tuple.Set<int?>("generated", null));
        Assert.ThrowsExactly<InvalidOperationException>(() => tuple.Set(0, SpiParameter.Create(42)));
        Assert.ThrowsExactly<InvalidOperationException>(() => tuple.Set("generated", SpiParameter.Create<int?>(null)));
        Assert.IsNull(tuple.Get<int?>("nullable"));
        Assert.IsNull(tuple[2]);
        Assert.IsFalse(tuple.Descriptor.Attributes[1].IsUnavailable);
        Assert.ThrowsExactly<ArgumentException>(() => new PgHeapTuple(descriptor, [42, null, null]));
    }

    /// <summary>
    /// Shallow cloning preserves unavailable metadata and independent editable cell slots.
    /// </summary>
    [TestMethod]
    public void ClonesPreserveAvailabilityAndIndependentCells()
    {
        var descriptor = new PgTupleDescriptor(9100, -1,
            [new("generated", 23, 23, -1, 0, isUnavailable: true), new("number", 23, 23, -1, 0)]);
        var tuple = new PgHeapTuple(descriptor, [null, 42]);
        PgHeapTuple clone = tuple.Clone();
        Assert.AreSame(descriptor, clone.Descriptor);
        Assert.IsTrue(clone.Descriptor.Attributes[0].IsUnavailable);
        Assert.ThrowsExactly<InvalidOperationException>(() => clone.Get<int?>(0));
        clone.Set(1, -7);
        Assert.AreEqual(42, tuple.Get<int>(1));
        Assert.AreEqual(-7, clone.Get<int>(1));
    }

    /// <summary>
    /// The tuple wire format uses flag eight with a canonical NULL payload for unavailable fields without calling their getters.
    /// </summary>
    [TestMethod]
    public void UnavailableTransportUsesAnIndependentFlagAndNullEncoding()
    {
        var descriptor = new PgTupleDescriptor(9100, -1, [new("x", 23, 23, -1, 0, isNotNull: true, isUnavailable: true)]);
        NativeValue value = NativeValue.FromTuple(descriptor.CreateTuple());
        try
        {
            byte[] bytes = value.ReadBytes();
            Assert.AreEqual(10, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(28)));
            Assert.AreEqual(1, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(57)));
            Assert.AreSequenceEqual(new byte[20], bytes.AsSpan(37, 20).ToArray());
            Assert.AreEqual(0, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(61)));
            Assert.HasCount(65, bytes);
            PgHeapTuple copy = value.ReadTuple();
            Assert.IsTrue(copy.Descriptor.Attributes[0].IsUnavailable);
            Assert.IsTrue(copy.Descriptor.Attributes[0].IsNotNull);
            Assert.IsFalse(copy.Descriptor.Attributes[0].IsDropped);
            Assert.AreEqual(23U, copy.Descriptor.Attributes[0].TypeOid);
            Assert.ThrowsExactly<InvalidOperationException>(() => copy[0]);
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Nested tuple and array conversion preserve unavailable fields while retaining readable owned sibling data.
    /// </summary>
    [TestMethod]
    public void NestedUnavailableColumnsSurviveTupleAndArrayTransport()
    {
        var childDescriptor = new PgTupleDescriptor(9101, -1,
            [new("generated", 23, 23, -1, 0, isUnavailable: true), new("text", 25, 25, -1, 100)]);
        var child = new PgHeapTuple(childDescriptor, [null, "é 😀"]);
        var descriptor = new PgTupleDescriptor(9100, -1,
            [new("child", 9101, 9101, -1, 0, isComposite: true), new("children", 9102, 9102, -1, 0)]);
        var tuple = new PgHeapTuple(descriptor, [child, childDescriptor.CreateArray([null, child])]);
        NativeValue value = NativeValue.FromTuple(tuple);
        PgHeapTuple copy;
        try
        {
            copy = value.ReadTuple();
        }
        finally
        {
            value.Release();
        }

        child.Set(1, "changed");
        PgHeapTuple nested = copy.Get<PgHeapTuple>(0);
        Assert.IsTrue(nested.Descriptor.Attributes[0].IsUnavailable);
        Assert.ThrowsExactly<InvalidOperationException>(() => nested.Get<int?>(0));
        Assert.AreEqual("é 😀", nested.Get<string>(1));
        PgArray<PgHeapTuple?> children = copy.Get<PgArray<PgHeapTuple?>>(1);
        Assert.AreEqual(9101U, children.ElementTypeOid);
        Assert.IsNull(children[0]);
        Assert.IsNotNull(children[1]);
        Assert.IsTrue(children[1]!.Descriptor.Attributes[0].IsUnavailable);
        Assert.ThrowsExactly<InvalidOperationException>(() => children[1]!.Get<int?>(0));
        Assert.AreEqual("é 😀", children[1]!.Get<string>(1));
    }

    /// <summary>
    /// An independent modification of tuple transport rejects unavailable nonnull cells and unknown metadata flags.
    /// </summary>
    [TestMethod]
    [DataRow(8)]
    [DataRow(16)]
    public void MalformedAvailabilityTransportIsRejected(int flags)
    {
        NativeValue original = NativeValue.FromTuple(new PgHeapTuple(CreateDescriptor(), [42]));
        byte[] bytes;
        try
        {
            bytes = original.ReadBytes();
        }
        finally
        {
            original.Release();
        }

        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(28), flags);
        NativeValue malformed = NativeValue.FromBytes(bytes);
        TransportKind(ref malformed) = -4;
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => malformed.ReadTuple());
        }
        finally
        {
            malformed.Release();
        }
    }

    /// <summary>
    /// Creates an independent target relation descriptor whose OID differs from the relation and trigger OIDs.
    /// </summary>
    private static PgTupleDescriptor CreateDescriptor() => new(9100, -1, [new("number", 23, 23, -1, 0)]);

    /// <summary>
    /// Creates an owned native invocation with explicit OLD and NEW slot presence and distinct row values.
    /// </summary>
    /// <param name="eventBits">The trigger event.</param>
    /// <param name="hasOld">Whether the OLD slot holds a row.</param>
    /// <param name="hasNew">Whether the NEW slot holds a row.</param>
    /// <param name="site">The native call site's identity, or zero for an uncached call.</param>
    private static NativeEnvelope CreateEnvelope(uint eventBits, bool hasOld = false, bool hasNew = false, long site = 0)
    {
        PgTupleDescriptor descriptor = CreateDescriptor();
        NativeValue[] values =
        [
            new() { Integral = eventBits }, new() { Integral = 8100 }, new() { Integral = 8200 },
            NativeValue.FromString("audit \"é\" 😀"), NativeValue.FromString("table é"), NativeValue.FromString("schema space"),
            new() { IsNull = 1 }, new() { IsNull = 1 },
            NativeValue.FromArray(new PgArray<string>(["", "a,b", "é 😀", "quote\"slash\\"])),
            hasOld ? NativeValue.FromTuple(new PgHeapTuple(descriptor, [-7])) : new NativeValue { IsNull = 1 },
            hasNew ? NativeValue.FromTuple(new PgHeapTuple(descriptor, [42])) : new NativeValue { IsNull = 1 },
            NativeValue.FromTuple(descriptor.CreateTuple()),
            NativeValue.FromArray(new PgArray<int>([])),
            (eventBits & 3) == 2 ? NativeValue.FromArray(new PgArray<int>([0])) : new NativeValue { IsNull = 1 },
            new() { Integral = 0 },
            new() { Integral = site },
        ];

        return new NativeEnvelope(values);
    }

    /// <summary>
    /// Accesses the transport marker to construct independently malformed tuple envelopes.
    /// </summary>
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_auxiliary1")]
    private static extern ref int TransportKind(ref NativeValue value);

    /// <summary>
    /// Releases each owned invocation buffer after tests finish, including values replaced by malformed inputs.
    /// </summary>
    private sealed class NativeEnvelope(NativeValue[] values) : IDisposable
    {
        /// <summary>
        /// Gets the owned native invocation slots.
        /// </summary>
        internal NativeValue[] Values { get; } = values;

        /// <summary>
        /// Releases a replaced slot before taking ownership of its replacement.
        /// </summary>
        internal void Replace(int index, NativeValue replacement)
        {
            Values[index].Release();
            Values[index] = replacement;
        }

        /// <summary>
        /// Releases and clears every native buffer exactly once.
        /// </summary>
        public void Dispose()
        {
            for (int index = 0; index < Values.Length; index++)
            {
                Values[index].Release();
            }
        }
    }
}
