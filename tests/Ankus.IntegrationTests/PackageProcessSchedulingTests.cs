namespace Ankus.IntegrationTests;

/// <summary>
/// Keeps compiler capacity available without queuing commands that only inspect or use existing publications.
/// </summary>
[TestClass]
public sealed class PackageProcessSchedulingTests
{
    /// <summary>
    /// Every processor remains budgeted even when the configured concurrency does not divide the host count.
    /// </summary>
    /// <param name="processors">The host's logical processors.</param>
    /// <param name="concurrency">The concurrent-build limit.</param>
    /// <param name="expected">The exact allocation across the slots.</param>
    [TestMethod]
    [DataRow(8, 3, new[] { 3, 3, 2 })]
    [DataRow(8, 4, new[] { 2, 2, 2, 2 })]
    [DataRow(4, 8, new[] { 1, 1, 1, 1, 1, 1, 1, 1 })]
    [DataRow(1, 1, new[] { 1 })]
    [DataRow(32, 20, new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1 })]
    public void BuildSlotsRetainCompleteProcessorBudget(int processors, int concurrency, int[] expected)
    {
        int[] budgets = PackageProcessRunner.CreateProcessorBudgets(processors, concurrency);
        Assert.AreSequenceEqual(expected, budgets);
        Assert.AreEqual(Math.Max(processors, concurrency), budgets.Sum());
    }

    /// <summary>
    /// Invalid capacity cannot produce zero-sized or unbounded compiler allocations.
    /// </summary>
    /// <param name="processors">The host's logical processors.</param>
    /// <param name="concurrency">The concurrent-build limit.</param>
    /// <param name="parameter">The invalid argument.</param>
    [TestMethod]
    [DataRow(0, 1, "logicalProcessors")]
    [DataRow(1, 0, "concurrency")]
    public void BuildSlotsRejectInvalidCapacity(int processors, int concurrency, string parameter)
        => Assert.AreEqual(parameter, Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PackageProcessRunner.CreateProcessorBudgets(processors, concurrency)).ParamName);

    /// <summary>
    /// Existing-publication, dry-run and report modes bypass compiler capacity while real builds, including project
    /// property reads, remain bounded.
    /// </summary>
    /// <param name="executable">The child process filename.</param>
    /// <param name="arguments">The tool's literal argument list.</param>
    /// <param name="reserved">Whether this invocation can compile an extension.</param>
    [TestMethod]
    [DataRow("ankus", new[] { "regress", "--dry-run" }, false)]
    [DataRow("ankus.exe", new[] { "regress", "--dry-run=true" }, false)]
    [DataRow("ankus", new[] { "regress", "--dry-run:false" }, true)]
    [DataRow("ankus", new[] { "regress", "--dry-run", "false" }, true)]
    [DataRow("ankus", new[] { "regress", "--no-build" }, false)]
    [DataRow("ankus", new[] { "regress" }, true)]
    [DataRow("ankus", new[] { "run", "--no-build" }, false)]
    [DataRow("ankus", new[] { "run", "--", "--no-build" }, true)]
    [DataRow("ankus", new[] { "run", "--no-build", "false" }, true)]
    [DataRow("ankus", new[] { "bench", "--no-build:true" }, false)]
    [DataRow("ankus", new[] { "bench", "--no-build=false" }, true)]
    [DataRow("ankus", new[] { "publish" }, true)]
    [DataRow("ankus", new[] { "install", "--from", "published extension" }, false)]
    [DataRow("ankus", new[] { "install", "--project", "extension.csproj" }, true)]
    [DataRow("ankus", new[] { "package", "--from=published extension" }, false)]
    [DataRow("ankus", new[] { "package", "--property", "Mode=--from" }, true)]
    [DataRow("ankus", new[] { "schema", "--from:published extension" }, false)]
    [DataRow("ankus", new[] { "schema", "--", "--from" }, true)]
    [DataRow("ankus", new[] { "schema", "--no-build" }, false)]
    [DataRow("ankus", new[] { "schema", "--skip-build" }, false)]
    [DataRow("ankus", new[] { "schema", "--skip-build=false" }, true)]
    [DataRow("ankus", new[] { "upgrade" }, false)]
    [DataRow("ankus", new[] { "get", "default_version", "--project", "extension.csproj" }, true)]
    [DataRow("ankus", new[] { "get", "default_version", "--from", "published extension" }, false)]
    [DataRow("ankus", new[] { "bench", "--report" }, false)]
    [DataRow("ankus", new[] { "bench", "--report=false" }, true)]
    [DataRow("dotnet", new[] { "test", "--no-build" }, true)]
    [DataRow("dotnet.exe", new[] { "publish", "--no-restore" }, true)]
    [DataRow("psql.exe", new[] { "--no-build" }, false)]
    public void ReservesCapacityForCompilation(string executable, string[] arguments, bool reserved)
        => Assert.AreEqual(reserved, PackageProcessRunner.RequiresSlot(executable, arguments));
}
