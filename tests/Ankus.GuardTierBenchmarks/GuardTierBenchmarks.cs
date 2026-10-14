namespace Ankus.GuardTierBenchmarks;

/// <summary>
/// Measures each native guard tier inside PostgreSQL, so guard changes can be compared across majors.
/// </summary>
public static class GuardTierBenchmarks
{
    /// <summary>
    /// The lightweight tier: numeric addition under the native error guard, with no subtransaction.
    /// </summary>
    /// <param name="bencher">The PostgreSQL benchmark timing boundary.</param>
    [PgBenchmark]
    public static void NumericAdd(PgBencher bencher)
    {
        PgNumeric left = PgNumeric.Parse("123.45");
        PgNumeric right = PgNumeric.Parse("67.89");
        bencher.Iterate(() => PgBenchmark.BlackBox(left + right));
    }

    /// <summary>
    /// The lightweight tier around a successful input function.
    /// </summary>
    /// <param name="bencher">The PostgreSQL benchmark timing boundary.</param>
    [PgBenchmark]
    public static void NumericParse(PgBencher bencher)
        => bencher.Iterate(static () => PgBenchmark.BlackBox(PgNumeric.Parse("123.45")));

    /// <summary>
    /// TryParse of invalid text: a soft input error from PostgreSQL 16, an internal subtransaction before.
    /// </summary>
    /// <param name="bencher">The PostgreSQL benchmark timing boundary.</param>
    [PgBenchmark]
    public static void NumericTryParseInvalid(PgBencher bencher)
        => bencher.Iterate(static () => PgBenchmark.BlackBox(PgNumeric.TryParse("not a number", out _)));

    /// <summary>
    /// The subtransaction tier: an explicit recovery scope around the same addition.
    /// </summary>
    /// <param name="bencher">The PostgreSQL benchmark timing boundary.</param>
    [PgBenchmark]
    public static void NumericAddInSubtransaction(PgBencher bencher)
    {
        PgNumeric left = PgNumeric.Parse("123.45");
        PgNumeric right = PgNumeric.Parse("67.89");
        bencher.Iterate(() => PgTransaction.RunInSubtransaction(() => PgBenchmark.BlackBox(left + right)));
    }

    /// <summary>
    /// SPI, where each statement runs in its own internal subtransaction.
    /// </summary>
    /// <param name="bencher">The PostgreSQL benchmark timing boundary.</param>
    [PgBenchmark]
    public static void SpiSelectOne(PgBencher bencher)
        => bencher.Iterate(static () => PgBenchmark.BlackBox(Spi.ExecuteScalar<int>("SELECT 1")));

    /// <summary>
    /// A report below the server's minimum message levels, which measures the report guard without output.
    /// </summary>
    /// <param name="bencher">The PostgreSQL benchmark timing boundary.</param>
    [PgBenchmark]
    public static void LogFilteredDebug(PgBencher bencher)
        => bencher.Iterate(static () => PgLog.Write(PgLogLevel.Debug5, "guard tier benchmark"));

    /// <summary>
    /// Replacing a text cell of a detached row, as a trigger or custom scan does for every row.
    /// </summary>
    /// <param name="bencher">The PostgreSQL benchmark timing boundary.</param>
    [PgBenchmark]
    public static void TupleSetText(PgBencher bencher)
    {
        PgHeapTuple row = PgTupleDescriptor.Load("pg_settings").CreateTuple();
        int ordinal = row.Descriptor.GetOrdinal("setting");
        bencher.Iterate(() => row.Set(ordinal, "work_mem"));
    }

    /// <summary>
    /// Replacing an integer cell of a detached row.
    /// </summary>
    /// <param name="bencher">The PostgreSQL benchmark timing boundary.</param>
    [PgBenchmark]
    public static void TupleSetInt(PgBencher bencher)
    {
        PgHeapTuple row = PgTupleDescriptor.Load("pg_settings").CreateTuple();
        int ordinal = row.Descriptor.GetOrdinal("sourceline");
        bencher.Iterate(() => row.Set(ordinal, 42));
    }
}
