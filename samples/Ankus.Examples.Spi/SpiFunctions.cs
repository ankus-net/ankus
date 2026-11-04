[assembly: PgSql("spi.tables", """
    CREATE TABLE spi.spi_example (id bigserial PRIMARY KEY, title text);
    INSERT INTO spi.spi_example (title)
    VALUES ('This is a test'), ('Hello There!'), ('I like pudding');
    CREATE TABLE spi.foo ();
    """, Requires = ["spi.schema"])]

namespace Ankus.Examples.Spi;

/// <summary>
/// Ports pgrx's SPI example using owned results and parameterized commands.
/// </summary>
[PgSchema("spi", Id = "spi.schema")]
public static class SpiFunctions
{
    /// <summary>
    /// Reads catalog identities into managed rows before the SPI session ends.
    /// </summary>
    /// <returns>Relation OIDs and names suffixed with the PostgreSQL major.</returns>
    [PgFunction]
    public static IEnumerable<(uint? Oid, string? Name)> SpiReturnQuery()
        => Ankus.Spi.Connect(static session => session.Select("""
            SELECT oid, relname::text || '-pg' || (current_setting('server_version_num')::integer / 10000)::text AS name
            FROM pg_catalog.pg_class
            """).Select(static row => (row.Get<uint?>("oid"), row.Get<string?>("name"))).ToArray());

    /// <summary>
    /// Chooses one existing row, returning null when the table is empty.
    /// </summary>
    /// <returns>A randomly selected identifier.</returns>
    [PgFunction]
    public static long? SpiQueryRandomId()
        => Ankus.Spi.ExecuteScalar<long?>("SELECT id FROM spi.spi_example ORDER BY random() LIMIT 1");

    /// <summary>
    /// Finds a title using a separately bound value.
    /// </summary>
    /// <param name="title">The exact title to find.</param>
    /// <returns>The first matching identifier, or null.</returns>
    [PgFunction]
    public static long? SpiQueryTitle(string title)
        => Ankus.Spi.ExecuteScalar<long?>(Ankus.Spi.Sql($"SELECT id FROM spi.spi_example WHERE title = {title}"));

    /// <summary>
    /// Reads and logs an identifier while returning its independently owned title.
    /// </summary>
    /// <param name="id">The identifier to find.</param>
    /// <returns>The title, or null for a missing row or SQL NULL.</returns>
    [PgFunction]
    public static string? SpiQueryById(long id)
    {
        (long? foundId, string? title) = Ankus.Spi.Connect(session =>
        {
            SpiResult rows = session.Select(Ankus.Spi.Sql($"SELECT id, title FROM spi.spi_example WHERE id = {id}"));
            return rows.Count == 0 ? (null, null) : (rows[0].Get<long?>(0), rows[0].Get<string?>(1));
        });
        PgLog.Info(FormattableString.Invariant($"id={foundId}"));
        return title;
    }

    /// <summary>
    /// Inserts a bound title and returns the database-generated identifier.
    /// </summary>
    /// <param name="title">The literal title to insert.</param>
    /// <returns>The inserted identifier.</returns>
    [PgFunction]
    public static long? SpiInsertTitle(string title)
        => Ankus.Spi.ExecuteScalar<long?>(Ankus.Spi.Sql($"INSERT INTO spi.spi_example(title) VALUES ({title}) RETURNING id"));

    /// <summary>
    /// Inserts a bound title and returns both columns as a one-row SQL table.
    /// </summary>
    /// <param name="title">The literal title to insert.</param>
    /// <returns>The inserted identifier and title.</returns>
    [PgFunction]
    public static IEnumerable<(long? Id, string? Title)> SpiInsertTitle2(string title)
    {
        (long? id, string? insertedTitle) = Ankus.Spi.ExecuteScalars<long?, string?>(
            Ankus.Spi.Sql($"INSERT INTO spi.spi_example(title) VALUES ({title}) RETURNING id, title"));
        return [(id, insertedTitle)];
    }

    /// <summary>
    /// Retains fetched text after both the cursor and its SPI session have closed.
    /// </summary>
    /// <returns>The first of ten thousand detached values.</returns>
    [PgFunction(Name = "issue1209_fixed")]
    public static string? DetachedCursorText()
    {
        string?[] values = Ankus.Spi.Connect(static session =>
        {
            using SpiCursor cursor = session.OpenCursor("SELECT 'hello'::text FROM generate_series(1, 10000)");
            return cursor.Fetch(10000).Select(static row => row.Get<string?>(0)).ToArray();
        });
        return values.Length == 0 ? null : values[0];
    }
}
