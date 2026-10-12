namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks the paths and sort keys a managed custom scan provider offers.
/// </summary>
[TestClass]
public sealed class PgCustomScanPlannerTests
{
    /// <summary>
    /// A table with a dropped column between its two live ones.
    /// </summary>
    private static readonly PgTupleDescriptor s_numbers = new(9200, -1,
        [new("value", 20, 20, -1, 0), new("dropped", 0, 0, -1, 0, isDropped: true), new("label", 25, 25, -1, 100)]);

    /// <summary>
    /// A key without an explicit NULL placement puts NULL where PostgreSQL's <c>ORDER BY</c> does: last when ascending
    /// and first when descending.
    /// </summary>
    [TestMethod]
    public void SortKeysDefaultToPostgresNullPlacement()
    {
        Assert.AreEqual(new PgCustomScanSortKey(2, false, false), new PgCustomScanSortKey(2));
        Assert.AreEqual(new PgCustomScanSortKey(2, true, true), new PgCustomScanSortKey(2, descending: true));
        Assert.AreNotEqual(new PgCustomScanSortKey(2, true, false), new PgCustomScanSortKey(2, descending: true));
    }

    /// <summary>
    /// A path keeps the sort keys it had when it was added, and an empty list leaves the path unordered.
    /// </summary>
    [TestMethod]
    public void AddedPathsKeepTheirSortKeys()
    {
        var paths = new List<PgCustomScanPath>();
        var keys = new List<PgCustomScanSortKey> { new(0), new(2, true, false) };
        var planner = new PgCustomScanPlanner("numbers", paths, s_numbers);
        planner.AddPath(new(0, 1) { SortKeys = keys });
        planner.AddPath(new(0, 1) { SortKeys = [] });
        keys[0] = new(2);
        Assert.AreSequenceEqual([new PgCustomScanSortKey(0), new PgCustomScanSortKey(2, true, false)], [.. paths[0].SortKeys!]);
        Assert.IsNull(paths[1].SortKeys);
    }

    /// <summary>
    /// A sort key must name a live column of the table, and a rejected path is not added.
    /// </summary>
    /// <param name="ordinal">The key's column.</param>
    /// <param name="dropped">Whether the column exists but was dropped.</param>
    [TestMethod]
    [DataRow(-1, false)]
    [DataRow(3, false)]
    [DataRow(1, true)]
    public void SortKeysMustNameLiveColumns(int ordinal, bool dropped)
    {
        var paths = new List<PgCustomScanPath>();
        PgCustomScanPath path = new(0, 1) { SortKeys = [new(0), new(ordinal)] };
        if (dropped)
        {
            ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => new PgCustomScanPlanner("numbers", paths, s_numbers).AddPath(path));
            Assert.AreEqual("path", error.ParamName);
            Assert.Contains("dropped column", error.Message);
        }
        else
        {
            ArgumentOutOfRangeException error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                new PgCustomScanPlanner("numbers", paths, s_numbers).AddPath(path));
            Assert.AreEqual(nameof(PgCustomScanPath.SortKeys), error.ParamName);
        }

        Assert.IsEmpty(paths);
    }
}
