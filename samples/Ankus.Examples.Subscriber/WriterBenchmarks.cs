using System.Globalization;

namespace Ankus.Examples.Subscriber;

/// <summary>
/// Measures, inside PostgreSQL, the relation writer steps that the subscriber repeats for each remote transaction.
/// </summary>
public static class WriterBenchmarks
{
    private static int s_next;

    /// <summary>
    /// Opens and closes a writer, as each applied transaction does for every table it changes.
    /// </summary>
    /// <param name="bencher">The PostgreSQL benchmark timing boundary.</param>
    [PgBenchmark(Setup = nameof(CreateTable))]
    public static void OpenAndClose(PgBencher bencher)
    {
        uint table = Spi.ExecuteScalar<uint>("SELECT 'bench_items'::regclass::oid");
        bencher.Iterate(() =>
        {
            using PgRelationWriter writer = PgRelationWriter.Open(table);
        });
    }

    /// <summary>
    /// Loads the table's row descriptor, which opening a writer includes.
    /// </summary>
    /// <param name="bencher">The PostgreSQL benchmark timing boundary.</param>
    [PgBenchmark(Setup = nameof(CreateTable))]
    public static void LoadDescriptor(PgBencher bencher)
    {
        uint type = Spi.ExecuteScalar<uint>("SELECT 'bench_items'::regtype::oid");
        bencher.Iterate(() => PgBenchmark.BlackBox(PgTupleDescriptor.Load(type)));
    }

    /// <summary>
    /// Inserts a row in replication's text form through an open writer, as the subscriber applies an insert.
    /// </summary>
    /// <param name="bencher">The PostgreSQL benchmark timing boundary.</param>
    /// <remarks>
    /// The loop runs after this method returns, so the writer opens on the first iteration. The benchmark's transaction
    /// always rolls back, which releases a writer left open.
    /// </remarks>
    [PgBenchmark(Setup = nameof(CreateTable))]
    public static void InsertReplicatedRow(PgBencher bencher)
    {
        uint table = Spi.ExecuteScalar<uint>("SELECT 'bench_items'::regclass::oid");
        PgRelationWriter? writer = null;
        PgReplicatedRow? row = null;
        bencher.Iterate(() =>
        {
            writer ??= PgRelationWriter.Open(table);
            row ??= writer.CreateReplicatedRow();
            row.Clear();
            row.SetText(0, (++s_next).ToString(CultureInfo.InvariantCulture));
            row.SetText(1, "row"u8);
            row.SetText(2, "1.5"u8);
            writer.Insert(row);
        });
    }

    /// <summary>
    /// Inserts the same row through SPI, for comparison with the writer.
    /// </summary>
    /// <param name="bencher">The PostgreSQL benchmark timing boundary.</param>
    [PgBenchmark(Setup = nameof(CreateTable))]
    public static void InsertWithSpi(PgBencher bencher)
        => bencher.Iterate(static () => Spi.Execute("INSERT INTO bench_items VALUES ($1, 'row', 1.5)", SpiParameter.Create(++s_next)));

    /// <summary>
    /// Creates the benchmarks' table, like the replication benchmark's, inside the rollback-only run.
    /// </summary>
    internal static void CreateTable()
        => Spi.Execute("CREATE TABLE IF NOT EXISTS bench_items (id integer PRIMARY KEY, name text, amount numeric)");
}
