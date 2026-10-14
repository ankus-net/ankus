namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies exact generated enum contracts and detached enum array ownership and conversions.
/// </summary>
[TestClass]
public sealed class PgEnumTests
{
    /// <summary>
    /// Registers independent test contracts without requiring the extension source generator or a backend.
    /// </summary>
    static PgEnumTests()
    {
        PgEnumRegistry.Register<SignedState>("signed_state", null,
        [
            new(SignedState.Ready, "Ready"),
            new(SignedState.Lowest, "quote'\" \\ 😀"),
            new(SignedState.Highest, ""),
            new(SignedState.Zero, "ready"),
        ]);
        PgEnumRegistry.Register<UnsignedState>("unsigned_state", "fixed_schema",
        [new(UnsignedState.Highest, "maximum"), new(UnsignedState.Zero, "zero")]);
        PgEnumRegistry.Register<OtherState>("other_state", null, [new(OtherState.Ready, "Ready")]);
        PgEnumRegistry.Register<ByteState>("byte_state", null, [new(ByteState.Highest, "maximum"), new(ByteState.Zero, "zero")]);

        KeyValuePair<OwnedState, string>[] labels = [new(OwnedState.Value, "  original  ")];
        PgEnumRegistry.Register("owned_state", null, labels);
        labels[0] = new(OwnedState.Value, "replacement");
    }

    /// <summary>
    /// Maps declaration values directly, including signed extremes, zero, and case-distinct labels.
    /// </summary>
    [TestMethod]
    public void SignedValuesAndExactLabelsRoundTripIndependentlyOfNumericOrder()
    {
        Assert.AreEqual("Ready", PgEnums.GetLabel(SignedState.Ready));
        Assert.AreEqual("quote'\" \\ 😀", PgEnums.GetLabel(SignedState.Lowest));
        Assert.AreEqual("", PgEnums.GetLabel(SignedState.Highest));
        Assert.AreEqual("ready", PgEnums.GetLabel(SignedState.Zero));
        Assert.AreEqual(SignedState.Ready, PgEnums.Parse<SignedState>("Ready"));
        Assert.AreEqual(SignedState.Lowest, PgEnums.Parse<SignedState>("quote'\" \\ 😀"));
        Assert.AreEqual(SignedState.Highest, PgEnums.Parse<SignedState>(""));
        Assert.AreEqual(SignedState.Zero, PgEnums.Parse<SignedState>("ready"));
    }

    /// <summary>
    /// Preserves unsigned values that cannot be represented by a signed sixty-four-bit integer.
    /// </summary>
    [TestMethod]
    public void UnsignedMaximumDoesNotOverflowOrBecomeAnOrdinal()
    {
        Assert.AreEqual("maximum", PgEnums.GetLabel(UnsignedState.Highest));
        Assert.AreEqual(UnsignedState.Highest, PgEnums.Parse<UnsignedState>("maximum"));
        Assert.AreEqual("zero", PgEnums.GetLabel(UnsignedState.Zero));
        Assert.AreEqual(UnsignedState.Zero, PgEnums.Parse<UnsignedState>("zero"));
        Assert.ThrowsExactly<ArgumentException>(() => PgEnums.GetLabel((UnsignedState)(ulong.MaxValue - 1)));
    }

    /// <summary>
    /// Owns registration input and retains leading and trailing whitespace in labels.
    /// </summary>
    [TestMethod]
    public void RegistrationCopiesLabelsWithoutTrimmingThem()
    {
        Assert.AreEqual("  original  ", PgEnums.GetLabel(OwnedState.Value));
        Assert.AreEqual(OwnedState.Value, PgEnums.Parse<OwnedState>("  original  "));
        Assert.ThrowsExactly<ArgumentException>(() => PgEnums.Parse<OwnedState>("original"));
        Assert.ThrowsExactly<ArgumentException>(() => PgEnums.Parse<OwnedState>("replacement"));
    }

    /// <summary>
    /// Rejects unknown case-sensitive labels, null labels, and undeclared numeric values.
    /// </summary>
    [TestMethod]
    public void UnknownValuesAndLabelsFailWithTheirArgumentNames()
    {
        ArgumentException value = Assert.ThrowsExactly<ArgumentException>(() => PgEnums.GetLabel((SignedState)(-1)));
        Assert.AreEqual("value", value.ParamName);
        ArgumentException label = Assert.ThrowsExactly<ArgumentException>(() => PgEnums.Parse<SignedState>("READY"));
        Assert.AreEqual("label", label.ParamName);
        Assert.ThrowsExactly<ArgumentException>(() => PgEnums.Parse<SignedState>("Ready\0"));
        ArgumentNullException missing = Assert.ThrowsExactly<ArgumentNullException>(() => PgEnums.Parse<SignedState>(null!));
        Assert.AreEqual("label", missing.ParamName);
    }

    /// <summary>
    /// Requires generated registration even for enum types with declared members and empty arrays.
    /// </summary>
    [TestMethod]
    public void UnregisteredEnumsFailBeforeBackendAccess()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => PgEnums.GetLabel(UnregisteredState.Value));
        Assert.ThrowsExactly<NotSupportedException>(() => PgEnums.Parse<UnregisteredState>("Value"));
        Assert.ThrowsExactly<NotSupportedException>(() => PgEnums.GetTypeOid<UnregisteredState>());
        Assert.ThrowsExactly<NotSupportedException>(() => new PgArray<UnregisteredState>([]));
        Assert.ThrowsExactly<NotSupportedException>(() => new PgArray<UnregisteredState?>([null]));
    }

    /// <summary>
    /// Duplicate registration cannot replace an existing scalar or array contract.
    /// </summary>
    [TestMethod]
    public void DuplicateRegistrationKeepsTheOriginalContract()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => PgEnumRegistry.Register<SignedState>(
            "replacement_state", "replacement_schema", [new(SignedState.Ready, "replacement")]));
        Assert.AreEqual("Ready", PgEnums.GetLabel(SignedState.Ready));
        Assert.AreEqual(SignedState.Lowest, PgEnums.Parse<SignedState>("quote'\" \\ 😀"));
        Assert.ThrowsExactly<ArgumentException>(() => PgEnums.Parse<SignedState>("replacement"));
        var row = new SpiRow([new PgArray<SignedState>([SignedState.Highest])], [new("value", 0)]);
        Assert.AreSequenceEqual([SignedState.Highest], row.Get<SignedState?[]>(0));
    }

    /// <summary>
    /// Duplicate values and labels fail before publishing partial registrations.
    /// </summary>
    [TestMethod]
    public void AmbiguousContractsDoNotLeavePartialRegistrations()
    {
        Assert.ThrowsExactly<ArgumentException>(() => PgEnumRegistry.Register<DuplicateValueState>(
            "duplicate_values", null, [new(DuplicateValueState.Value, "one"), new(DuplicateValueState.Value, "two")]));
        Assert.ThrowsExactly<NotSupportedException>(() => PgEnums.GetLabel(DuplicateValueState.Value));
        PgEnumRegistry.Register<DuplicateValueState>("valid_value", null, [new(DuplicateValueState.Value, "valid")]);
        Assert.AreEqual("valid", PgEnums.GetLabel(DuplicateValueState.Value));

        Assert.ThrowsExactly<ArgumentException>(() => PgEnumRegistry.Register<DuplicateLabelState>(
            "duplicate_labels", null, [new(DuplicateLabelState.First, "same"), new(DuplicateLabelState.Second, "same")]));
        Assert.ThrowsExactly<NotSupportedException>(() => PgEnums.Parse<DuplicateLabelState>("same"));
        PgEnumRegistry.Register<DuplicateLabelState>("valid_labels", null,
            [new(DuplicateLabelState.First, "first"), new(DuplicateLabelState.Second, "second")]);
        Assert.AreEqual(DuplicateLabelState.Second, PgEnums.Parse<DuplicateLabelState>("second"));
    }

    /// <summary>
    /// Rejects absent registration names and label collections before making the type available.
    /// </summary>
    [TestMethod]
    public void MissingRegistrationArgumentsDoNotRegisterTheEnum()
    {
        ArgumentNullException name = Assert.ThrowsExactly<ArgumentNullException>(() => PgEnumRegistry.Register<InvalidContractState>(null!, null, []));
        Assert.AreEqual("name", name.ParamName);
        ArgumentException empty = Assert.ThrowsExactly<ArgumentException>(() => PgEnumRegistry.Register<InvalidContractState>("", null, []));
        Assert.AreEqual("name", empty.ParamName);
        ArgumentNullException labels = Assert.ThrowsExactly<ArgumentNullException>(() => PgEnumRegistry.Register<InvalidContractState>("invalid", null, null!));
        Assert.AreEqual("labels", labels.ParamName);
        Assert.ThrowsExactly<NotSupportedException>(() => PgEnums.GetLabel(InvalidContractState.Value));
    }

    /// <summary>
    /// Type identities require a current backend even though label conversion works without one.
    /// </summary>
    [TestMethod]
    public void CatalogIdentityRequiresBackendAccess()
    {
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => PgEnums.GetTypeOid<SignedState>());
        Assert.AreEqual("PostgreSQL APIs can only be used on the active PostgreSQL backend thread.", error.Message);
        Assert.ThrowsExactly<InvalidOperationException>(() => SpiParameter.Create(SignedState.Ready));
        Assert.ThrowsExactly<InvalidOperationException>(() => SpiParameter.Create<SignedState?>(null));
        Assert.ThrowsExactly<InvalidOperationException>(() => SpiParameter.Create(new PgArray<SignedState>([])));
        Assert.AreEqual("Ready", PgEnums.GetLabel(SignedState.Ready));
    }

    /// <summary>
    /// Detached scalar rows retain enum identity and distinguish SQL NULL from a zero-valued member.
    /// </summary>
    [TestMethod]
    public void ScalarRowsPreserveIdentityAndNull()
    {
        var row = new SpiRow([SignedState.Zero, null], [new("value", 0), new("missing", 0)]);
        Assert.AreEqual(SignedState.Zero, row.Get<SignedState>("value"));
        Assert.AreEqual(SignedState.Zero, row.Get<SignedState?>(0));
        Assert.IsNull(row.Get<SignedState?>("missing"));
        Assert.ThrowsExactly<InvalidOperationException>(() => row.Get<SignedState>(1));
        Assert.ThrowsExactly<InvalidCastException>(() => row.Get<OtherState>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => row.Get<long>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => row.Get<string>(0));
    }

    /// <summary>
    /// Enum arrays copy values and shapes while preserving SQL NULL and PostgreSQL subscripts.
    /// </summary>
    [TestMethod]
    public void DetachedArraysOwnTheirValuesDimensionsAndBounds()
    {
        SignedState?[] values = [SignedState.Ready, null, SignedState.Highest, SignedState.Lowest];
        int[] lengths = [2, 2];
        int[] bounds = [-3, 4];
        var array = new PgArray<SignedState?>(values, lengths, bounds);
        values[0] = SignedState.Zero;
        lengths[0] = 1;
        bounds[0] = 1;

        Assert.AreEqual(4, array.Count);
        Assert.AreEqual(2, array.Rank);
        Assert.AreSequenceEqual([2, 2], array.Lengths.ToArray());
        Assert.AreSequenceEqual([-3, 4], array.LowerBounds.ToArray());
        Assert.AreEqual(SignedState.Ready, array.GetValue(-3, 4));
        Assert.IsNull(array.GetValue(-3, 5));
        Assert.AreEqual(SignedState.Lowest, array.GetValue(-2, 5));
        Assert.AreSequenceEqual([SignedState.Ready, null, SignedState.Highest, SignedState.Lowest], array);
        array.ToArray()[0] = SignedState.Zero;
        Assert.AreEqual(SignedState.Ready, array[0]);
        Assert.ThrowsExactly<InvalidOperationException>(array.ToVector);
    }

    /// <summary>
    /// All required and nullable vectors and shape-preserving forms convert without a backend.
    /// </summary>
    [TestMethod]
    public void DetachedRowsConvertAllEnumArrayFormsWithoutChangingValues()
    {
        SignedState[] required = [SignedState.Highest, SignedState.Zero, SignedState.Lowest];
        SignedState?[] nullable = [SignedState.Highest, SignedState.Zero, SignedState.Lowest];
        object[] sources = [required, nullable, new PgArray<SignedState>(required), new PgArray<SignedState?>(nullable)];
        foreach (SpiRow row in sources.Select(static source => new SpiRow([source], [new("value", 0)])))
        {
            Assert.AreSequenceEqual(required, row.Get<SignedState[]>(0));
            Assert.AreSequenceEqual(nullable, row.Get<SignedState?[]>(0));
            PgArray<SignedState> requiredArray = row.Get<PgArray<SignedState>>(0);
            PgArray<SignedState?> nullableArray = row.Get<PgArray<SignedState?>>(0);
            Assert.AreSequenceEqual(required, requiredArray);
            Assert.AreSequenceEqual(nullable, nullableArray);
            Assert.AreSequenceEqual([3], requiredArray.Lengths.ToArray());
            Assert.AreSequenceEqual([1], nullableArray.LowerBounds.ToArray());
        }

        var vectorRow = new SpiRow([required], [new("value", 0)]);
        PgArray<SignedState?> owned = vectorRow.Get<PgArray<SignedState?>>(0);
        required[0] = SignedState.Ready;
        Assert.AreEqual(SignedState.Highest, owned[0]);
    }

    /// <summary>
    /// Empty enum arrays preserve rank zero and still reject arrays of another enum type.
    /// </summary>
    [TestMethod]
    public void EmptyArraysRetainEnumIdentityAndNormalizeTheirShape()
    {
        object[] sources = [Array.Empty<SignedState>(), Array.Empty<SignedState?>(), new PgArray<SignedState>([]), new PgArray<SignedState?>([], [2, 0], [-4, 3])];
        foreach (SpiRow row in sources.Select(static source => new SpiRow([source], [new("value", 0)])))
        {
            Assert.IsEmpty(row.Get<SignedState[]>(0));
            Assert.IsEmpty(row.Get<SignedState?[]>(0));
            PgArray<SignedState> required = row.Get<PgArray<SignedState>>(0);
            PgArray<SignedState?> nullable = row.Get<PgArray<SignedState?>>(0);
            Assert.AreEqual(0, required.Rank);
            Assert.AreEqual(0, nullable.Rank);
            Assert.IsEmpty(required.Lengths.ToArray());
            Assert.IsEmpty(nullable.LowerBounds.ToArray());
            Assert.ThrowsExactly<InvalidCastException>(() => row.Get<OtherState[]>(0));
            Assert.ThrowsExactly<InvalidCastException>(() => row.Get<PgArray<OtherState?>>(0));
        }
    }

    /// <summary>
    /// NULL elements cannot become zero-valued members, and a NULL array remains distinct from an empty one.
    /// </summary>
    [TestMethod]
    public void ArrayRowsRejectNullElementsForRequiredEnums()
    {
        SignedState?[] values = [SignedState.Zero, null, SignedState.Ready];
        foreach (object source in new object[] { values, new PgArray<SignedState?>(values) })
        {
            var row = new SpiRow([source, null], [new("value", 0), new("missing", 0)]);
            Assert.AreSequenceEqual(values, row.Get<SignedState?[]>(0));
            Assert.AreSequenceEqual(values, row.Get<PgArray<SignedState?>>(0));
            Assert.ThrowsExactly<InvalidOperationException>(() => row.Get<SignedState[]>(0));
            Assert.ThrowsExactly<InvalidOperationException>(() => row.Get<PgArray<SignedState>>(0));
            Assert.IsNull(row.Get<SignedState[]>(1));
            Assert.IsNull(row.Get<SignedState?[]>(1));
            Assert.IsNull(row.Get<PgArray<SignedState>>(1));
            Assert.IsNull(row.Get<PgArray<SignedState?>>(1));
        }
    }

    /// <summary>
    /// Changing nullability preserves dimensions and lower bounds, while implicit flattening is rejected.
    /// </summary>
    [TestMethod]
    public void ArrayRowsPreserveShapesAndRejectLossyVectors()
    {
        SignedState[] values = [SignedState.Ready, SignedState.Zero];
        var source = new PgArray<SignedState>(values, [1, 2], [-3, 5]);
        var row = new SpiRow([source], [new("value", 0)]);
        PgArray<SignedState?> nullable = row.Get<PgArray<SignedState?>>(0);
        Assert.AreSequenceEqual([1, 2], nullable.Lengths.ToArray());
        Assert.AreSequenceEqual([-3, 5], nullable.LowerBounds.ToArray());
        Assert.AreEqual(SignedState.Zero, nullable.GetValue(-3, 6));
        var convertedRow = new SpiRow([nullable], [new("value", 0)]);
        PgArray<SignedState> required = convertedRow.Get<PgArray<SignedState>>(0);
        Assert.AreSequenceEqual(values, required);
        Assert.AreSequenceEqual([1, 2], required.Lengths.ToArray());
        Assert.AreSequenceEqual([-3, 5], required.LowerBounds.ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => row.Get<SignedState[]>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => row.Get<SignedState?[]>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => convertedRow.Get<SignedState[]>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => convertedRow.Get<SignedState?[]>(0));

        var shifted = new SpiRow([new PgArray<SignedState>(values, [2], [0])], [new("value", 0)]);
        Assert.ThrowsExactly<InvalidOperationException>(() => shifted.Get<SignedState[]>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => shifted.Get<SignedState?[]>(0));
        Assert.AreSequenceEqual([0], shifted.Get<PgArray<SignedState?>>(0).LowerBounds.ToArray());
    }

    /// <summary>
    /// Equal labels and numeric values do not permit conversion between distinct enum array types.
    /// </summary>
    [TestMethod]
    public void ArrayRowsRejectForeignEnumAndUnderlyingIntegerTypes()
    {
        SignedState[] required = [SignedState.Ready];
        SignedState?[] nullable = [SignedState.Ready];
        object[] sources = [required, nullable, new PgArray<SignedState>(required), new PgArray<SignedState?>(nullable)];
        foreach (SpiRow row in sources.Select(static source => new SpiRow([source], [new("value", 0)])))
        {
            Assert.ThrowsExactly<InvalidCastException>(() => row.Get<OtherState[]>(0));
            Assert.ThrowsExactly<InvalidCastException>(() => row.Get<OtherState?[]>(0));
            Assert.ThrowsExactly<InvalidCastException>(() => row.Get<PgArray<OtherState>>(0));
            Assert.ThrowsExactly<InvalidCastException>(() => row.Get<PgArray<OtherState?>>(0));
            Assert.ThrowsExactly<InvalidCastException>(() => row.Get<long[]>(0));
            Assert.ThrowsExactly<InvalidCastException>(() => row.Get<string[]>(0));
        }

        var primitive = new SpiRow([new long[] { 17 }], [new("value", 1016)]);
        Assert.ThrowsExactly<InvalidCastException>(() => primitive.Get<SignedState[]>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => primitive.Get<SignedState?[]>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => primitive.Get<PgArray<SignedState>>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => primitive.Get<PgArray<SignedState?>>(0));
    }

    /// <summary>
    /// Byte-backed enum arrays retain their enum identity instead of becoming binary scalar values.
    /// </summary>
    [TestMethod]
    public void ByteEnumVectorsRemainDistinctFromBinaryValues()
    {
        var row = new SpiRow([new ByteState[] { ByteState.Highest, ByteState.Zero }], [new("value", 0)]);
        Assert.AreSequenceEqual([ByteState.Highest, ByteState.Zero], row.Get<ByteState[]>(0));
        Assert.AreSequenceEqual([ByteState.Highest, ByteState.Zero], row.Get<ByteState?[]>(0));
        Assert.AreSequenceEqual([ByteState.Highest, ByteState.Zero], row.Get<PgArray<ByteState>>(0));
        Assert.AreSequenceEqual([ByteState.Highest, ByteState.Zero], row.Get<PgArray<ByteState?>>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => row.Get<byte[]>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => row.Get<byte?[]>(0));

        var binary = new SpiRow([new byte[] { byte.MaxValue, 0 }], [new("value", 17)]);
        Assert.ThrowsExactly<InvalidCastException>(() => binary.Get<ByteState[]>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => binary.Get<ByteState?[]>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => binary.Get<PgArray<ByteState>>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => binary.Get<PgArray<ByteState?>>(0));
    }

    /// <summary>
    /// Owned enum values and arrays can be read on worker threads without acquiring backend state.
    /// </summary>
    [TestMethod]
    public async Task DetachedEnumConversionsAreAvailableOnWorkerThreads()
    {
        var row = new SpiRow([new PgArray<SignedState?>([SignedState.Highest, SignedState.Lowest])], [new("value", 0)]);
        (string Label, SignedState Parsed, SignedState[] Values) = await Task.Run(() =>
            (PgEnums.GetLabel(UnsignedState.Highest), PgEnums.Parse<SignedState>("Ready"), row.Get<SignedState[]>(0)));
        Assert.AreEqual("maximum", Label);
        Assert.AreEqual(SignedState.Ready, Parsed);
        Assert.AreSequenceEqual([SignedState.Highest, SignedState.Lowest], Values);
    }

    /// <summary>
    /// Supplies deliberately nonordinal values and label-sensitive mappings.
    /// </summary>
    private enum SignedState : long
    {
        /// <summary>
        /// A representative positive value declared before numeric extremes.
        /// </summary>
        Ready = 17,

        /// <summary>
        /// The smallest signed value.
        /// </summary>
        Lowest = long.MinValue,

        /// <summary>
        /// The largest signed value with the valid empty PostgreSQL label.
        /// </summary>
        Highest = long.MaxValue,

        /// <summary>
        /// A zero-valued member distinct from SQL NULL.
        /// </summary>
        Zero = 0,
    }

    /// <summary>
    /// Supplies values outside the signed representation used by many enum adapters.
    /// </summary>
    private enum UnsignedState : ulong
    {
        /// <summary>
        /// The largest unsigned value.
        /// </summary>
        Highest = ulong.MaxValue,

        /// <summary>
        /// The zero unsigned value.
        /// </summary>
        Zero = 0,
    }

    /// <summary>
    /// Shares a value and label with SignedState without sharing its type identity.
    /// </summary>
    private enum OtherState : long
    {
        /// <summary>
        /// The numerically equal member of the foreign enum.
        /// </summary>
        Ready = 17,
    }

    /// <summary>
    /// Shares a CLR vector representation with bytes while remaining a PostgreSQL enum array.
    /// </summary>
    private enum ByteState : byte
    {
        /// <summary>
        /// The largest byte-sized enum value.
        /// </summary>
        Highest = byte.MaxValue,

        /// <summary>
        /// A byte-sized zero member.
        /// </summary>
        Zero = 0,
    }

    /// <summary>
    /// Isolates registration input ownership checks.
    /// </summary>
    private enum OwnedState
    {
        /// <summary>
        /// The member whose source label array is subsequently modified.
        /// </summary>
        Value,
    }

    /// <summary>
    /// Remains unregistered for unsupported-type checks.
    /// </summary>
    private enum UnregisteredState
    {
        /// <summary>
        /// A declared member without a generated mapping.
        /// </summary>
        Value,
    }

    /// <summary>
    /// Isolates duplicate-value registration failures.
    /// </summary>
    private enum DuplicateValueState
    {
        /// <summary>
        /// The value supplied twice by an invalid mapping.
        /// </summary>
        Value,
    }

    /// <summary>
    /// Isolates duplicate-label registration failures.
    /// </summary>
    private enum DuplicateLabelState
    {
        /// <summary>
        /// The first value of an invalid shared-label mapping.
        /// </summary>
        First,

        /// <summary>
        /// The second value of an invalid shared-label mapping.
        /// </summary>
        Second,
    }

    /// <summary>
    /// Remains unregistered after argument validation failures.
    /// </summary>
    private enum InvalidContractState
    {
        /// <summary>
        /// The member whose registration is always invalid.
        /// </summary>
        Value,
    }
}
