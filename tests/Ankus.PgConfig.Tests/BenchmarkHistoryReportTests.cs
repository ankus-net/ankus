using Ankus.Tool;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies the history report <c>ankus bench --report</c> prints, as pgrx's report section tests do.
/// </summary>
[TestClass]
public sealed class BenchmarkHistoryReportTests
{
    private static readonly string[] s_everyCategory = ["configuration", "postgres version", "runtime", "pg_settings"];

    /// <summary>
    /// Failed runs are counted and omitted, the first successful run is the baseline, later runs show their change
    /// from it, and a run whose settings differ is marked as drifted, as pgrx's
    /// <c>report_sections_omit_failed_runs_and_mark_drift</c> checks.
    /// </summary>
    [TestMethod]
    public void SectionsOmitFailedRunsAndMarkDrift()
    {
        BenchmarkHistoryRun[] runs =
        [
            Run("bench_parse_query", 1, "baseline", 100, settings: "{}"),
            Run("bench_parse_query", 2, "rewrite", 80, settings: """{"shared_buffers": {"unit": "8kB", "setting": "131072"}}"""),
            Failed("bench_parse_query", 3, "broken"),
        ];
        Assert.AreEqual(Lines(
            "Bench history report",
            "  Database ankus_bench",
            "     Scope last 10 groups per benchmark",
            string.Empty,
            "bench_parse_query",
            "  baseline: baseline (100.0 ns)",
            "  baseline     |############################| 100.0 ns (baseline)",
            "  rewrite*     |######################      | 80.0 ns (-20.0%)",
            "  * broad drift vs baseline in: pg_settings",
            "  1 failed run omitted from the last 10 groups"), BenchmarkHistoryReport.Format("ankus_bench", null, runs));
    }

    /// <summary>
    /// Every broad difference is named, and settings are compared only when both groups recorded them.
    /// </summary>
    [TestMethod]
    public void DriftNamesEachBroadDifference()
    {
        BenchmarkHistoryRun baseline = Run("bench", 1, "first", 10, settings: "{}");
        Assert.AreSequenceEqual([], BenchmarkHistoryReport.Drift(baseline, baseline with { GroupId = 2 }));
        Assert.AreSequenceEqual(s_everyCategory,
            BenchmarkHistoryReport.Drift(baseline, baseline with
            {
                GroupId = 2,
                Configuration = "Debug",
                PostgresMajor = "17",
                RuntimeIdentifier = "win-x64",
                Settings = """{"work_mem": {"unit": "kB", "setting": "65536"}}""",
            }));
        Assert.AreSequenceEqual([], BenchmarkHistoryReport.Drift(baseline with { Settings = null },
            baseline with { GroupId = 2, Settings = """{"work_mem": {"unit": "kB", "setting": "65536"}}""" }));
    }

    /// <summary>
    /// Each benchmark shows only its ten most recent groups while keeping its first successful run as the baseline,
    /// and runs without a primary estimate are counted separately from failures.
    /// </summary>
    [TestMethod]
    public void SectionsKeepTheFirstBaselineAndTheLastTenGroups()
    {
        var runs = new List<BenchmarkHistoryRun> { Run("alpha", 1, "g01", 50) };
        for (int group = 2; group <= 11; group++)
        {
            runs.Add(Run("alpha", group, "g" + group.ToString("00", System.Globalization.CultureInfo.InvariantCulture), 100));
        }

        runs.Add(Run("alpha", 12, "g12", null));
        runs.Add(Failed("zeta", 12, "g12"));
        string report = BenchmarkHistoryReport.Format("bench_db", "a", runs);
        string[] lines = report.Split(Environment.NewLine);
        Assert.AreEqual("    Filter a", lines[3]);
        Assert.AreEqual("  baseline: g01 (50.0 ns)", lines[6]);
        Assert.DoesNotContain("  g01 ", report);
        Assert.DoesNotContain("  g02 ", report);
        Assert.AreEqual("  g03          |############################| 100.0 ns (+100.0%)", lines[7]);
        Assert.AreEqual("  g11          |############################| 100.0 ns (+100.0%)", lines[15]);
        Assert.AreEqual("  1 successful run without a primary estimate omitted", lines[16]);
        Assert.AreEqual(Lines(
            "zeta",
            "  no successful baseline has been recorded for this benchmark yet",
            "  no successful runs to display",
            "  1 failed run omitted from the last 10 groups"), string.Join(Environment.NewLine, lines[18..]));
    }

    /// <summary>
    /// Long group names are shortened to the label width, and a zero baseline leaves later runs without a change to
    /// show.
    /// </summary>
    [TestMethod]
    public void LongNamesAndZeroBaselinesStayAligned()
    {
        BenchmarkHistoryRun[] runs =
        [
            Run("bench", 1, "20261009_120000_0123456789abcdef_with_a_long_suffix", 2_000),
            Run("bench", 2, "short", 1_000),
        ];
        string[] lines = BenchmarkHistoryReport.Format("db", null, runs).Split(Environment.NewLine);
        Assert.AreEqual("  20261009_120000_0123456789abc... |############################| 2.0 us (baseline)", lines[6]);
        Assert.AreEqual("  short                            |##############              | 1.0 us (-50.0%)", lines[7]);
        Assert.AreEqual("  short        |############################| 1.0 us (n/a)",
            BenchmarkHistoryReport.Format("db", null, [Run("bench", 1, "zero", 0), runs[1]]).Split(Environment.NewLine)[7]);
    }

    private static BenchmarkHistoryRun Run(string benchmark, long group, string name, double? point, string? settings = null)
        => new(benchmark, group, name, true, point, "Release", "18", "linux-x64", settings);

    private static BenchmarkHistoryRun Failed(string benchmark, long group, string name)
        => new(benchmark, group, name, false, null, "Release", "18", "linux-x64", null);

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
