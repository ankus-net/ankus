using System.Globalization;
using System.Text;
using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Tool;

/// <summary>
/// Formats benchmark results for the console as <c>cargo pgrx bench</c> does, in Criterion's layout.
/// </summary>
internal static class BenchmarkConsoleFormat
{
    /// <summary>
    /// Aligns result lines under the benchmark name, as Criterion does.
    /// </summary>
    private static readonly string s_indent = new(' ', 28);

    /// <summary>
    /// Formats the line printed before a benchmark runs, with its settings.
    /// </summary>
    /// <param name="descriptor">The benchmark descriptor from the backend.</param>
    /// <returns>The running line without a trailing newline.</returns>
    internal static string Running(JsonElement descriptor)
    {
        JsonElement config = descriptor.GetProperty("config");
        string setup = descriptor.TryGetProperty("setup_function", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "none";
        return string.Create(CultureInfo.InvariantCulture,
            $"     Running {descriptor.GetProperty("bench_name").GetString()} [transaction={descriptor.GetProperty("transaction_mode").GetString()}, " +
            $"setup={setup}, sample_size={config.GetProperty("sample_size").GetInt32()}, " +
            $"warm_up={config.GetProperty("warm_up_time_ms").GetInt32()}ms, measurement={config.GetProperty("measurement_time_ms").GetInt32()}ms, " +
            $"nresamples={config.GetProperty("nresamples").GetInt32()}, noise_threshold={config.GetProperty("noise_threshold").GetDouble()}, " +
            $"significance_level={config.GetProperty("significance_level").GetDouble()}]");
    }

    /// <summary>
    /// Formats the message printed to standard error before pausing for debugger attachment.
    /// </summary>
    /// <param name="seconds">The pause in whole seconds.</param>
    /// <returns>The message without a trailing newline.</returns>
    internal static string Waiting(int seconds)
        => string.Create(CultureInfo.InvariantCulture,
            $"     Waiting {seconds} {(seconds == 1 ? "second" : "seconds")} before starting benchmarks");

    /// <summary>
    /// Formats the warning printed to standard error when the server checks assertions, which make timings unrepresentative.
    /// </summary>
    /// <param name="version">The server's PostgreSQL version.</param>
    /// <returns>The warning without a trailing newline.</returns>
    internal static string AssertionWarning(PostgresVersion version)
        => string.Create(CultureInfo.InvariantCulture,
            $"Ankus: warning: PostgreSQL {version} was built with assertions, which slow the whole server. For representative " +
            $"timings, benchmark a release build, such as one from 'ankus init --pg{version.Major} download --release'.");

    /// <summary>
    /// Formats the group summary printed after all benchmarks ran.
    /// </summary>
    /// <param name="group">The run group.</param>
    /// <param name="comparison">The compared group, if any.</param>
    /// <param name="total">The number of benchmarks run.</param>
    /// <param name="failed">The number that failed.</param>
    /// <param name="missing">Benchmarks the compared group ran that this run did not.</param>
    /// <returns>The summary lines.</returns>
    internal static string Summary(string group, string? comparison, int total, int failed, IReadOnlyList<string> missing)
    {
        var text = new StringBuilder();
        text.AppendLine("Bench group " + group);
        if (comparison is not null)
        {
            text.AppendLine("   Compared " + comparison);
        }

        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    Result {total} total, {total - failed} ok, {failed} failed"));
        if (missing.Count != 0)
        {
            text.AppendLine("Missing From Current Run");
            foreach (string name in missing)
            {
                text.AppendLine("  " + name);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Formats one benchmark's result.
    /// </summary>
    /// <param name="name">The benchmark name.</param>
    /// <param name="result">The result payload from the backend runner.</param>
    /// <param name="comparisonNote">Why a requested comparison is unavailable, printed in place of the change.</param>
    /// <returns>The name, its timing lines, the comparison when present and a closing blank line.</returns>
    internal static string Format(string name, JsonElement result, string? comparisonNote = null)
    {
        var text = new StringBuilder();
        text.AppendLine(name);
        if (result.GetProperty("status").GetString() != "ok")
        {
            string error = result.TryGetProperty("error_text", out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : "benchmark failed without an error message";
            text.Append(s_indent).AppendLine("error:  " + error).AppendLine();
            return text.ToString();
        }

        var estimates = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonElement estimate in result.GetProperty("estimates").EnumerateArray())
        {
            estimates[estimate.GetProperty("estimate_kind").GetString()!] = estimate;
        }

        bool hasSlope = estimates.TryGetValue("slope", out JsonElement slope);
        bool hasMean = estimates.TryGetValue("mean", out JsonElement mean);
        if (hasSlope || hasMean)
        {
            JsonElement primary = hasSlope ? slope : mean;
            text.Append(s_indent).AppendLine("time:   " + Interval(primary));
            if (result.TryGetProperty("throughput", out JsonElement throughput) && throughput.ValueKind == JsonValueKind.Object)
            {
                text.Append(s_indent).AppendLine("thrpt:  " + Throughput(throughput.GetProperty("kind").GetString()!,
                    throughput.GetProperty("value").GetDouble(), primary.GetProperty("point_estimate_ns").GetDouble(),
                    OptionalNumber(primary, "ci_lower_bound_ns"), OptionalNumber(primary, "ci_upper_bound_ns")));
            }
        }

        if (result.TryGetProperty("comparison", out JsonElement comparison) && comparison.ValueKind == JsonValueKind.Object)
        {
            JsonElement change = comparison.GetProperty("mean");
            double pValue = comparison.GetProperty("p_value").GetDouble();
            double significance = comparison.GetProperty("significance_level").GetDouble();
            text.Append(s_indent).Append("change: [")
                .Append(Percent(change.GetProperty("ci_lower_bound").GetDouble() * 100)).Append(' ')
                .Append(Percent(change.GetProperty("point_estimate").GetDouble() * 100)).Append(' ')
                .Append(Percent(change.GetProperty("ci_upper_bound").GetDouble() * 100)).Append(']')
                .AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $" (p = {pValue:0.00} {(pValue < significance ? '<' : '>')} {significance:0.00})"));
            text.Append(s_indent).AppendLine(comparison.GetProperty("summary").GetString());
        }
        else if (comparisonNote is not null)
        {
            text.Append(s_indent).AppendLine(comparisonNote);
        }

        if (hasSlope)
        {
            text.Append(s_indent).AppendLine("slope:  " + Interval(slope));
        }

        if (hasMean)
        {
            text.Append(s_indent).Append("mean:   " + Interval(mean));
            if (estimates.TryGetValue("std_dev", out JsonElement deviation))
            {
                text.Append(" std. dev. " + Interval(deviation));
            }

            text.AppendLine();
        }

        if (estimates.TryGetValue("median", out JsonElement median))
        {
            text.Append(s_indent).Append("median: " + Interval(median));
            if (estimates.TryGetValue("median_abs_dev", out JsonElement absolute))
            {
                text.Append(" med. abs. dev. " + Interval(absolute));
            }

            text.AppendLine();
        }

        return text.AppendLine().ToString();
    }

    /// <summary>
    /// Formats a duration with the largest unit that keeps it at least one, as pgrx does.
    /// </summary>
    /// <param name="nanoseconds">The duration in nanoseconds.</param>
    /// <returns>Four significant decimal places below 10, three below 100 and two above, without trailing zeros.</returns>
    internal static string Duration(double nanoseconds)
    {
        double magnitude = Math.Abs(nanoseconds);
        (double value, string unit) = magnitude >= 1_000_000_000 ? (nanoseconds / 1_000_000_000, "s")
            : magnitude >= 1_000_000 ? (nanoseconds / 1_000_000, "ms")
            : magnitude >= 1_000 ? (nanoseconds / 1_000, "us")
            : magnitude >= 1 ? (nanoseconds, "ns")
            : (nanoseconds * 1_000, "ps");
        return Number(value, signed: false) + " " + unit;
    }

    /// <summary>
    /// Formats the rate of work implied by a time estimate, as pgrx and Criterion do: the longest time gives the lowest
    /// rate, so the interval runs from the upper time bound to the lower one, in units chosen from the point estimate.
    /// </summary>
    /// <param name="kind"><c>bytes</c>, <c>bytesdecimal</c> or <c>elements</c>.</param>
    /// <param name="perIteration">The work one iteration performs.</param>
    /// <param name="point">The estimate's point in nanoseconds.</param>
    /// <param name="lower">The interval's lower bound in nanoseconds, if any.</param>
    /// <param name="upper">The interval's upper bound in nanoseconds, if any.</param>
    /// <returns>The rate or interval of rates.</returns>
    internal static string Throughput(string kind, double perIteration, double point, double? lower, double? upper)
    {
        if (point <= 0 || !double.IsFinite(point))
        {
            return "invalid throughput";
        }

        double typical = perIteration * (1e9 / point);
        (double scale, string unit) = kind switch
        {
            "bytes" => Scale(typical, 1024, "B/s", "KiB/s", "MiB/s", "GiB/s"),
            "bytesdecimal" => Scale(typical, 1000, "B/s", "KB/s", "MB/s", "GB/s"),
            "elements" => Scale(typical, 1000, "elem/s", "Kelem/s", "Melem/s", "Gelem/s"),
            _ => (1, "ops/s"),
        };
        string Rate(double time)
            => time <= 0 || !double.IsFinite(time) ? "invalid throughput" : Number(perIteration * (1e9 / time) / scale, signed: false) + " " + unit;
        return lower is double low && upper is double high ? "[" + Rate(high) + " " + Rate(point) + " " + Rate(low) + "]" : Rate(point);
    }

    /// <summary>
    /// Formats a signed percentage, as pgrx does.
    /// </summary>
    /// <param name="percent">The percentage.</param>
    /// <returns>The signed value with a percent sign.</returns>
    internal static string Percent(double percent) => Number(percent, signed: true) + "%";

    private static (double Scale, string Unit) Scale(double perSecond, double step, string one, string thousand, string million, string billion)
        => perSecond < step ? (1, one)
            : perSecond < step * step ? (step, thousand)
            : perSecond < step * step * step ? (step * step, million)
            : (step * step * step, billion);

    private static double? OptionalNumber(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    /// <summary>
    /// Formats an estimate with its confidence interval when present.
    /// </summary>
    private static string Interval(JsonElement estimate)
    {
        string point = Duration(estimate.GetProperty("point_estimate_ns").GetDouble());
        return estimate.TryGetProperty("ci_lower_bound_ns", out JsonElement lower) && lower.ValueKind == JsonValueKind.Number &&
            estimate.TryGetProperty("ci_upper_bound_ns", out JsonElement upper) && upper.ValueKind == JsonValueKind.Number
            ? "[" + Duration(lower.GetDouble()) + " " + point + " " + Duration(upper.GetDouble()) + "]"
            : point;
    }

    /// <summary>
    /// Formats a number with pgrx's precision steps and trims trailing zeros, keeping one after the decimal point.
    /// </summary>
    private static string Number(double value, bool signed)
    {
        double magnitude = Math.Abs(value);
        string format = magnitude >= 100 ? "0.00" : magnitude >= 10 ? "0.000" : "0.0000";
        string text = value.ToString(format, CultureInfo.InvariantCulture);
        if (signed && !text.StartsWith('-'))
        {
            text = "+" + text;
        }

        text = text.TrimEnd('0');
        return text.EndsWith('.') ? text + "0" : text;
    }
}
