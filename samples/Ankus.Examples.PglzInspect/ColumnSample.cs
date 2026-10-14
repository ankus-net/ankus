using System.Globalization;

namespace Ankus.Examples.PglzInspect;

/// <summary>
/// Holds one column's randomly sampled values after PGLZ compression.
/// </summary>
/// <param name="DisplayName">The table's name as <c>regclass</c> text, qualified only when the search path requires it.</param>
/// <param name="QuotedColumn">The column name quoted for SQL.</param>
/// <param name="EstimatedRows">The table's <c>pg_class.reltuples</c> estimate; -1 or 0 when unknown.</param>
/// <param name="Probes">The compressed size of each sampled non-NULL value.</param>
internal sealed record ColumnSample(string DisplayName, string QuotedColumn, double EstimatedRows, IReadOnlyList<ColumnSample.Probe> Probes)
{
    /// <summary>
    /// Samples up to <paramref name="sampleSize"/> random non-NULL values of a column and compresses each one.
    /// </summary>
    /// <param name="tbl">The table OID.</param>
    /// <param name="col">The exact column name.</param>
    /// <param name="sampleSize">The maximum number of rows; negative values sample none.</param>
    /// <param name="strategy">The PGLZ acceptance strategy.</param>
    /// <returns>The sampled values' sizes with the table's identity and row estimate.</returns>
    /// <remarks>
    /// <c>bytea</c> values are compressed as stored. Other values are compressed as their text in the database
    /// encoding, which for text columns is exactly the payload TOAST compresses.
    /// </remarks>
    internal static ColumnSample Collect(uint tbl, string col, int sampleSize, PglzStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(col);
        int limit = Math.Max(sampleSize, 0);
        SpiResult relation = Spi.Select("""
            SELECT n.nspname::text, c.relname::text, c.oid::regclass::text, c.reltuples
            FROM pg_catalog.pg_class AS c
            JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE c.oid = $1
            """, SpiParameter.Create(tbl));
        if (relation.Count == 0)
        {
            throw new PgException(PgSqlStates.UndefinedTable,
                string.Create(CultureInfo.InvariantCulture, $"relation with OID {tbl} does not exist"));
        }

        SpiRow identity = relation[0];
        string table = Spi.QuoteQualifiedIdentifier(identity.Get<string>(0), identity.Get<string>(1));
        string column = Spi.QuoteIdentifier(col);
        uint? type = Spi.ExecuteScalar<uint?>("""
            SELECT atttypid FROM pg_catalog.pg_attribute
            WHERE attrelid = $1 AND attname = $2 AND NOT attisdropped
            """, SpiParameter.Create(tbl), SpiParameter.Create(col));

        // Native bytea needs no text round trip. Text is read in the database encoding, so backslashes stay data.
        string sql = type == (uint)PgBuiltInOid.ByteaOid
            ? $"SELECT {column} FROM {table} WHERE {column} IS NOT NULL ORDER BY random() LIMIT $1"
            : $"SELECT convert_to({column}::text, pg_catalog.getdatabaseencoding()) FROM {table} WHERE {column} IS NOT NULL ORDER BY random() LIMIT $1";
        SpiResult rows = Spi.Select(sql, SpiParameter.Create(limit));

        var probes = new List<Probe>(rows.Count);
        byte[] scratch = [];
        foreach (byte[]? value in rows.Select(static row => row.Get<byte[]?>(0)))
        {
            if (value is null)
            {
                continue;
            }

            int capacity = Pglz.MaxOutput(value.Length);
            if (scratch.Length < capacity)
            {
                // One reusable buffer avoids an allocation per compressed value.
                scratch = GC.AllocateUninitializedArray<byte>(capacity);
            }

            probes.Add(new(value.Length, Pglz.TryCompress(value, scratch, strategy, out int written) ? written : null));
        }

        return new(identity.Get<string>(2), column, identity.Get<float>(3), probes);
    }

    /// <summary>
    /// Records one sampled value's size before and after compression.
    /// </summary>
    /// <param name="RawBytes">The uncompressed length.</param>
    /// <param name="CompressedBytes">The compressed length, or null when PGLZ rejected the value.</param>
    internal readonly record struct Probe(int RawBytes, int? CompressedBytes);
}
