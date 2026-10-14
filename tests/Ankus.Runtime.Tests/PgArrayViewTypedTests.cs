namespace Ankus.Runtime.Tests;

public sealed partial class PgArrayViewTests
{
    /// <summary>
    /// A missing native address fails before resolving the element's array identity through the backend.
    /// </summary>
    [TestMethod]
    public void TypedArrayNativeCallsRejectMissingAddressesBeforeBackendAccess()
    {
        Assert.AreEqual("function", Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => PgFunctions.DangerousCall<PgArrayView<int>>(0, 0, [])).ParamName);
    }

    /// <summary>
    /// A writer-only element rejects native result dispatch without constructing its converter or accessing the backend.
    /// </summary>
    [TestMethod]
    public void TypedArrayNativeCallsRequireElementReadersBeforeBackendAccess()
    {
        PgDatumRegistry.RegisterValue<WriteOnlyArrayItem>("int4", "pg_catalog", PgTypeOrigin.External,
            typeof(WriteOnlyArrayItem), static () => throw new InvalidOperationException("Converter must remain unconstructed."),
            canRead: false, canWrite: true);
        Assert.ThrowsExactly<NotSupportedException>(() => PgFunctions.DangerousCall<PgArrayView<WriteOnlyArrayItem>>(1, 0, []));
    }

    /// <summary>
    /// A typed SQL NULL validates its native element identity without opening a native array owner.
    /// </summary>
    [TestMethod]
    public void TypedArrayFactoriesValidateNullWithoutAllocatingViews()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new ArrayScript(fixture) { ElementType = 23 };
        PgDatum absent = PgDatum.DangerousCreate(0, 1007, PgMemoryContext.Current, isNull: true);
        Assert.IsNull(absent.Read<PgArrayView<int?>?>());
        Assert.AreSequenceEqual<(uint, long)>([(23, 0)], script.Contracts);
        Assert.AreSequenceEqual<(int, nint)>([(12, 0)], script.Requests);
        Assert.AreEqual(1, script.Releases);
        Assert.DoesNotContain(static request => request._operation == NativeMemoryOperation.Create, fixture.Requests);
        Assert.IsEmpty(script.Deleted);
    }

    /// <summary>
    /// Typed datum factories and provisional result cleanup share the original checked array ownership.
    /// </summary>
    [TestMethod]
    public void TypedArrayFactoriesRetainAndReleaseProvisionalOwners()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new ArrayScript(fixture) { ElementType = 23, HasNulls = false };
        PgDatum source = PgDatum.DangerousCreate(123, 1007, PgMemoryContext.Current);
        using PgArrayView<int> view = source.Read<PgArrayView<int>>();
        SpiParameter parameter = SpiParameter.Create(view);
        Assert.AreEqual(1007U, parameter.TypeOid);
        Assert.AreSame(view.Datum, parameter.Value);
        using var conversions = new SpiConversionScope();
        Assert.AreSame(view, conversions.Add(view));
        var primary = new InvalidCastException("Later column failed.");
        conversions.ReleaseAfterFailure(primary);
        Assert.IsEmpty(primary.Data);
        Assert.AreSequenceEqual<nint>([202], script.Deleted);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = view.Datum);
        Assert.AreEqual((nuint)123, source.DangerousGetBits());
    }

    /// <summary>
    /// Invalid sources and unsupported element representations fail before native allocation.
    /// </summary>
    [TestMethod]
    public void TypedArrayInputsRejectBeforeNativeAllocation()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        PgDatum absent = PgDatum.DangerousCreate(0, 1007, PgMemoryContext.Current, isNull: true);
        PgDatum source = PgDatum.DangerousCreate(123, 1007, PgMemoryContext.Current);
        Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentNullException>(() => new PgArrayView<int>(null!)).ParamName);
        Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentException>(() => new PgArrayView<int>(absent)).ParamName);
        Assert.ThrowsExactly<NotSupportedException>(() => new PgArrayView<int[]>(source));
        Assert.ThrowsExactly<NotSupportedException>(() => new PgArrayView<PgArray<int>>(source));
        Assert.ThrowsExactly<NotSupportedException>(() => new PgArrayView<object>(source));
        fixture.Handler = static _ => new NativeMemoryResult { _value = 902 };
        Assert.ThrowsExactly<ObjectDisposedException>(() => new PgArrayView<int>(source));
        Assert.DoesNotContain(static request => request._operation == NativeMemoryOperation.Create, fixture.Requests);
    }

    /// <summary>
    /// Nullable integers preserve the same metadata while required integers fail without leaking a context.
    /// </summary>
    [TestMethod]
    public void TypedArrayNullabilityFailureReleasesOwner()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new ArrayScript(fixture) { ElementType = 23 };
        PgDatum source = PgDatum.DangerousCreate(123, 1007, PgMemoryContext.Current);
        Assert.ThrowsExactly<InvalidCastException>(() => new PgArrayView<int>(source));
        Assert.AreSequenceEqual<nint>([202], script.Deleted);
        Assert.AreEqual(1, script.Releases);
        using var optional = new PgArrayView<int?>(source);
        Assert.AreEqual(1007U, optional.TypeOid);
        Assert.AreEqual(23U, optional.ElementTypeOid);
        Assert.AreEqual(3, optional.Count);
        Assert.AreEqual(1, optional.Rank);
        Assert.IsTrue(optional.HasNulls);
        Assert.AreSequenceEqual<int>([3], optional.Lengths.ToArray());
        Assert.AreSequenceEqual<int>([-1], optional.LowerBounds.ToArray());
        Assert.AreSequenceEqual<(uint, long)>([(23, 0), (23, 0)], script.Contracts);
        optional.Dispose();
        Assert.AreSequenceEqual<nint>([202, 203], script.Deleted);
        Assert.AreEqual(3, optional.Count);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = optional.Datum);
    }

    /// <summary>
    /// Repeated Current reads return one conversion and still validate the native cursor after disposal.
    /// </summary>
    [TestMethod]
    public void TypedArrayCursorsCacheConversionsAndCheckLifetime()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new ArrayScript(fixture);
        using var view = new PgArrayView<string?>(PgDatum.DangerousCreate(123, 1009, PgMemoryContext.Current));
        using IEnumerator<string?> cursor = view.GetEnumerator();
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = cursor.Current);
        script.Cells.Enqueue((7001, false, true));
        script.Conversions.Enqueue(NativeValue.FromString("one conversion"));
        Assert.IsTrue(cursor.MoveNext());
        string? value = cursor.Current;
        Assert.AreEqual("one conversion", value);
        int operations = script.Requests.Count;
        Assert.AreSame(value, cursor.Current);
        Assert.AreSame(value, ((System.Collections.IEnumerator)cursor).Current);
        Assert.HasCount(operations, script.Requests);
        script.Cells.Enqueue((0, true, true));
        script.Conversions.Enqueue(new NativeValue { IsNull = 1 });
        Assert.IsTrue(cursor.MoveNext());
        Assert.IsNull(cursor.Current);
        script.Cells.Enqueue((0, false, false));
        Assert.IsFalse(cursor.MoveNext());
        Assert.IsFalse(cursor.MoveNext());
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = cursor.Current);
        Assert.ThrowsExactly<NotSupportedException>(cursor.Reset);
        cursor.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = cursor.Current);
        Assert.ThrowsExactly<ObjectDisposedException>(() => cursor.MoveNext());
        Assert.AreEqual("one conversion", value);
        Assert.AreEqual(2, script.Requests.Count(static request => request.Operation == 0));
        Assert.AreSequenceEqual<nint>([203], script.Deleted);
    }

    /// <summary>
    /// Cached typed elements cannot bypass source generation, provider or thread validation.
    /// </summary>
    [TestMethod]
    public void TypedArrayRejectsForeignBackendAndSourceExpiry()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope memory = MemoryContextTestFixture.Enter();
        using var script = new ArrayScript(fixture) { ElementType = 23, HasNulls = false };
        using var view = new PgArrayView<int>(PgDatum.DangerousCreate(123, 1007, PgMemoryContext.Current));
        using IEnumerator<int> cursor = view.GetEnumerator();
        script.Cells.Enqueue((7, false, true));
        script.Conversions.Enqueue(new NativeValue { Integral = 7 });
        Assert.IsTrue(cursor.MoveNext());
        Assert.AreEqual(7, cursor.Current);
        int operations = script.Requests.Count;
        using (MemoryContextTestFixture.Enter(29))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = cursor.Current);
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = view[0]);
            Assert.ThrowsExactly<InvalidOperationException>(view.Dispose);
        }

        DedicatedThread.Run(() =>
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = cursor.Current);
            Assert.ThrowsExactly<InvalidOperationException>(() => view.GetEnumerator());
            Assert.ThrowsExactly<InvalidOperationException>(view.Dispose);
        });

        Func<NativeMemoryRequest, NativeMemoryResult> previous = fixture.Handler!;
        fixture.Handler = request => request._operation == NativeMemoryOperation.CaptureGeneration && request._context == 101
            ? new NativeMemoryResult { _value = 902 }
            : previous(request);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = cursor.Current);
        Assert.ThrowsExactly<ObjectDisposedException>(() => cursor.MoveNext());
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.GetValue(-1));
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = view.Datum);
        Assert.HasCount(operations, script.Requests);
    }

    /// <summary>
    /// Supplies a unique mapping identity whose read capability is deliberately absent.
    /// </summary>
    private readonly record struct WriteOnlyArrayItem;
}
