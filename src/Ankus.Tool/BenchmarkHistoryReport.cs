using System.Globalization;
using System.Text;

namespace Ankus.Tool;

/// <summary>
/// One retained benchmark run with the metadata of its run group.
/// </summary>
/// <param name="Benchmark">The benchmark name.</param>
/// <param name="GroupId">The run group's identity.</param>
/// <param name="GroupName">The run group's name.</param>
/// <param name="Succeeded">Whether the run completed.</param>
/// <param name="PointEstimate">The primary estimate in nanoseconds: the slope when present, otherwise the mean.</param>
/// <param name="Configuration">The build configuration.</param>
/// <param name="PostgresMajor">The PostgreSQL major version.</param>
/// <param name="RuntimeIdentifier">The runtime identifier.</param>
/// <param name="Settings">The group's non-default server settings in canonical form, or null when they were not recorded.</param>
internal sealed record BenchmarkHistoryRun(
    string Benchmark,
    long GroupId,
    string GroupName,
    bool Succeeded,
    double? PointEstimate,
    string Configuration,
    string PostgresMajor,
    string RuntimeIdentifier,
    string? Settings);

/// <summary>
/// Renders retained benchmark history as <c>cargo pgrx bench --report</c> does: one section per benchmark showing its
/// recent groups against the first successful run.
/// </summary>
internal static class BenchmarkHistoryReport
{
    /// <summary>
    /// The number of most recent groups shown for each benchmark.
    /// </summary>
    internal const int Limit = 10;

    /// <summary>
    /// The width of each run's bar.
    /// </summary>
    private const int BarWidth = 28;

    /// <summary>
    /// Formats the report.
    /// </summary>
    /// <param name="database">The database holding the history.</param>
    /// <param name="filter">The benchmark name filter, if any.</param>
    /// <param name="runs">Every retained run, oldest first.</param>
    /// <returns>The report text.</returns>
    internal static string Format(string database, string? filter, IReadOnlyList<BenchmarkHistoryRun> runs)
    {
        var text = new StringBuilder();
        text.AppendLine("Bench history report");
        text.AppendLine("  Database " + database);
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"     Scope last {Limit} groups per benchmark"));
        if (filter is not null)
        {
            text.AppendLine("    Filter " + filter);
        }

        foreach (IGrouping<string, BenchmarkHistoryRun> benchmark in runs.GroupBy(static run => run.Benchmark, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            text.AppendLine();
            AppendSection(text, benchmark.Key, [.. benchmark]);
        }

        return text.ToString();
    }

    private static void AppendSection(StringBuilder text, string benchmark, BenchmarkHistoryRun[] runs)
    {
        text.AppendLine(benchmark);
        BenchmarkHistoryRun? baseline = runs.FirstOrDefault(static run => run.Succeeded && run.PointEstimate is not null);
        BenchmarkHistoryRun[] recent = runs[Math.Max(0, runs.Length - Limit)..];
        text.AppendLine(baseline is null
            ? "  no successful baseline has been recorded for this benchmark yet"
            : "  baseline: " + baseline.GroupName + " (" + BenchmarkConsoleFormat.Duration(baseline.PointEstimate!.Value) + ")");
        BenchmarkHistoryRun[] displayed = [.. recent.Where(static run => run.Succeeded && run.PointEstimate is not null)];
        var drift = new SortedSet<string>(StringComparer.Ordinal);
        if (displayed.Length == 0)
        {
            text.AppendLine("  no successful runs to display");
        }
        else
        {
            double maximum = displayed.Max(static run => run.PointEstimate!.Value);
            (BenchmarkHistoryRun Run, string[] Drift)[] rows = [.. displayed.Select(run => (run, baseline is null ? [] : Drift(baseline, run)))];
            int width = Math.Clamp(rows.Max(static row => row.Run.GroupName.Length + (row.Drift.Length == 0 ? 0 : 1)), 12, 32);
            foreach ((BenchmarkHistoryRun run, string[] categories) in rows)
            {
                drift.UnionWith(categories);
                string label = Shorten(run.GroupName + (categories.Length == 0 ? string.Empty : "*"), width).PadRight(width);
                double point = run.PointEstimate!.Value;
                text.AppendLine("  " + label + " " + Bar(point, maximum) + " " + BenchmarkConsoleFormat.Duration(point) + " " +
                    Delta(run, baseline));
            }
        }

        if (drift.Count != 0)
        {
            text.AppendLine("  * broad drift vs baseline in: " + string.Join(", ", drift));
        }

        int failed = recent.Count(static run => !run.Succeeded);
        if (failed != 0)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  {failed} failed {(failed == 1 ? "run" : "runs")} omitted from the last {Limit} groups"));
        }

        int incomplete = recent.Count(static run => run.Succeeded && run.PointEstimate is null);
        if (incomplete != 0)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  {incomplete} successful {(incomplete == 1 ? "run" : "runs")} without a primary estimate omitted"));
        }
    }

    /// <summary>
    /// Names what differs between a run's group and the baseline's group in ways that broadly change timings.
    /// </summary>
    internal static string[] Drift(BenchmarkHistoryRun baseline, BenchmarkHistoryRun run)
    {
        var categories = new List<string>();
        if (!string.Equals(baseline.Configuration, run.Configuration, StringComparison.Ordinal))
        {
            categories.Add("configuration");
        }

        if (!string.Equals(baseline.PostgresMajor, run.PostgresMajor, StringComparison.Ordinal))
        {
            categories.Add("postgres version");
        }

        if (!string.Equals(baseline.RuntimeIdentifier, run.RuntimeIdentifier, StringComparison.Ordinal))
        {
            categories.Add("runtime");
        }

        if (baseline.Settings is not null && run.Settings is not null && !string.Equals(baseline.Settings, run.Settings, StringComparison.Ordinal))
        {
            categories.Add("pg_settings");
        }

        return [.. categories];
    }

    private static string Delta(BenchmarkHistoryRun run, BenchmarkHistoryRun? baseline)
    {
        if (baseline is null)
        {
            return "(n/a)";
        }

        if (baseline.GroupId == run.GroupId)
        {
            return "(baseline)";
        }

        double reference = baseline.PointEstimate!.Value;
        return reference is 0 ? "(n/a)" : "(" + BenchmarkConsoleFormat.Percent((run.PointEstimate!.Value - reference) / reference * 100) + ")";
    }

    private static string Bar(double value, double maximum)
    {
        if (value <= 0 || maximum <= 0)
        {
            return "|" + new string(' ', BarWidth) + "|";
        }

        int filled = Math.Clamp((int)Math.Round(value / maximum * BarWidth, MidpointRounding.AwayFromZero), 1, BarWidth);
        return "|" + new string('#', filled) + new string(' ', BarWidth - filled) + "|";
    }

    private static string Shorten(string label, int width)
        => label.Length <= width ? label : width <= 3 ? label[..width] : label[..(width - 3)] + "...";
}
