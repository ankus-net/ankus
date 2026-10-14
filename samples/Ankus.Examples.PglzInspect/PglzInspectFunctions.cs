using System.Globalization;

namespace Ankus.Examples.PglzInspect;

/// <summary>
/// Ports pgrx's <c>pglz_inspect</c> example: helps DBAs decide whether PGLZ compression is worth enabling on a column.
/// </summary>
/// <remarks>
/// The functions sample real rows through SPI with the caller's privileges, run each value through PostgreSQL's
/// in-tree <c>pglz_compress</c> and report ratios, acceptance, estimated savings and a recommendation. Text and other
/// non-<c>bytea</c> columns are measured as their text bytes in the database encoding, which is the payload TOAST
/// compresses for text. Table arguments are relation OIDs, so pass <c>'name'::regclass</c>.
/// </remarks>
public static class PglzInspectFunctions
{
    /// <summary>
    /// The minimum accepted share of sampled rows for a recommendation.
    /// </summary>
    private const double RecommendPctAccepted = 0.80;

    /// <summary>
    /// The maximum average compressed-to-raw ratio for a recommendation.
    /// </summary>
    private const double RecommendAvgRatio = 0.70;

    /// <summary>
    /// The accepted share below which compression is not worth enabling.
    /// </summary>
    private const double SkipPctAccepted = 0.30;

    /// <summary>
    /// The average ratio above which compression is not worth enabling.
    /// </summary>
    private const double SkipAvgRatio = 0.90;

    /// <summary>
    /// The histogram buckets: five ratio ranges and the values PGLZ rejected.
    /// </summary>
    private static readonly string[] s_buckets = ["0.0-0.2", "0.2-0.4", "0.4-0.6", "0.6-0.8", "0.8-1.0", "incompressible"];

    /// <summary>
    /// Probes how PGLZ's default strategy would handle one value.
    /// </summary>
    /// <param name="input">The value to compress.</param>
    /// <returns>
    /// One row with the raw and compressed sizes, their ratio and whether PGLZ accepted the value. A rejected value
    /// reports its raw size and a ratio of 1.
    /// </returns>
    [PgFunction]
    public static IEnumerable<(int RawBytes, int CompressedBytes, double Ratio, bool Accepted)> PglzSize(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        int raw = input.Length;
        byte[]? compressed = Pglz.Compress(input, PglzStrategy.Default);
        return compressed is null
            ? [(raw, raw, 1.0, false)]
            : [(raw, compressed.Length, raw == 0 ? 0.0 : (double)compressed.Length / raw, true)];
    }

    /// <summary>
    /// Samples a column and reports aggregate compression statistics and the estimated table-wide savings.
    /// </summary>
    /// <param name="tbl">The table OID.</param>
    /// <param name="col">The exact column name.</param>
    /// <param name="sampleSize">The maximum number of non-NULL rows sampled at random; negative values sample none.</param>
    /// <param name="strategy"><c>default</c> or <c>always</c>.</param>
    /// <returns>One row of averages and shares over the sampled rows; savings use <c>pg_class.reltuples</c>.</returns>
    [PgFunction]
    public static IEnumerable<(int SampledRows, double AvgRawBytes, double AvgCompressed, double AvgRatio, double PctAccepted,
        double PctIncompressible, long EstSavingsBytes)> PglzAnalyzeColumn(uint tbl, string col, int sampleSize = 1000,
        string strategy = "default")
    {
        PglzStrategy selected = ParseStrategy(strategy);
        ColumnSample sample = ColumnSample.Collect(tbl, col, sampleSize, selected);
        int sampled = sample.Probes.Count;
        long totalRaw = 0;
        long totalCompressed = 0;
        int accepted = 0;
        foreach (ColumnSample.Probe probe in sample.Probes)
        {
            totalRaw += probe.RawBytes;
            totalCompressed += probe.CompressedBytes ?? probe.RawBytes;
            accepted += probe.CompressedBytes is null ? 0 : 1;
        }

        int incompressible = sampled - accepted;
        double averageRaw = 0.0;
        double averageCompressed = 0.0;
        double averageRatio = 1.0;
        if (sampled > 0)
        {
            averageRaw = (double)totalRaw / sampled;
            averageCompressed = (double)totalCompressed / sampled;
            averageRatio = totalRaw == 0 ? 1.0 : averageCompressed / averageRaw;
        }

        double pctAccepted = sampled > 0 ? (double)accepted / sampled : 0.0;
        double pctIncompressible = sampled > 0 ? (double)incompressible / sampled : 0.0;
        double reltuples = sample.EstimatedRows;
        // Converting to long saturates like Rust's `as i64`.
        long savings = reltuples > 0.0 && averageRaw > averageCompressed ? (long)((averageRaw - averageCompressed) * reltuples) : 0;
        return [(sampled, averageRaw, averageCompressed, averageRatio, pctAccepted, pctIncompressible, savings)];
    }

    /// <summary>
    /// Samples a column and counts the per-row compressed-to-raw ratios in five buckets plus <c>incompressible</c>.
    /// </summary>
    /// <param name="tbl">The table OID.</param>
    /// <param name="col">The exact column name.</param>
    /// <param name="sampleSize">The maximum number of non-NULL rows sampled at random.</param>
    /// <returns>Six rows in bucket order, including empty buckets.</returns>
    [PgFunction]
    public static IEnumerable<(string Bucket, int RowCount)> PglzRatioHistogram(uint tbl, string col, int sampleSize = 1000)
    {
        ColumnSample sample = ColumnSample.Collect(tbl, col, sampleSize, PglzStrategy.Default);
        int[] counts = new int[s_buckets.Length];
        foreach (ColumnSample.Probe probe in sample.Probes)
        {
            if (probe.CompressedBytes is int compressed)
            {
                double ratio = probe.RawBytes == 0 ? 1.0 : (double)compressed / probe.RawBytes;
                counts[Math.Min((int)Math.Floor(ratio * 5.0), 4)]++;
            }
            else
            {
                counts[^1]++;
            }
        }

        return [.. s_buckets.Select((bucket, index) => (bucket, counts[index]))];
    }

    /// <summary>
    /// Samples a column and returns a RECOMMEND, MARGINAL or SKIP verdict, with ready-to-run DDL when recommended.
    /// </summary>
    /// <param name="tbl">The table OID.</param>
    /// <param name="col">The exact column name.</param>
    /// <param name="sampleSize">The maximum number of non-NULL rows sampled at random.</param>
    /// <returns>The one-line verdict, or a NO DATA message when no non-NULL rows were sampled.</returns>
    [PgFunction]
    public static string PglzRecommend(uint tbl, string col, int sampleSize = 1000)
    {
        ColumnSample sample = ColumnSample.Collect(tbl, col, sampleSize, PglzStrategy.Default);
        int sampled = sample.Probes.Count;
        if (sampled == 0)
        {
            return $"NO DATA: no non-null rows sampled from {sample.DisplayName}.{col}";
        }

        long totalRaw = 0;
        long totalCompressed = 0;
        int accepted = 0;
        foreach (ColumnSample.Probe probe in sample.Probes)
        {
            totalRaw += probe.RawBytes;
            totalCompressed += probe.CompressedBytes ?? probe.RawBytes;
            accepted += probe.CompressedBytes is null ? 0 : 1;
        }

        double pctAccepted = (double)accepted / sampled;
        double averageRatio = totalRaw == 0 ? 1.0 : (double)totalCompressed / totalRaw;
        double savingsPct = Math.Max((1.0 - averageRatio) * 100.0, 0.0);
#if ANKUS_PG13
        string ddl = $"-- pg13: SET COMPRESSION unavailable; use a BEFORE INSERT trigger that PGLZ-compresses {sample.DisplayName}.{sample.QuotedColumn} into a bytea sibling column.";
#else
        string ddl = $"ALTER TABLE {sample.DisplayName} ALTER COLUMN {sample.QuotedColumn} SET COMPRESSION pglz;";
#endif
        if (pctAccepted >= RecommendPctAccepted && averageRatio <= RecommendAvgRatio)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"RECOMMEND: PGLZ saves ~{RustFormat.Fixed(savingsPct, 0)}% on {sample.DisplayName}.{col} ({accepted} of {sampled} sampled rows accepted). Run: {ddl}");
        }

        return pctAccepted < SkipPctAccepted || averageRatio > SkipAvgRatio
            ? $"SKIP: only {RustFormat.Fixed(pctAccepted * 100.0, 0)}% of sampled rows accepted, avg ratio {RustFormat.Fixed(averageRatio, 2)} — mostly incompressible."
            : $"MARGINAL: {RustFormat.Fixed(pctAccepted * 100.0, 0)}% accepted, avg ratio {RustFormat.Fixed(averageRatio, 2)} (~{RustFormat.Fixed(savingsPct, 0)}% savings). Decide based on workload.";
    }

    /// <summary>
    /// Selects the PGLZ strategy named in SQL.
    /// </summary>
    /// <param name="strategy"><c>default</c> or <c>always</c>, case-sensitively.</param>
    /// <returns>The selected strategy.</returns>
    /// <exception cref="PgException">The name is unknown; SQLSTATE <c>22023</c>.</exception>
    private static PglzStrategy ParseStrategy(string strategy) => strategy switch
    {
        "default" => PglzStrategy.Default,
        "always" => PglzStrategy.Always,
        _ => throw new PgException(PgSqlStates.InvalidParameterValue,
            $"unknown strategy {RustFormat.Debug(strategy)}: expected 'default' or 'always'"),
    };
}
