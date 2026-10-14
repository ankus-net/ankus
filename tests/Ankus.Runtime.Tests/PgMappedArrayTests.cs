using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies closed mapped arrays, independent native envelopes and temporary owner boundaries.
/// </summary>
[TestClass]
public sealed unsafe class PgMappedArrayTests
{
    /// <summary>
    /// Registers statically closed contracts without executing a converter or resolving a backend type.
    /// </summary>
    static PgMappedArrayTests()
    {
        PgDatumRegistry.RegisterValue<Number>("array_number", "fixed", PgTypeOrigin.External, typeof(NumberConverter),
            static () => new NumberConverter(), true, true);
        PgDatumRegistry.RegisterValue<Alias>("array_number", "fixed", PgTypeOrigin.External, typeof(AliasReader),
            static () => new AliasReader(), true, false);
        PgDatumRegistry.RegisterValue<ReadOnly>("array_number", "fixed", PgTypeOrigin.External, typeof(ReadOnlyConverter),
            static () => throw new InvalidOperationException("Absent read-only arrays must not create a converter."), true, false);
        PgDatumRegistry.RegisterValue<WriteOnly>("array_number", "fixed", PgTypeOrigin.External, typeof(WriteOnlyConverter),
            static () => throw new InvalidOperationException("Absent write-only arrays must not create a converter."), false, true);
        PgDatumRegistry.RegisterValue<Kind>("array_number", "fixed", PgTypeOrigin.External, typeof(KindConverter),
            static () => new KindConverter(), true, true);
        PgDatumRegistry.RegisterReference<Message>("array_message", "fixed", PgTypeOrigin.External, typeof(MessageConverter),
            static () => new MessageConverter(), true, true);
        PgDatumRegistry.RegisterReference<NumericMessage>("array_message", "fixed", PgTypeOrigin.External, typeof(DerivedReader),
            static () => throw new InvalidOperationException("The declared base converter must be used."), true, false);
        PgTypeRegistry.RegisterReference<CustomRoot>("array_custom", "fixed", static () => new CustomCodec());
        PgDatumRegistry.RegisterReference<CustomLeaf>("array_leaf", "fixed", PgTypeOrigin.External, typeof(CustomLeafReader),
            static () => throw new InvalidOperationException("The retained custom root converter must be used."), true, false);
    }

    /// <summary>
    /// Offline shape creation and compatible registrations retain one lazy converter across scalar and array uses.
    /// </summary>
    [TestMethod]
    public void RegistrationSharesLazyConverterAndAllowsOfflineShapes()
    {
        int factories = 0;
        PgDatumRegistry.RegisterValue<Shared>("array_shared", "fixed", PgTypeOrigin.External, typeof(SharedConverter),
            () =>
            {
                factories++;
                return new SharedConverter();
            }, true, true);
        PgDatumRegistry.RegisterValue<Shared>("array_shared", "fixed", PgTypeOrigin.External, typeof(SharedConverter),
            static () => throw new InvalidOperationException("Compatible registration must retain the first factory."), true, true);
        var empty = new PgArray<Shared>([]);
        var shaped = new PgArray<Shared?>([new(3), null], [1, 2], [-1, 4]);
        Assert.AreEqual(0, factories);
        Assert.AreEqual(0, empty.Rank);
        Assert.AreSequenceEqual<int>([1, 2], shaped.Lengths.ToArray());
        Assert.AreSequenceEqual<int>([-1, 4], shaped.LowerBounds.ToArray());
        using var script = new Script { Cells = [new(7), new(9)] };
        Assert.AreEqual(new Shared(8), Script.Datum(script.ElementOid, 7).Read<Shared>());
        Assert.AreSequenceEqual<Shared>([new(8), new(10)], script.Array().Read<Shared[]>());
        NativeValue output = NativeValue.FromMapped(shaped);
        try
        {
            Assert.AreSequenceEqual<nuint>([13, 0], script.Written.Select(static item => item.Bits));
            Assert.AreEqual(1, factories);
        }
        finally
        {
            output.Release();
        }
    }

    /// <summary>
    /// Reads exact literal cells and validates shape before any element converter can run.
    /// </summary>
    [TestMethod]
    public void ShapedReadsPreserveCellsBoundsAndLeaveSourceLive()
    {
        using var script = new Script { Lengths = [2, 2], Bounds = [-2, 4], Cells = [new(0), new(7), new(0, true), new(19)] };
        PgDatum source = script.Array();
        PgArray<Number?> values = source.Read<PgArray<Number?>>();
        Assert.AreSequenceEqual<Number?>([new(100), new(107), null, new(119)], [.. values]);
        Assert.AreSequenceEqual<int>([2, 2], values.Lengths.ToArray());
        Assert.AreSequenceEqual<int>([-2, 4], values.LowerBounds.ToArray());
        Assert.AreEqual(new Number(119), values.GetValue(-1, 5));
        Assert.AreEqual(3, script.Reads);
        Assert.AreEqual(1, script.Deletes);
        Assert.AreEqual((nuint)701, source.DangerousGetBits());
        Assert.ThrowsExactly<ObjectDisposedException>(() => script.Captured!.DangerousGetBits());
        script.Reads = 0;
        Assert.ThrowsExactly<InvalidOperationException>(() => source.Read<Number?[]>());
        Assert.AreEqual(0, script.Reads);
        Assert.AreEqual(2, script.Deletes);
        script.Lengths = [1];
        script.Bounds = [0];
        script.Cells = [new(3)];
        Assert.ThrowsExactly<InvalidOperationException>(() => source.Read<Number[]>());
        Assert.AreEqual(0, script.Reads);
    }

    /// <summary>
    /// Whole NULL, empty and all-NULL arrays preserve distinct states without invoking absent converters.
    /// </summary>
    [TestMethod]
    public void NullEmptyAndRequiredCellsKeepTheirDistinctContracts()
    {
        using var script = new Script();
        Assert.IsNull(script.Array(isNull: true).Read<ReadOnly?[]>());
        Assert.AreEqual(0, script.Creates);
        script.Lengths = [];
        script.Bounds = [];
        script.Cells = [];
        Assert.IsEmpty(script.Array().Read<ReadOnly[]>());
        script.Lengths = [2];
        script.Bounds = [1];
        script.Cells = [new(0, true), new(0, true)];
        Assert.AreSequenceEqual<ReadOnly?>([null, null], script.Array().Read<ReadOnly?[]>());
        Assert.AreSequenceEqual<Message?>([null, null], script.Array().Read<Message?[]>());
        InvalidOperationException required = Assert.ThrowsExactly<InvalidOperationException>(() => script.Array().Read<ReadOnly[]>());
        Assert.AreEqual("SQL NULL cannot be read as a non-nullable managed value.", required.Message);
        Assert.AreEqual(0, script.Reads);
        NativeValue absent = NativeValue.FromMapped<WriteOnly[]?>(null);
        Assert.AreEqual((byte)1, absent.IsNull);
        NativeValue empty = NativeValue.FromMapped(Array.Empty<WriteOnly>());
        NativeValue nullCells = NativeValue.FromMapped<WriteOnly?[]>([null, null]);
        try
        {
            Assert.AreEqual((byte)0, empty.IsNull);
            Assert.AreEqual((byte)0, nullCells.IsNull);
            Assert.AreEqual(2, script.Builds);
            Assert.IsTrue(script.Written.All(static item => item.IsNull && item.Length == 0));
            Assert.AreEqual(0, script.Writes);
        }
        finally
        {
            empty.Release();
            nullCells.Release();
        }
    }

    /// <summary>
    /// Missing directions reject NULL and empty work before owner allocation, lookup, or SQL.
    /// </summary>
    [TestMethod]
    public void DirectionsPreflightEveryArrayPathWhileDefaultsUseOnlyMetadata()
    {
        using var script = new Script();
        PgDatum source = script.Array(isNull: true);
        script.Memory.Requests.Clear();
        Assert.ThrowsExactly<NotSupportedException>(() => source.Read<WriteOnly[]>());
        Assert.ThrowsExactly<NotSupportedException>(() => NativeValue.FromMapped<ReadOnly[]?>(null));
        Assert.ThrowsExactly<NotSupportedException>(() => NativeValue.FromMapped(Array.Empty<ReadOnly>()));
        Assert.ThrowsExactly<NotSupportedException>(() => SpiParameter.Create<ReadOnly[]?>(null));
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.ExecuteScalars<Number, WriteOnly[]>("SELECT arrays"));
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.ExecuteScalars<PgAnyElement, Number[], PgArray<WriteOnly>>("SELECT arrays"));
        Assert.ThrowsExactly<NotSupportedException>(() => PgFunctions.Call<WriteOnly[]>("fixed.array"));
        Assert.AreEqual(0, script.Lookups);
        Assert.AreEqual(0, script.Creates);
        Assert.AreEqual(0, script.Executions);
        Assert.IsEmpty(script.Memory.Requests);
        Assert.AreEqual(script.ArrayOid, PgFunctionArgument.Default<ReadOnly[]>().TypeOid);
        Assert.AreEqual(script.ArrayOid, PgFunctionArgument.Default<PgArray<WriteOnly>>().TypeOid);
        Assert.AreEqual(0, script.Creates);
        Assert.AreEqual(0, script.Builds);
    }

    /// <summary>
    /// Exact outer and element identities are checked even for absent and empty arrays, and current OIDs are refreshed.
    /// </summary>
    [TestMethod]
    public void ReadsRejectWrongIdentityBeforeFactoriesAndRefreshCatalogTypes()
    {
        using var script = new Script();
        foreach (uint wrong in new uint[] { 1007, 9003, 9010 })
        {
            Assert.ThrowsExactly<InvalidCastException>(() => Script.Datum(wrong, 701).Read<Number[]>());
            Assert.ThrowsExactly<InvalidCastException>(() => Script.Datum(wrong, 0, true).Read<Number[]>());
        }

        Assert.AreEqual(0, script.Extractions);
        script.ReportedElement = 23;
        Assert.ThrowsExactly<InvalidCastException>(() => script.Array().Read<Number[]>());
        script.Cells = [];
        script.Lengths = [];
        script.Bounds = [];
        Assert.ThrowsExactly<InvalidCastException>(() => script.Array().Read<Number[]>());
        Assert.AreEqual(0, script.Reads);
        script.ReportedElement = null;
        script.ElementOid = 9011;
        script.ArrayOid = 9012;
        Assert.IsEmpty(script.Array().Read<Number[]>());
        Assert.AreEqual(9011U, new PgArray<Number>([]).ElementTypeOid);
    }

    /// <summary>
    /// Catalog changes inside one converter stop the next element before user code or array construction.
    /// </summary>
    /// <param name="write">Whether to change identity inside a writer rather than a reader.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReentrantCatalogChangesStopBeforeTheNextElement(bool write)
    {
        using var script = new Script { ChangeElementDuringConversion = true };
        PgDatum source = script.Array();
        if (write)
        {
            InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.FromMapped<Number[]>([new(2), new(5)]));
            Assert.AreEqual("The mapped PostgreSQL element type changed during array construction.", failure.Message);
            Assert.AreEqual(1, script.Writes);
            Assert.AreEqual(0, script.Reads);
            Assert.AreEqual(0, script.Builds);
            Assert.ThrowsExactly<ObjectDisposedException>(() => script.Returned[0].DangerousGetBits());
        }
        else
        {
            InvalidCastException failure = Assert.ThrowsExactly<InvalidCastException>(() => source.Read<Number[]>());
            Assert.AreEqual("PostgreSQL datum type OID 9001 does not match mapped type OID 9011.", failure.Message);
            Assert.AreEqual(1, script.Reads);
            Assert.AreEqual(0, script.Writes);
            Assert.ThrowsExactly<ObjectDisposedException>(() => script.Captured!.DangerousGetBits());
        }

        Assert.AreEqual(1, script.Deletes);
        Assert.AreEqual((nuint)701, source.DangerousGetBits());
    }

    /// <summary>
    /// Array writers produce independently specified shape bytes and preserve framework versus writer-produced NULL envelopes.
    /// </summary>
    [TestMethod]
    public void WriterBuildsExactTransportBeforeDeletingElementStorage()
    {
        using var script = new Script { WriterNull = true };
        Number?[] original = [new(0), null, new(-1), new(7)];
        var value = new PgArray<Number?>(original, [2, 2], [-2, 4]);
        NativeValue output = NativeValue.FromMapped(value);
        try
        {
            Assert.AreSequenceEqual<byte>(Convert.FromHexString("00000002000000040000232900000002FFFFFFFE0000000200000004"), script.Shape);
            Assert.AreEqual(0, script.BuilderMode);
            Assert.AreEqual(9002U, script.BuilderOid);
            Assert.AreEqual(101, script.FinalContext);
            Assert.AreSequenceEqual<nuint>([10, 0, 0, 17], script.Written.Select(static item => item.Bits));
            Assert.AreSequenceEqual<bool>([false, true, true, false], script.Written.Select(static item => item.IsNull));
            Assert.AreEqual(0, script.Written[1].Length);
            Assert.AreEqual(sizeof(NativeDatumReference), script.Written[2].Length);
            Assert.AreSequenceEqual<uint>([9001, 9001, 9001, 9001], script.Written.Select(static item => item.Type));
            Assert.IsTrue(script.Written.Where(static item => item.Length != 0).All(item => item.Context == script.WriterDestination));
            Assert.AreEqual(0, script.DeletesAtBuild);
            Assert.AreEqual(1, script.Deletes);
            Assert.AreEqual(3, script.Writes);
            Assert.ThrowsExactly<ObjectDisposedException>(() => script.Returned[0].DangerousGetBits());
            NativeDatumReference final = MemoryMarshal.Read<NativeDatumReference>(output.ReadBytes());
            Assert.AreEqual((nuint)8001, final._bits);
            Assert.AreEqual(101, final._context);
            Assert.AreEqual(9002, output.Integral);
            Assert.AreSequenceEqual<Number?>([new(0), null, new(-1), new(7)], original);
        }
        finally
        {
            output.Release();
        }
    }

    /// <summary>
    /// Rank-zero and maximum-rank shape bytes are independent of element storage and preserve signed bounds.
    /// </summary>
    [TestMethod]
    public void EmptyAndSixDimensionalShapesHaveIndependentWireFixtures()
    {
        var empty = new PgArray<Number>([]);
        Assert.AreSequenceEqual<byte>(Convert.FromHexString("000000000000000000002329"), NativeBackend.EncodeMappedArrayShape(empty, 9001));
        var six = new PgArray<Number>([new(2)], [1, 1, 1, 1, 1, 1], [-1, 0, 1, 2, 3, 4]);
        Assert.AreSequenceEqual<byte>(Convert.FromHexString("00000006000000010000232900000001FFFFFFFF00000001000000000000000100000001000000010000000200000001000000030000000100000004"),
            NativeBackend.EncodeMappedArrayShape(six, 9001));
    }

    /// <summary>
    /// Value-array runtime identity prevents enum reinterpretation while declared reference covariance selects the base converter.
    /// </summary>
    [TestMethod]
    public void EnumArraysRejectIntegerAliasesAndReferenceArraysUseDeclaredContract()
    {
        using var script = new Script();
        int[] integers = [1, 2];
        int?[] optional = [1, null, 2];
        Assert.ThrowsExactly<InvalidCastException>(() => PgDatumRegistry.FindArray(typeof(Kind[]))!.Write(integers));
        Assert.ThrowsExactly<InvalidCastException>(() => PgDatumRegistry.FindArray(typeof(Kind?[]))!.Write(optional));
        Assert.AreEqual(0, script.Creates);
        Assert.AreEqual(0, script.Writes);
        Kind[] valid = [Kind.One, (Kind)7];
        NativeValue enumOutput = NativeValue.FromMapped(valid);
        try
        {
            Assert.AreSequenceEqual<nuint>([3, 21], script.Written.Select(static item => item.Bits));
        }
        finally
        {
            enumOutput.Release();
        }

        NumericMessage[] derived = [new(2), new(5)];
        NativeValue referenceOutput = Marshal(SpiParameter.Create<Message[]>(derived));
        try
        {
            Assert.AreSequenceEqual<nuint>([22, 25], script.Written.Select(static item => item.Bits));
            script.Cells = [new(3), new(8)];
            Message[] read = script.Array().Read<Message[]>();
            Assert.AreEqual(typeof(Message[]), read.GetType());
            read[0] = new TextMessage("sibling");
            Assert.AreEqual(new TextMessage("sibling"), read[0]);
            Assert.AreEqual(new NumericMessage(8), read[1]);
            Assert.AreSequenceEqual<NumericMessage>([new(2), new(5)], derived);
            Assert.AreSequenceEqual<int>([1, 2], integers);
            Assert.AreSequenceEqual<int?>([1, null, 2], optional);
        }
        finally
        {
            referenceOutput.Release();
        }
    }

    /// <summary>
    /// Retained declared parameters reject replacement array identity before any writer, including NULL and empty values.
    /// </summary>
    [TestMethod]
    public void RetainedParametersRejectChangedArrayOidBeforeWriters()
    {
        using var script = new Script();
        SpiParameter present = SpiParameter.Create<Number[]>([new(7)]);
        SpiParameter empty = SpiParameter.Create(Array.Empty<Number>());
        SpiParameter absent = SpiParameter.Create<Number[]?>(null);
        script.ArrayOid = 9012;
        foreach (SpiParameter parameter in new[] { present, empty, absent })
        {
            InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => Marshal(parameter));
            Assert.AreEqual("The mapped PostgreSQL parameter type has changed since the parameter was created.", error.Message);
        }

        Assert.AreEqual(0, script.Writes);
        Assert.AreEqual(0, script.Creates);
        Assert.AreEqual(0, script.Builds);
    }

    /// <summary>
    /// Wrong or stale writer results fail before construction, including SQL NULL and later owner resets.
    /// </summary>
    /// <param name="mode">The invalid writer result partition.</param>
    [TestMethod]
    [DataRow("wrong")]
    [DataRow("wrong-null")]
    [DataRow("stale")]
    [DataRow("stale-null")]
    [DataRow("reset-earlier")]
    [DataRow("null-handle")]
    public void InvalidWriterResultsNeverReachArrayConstruction(string mode)
    {
        using var script = new Script { WriterMode = mode };
        static void write() => NativeValue.FromMapped<Number[]>([new(1), new(2)]);
        if (mode.StartsWith("wrong", StringComparison.Ordinal))
        {
            Assert.ThrowsExactly<InvalidCastException>(write);
        }
        else if (mode == "null-handle")
        {
            Assert.ThrowsExactly<InvalidOperationException>(write);
        }
        else
        {
            Assert.ThrowsExactly<ObjectDisposedException>(write);
        }

        Assert.AreEqual(0, script.Builds);
        Assert.AreEqual(1, script.Deletes);
    }

    /// <summary>
    /// Element failures, cleanup failures and both retain order and the original managed primary at each array owner.
    /// </summary>
    /// <param name="write">Whether to exercise writer rather than extraction ownership.</param>
    /// <param name="primaryFailure">Whether a later element converter fails.</param>
    /// <param name="cleanupFailure">Whether deleting temporary storage fails.</param>
    [TestMethod]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, true, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public void ArrayOwnersPreservePrimaryAndCleanupErrors(bool write, bool primaryFailure, bool cleanupFailure)
    {
        var primary = new InvalidOperationException("later element failed");
        using var script = new Script { Cells = [new(2), new(5)], Failure = primaryFailure ? primary : null, FailDelete = cleanupFailure };
        PgDatum source = script.Array();
        Action operation = write ? () => NativeValue.FromMapped<Number[]>([new(2), new(5)]) : () => source.Read<Number[]>();
        if (primaryFailure && cleanupFailure)
        {
            AggregateException error = Assert.ThrowsExactly<AggregateException>(operation);
            Assert.HasCount(2, error.InnerExceptions);
            Assert.AreSame(primary, error.InnerExceptions[0]);
            Assert.AreEqual("55006", Assert.IsInstanceOfType<PgException>(error.InnerExceptions[1]).SqlState);
        }
        else if (primaryFailure)
        {
            Assert.AreSame(primary, Assert.ThrowsExactly<InvalidOperationException>(operation));
        }
        else
        {
            Assert.AreEqual("55006", Assert.ThrowsExactly<PgException>(operation).SqlState);
        }

        Assert.AreEqual(1, script.Deletes);
        Assert.AreEqual((nuint)701, source.DangerousGetBits());
        PgDatum retained = write ? script.Returned[0] : script.Captured!;
        if (cleanupFailure)
        {
            Assert.AreEqual(write ? (nuint)12 : 5, retained.DangerousGetBits());
        }
        else
        {
            Assert.ThrowsExactly<ObjectDisposedException>(() => retained.DangerousGetBits());
        }

        Assert.AreEqual(write && primaryFailure ? 0 : 1, script.Releases);
    }

    /// <summary>
    /// A later writer cannot consume a caller-owned returned datum and final array storage survives temporary deletion.
    /// </summary>
    [TestMethod]
    public void WritersMayReturnCallerOwnedValuesWithoutTransferringOwnership()
    {
        using var script = new Script { ReturnCallerOwned = true };
        NativeValue output = NativeValue.FromMapped<Number[]>([new(3), new(6)]);
        try
        {
            Assert.AreSequenceEqual<nuint>([13, 16], script.Returned.Select(static datum => datum.DangerousGetBits()));
            Assert.AreEqual(1, script.Deletes);
            Assert.IsTrue(script.Written.All(static item => item.Context == 101));
            NativeDatumReference final = MemoryMarshal.Read<NativeDatumReference>(output.ReadBytes());
            Assert.AreEqual(101, final._context);
            Assert.AreEqual((nuint)8001, final._bits);
        }
        finally
        {
            output.Release();
        }
    }

    /// <summary>
    /// Typed scalar callers select raw array readers while native-address calls remain denied.
    /// </summary>
    [TestMethod]
    public void TypedCatalogAndSpiResultsKeepExactArraysAndDeferredBoundaries()
    {
        using var script = new Script { Cells = [new(3), new(8)] };
        Assert.AreSequenceEqual<Number>([new(103), new(108)], Spi.ExecuteScalar<Number[]>("SELECT array"));
        Assert.AreSequenceEqual<Alias>([new(1003), new(1008)], PgFunctions.Call<Alias[]>(77));
        Assert.AreEqual(1, script.CallMode);
        Assert.AreEqual(9002U, script.CallExpectedOid);
        Assert.ThrowsExactly<NotSupportedException>(() => PgFunctions.DangerousCall<WriteOnly[]>(1, 0));
    }

    /// <summary>
    /// Native array results preserve independent cells and bounds while ending both temporary owners and retaining arguments.
    /// </summary>
    [TestMethod]
    public void NativeArrayResultsPreserveShapesAndOwnerBoundaries()
    {
        using var script = new Script { Lengths = [2, 2], Bounds = [-2, 4], Cells = [new(0), new(7), new(0, true), new(19)] };
        PgDatum argument = script.Array();
        PgArray<Number?> result = PgFunctions.DangerousCall<PgArray<Number?>>(4567, 345, argument);
        Assert.AreSequenceEqual<Number?>([new(100), new(107), null, new(119)], [.. result]);
        Assert.AreSequenceEqual<int>([2, 2], result.Lengths.ToArray());
        Assert.AreSequenceEqual<int>([-2, 4], result.LowerBounds.ToArray());
        Assert.AreEqual(new Number(119), result.GetValue(-1, 5));
        Assert.AreEqual(4567, script.CallPointer);
        Assert.AreEqual(0, script.CallMode);
        Assert.AreEqual(9002U, script.CallExpectedOid);
        Assert.AreEqual(3, script.Reads);
        Assert.AreEqual(1, script.Executions);
        Assert.AreEqual(2, script.Creates);
        Assert.AreEqual(2, script.Deletes);
        Assert.AreEqual(2, script.Releases);
        Assert.AreSequenceEqual<nint>([203, 202], script.Memory.Requests.Where(static request => request._operation == NativeMemoryOperation.Delete)
            .Select(static request => request._context));
        Assert.ThrowsExactly<ObjectDisposedException>(() => script.Captured!.DangerousGetBits());
        Assert.AreEqual((nuint)701, argument.DangerousGetBits());

        script.Reads = 0;
        Assert.ThrowsExactly<InvalidOperationException>(() => PgFunctions.DangerousCall<Number?[]>(4567, 0));
        Assert.AreEqual(0, script.Reads);
        Assert.AreEqual(2, script.Executions);
        Assert.AreEqual(4, script.Deletes);
        script.Lengths = [2];
        script.Bounds = [1];
        script.Cells = [new(3), new(8)];
        Assert.AreSequenceEqual<Alias>([new(1003), new(1008)], PgFunctions.DangerousCall<Alias[]>(4567, 0));
        Assert.AreEqual(3, script.Executions);
        Assert.AreEqual(6, script.Deletes);
        Message[] messages = PgFunctions.DangerousCall<Message[]>(4567, 0);
        Assert.AreEqual(typeof(Message[]), messages.GetType());
        Assert.AreSequenceEqual<Message>([new NumericMessage(3), new NumericMessage(8)], messages);
        Assert.AreEqual(4, script.Executions);
        Assert.AreEqual(8, script.Deletes);
    }

    /// <summary>
    /// Native whole NULL, empty arrays and all-NULL arrays remain distinct without creating absent element converters.
    /// </summary>
    [TestMethod]
    public void NativeArrayAbsenceKeepsContainerAndElementNullability()
    {
        using var script = new Script { NativeResultNull = true };
        Assert.IsNull(PgFunctions.DangerousCall<ReadOnly[]>(4567, 0));
        Assert.AreEqual(0, script.Extractions);
        Assert.AreEqual(1, script.Deletes);
        script.NativeResultNull = false;
        script.Cells = [];
        script.Lengths = [];
        script.Bounds = [];
        Assert.IsEmpty(PgFunctions.DangerousCall<ReadOnly[]>(4567, 0));
        script.Cells = [new(0, true), new(0, true)];
        script.Lengths = [2];
        script.Bounds = [1];
        Assert.AreSequenceEqual<ReadOnly?>([null, null], PgFunctions.DangerousCall<ReadOnly?[]>(4567, 0));
        Assert.AreSequenceEqual<Message?>([null, null], PgFunctions.DangerousCall<Message?[]>(4567, 0));
        InvalidOperationException required = Assert.ThrowsExactly<InvalidOperationException>(() => PgFunctions.DangerousCall<ReadOnly[]>(4567, 0));
        Assert.AreEqual("SQL NULL cannot be read as a non-nullable managed value.", required.Message);
        Assert.AreEqual(0, script.Reads);
        Assert.AreEqual(5, script.Executions);
        Assert.AreEqual(4, script.Extractions);
        Assert.AreEqual(9, script.Deletes);
        Assert.AreEqual(9, script.Releases);
    }

    /// <summary>
    /// Native invocation cannot replace the captured array identity, even when no element reader would run.
    /// </summary>
    /// <param name="state">A present array, whole NULL, or present empty array.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void NativeArraysRevalidateIdentityBeforeExtraction(int state)
    {
        using var script = new Script { ChangeIdentityDuringCall = true, NativeResultNull = state == 1 };
        if (state == 2)
        {
            script.Cells = [];
            script.Lengths = [];
            script.Bounds = [];
        }

        InvalidCastException failure = Assert.ThrowsExactly<InvalidCastException>(() => PgFunctions.DangerousCall<Number[]>(4567, 0));
        Assert.AreEqual("PostgreSQL datum type OID 9002 does not match mapped array type OID 9012.", failure.Message);
        Assert.AreEqual(9002U, script.CallExpectedOid);
        Assert.AreEqual(0, script.CallMode);
        Assert.AreEqual(0, script.Reads);
        Assert.AreEqual(0, script.Extractions);
        Assert.AreEqual(1, script.Executions);
        Assert.AreEqual(1, script.Creates);
        Assert.AreEqual(1, script.Deletes);
        Assert.AreEqual(1, script.Releases);
    }

    /// <summary>
    /// Inner array-reader and outer native-result cleanup both run without hiding the original reader failure.
    /// </summary>
    /// <param name="cleanupFailure">Whether both temporary owner deletions fail.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeArrayCleanupPreservesNestedFailures(bool cleanupFailure)
    {
        var primary = new InvalidOperationException("second native array cell failed");
        using var script = new Script { Failure = primary, FailDelete = cleanupFailure };
        PgDatum argument = script.Array();
        if (cleanupFailure)
        {
            AggregateException outer = Assert.ThrowsExactly<AggregateException>(() => PgFunctions.DangerousCall<Number[]>(4567, 0, argument));
            Assert.HasCount(2, outer.InnerExceptions);
            AggregateException inner = Assert.IsInstanceOfType<AggregateException>(outer.InnerExceptions[0]);
            Assert.HasCount(2, inner.InnerExceptions);
            Assert.AreSame(primary, inner.InnerExceptions[0]);
            Exception[] cleanupFailures = [inner.InnerExceptions[1], outer.InnerExceptions[1]];
            foreach (Exception exception in cleanupFailures)
            {
                PgException cleanup = Assert.IsInstanceOfType<PgException>(exception);
                Assert.AreEqual("55006", cleanup.SqlState);
                Assert.AreEqual("array cleanup failed", cleanup.Message);
                Assert.AreEqual("cleanup detail", cleanup.Detail);
            }

            Assert.AreEqual((nuint)5, script.Captured!.DangerousGetBits());
        }
        else
        {
            Assert.AreSame(primary, Assert.ThrowsExactly<InvalidOperationException>(() => PgFunctions.DangerousCall<Number[]>(4567, 0, argument)));
            Assert.ThrowsExactly<ObjectDisposedException>(() => script.Captured!.DangerousGetBits());
        }

        Assert.AreEqual((nuint)701, argument.DangerousGetBits());
        Assert.AreEqual(2, script.Reads);
        Assert.AreEqual(1, script.Executions);
        Assert.AreEqual(2, script.Deletes);
        Assert.AreEqual(2, script.Releases);
        Assert.AreSequenceEqual<nint>([203, 202], script.Memory.Requests.Where(static request => request._operation == NativeMemoryOperation.Delete)
            .Select(static request => request._context));
    }

    /// <summary>
    /// Native acquisition and builder errors retain owned diagnostics alongside cleanup failures.
    /// </summary>
    /// <param name="write">Whether to exercise construction instead of extraction.</param>
    /// <param name="cleanupFailure">Whether temporary owner deletion also fails.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void NativeArrayFailuresPreserveDiagnosticsAndCleanup(bool write, bool cleanupFailure)
    {
        using var script = new Script { FailNative = true, FailDelete = cleanupFailure };
        PgDatum source = script.Array();
        Action operation = write ? () => NativeValue.FromMapped<Number[]>([new(2), new(5)]) : () => source.Read<Number[]>();
        PgException primary;
        if (cleanupFailure)
        {
            AggregateException failure = Assert.ThrowsExactly<AggregateException>(operation);
            Assert.HasCount(2, failure.InnerExceptions);
            primary = Assert.IsInstanceOfType<PgException>(failure.InnerExceptions[0]);
            PgException cleanup = Assert.IsInstanceOfType<PgException>(failure.InnerExceptions[1]);
            Assert.AreEqual("55006", cleanup.SqlState);
            Assert.AreEqual("array cleanup failed", cleanup.Message);
            Assert.AreEqual("cleanup detail", cleanup.Detail);
        }
        else
        {
            primary = Assert.ThrowsExactly<PgException>(operation);
        }

        Assert.AreEqual("P8601", primary.SqlState);
        Assert.AreEqual("array native operation failed", primary.Message);
        Assert.AreEqual("owned detail", primary.Detail);
        Assert.AreEqual("owned hint", primary.Hint);
        Assert.AreEqual(1, script.Deletes);
        Assert.AreEqual(1, script.Releases);
        Assert.AreEqual((nuint)701, source.DangerousGetBits());
        Assert.AreEqual(0, script.Reads);
    }

    /// <summary>
    /// The same lazy failure is shared by scalar, vector and shaped operations while absent cells bypass it.
    /// </summary>
    [TestMethod]
    public void FactoryFailureIsSharedAcrossScalarAndArrayDirections()
    {
        var primary = new InvalidOperationException("shared array factory failed");
        int factories = 0;
        PgDatumRegistry.RegisterValue<FactoryValue>("array_factory", "fixed", PgTypeOrigin.External, typeof(FactoryConverter),
            () =>
            {
                factories++;
                throw primary;
            }, true, true);
        using var script = new Script();
        NativeValue empty = NativeValue.FromMapped(Array.Empty<FactoryValue>());
        empty.Release();
        Assert.AreEqual(0, factories);
        Assert.AreSame(primary, Assert.ThrowsExactly<InvalidOperationException>(() => Script.Datum(script.ElementOid, 1).Read<FactoryValue>()));
        Assert.AreSame(primary, Assert.ThrowsExactly<InvalidOperationException>(() => script.Array().Read<FactoryValue[]>()));
        Assert.AreSame(primary, Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.FromMapped(new PgArray<FactoryValue>([new(3)]))));
        Assert.AreEqual(1, factories);
        Assert.AreEqual(3, script.Deletes);
    }

    /// <summary>
    /// Missing SPI rows and utility results preserve array absence without a converter at every selected width.
    /// </summary>
    [TestMethod]
    public void MissingSpiRowsAndUtilityResultsReturnAbsentMappedArrays()
    {
        using var script = new Script { Rows = 0 };
        Assert.IsNull(Spi.ExecuteScalar<ReadOnly[]>("SELECT no_rows"));
        (ReadOnly[]? first, PgArray<ReadOnly>? second) = Spi.ExecuteScalars<ReadOnly[], PgArray<ReadOnly>>("SELECT no_rows");
        Assert.IsNull(first);
        Assert.IsNull(second);
        (ReadOnly[]? left, PgArray<ReadOnly>? middle, ReadOnly?[]? right) = Spi.ExecuteScalars<ReadOnly[], PgArray<ReadOnly>, ReadOnly?[]>("SELECT no_rows");
        Assert.IsNull(left);
        Assert.IsNull(middle);
        Assert.IsNull(right);
        script.Utility = true;
        Assert.IsNull(Spi.ExecuteScalar<ReadOnly[]>("UTILITY"));
        Assert.AreEqual(0, script.Reads);
        Assert.AreEqual(0, script.Extractions);
        Assert.AreEqual(4, script.Deletes);
        Assert.AreEqual(4, script.Releases);
    }

    /// <summary>
    /// A declared custom base array retains its codec when a covariant runtime leaf has a distinct datum mapping.
    /// </summary>
    [TestMethod]
    public void DeclaredCustomArrayConverterPrecedesRuntimeDatumMapping()
    {
        using var script = new Script();
        CustomLeaf[] source = [new(3), new(6)];
        SpiParameter parameter = SpiParameter.Create<CustomRoot[]>(source);
        NativeValue output = Marshal(parameter);
        try
        {
            Assert.AreSequenceEqual<byte>([43, 46], script.CodecBytes);
            Assert.AreSequenceEqual<CustomRoot>([new CustomLeaf(3), new CustomLeaf(6)], output.ReadArray<CustomRoot>().ToVector());
            Assert.AreSequenceEqual<CustomLeaf>([new(3), new(6)], source);
            Assert.AreEqual(0, script.Builds);
            Assert.AreEqual(0, script.Writes);
        }
        finally
        {
            output.Release();
        }
    }

    /// <summary>
    /// Erased row, tuple and canonical-array routes reject mapped containers before losing type metadata. A declared
    /// mapped array is written through its mapping instead, which the backend tests cover.
    /// </summary>
    [TestMethod]
    public void ErasedArrayPathsRejectPresentEmptyAndNullWithoutChangingOwners()
    {
        using var script = new Script { ElementOid = 23, ArrayOid = 1007 };
        int[] original = [5, 6];
        Kind[] mapped = [Kind.One, (Kind)2];
        var row = new SpiRow([original], [new SpiColumn("array", 1007)]);
        var tuple = new PgHeapTuple(new PgTupleDescriptor(2249, -1, [new PgTupleAttributeInfo("array", 1007, 1007, -1, 0)]), [original]);
        int lookups = script.Lookups;
        script.Memory.Requests.Clear();
        Assert.ThrowsExactly<NotSupportedException>(() => row.Set<object>(0, mapped));
        Assert.ThrowsExactly<NotSupportedException>(() => tuple.Set<object>(0, mapped));
        Assert.ThrowsExactly<NotSupportedException>(() => tuple.Set(0, SpiParameter.CreateCell(1007, mapped)));
        Assert.ThrowsExactly<NotSupportedException>(() => PgHeapTuple.Create(("array", SpiParameter.CreateCell(1007, mapped))));
        Assert.ThrowsExactly<NotSupportedException>(() => SpiType.ToNative(mapped));
        Assert.ThrowsExactly<NotSupportedException>(() => SpiArray.Wrap(mapped));
        var empty = new PgArray<Kind>([]);
        var nulls = new PgArray<Kind?>([null, null]);
        Assert.ThrowsExactly<NotSupportedException>(() => SpiArray.Cast<int>(empty));
        Assert.ThrowsExactly<NotSupportedException>(() => SpiArray.Convert(nulls, typeof(int?[])));
        Assert.ThrowsExactly<NotSupportedException>(() => NativeValue.FromArray(empty));
        Assert.ThrowsExactly<NotSupportedException>(() => NativeValue.FromArray(nulls));
        Assert.ThrowsExactly<NotSupportedException>(() => SpiRow.Convert<int[]>(mapped));
        Assert.ThrowsExactly<NotSupportedException>(() => SpiRow.Convert<Kind[]?>(null));
        NativeValue ordinary = NativeValue.FromArray(new PgArray<int?>([1, null, 2]));
        try
        {
            Assert.ThrowsExactly<NotSupportedException>(ordinary.ReadArray<Kind?>);
            Assert.AreSequenceEqual<int?>([1, null, 2], ordinary.ReadArray<int?>().ToVector());
        }
        finally
        {
            ordinary.Release();
        }

        Assert.AreSame(original, row.Get<int[]>(0));
        Assert.AreSame(original, tuple.Get<int[]>(0));
        Assert.AreSequenceEqual<int>([5, 6], original);
        Assert.AreEqual(lookups, script.Lookups);
        Assert.AreEqual(0, script.Creates);
        Assert.AreEqual(0, script.Executions);
        Assert.AreEqual(0, script.Reads);
        Assert.AreEqual(0, script.Writes);
        Assert.IsEmpty(script.Memory.Requests);
    }

    /// <summary>
    /// IEnumerable preserves source array identity while explicit element projection and spans create intentional values.
    /// </summary>
    [TestMethod]
    public void EnumerableMappedEnumsRejectUnderlyingArrayIdentity()
    {
        int[] source = [1, 7];
        object boxed = source;
        IEnumerable<Kind> aliased = (Kind[])boxed;
        Assert.ThrowsExactly<InvalidCastException>(() => new PgArray<Kind>(aliased));
        var projected = new PgArray<Kind>(source.Select(static item => (Kind)item));
        Assert.AreSequenceEqual<Kind>([Kind.One, (Kind)7], projected.ToVector());
        var explicitView = new PgArray<Kind>(MemoryMarshal.Cast<int, Kind>(source.AsSpan()), [2]);
        Assert.AreSequenceEqual<Kind>([Kind.One, (Kind)7], explicitView.ToVector());
        NumericMessage[] derived = [new(3), new(5)];
        var covariant = new PgArray<Message>(derived);
        Assert.AreSequenceEqual<Message>([new NumericMessage(3), new NumericMessage(5)], covariant.ToVector());
        Assert.AreSequenceEqual<int>([1, 7], source);
    }

    /// <summary>
    /// Applies exactly the declared parameter metadata used by native invocation.
    /// </summary>
    private static NativeValue Marshal(SpiParameter value) => SpiType.ToNative(value.Value, value.CustomMapping,
        value.CustomArrayMapping, value.DatumMapping, value.TypeOid, value.DatumArrayMapping);

    /// <summary>
    /// Converts a numeric leaf while recording its temporary read and write owners.
    /// </summary>
    private sealed class NumberConverter : IPgDatumReader<Number>, IPgDatumWriter<Number>
    {
        /// <inheritdoc />
        public Number Read(PgDatum value) => new(Script.Current.Read(value) + 100);

        /// <inheritdoc />
        public PgDatum Write(Number value, uint typeOid, PgMemoryContext destination)
            => Script.Current.Write(value.Value + 10, typeOid, destination, value.Value == -1);
    }

    /// <summary>
    /// Shares the SQL identity with a distinguishable target-directed reader.
    /// </summary>
    private sealed class AliasReader : IPgDatumReader<Alias>
    {
        /// <inheritdoc />
        public Alias Read(PgDatum value) => new(Script.Current.Read(value) + 1000);
    }

    /// <summary>
    /// Makes shared factory reuse visible through independent read and write transformations.
    /// </summary>
    private sealed class SharedConverter : IPgDatumReader<Shared>, IPgDatumWriter<Shared>
    {
        /// <inheritdoc />
        public Shared Read(PgDatum value) => new(Script.Current.Read(value) + 1);

        /// <inheritdoc />
        public PgDatum Write(Shared value, uint typeOid, PgMemoryContext destination)
            => Script.Current.Write(value.Value + 10, typeOid, destination);
    }

    /// <summary>
    /// Supplies an enum converter unrelated to its CLR underlying integer bits.
    /// </summary>
    private sealed class KindConverter : IPgDatumReader<Kind>, IPgDatumWriter<Kind>
    {
        /// <inheritdoc />
        public Kind Read(PgDatum value) => (Kind)Script.Current.Read(value);

        /// <inheritdoc />
        public PgDatum Write(Kind value, uint typeOid, PgMemoryContext destination)
            => Script.Current.Write((int)value * 3, typeOid, destination);
    }

    /// <summary>
    /// Chooses the declared reference contract even when a derived value has a separate mapping.
    /// </summary>
    private sealed class MessageConverter : IPgDatumReader<Message>, IPgDatumWriter<Message>
    {
        /// <inheritdoc />
        public Message Read(PgDatum value) => new NumericMessage(Script.Current.Read(value));

        /// <inheritdoc />
        public PgDatum Write(Message value, uint typeOid, PgMemoryContext destination)
            => Script.Current.Write(((NumericMessage)value).Value + 20, typeOid, destination);
    }

    /// <summary>
    /// Defines a reader whose factory must be bypassed for absent cells.
    /// </summary>
    private sealed class ReadOnlyConverter : IPgDatumReader<ReadOnly>
    {
        /// <inheritdoc />
        public ReadOnly Read(PgDatum value) => throw new InvalidOperationException("The absent reader must not execute.");
    }

    /// <summary>
    /// Defines a writer whose factory must be bypassed for absent cells.
    /// </summary>
    private sealed class WriteOnlyConverter : IPgDatumWriter<WriteOnly>
    {
        /// <inheritdoc />
        public PgDatum Write(WriteOnly value, uint typeOid, PgMemoryContext destination)
            => throw new InvalidOperationException("The absent writer must not execute.");
    }

    /// <summary>
    /// Makes accidental runtime-derived reader selection fail immediately.
    /// </summary>
    private sealed class DerivedReader : IPgDatumReader<NumericMessage>
    {
        /// <inheritdoc />
        public NumericMessage Read(PgDatum value) => throw new InvalidOperationException("The declared base reader must execute.");
    }

    /// <summary>
    /// Defines both directions of a converter whose factory deliberately fails.
    /// </summary>
    private sealed class FactoryConverter : IPgDatumReader<FactoryValue>, IPgDatumWriter<FactoryValue>
    {
        /// <inheritdoc />
        public FactoryValue Read(PgDatum value) => throw new InvalidOperationException("The factory must fail first.");

        /// <inheritdoc />
        public PgDatum Write(FactoryValue value, uint typeOid, PgMemoryContext destination)
            => throw new InvalidOperationException("The factory must fail first.");
    }

    /// <summary>
    /// Defines the unused mapping on a derived custom base-type value.
    /// </summary>
    private sealed class CustomLeafReader : IPgDatumReader<CustomLeaf>
    {
        /// <inheritdoc />
        public CustomLeaf Read(PgDatum value) => throw new InvalidOperationException("Use the retained custom root codec.");
    }

    /// <summary>
    /// Supplies an independently observable byte encoding for the declared custom root.
    /// </summary>
    private sealed class CustomCodec : PgTypeCodec<CustomRoot>
    {
        /// <inheritdoc />
        public override CustomRoot Parse(string value) => throw new InvalidOperationException("Only binary custom-array transport is expected.");

        /// <inheritdoc />
        public override string Format(CustomRoot value) => throw new InvalidOperationException("Only binary custom-array transport is expected.");

        /// <inheritdoc />
        public override CustomRoot Read(ReadOnlySpan<byte> payload) => new CustomLeaf(payload[0] - 40);

        /// <inheritdoc />
        public override void Write(CustomRoot value, IBufferWriter<byte> destination)
        {
            byte encoded = checked((byte)(value.Value + 40));
            Script.Current.CodecBytes.Add(encoded);
            destination.GetSpan(1)[0] = encoded;
            destination.Advance(1);
        }
    }

    /// <summary>
    /// Gives a user-code failure a recognizable stack origin through cleanup.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowElement(Exception error) => throw error;

    /// <summary>
    /// Scripts transport responses and owner invalidation without modeling native PostgreSQL array internals.
    /// </summary>
    private sealed class Script : IDisposable
    {
        [ThreadStatic]
        private static Script? s_current;

        /// <summary>
        /// Makes <paramref name="current"/> the Script this thread's native callbacks use, or restores the enclosing one.
        /// </summary>
        private static void Install(Script? current) => s_current = current;
        private readonly Script? _previous = s_current;
        private readonly MemoryContextTestFixture.Scope _scope;
        private readonly nint _previousBackend;
        private readonly HashSet<nint> _deleted = [];
        private readonly Dictionary<nint, nint> _generations = [];

        /// <summary>
        /// Installs synchronous thread-local responders for the existing native seams.
        /// </summary>
        internal Script()
        {
            Install(this);
            Memory = new MemoryContextTestFixture();
            Memory.Handler = RespondMemory;
            _scope = MemoryContextTestFixture.Enter();
            _previousBackend = NativeBackend.Enter((nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Invoke);
        }

        /// <summary>
        /// Gets the current script without sharing mutable counters across test threads.
        /// </summary>
        internal static Script Current => s_current!;

        /// <summary>
        /// Gets the memory responder and its recorded requests.
        /// </summary>
        internal MemoryContextTestFixture Memory { get; }

        /// <summary>
        /// Gets or sets independently supplied raw array cells.
        /// </summary>
        internal Cell[] Cells
        {
            get;
            set;
        } = [new(2), new(5)];

        /// <summary>
        /// Gets or sets independently supplied dimension lengths.
        /// </summary>
        internal int[] Lengths
        {
            get;
            set;
        } = [2];

        /// <summary>
        /// Gets or sets independently supplied lower bounds.
        /// </summary>
        internal int[] Bounds
        {
            get;
            set;
        } = [1];

        /// <summary>
        /// Gets or sets the live catalog element identity.
        /// </summary>
        internal uint ElementOid
        {
            get;
            set;
        } = 9001;

        /// <summary>
        /// Gets or sets the live catalog array identity.
        /// </summary>
        internal uint ArrayOid
        {
            get;
            set;
        } = 9002;

        /// <summary>
        /// Gets or sets an independently reported extraction identity.
        /// </summary>
        internal uint? ReportedElement
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the number of SPI rows.
        /// </summary>
        internal int Rows
        {
            get;
            set;
        } = 1;

        /// <summary>
        /// Gets or sets whether SPI returns zero columns for a utility command.
        /// </summary>
        internal bool Utility
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets a later managed converter failure.
        /// </summary>
        internal Exception? Failure
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether native owner deletion fails.
        /// </summary>
        internal bool FailDelete
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether raw extraction or construction fails inside the native error seam.
        /// </summary>
        internal bool FailNative
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether the selected present negative leaf writes SQL NULL.
        /// </summary>
        internal bool WriterNull
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the deliberately invalid writer provenance partition.
        /// </summary>
        internal string? WriterMode
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether writer results use an independent caller owner.
        /// </summary>
        internal bool ReturnCallerOwned
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether the first converter changes the live catalog identity before the next element.
        /// </summary>
        internal bool ChangeElementDuringConversion
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether a direct function reports SQL NULL rather than a present array.
        /// </summary>
        internal bool NativeResultNull
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether a direct call changes the mapping identity before returning its captured result.
        /// </summary>
        internal bool ChangeIdentityDuringCall
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the latest reader input retained for lifetime assertions.
        /// </summary>
        internal PgDatum? Captured
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the reader invocation count.
        /// </summary>
        internal int Reads
        {
            get;
            set;
        }

        /// <summary>
        /// Gets the writer invocation count.
        /// </summary>
        internal int Writes
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the live element lookup count.
        /// </summary>
        internal int Lookups
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the temporary allocation count.
        /// </summary>
        internal int Creates
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the attempted temporary deletion count.
        /// </summary>
        internal int Deletes
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the attempted extraction count.
        /// </summary>
        internal int Extractions
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the attempted eager construction count.
        /// </summary>
        internal int Builds
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the SPI and catalog execution count.
        /// </summary>
        internal int Executions
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the released transport count.
        /// </summary>
        internal int Releases
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the writer's explicit supplied destination.
        /// </summary>
        internal nint WriterDestination
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets all original writer handles, including those expected to expire.
        /// </summary>
        internal List<PgDatum> Returned { get; } = [];

        /// <summary>
        /// Gets independently recorded custom codec payload bytes.
        /// </summary>
        internal List<byte> CodecBytes { get; } = [];

        /// <summary>
        /// Gets independently captured construction transport cells.
        /// </summary>
        internal WrittenCell[] Written
        {
            get;
            private set;
        } = [];

        /// <summary>
        /// Gets the exact shape metadata presented to the native builder.
        /// </summary>
        internal byte[] Shape
        {
            get;
            private set;
        } = [];

        /// <summary>
        /// Gets the construction mode.
        /// </summary>
        internal int BuilderMode
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the expected final array identity.
        /// </summary>
        internal uint BuilderOid
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the final array destination before temporary cleanup.
        /// </summary>
        internal nint FinalContext
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the number of deletion attempts already made at construction.
        /// </summary>
        internal int DeletesAtBuild
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the catalog request's exactness mode.
        /// </summary>
        internal int CallMode
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the requested catalog return identity.
        /// </summary>
        internal uint CallExpectedOid
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the native address independently recorded from a direct invocation request.
        /// </summary>
        internal nint CallPointer
        {
            get;
            private set;
        }

        /// <summary>
        /// Creates a live raw array whose original owner is distinct from extraction storage.
        /// </summary>
        internal PgDatum Array(bool isNull = false) => Datum(ArrayOid, 701, isNull);

        /// <summary>
        /// Creates a deterministic live datum with an independently chosen identity.
        /// </summary>
        internal static PgDatum Datum(uint oid, nuint bits, bool isNull = false)
            => PgDatum.DangerousCreate(bits, oid, PgMemoryContext.Current, isNull);

        /// <summary>
        /// Records a reader call before optionally failing on the second element.
        /// </summary>
        internal int Read(PgDatum value)
        {
            Reads++;
            Captured = value;
            if (Reads == 1 && ChangeElementDuringConversion)
            {
                ElementOid = 9011;
                ArrayOid = 9012;
            }

            if (Reads == 2 && Failure is { } failure)
            {
                ThrowElement(failure);
            }

            return checked((int)value.DangerousGetBits());
        }

        /// <summary>
        /// Produces checked raw values with independently controlled ownership, identity and NULL state.
        /// </summary>
        internal PgDatum Write(int bits, uint typeOid, PgMemoryContext destination, bool nullCandidate = false)
        {
            Writes++;
            WriterDestination = destination.Id;
            if (Writes == 2 && Failure is { } failure)
            {
                ThrowElement(failure);
            }

            if (WriterMode == "null-handle")
            {
                return null!;
            }

            if (WriterMode == "reset-earlier" && Writes == 2)
            {
                _generations[destination.Id] = Generation(destination.Id) + 1;
            }

            PgMemoryContext owner = ReturnCallerOwned ? PgMemoryContext.Current : destination;
            bool isNull = (WriterNull && nullCandidate) || WriterMode is "wrong-null" or "stale-null";
            uint oid = WriterMode is "wrong" or "wrong-null" ? typeOid + 1 : typeOid;
            PgDatum result = PgDatum.DangerousCreate(isNull ? 0 : checked((nuint)bits), oid, owner, isNull);
            Returned.Add(result);
            if (Writes == 1 && ChangeElementDuringConversion)
            {
                ElementOid = 9011;
                ArrayOid = 9012;
            }

            if (WriterMode is "stale" or "stale-null")
            {
                _generations[owner.Id] = Generation(owner.Id) + 1;
            }

            return result;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            NativeBackend.Exit(_previousBackend);
            _scope.Dispose();
            Memory.Dispose();
            Install(_previous);
        }

        /// <summary>
        /// Supplies generation changes independently from managed handle state.
        /// </summary>
        private nint Generation(nint context) => _generations.GetValueOrDefault(context, 901);

        /// <summary>
        /// Invalidates only successfully deleted owners and preserves failed-delete lifetimes.
        /// </summary>
        private NativeMemoryResult RespondMemory(NativeMemoryRequest request)
        {
            if (request._operation == NativeMemoryOperation.Callback)
            {
                return new NativeMemoryResult { _context = 101 };
            }

            if (request._operation == NativeMemoryOperation.Create)
            {
                return new NativeMemoryResult { _context = 201 + ++Creates };
            }

            if (request._operation == NativeMemoryOperation.Delete)
            {
                Deletes++;
                if (FailDelete)
                {
                    throw new PgException("55006", "array cleanup failed", detail: "cleanup detail");
                }

                _deleted.Add(request._context);
            }

            if (_deleted.Contains(request._context) && request._operation is NativeMemoryOperation.Name or NativeMemoryOperation.CaptureGeneration)
            {
                throw new PgException("55000", "deleted array owner");
            }

            if (request._operation == NativeMemoryOperation.CaptureGeneration)
            {
                return new NativeMemoryResult { _value = Generation(request._context) };
            }

            return Memory.Respond(request);
        }

        /// <summary>
        /// Transports scripted failures through the same owned native diagnostic boundary as production.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Invoke(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
        {
            try
            {
                Current.Respond(request, result);
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
        /// Answers current identities, literal array cells, construction acknowledgements and typed result envelopes.
        /// </summary>
        private void Respond(NativeSpiRequest* request, NativeSpiResult* result)
        {
            if (request->_operation is SpiOperation.DatumType or SpiOperation.CustomType)
            {
                Lookups++;
                result->_text.Integral = ElementOid;
                return;
            }

            if (request->_operation == SpiOperation.Enum)
            {
                result->_text.Integral = ArrayOid;
                return;
            }

            if (request->_operation == SpiOperation.OpenSession)
            {
                request->_sessionId = 51;
                return;
            }

            if (request->_operation == SpiOperation.Prepare)
            {
                request->_plan = 71;
                return;
            }

            if (request->_operation is SpiOperation.CloseSession or SpiOperation.FreePlan)
            {
                return;
            }

            result->_release = &Release;
            if (request->_operation == SpiOperation.Datum)
            {
                if (request->_scalarOperation == 2)
                {
                    NativeDatumReference reference = MemoryMarshal.Read<NativeDatumReference>(request->_parameters[0]._value.ReadBytes());
                    result->_text.Integral = (long)reference._bits;
                    result->_text.IsNull = request->_parameters[0]._value.IsNull;
                    return;
                }

                Extractions++;
                ThrowNativeIfRequested();
                int[] shape = [.. Lengths, .. Bounds];
                result->_text = NativeValue.FromBytes(MemoryMarshal.AsBytes(shape.AsSpan()));
                result->_resultTypeOid = ReportedElement ?? ElementOid;
                result->_rowCount = Cells.Length;
                result->_values = (NativeValue*)NativeMemory.AllocZeroed((nuint)Math.Max(1, Cells.Length), (nuint)sizeof(NativeValue));
                for (int index = 0; index < Cells.Length; index++)
                {
                    result->_values[index] = new NativeValue { Integral = Cells[index].Bits, IsNull = Cells[index].IsNull ? (byte)1 : (byte)0 };
                }

                return;
            }

            if (request->_operation == SpiOperation.Array)
            {
                Builds++;
                BuilderMode = request->_scalarOperation;
                BuilderOid = request->_scalarResultOid;
                FinalContext = request->_resultContext;
                DeletesAtBuild = Deletes;
                Shape = request->_parameters[0]._value.ReadBytes();
                Written = new WrittenCell[request->_parameterCount - 1];
                for (int index = 0; index < Written.Length; index++)
                {
                    NativeSpiParameter parameter = request->_parameters[index + 1];
                    byte[] bytes = parameter._value.ReadBytes();
                    NativeDatumReference reference = bytes.Length == 0 ? default : MemoryMarshal.Read<NativeDatumReference>(bytes);
                    Written[index] = new WrittenCell(parameter._typeOid, reference._bits, parameter._value.IsNull != 0, bytes.Length, reference._context);
                }

                ThrowNativeIfRequested();
                result->_resultTypeOid = ArrayOid;
                result->_text.Integral = 8001;
                return;
            }

            Executions++;
            if (request->_operation == SpiOperation.FunctionCall)
            {
                CallMode = request->_scalarOperation;
                CallExpectedOid = request->_scalarResultOid;
                CallPointer = request->_nativeFunction;
                result->_resultTypeOid = ArrayOid;
                result->_text.Integral = 701;
                result->_text.IsNull = NativeResultNull ? (byte)1 : (byte)0;
                if (CallPointer != 0 && ChangeIdentityDuringCall)
                {
                    ElementOid = 9011;
                    ArrayOid = 9012;
                }

                return;
            }

            int columns = Utility ? 0 : (int)request->_resultMode - 1;
            result->_columnCount = columns;
            result->_rowCount = Rows;
            result->_columns = (NativeSpiColumn*)NativeMemory.AllocZeroed((nuint)Math.Max(1, columns), (nuint)sizeof(NativeSpiColumn));
            result->_values = (NativeValue*)NativeMemory.AllocZeroed((nuint)Math.Max(1, columns * Rows), (nuint)sizeof(NativeValue));
            for (int index = 0; index < columns; index++)
            {
                result->_columns[index] = new NativeSpiColumn { _typeOid = ArrayOid, _baseTypeOid = ArrayOid, _name = NativeValue.FromString("array") };
                if (Rows != 0)
                {
                    result->_values[index] = new NativeValue { Integral = 701 };
                }
            }
        }

        /// <summary>
        /// Supplies a native diagnostic without running a managed converter.
        /// </summary>
        private void ThrowNativeIfRequested()
        {
            if (FailNative)
            {
                throw new PgException("P8601", "array native operation failed", detail: "owned detail", hint: "owned hint");
            }
        }

        /// <summary>
        /// Releases allocator-matched result bytes independently of owner deletion.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(NativeSpiResult* result)
        {
            Current.Releases++;
            for (int index = 0; index < result->_columnCount; index++)
            {
                result->_columns[index]._name.Release();
            }

            NativeMemory.Free(result->_columns);
            NativeMemory.Free(result->_values);
            result->_text.Release();
        }
    }

    /// <summary>
    /// Supplies raw cells independently of the mapped converter and canonical array serializer.
    /// </summary>
    private readonly record struct Cell(long Bits, bool IsNull = false);

    /// <summary>
    /// Records a native builder cell independently from its original managed value.
    /// </summary>
    private readonly record struct WrittenCell(uint Type, nuint Bits, bool IsNull, int Length, nint Context);

    /// <summary>
    /// Represents the primary reader and writer value.
    /// </summary>
    private readonly record struct Number(int Value);

    /// <summary>
    /// Represents a second CLR identity sharing the SQL type.
    /// </summary>
    private readonly record struct Alias(int Value);

    /// <summary>
    /// Isolates compatible duplicate registration and its shared factory.
    /// </summary>
    private readonly record struct Shared(int Value);

    /// <summary>
    /// Represents a reader-only value whose absent cells bypass its factory.
    /// </summary>
    private readonly record struct ReadOnly;

    /// <summary>
    /// Represents a writer-only value whose absent cells bypass its factory.
    /// </summary>
    private readonly record struct WriteOnly;

    /// <summary>
    /// Makes CLR integer and enum array equivalence observable.
    /// </summary>
    private enum Kind
    {
        /// <summary>
        /// Supplies a named integer value with a distinct mapped representation.
        /// </summary>
        One = 1,
    }

    /// <summary>
    /// Isolates a lazy factory failure shared by scalar and array uses.
    /// </summary>
    private readonly record struct FactoryValue(int Value);

    /// <summary>
    /// Supplies an existing custom base-type contract for declared covariance.
    /// </summary>
    private abstract record CustomRoot(int Value);

    /// <summary>
    /// Supplies values whose runtime datum mapping must not replace their declared custom base contract.
    /// </summary>
    private sealed record CustomLeaf(int Number) : CustomRoot(Number);

    /// <summary>
    /// Supplies a covariant declared reference root.
    /// </summary>
    private abstract record Message;

    /// <summary>
    /// Supplies the original covariant source values.
    /// </summary>
    private sealed record NumericMessage(int Value) : Message;

    /// <summary>
    /// Proves a read vector is writable with a sibling variant.
    /// </summary>
    private sealed record TextMessage(string Text) : Message;
}
