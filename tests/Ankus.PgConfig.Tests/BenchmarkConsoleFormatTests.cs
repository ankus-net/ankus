using System.Text.Json;
using Ankus.Tool;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies console benchmark output against the layout <c>cargo pgrx bench</c> prints.
/// </summary>
[TestClass]
public sealed class BenchmarkConsoleFormatTests
{
    private static readonly string s_indent = new(' ', 28);

    /// <summary>
    /// A compared result prints its primary time, the change and its p-value, the summary and every estimate interval.
    /// </summary>
    [TestMethod]
    public void ComparedResultsUseCriterionLayout()
    {
        using JsonDocument result = JsonDocument.Parse("""
            {"status":"ok","estimates":[
              {"estimate_kind":"mean","point_estimate_ns":1234.5,"ci_lower_bound_ns":1200,"ci_upper_bound_ns":1300.25},
              {"estimate_kind":"median","point_estimate_ns":1230,"ci_lower_bound_ns":1220,"ci_upper_bound_ns":1240},
              {"estimate_kind":"median_abs_dev","point_estimate_ns":12.5,"ci_lower_bound_ns":10,"ci_upper_bound_ns":15},
              {"estimate_kind":"slope","point_estimate_ns":1234,"ci_lower_bound_ns":1210,"ci_upper_bound_ns":1250},
              {"estimate_kind":"std_dev","point_estimate_ns":0.5,"ci_lower_bound_ns":0.25,"ci_upper_bound_ns":0.75}],
             "comparison":{"mean":{"point_estimate":0.0123,"ci_lower_bound":-0.0021,"ci_upper_bound":0.026},
               "p_value":0.4234,"significance_level":0.05,"summary":"No change in performance detected."}}
            """);
        Assert.AreEqual(string.Join(Environment.NewLine,
            "benches.insert()",
            s_indent + "time:   [1.21 us 1.234 us 1.25 us]",
            s_indent + "change: [-0.21% +1.23% +2.6%] (p = 0.42 > 0.05)",
            s_indent + "No change in performance detected.",
            s_indent + "slope:  [1.21 us 1.234 us 1.25 us]",
            s_indent + "mean:   [1.2 us 1.2345 us 1.3003 us] std. dev. [250.0 ps 500.0 ps 750.0 ps]",
            s_indent + "median: [1.22 us 1.23 us 1.24 us] med. abs. dev. [10.0 ns 12.5 ns 15.0 ns]",
            string.Empty, string.Empty), BenchmarkConsoleFormat.Format("benches.insert()", result.RootElement));
    }

    /// <summary>
    /// A failed run prints its error, a result without a slope uses the mean as its time, and an unavailable comparison
    /// prints its reason in place of the change.
    /// </summary>
    [TestMethod]
    public void FailuresAndFlatResultsKeepTheirLines()
    {
        using JsonDocument failed = JsonDocument.Parse("""{"status":"failed","error_text":"boom"}""");
        Assert.AreEqual("bench" + Environment.NewLine + s_indent + "error:  boom" + Environment.NewLine + Environment.NewLine,
            BenchmarkConsoleFormat.Format("bench", failed.RootElement));
        using JsonDocument flat = JsonDocument.Parse("""
            {"status":"ok","estimates":[{"estimate_kind":"mean","point_estimate_ns":2500000}],"comparison":null}
            """);
        Assert.AreEqual(string.Join(Environment.NewLine, "bench", s_indent + "time:   2.5 ms", s_indent + "mean:   2.5 ms",
            string.Empty, string.Empty), BenchmarkConsoleFormat.Format("bench", flat.RootElement));
        Assert.AreEqual(string.Join(Environment.NewLine, "bench", s_indent + "time:   2.5 ms",
            s_indent + "New benchmark; no baseline comparison available.", s_indent + "mean:   2.5 ms", string.Empty, string.Empty),
            BenchmarkConsoleFormat.Format("bench", flat.RootElement, "New benchmark; no baseline comparison available."));
    }

    /// <summary>
    /// The running line lists the benchmark's settings and the summary counts results and names missing benchmarks.
    /// </summary>
    [TestMethod]
    public void RunningLineListsSettingsAndSummaryNamesMissingBenchmarks()
    {
        using JsonDocument descriptor = JsonDocument.Parse("""
            {"bench_name":"benches.insert()","transaction_mode":"shared","setup_function":null,
             "config":{"sample_size":10,"measurement_time_ms":5000,"warm_up_time_ms":3000,"nresamples":100000,
                       "noise_threshold":0.01,"significance_level":0.05}}
            """);
        Assert.AreEqual("     Running benches.insert() [transaction=shared, setup=none, sample_size=10, warm_up=3000ms, " +
            "measurement=5000ms, nresamples=100000, noise_threshold=0.01, significance_level=0.05]",
            BenchmarkConsoleFormat.Running(descriptor.RootElement));
        Assert.AreEqual(string.Join(Environment.NewLine, "Bench group current", "   Compared baseline", "    Result 3 total, 2 ok, 1 failed",
            "Missing From Current Run", "  removed()", string.Empty),
            BenchmarkConsoleFormat.Summary("current", "baseline", 3, 1, ["removed()"]));
        Assert.AreEqual("Bench group current" + Environment.NewLine + "    Result 1 total, 1 ok, 0 failed" + Environment.NewLine,
            BenchmarkConsoleFormat.Summary("current", null, 1, 0, []));
    }

    /// <summary>
    /// Durations choose pgrx's unit and precision steps, and percentages keep their sign.
    /// </summary>
    /// <param name="nanoseconds">The duration.</param>
    /// <param name="expected">The printed duration.</param>
    [TestMethod]
    [DataRow(0.0005, "0.5 ps")]
    [DataRow(1.0, "1.0 ns")]
    [DataRow(12.3456, "12.346 ns")]
    [DataRow(999.999, "1000.0 ns")]
    [DataRow(1500.0, "1.5 us")]
    [DataRow(123456789.0, "123.46 ms")]
    [DataRow(2e9, "2.0 s")]
    public void DurationsUseScaledUnits(double nanoseconds, string expected)
        => Assert.AreEqual(expected, BenchmarkConsoleFormat.Duration(nanoseconds));

    /// <summary>
    /// Percentages keep an explicit sign and pgrx's precision steps.
    /// </summary>
    /// <param name="percent">The percentage.</param>
    /// <param name="expected">The printed percentage.</param>
    [TestMethod]
    [DataRow(0.0, "+0.0%")]
    [DataRow(-1.23456, "-1.2346%")]
    [DataRow(12.5, "+12.5%")]
    [DataRow(-150.0, "-150.0%")]
    public void PercentagesKeepTheirSign(double percent, string expected)
        => Assert.AreEqual(expected, BenchmarkConsoleFormat.Percent(percent));

    /// <summary>
    /// The pause before benchmarks names its duration with a singular or plural unit, as pgrx's
    /// <c>format_wait_duration_uses_singular_and_plural_units</c> checks.
    /// </summary>
    [TestMethod]
    public void WaitingMessageUsesSingularAndPluralUnits()
    {
        Assert.AreEqual("     Waiting 1 second before starting benchmarks", BenchmarkConsoleFormat.Waiting(1));
        Assert.AreEqual("     Waiting 2 seconds before starting benchmarks", BenchmarkConsoleFormat.Waiting(2));
    }

    /// <summary>
    /// Throughput divides the work per iteration by the time estimate, so the slowest time bounds the lowest rate, as
    /// pgrx's <c>throughput_interval_uses_time_estimate_to_compute_rate</c> and
    /// <c>throughput_without_interval_uses_decimal_units_when_requested</c> check.
    /// </summary>
    [TestMethod]
    public void ThroughputUsesTheTimeEstimateToComputeRates()
    {
        Assert.AreEqual("[887.78 MiB/s 976.56 MiB/s 1085.07 MiB/s]", BenchmarkConsoleFormat.Throughput("bytes", 1024, 1_000, 900, 1_100));
        Assert.AreEqual("2.5 MB/s", BenchmarkConsoleFormat.Throughput("bytesdecimal", 5_000, 2_000_000, null, null));
        Assert.AreEqual("500.0 Kelem/s", BenchmarkConsoleFormat.Throughput("elements", 1, 2_000, null, null));
        Assert.AreEqual("invalid throughput", BenchmarkConsoleFormat.Throughput("bytes", 1, 0, null, null));
        using JsonDocument result = JsonDocument.Parse("""
            {"status":"ok","throughput":{"kind":"elements","value":100},"estimates":[
              {"estimate_kind":"mean","point_estimate_ns":1000,"ci_lower_bound_ns":800,"ci_upper_bound_ns":1250}],
             "comparison":null}
            """);
        Assert.AreEqual(string.Join(Environment.NewLine, "bench", s_indent + "time:   [800.0 ns 1.0 us 1.25 us]",
            s_indent + "thrpt:  [80.0 Melem/s 100.0 Melem/s 125.0 Melem/s]", s_indent + "mean:   [800.0 ns 1.0 us 1.25 us]",
            string.Empty, string.Empty), BenchmarkConsoleFormat.Format("bench", result.RootElement));
    }

    /// <summary>
    /// The assertion warning names the server version and the release build to use instead.
    /// </summary>
    [TestMethod]
    public void AssertionWarningNamesTheReleaseBuild()
        => Assert.AreEqual("Ankus: warning: PostgreSQL 18.6 was built with assertions, which slow the whole server. For representative " +
            "timings, benchmark a release build, such as one from 'ankus init --pg18 download --release'.",
            BenchmarkConsoleFormat.AssertionWarning(new PostgresVersion(18, 6, PostgresReleaseStage.Stable, 0)));
}
