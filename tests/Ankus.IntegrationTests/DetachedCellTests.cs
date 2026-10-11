using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Reads and writes row and composite cells whose types have no managed conversion, as pgrx reads fields only on request.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class DetachedCellTests(TestContext context)
{
    private const string Input = """
        ROW('5', '3.5,-7.25', 'y:2 x:1', '01:02:03:04:05:06', '7.5', ARRAY['p:1'::tsvector, NULL], '16/B374D848', 'before')
            ::detached_cells.unconverted
        """;

    /// <summary>
    /// An SPI row reads by-value, fixed-length, variable-length and converted cells through their mapped readers.
    /// </summary>
    [TestMethod]
    public async Task SpiRowsReadEveryRepresentationThroughMappedReaders()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("42|1.25,-2.5|'a':1 'b':2|08002B010203|-1234|word|null",
            await Scalar<string>(connection, "SELECT detached_cells.spi_mapped()"));
    }

    /// <summary>
    /// Ordinary reads of a detached cell and mapped reads of another type fail in managed code without losing the row.
    /// </summary>
    [TestMethod]
    public async Task UnsupportedReadsFailWithoutLosingTheRow()
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        Assert.AreEqual(
            "NotSupportedException|NotSupportedException|NotSupportedException|InvalidCastException|InvalidCastException|InvalidOperationException|word",
            await Scalar<string>(connection, "SELECT detached_cells.spi_errors()"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Replacing SPI row cells with a mapped value or a raw datum keeps an independent copy and its type.
    /// </summary>
    [TestMethod]
    public async Task SpiRowEditsCopyMappedValuesAndRawDatums()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("-5|7|'c':3|True", await Scalar<string>(connection, "SELECT detached_cells.spi_set()"));
    }

    /// <summary>
    /// A composite returns every field it did not change exactly, including a variable-length array of an unconverted type.
    /// </summary>
    [TestMethod]
    public async Task CompositesKeepUnconvertedFieldsExactly()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.IsTrue(await Scalar<bool>(connection, $"""
            WITH input AS (SELECT {Input} AS v), output AS (SELECT v, detached_cells.composite_update(v) AS o FROM input)
            SELECT (o).number::integer = 6 AND (o).pair::text = (v).pair::text AND (o).words::text = (v).words::text AND
                (o).address = (v).address AND (o).amount = (v).amount AND (o).tags::text = (v).tags::text AND
                (o).position = (v).position AND (o).label = 'updated'
            FROM output
            """));
        Assert.AreEqual("5|3.5,-7.25|'x':1 'y':2|010203040506|750|null|null|NotSupportedException|InvalidCastException",
            await Scalar<string>(connection, $"SELECT detached_cells.composite_read({Input})"));
        Assert.AreEqual("updated|true", await Scalar<string>(connection, """
            SELECT (o).label || '|' || ((o).number IS NULL AND (o).words IS NULL AND (o).tags IS NULL)::text
            FROM (SELECT detached_cells.composite_update(ROW(NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'x')::detached_cells.unconverted) AS o) s
            """));
    }

    /// <summary>
    /// Arrays of mapped elements read, replace and build row and record cells, including NULL elements.
    /// </summary>
    [TestMethod]
    public async Task ArraysOfMappedElementsReadAndWriteCells()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("1,NULL,3|'a':1|InvalidCastException|4,5|6,NULL",
            await Scalar<string>(connection, "SELECT detached_cells.array_cells()"));
    }

    /// <summary>
    /// A record built from a mapped parameter and a raw datum reads both fields back through their readers.
    /// </summary>
    [TestMethod]
    public async Task RecordsAcceptMappedParametersAndRawDatums()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("9|08002B0A0B0C", await Scalar<string>(connection, "SELECT detached_cells.record_create()"));
    }

    /// <summary>
    /// A trigger changes converted and mapped columns and returns a TOASTed text-search vector and other unconverted
    /// columns unchanged.
    /// </summary>
    [TestMethod]
    public async Task TriggerRowsKeepUnconvertedColumnsExactly()
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, """
            CREATE TEMP TABLE detached_documents(id integer, words tsvector, address macaddr, amount money, tags tsvector[], label text);
            CREATE TRIGGER detached_update BEFORE UPDATE ON detached_documents
                FOR EACH ROW EXECUTE FUNCTION detached_cells.document_update();
            INSERT INTO detached_documents VALUES (1, array_to_tsvector(ARRAY(SELECT 'w' || g FROM generate_series(1, 20000) g)),
                '0a:0b:0c:0d:0e:0f', '1.00', ARRAY['q:1'::tsvector], 'draft');
            CREATE TEMP TABLE detached_before AS SELECT * FROM detached_documents;
            UPDATE detached_documents SET label = label;
            """);
        // A stored value larger than a page lives in TOAST storage, so the trigger received it detoasted.
        Assert.AreEqual("t|t|t|$1.01|DRAFT|t", await Scalar<string>(connection, """
            SELECT concat_ws('|', d.words = b.words, d.address = b.address, d.tags = b.tags, d.amount, d.label,
                pg_column_size(d.words) > 8192)
            FROM detached_documents d, detached_before b
            """));
    }

    /// <summary>
    /// Opens a fresh PostgreSQL backend.
    /// </summary>
    private Task<NpgsqlConnection> Open() => PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);

    /// <summary>
    /// Executes a scalar query.
    /// </summary>
    private async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        object? value = await command.ExecuteScalarAsync(context.CancellationToken);
        return (T)value!;
    }

    /// <summary>
    /// Executes statements and consumes every result.
    /// </summary>
    private async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }
}
