
namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks that trigger rows cross the native boundary as one value per column, and that a returned row sends only
/// the cells PostgreSQL must convert.
/// </summary>
[TestClass]
public sealed unsafe class PgTriggerRowTransportTests
{
    /// <summary>
    /// A relation with a dropped column, text and a mutable byte array.
    /// </summary>
    private static readonly PgTupleDescriptor s_pets = new(9300, -1,
        [new("id", 23, 23, -1, 0), new("gone", 0, 0, -1, 0, isDropped: true), new("name", 25, 25, -1, 100), new("payload", 17, 17, -1, 0)]);

    /// <summary>
    /// A relation with a stored generated column, as a call site describes it.
    /// </summary>
    private static readonly PgTupleDescriptor s_totals = new(9400, -1, [new("id", 23, 23, -1, 0), new("total", 23, 23, -1, 0)]);

    /// <summary>
    /// The same relation as a BEFORE trigger's NEW row sees it, before PostgreSQL computes the generated column.
    /// </summary>
    private static readonly PgTupleDescriptor s_pendingTotals = new(9400, -1,
        [new("id", 23, 23, -1, 0), new("total", 23, 23, -1, 0, isUnavailable: true)]);

    /// <summary>
    /// A returned NEW row keeps the cells the trigger did not replace when they cannot change in place, and converts
    /// the rest; dropped columns send nothing.
    /// </summary>
    [TestMethod]
    public void ReturnedNewRowsKeepUntouchedImmutableCells()
    {
        var row = new PgHeapTuple(s_pets, [5, null, " pet ", new byte[] { 1, 2 }]);
        var context = new PgTriggerContext(12, 8100, 8200, "trim", "pets", "public", null, null, [], null, row, s_pets);
        row.Set(2, "pet");
        NativeValue result = NativeValue.FromTriggerRow(context, row);
        try
        {
            Assert.AreEqual(-10, Kind(ref result));
            Assert.AreEqual(4, Length(ref result));
            Assert.AreEqual(9300L, result.Integral);
            var cells = (NativeValue*)Data(ref result);
            Assert.AreEqual(-11, Kind(ref cells[0]));
            Assert.AreEqual(1, cells[0].IsNull);
            Assert.AreEqual(0, Kind(ref cells[1]));
            Assert.AreEqual(1, cells[1].IsNull);
            Assert.AreEqual("pet", cells[2].ReadString());
            Assert.AreSequenceEqual<byte>([1, 2], cells[3].ReadBytes());
        }
        finally
        {
            result.Release();
        }
    }

    /// <summary>
    /// A returned OLD row, or a copy of NEW, has no original cells PostgreSQL could keep, so every cell is sent.
    /// </summary>
    [TestMethod]
    public void OtherRowsOfTheRelationSendEveryCell()
    {
        var oldRow = new PgHeapTuple(s_pets, [4, null, "old", null]);
        var newRow = new PgHeapTuple(s_pets, [5, null, "new", null]);
        var context = new PgTriggerContext(14, 8100, 8200, "trim", "pets", "public", null, null, [], oldRow, newRow, s_pets);
        PgHeapTuple[] rows = [oldRow, newRow.Clone()];
        foreach (PgHeapTuple row in rows)
        {
            NativeValue result = NativeValue.FromTriggerRow(context, row);
            try
            {
                Assert.AreEqual(-10, Kind(ref result));
                var cells = (NativeValue*)Data(ref result);
                Assert.AreEqual(0, Kind(ref cells[0]));
                Assert.AreEqual(row.Get<int>(0), (int)cells[0].Integral);
                Assert.AreEqual(row.Get<string>(2), cells[2].ReadString());
                Assert.AreEqual(1, cells[3].IsNull);
            }
            finally
            {
                result.Release();
            }
        }
    }

    /// <summary>
    /// A row built over another descriptor, even one with equal columns, describes itself so PostgreSQL can check it
    /// against the relation.
    /// </summary>
    [TestMethod]
    public void RowsOfAnotherDescriptorDescribeThemselves()
    {
        var other = new PgTupleDescriptor(9300, -1, [.. s_pets.Attributes]);
        var context = new PgTriggerContext(12, 8100, 8200, "trim", "pets", "public", null, null, [], null,
            new PgHeapTuple(s_pets, [5, null, "pet", null]), s_pets);
        NativeValue result = NativeValue.FromTriggerRow(context, new PgHeapTuple(other, [6, null, "other", null]));
        try
        {
            Assert.AreEqual(-4, Kind(ref result));
        }
        finally
        {
            result.Release();
        }
    }

    /// <summary>
    /// Cells are read over the call site's descriptor. A column PostgreSQL has not computed yet is marked
    /// unavailable, and the site keeps that variant of its descriptor for the next row.
    /// </summary>
    [TestMethod]
    public void CellRowsReadOverTheSiteDescriptor()
    {
        PgHeapTuple pending = new(s_pendingTotals, [7, null]);
        pending.Set(0, 7);
        var sender = new PgTriggerContext(12, 8100, 8200, "total", "totals", "public", null, null, [], null, pending, s_pendingTotals);
        using var envelope = new Envelope(NativeValue.FromTriggerRow(sender, pending), site: 7101);
        PgTriggerContext first = NativeValue.ReadTriggerContext(envelope.Values);
        PgTriggerContext second = NativeValue.ReadTriggerContext(envelope.Values);
        Assert.AreEqual(7, first.New!.Get<int>(0));
        Assert.IsTrue(first.New.Descriptor.Attributes[1].IsUnavailable);
        Assert.IsFalse(first.Descriptor.Attributes[1].IsUnavailable);
        Assert.AreSame(first.New.Descriptor, second.New!.Descriptor);
        Assert.AreSame(first.Descriptor, second.Descriptor);
    }

    /// <summary>
    /// Kept cells only describe a returned row, and a row must have one cell for each of the relation's columns.
    /// </summary>
    /// <param name="defect">The malformed input.</param>
    [TestMethod]
    [DataRow("kept")]
    [DataRow("columns")]
    public void MalformedCellRowsAreRejected(string defect)
    {
        PgHeapTuple row = defect == "kept" ? new(s_totals, [7, 8]) : new(s_pets, [5, null, "pet", null]);
        var sender = new PgTriggerContext(12, 8100, 8200, "total", "totals", "public", null, null, [], null, row, row.Descriptor);
        using var envelope = new Envelope(NativeValue.FromTriggerRow(sender, row), site: 0);
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => NativeValue.ReadTriggerContext(envelope.Values));
        Assert.Contains(defect == "kept" ? "cell does not match" : "does not match its relation's columns", error.Message);
    }

    private static ref int Kind(ref NativeValue value) => ref value._auxiliary1;

    private static ref int Length(ref NativeValue value) => ref value._length;

    private static ref byte* Data(ref NativeValue value) => ref value._data;

    /// <summary>
    /// Owns a BEFORE INSERT invocation over <see cref="s_totals"/> whose NEW slot holds a given transport.
    /// </summary>
    /// <param name="row">The NEW row transport, owned by the envelope.</param>
    /// <param name="site">The native call site's identity, or zero for an uncached call.</param>
    private sealed class Envelope(NativeValue row, long site) : IDisposable
    {
        /// <summary>
        /// Gets the sixteen native invocation slots.
        /// </summary>
        internal NativeValue[] Values { get; } =
        [
            new() { Integral = 12 }, new() { Integral = 8100 }, new() { Integral = 8200 },
            NativeValue.FromString("total"), NativeValue.FromString("totals"), NativeValue.FromString("public"),
            new() { IsNull = 1 }, new() { IsNull = 1 }, NativeValue.FromArray(new PgArray<string>([])),
            new() { IsNull = 1 }, row, NativeValue.FromTuple(s_totals.CreateTuple()), NativeValue.FromArray(new PgArray<int>([])),
            new() { IsNull = 1 }, new() { Integral = 0 }, new() { Integral = site },
        ];

        /// <inheritdoc />
        public void Dispose()
        {
            for (int index = 0; index < Values.Length; index++)
            {
                Values[index].Release();
            }
        }
    }
}
