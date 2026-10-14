using System.Globalization;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises raw cursor batches, independent native ownership, scrolling, and error cleanup.
/// </summary>
public static class SpiRawCursorFunctions
{
    /// <summary>
    /// Retains all batches until after later fetches and cursor disposal, then formats their native values.
    /// </summary>
    /// <param name="api">The standalone, session, retained-plan, or session-plan selection.</param>
    /// <param name="sql">The cursor query.</param>
    /// <param name="batchSize">The positive fetch size.</param>
    /// <returns>All values and the final empty batch's metadata.</returns>
    [PgFunction]
    public static string RawCursorBatches(int api, string sql, int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var batches = new List<SpiRawResult>();
        try
        {
            using (SpiCursor cursor = Open(api, sql))
            {
                SpiRawResult batch;
                do
                {
                    batch = cursor.FetchRaw(batchSize);
                    batches.Add(batch);
                }

                while (batch.Count != 0);
            }

            Spi.Execute("SELECT repeat('overwrite', 10000)");
            string metadata = string.Join(',', batches[^1].Columns.Select(static column => column.Name + ":" + column.TypeOid));
            return string.Join('/', batches.Where(static batch => batch.Count != 0).Select(Values)) + ";" + metadata;
        }
        finally
        {
            Disposal.DisposeAll(batches);
        }
    }

    /// <summary>
    /// Mixes raw and managed fetching while checking zero-count and backward movement through a scrollable cursor.
    /// </summary>
    /// <returns>The ordered batches and current-row observations.</returns>
    [PgFunction]
    public static string RawCursorMovement()
    {
        Spi.Execute("DECLARE raw_scroll SCROLL CURSOR FOR SELECT generate_series(1, 4) AS value");
        using SpiCursor cursor = Spi.FindCursor("raw_scroll");
        using SpiRawResult before = cursor.FetchRaw(0);
        using SpiRawResult first = cursor.FetchRaw(2);
        using SpiRawResult current = cursor.FetchRaw(0);
        using SpiRawResult back = cursor.FetchRaw(1, forward: false);
        int managed = cursor.Fetch(1)[0].Get<int>(0);
        using SpiRawResult next = cursor.FetchRaw(2);
        return before.Count.ToString(CultureInfo.InvariantCulture) + ":" + before.Columns.Count.ToString(CultureInfo.InvariantCulture) + "|" +
            Values(first) + "|" + Values(current) + "|" + Values(back) + "|" + managed.ToString(CultureInfo.InvariantCulture) + "|" + Values(next);
    }

    /// <summary>
    /// Rejects invalid counts and worker-thread access without moving the cursor, then validates batch disposal.
    /// </summary>
    /// <returns>The rejected operations and independently read value.</returns>
    [PgFunction]
    public static string RawCursorGuards()
    {
        using SpiCursor cursor = Spi.OpenCursor("SELECT 42");
        int rejected = 0;
        try
        {
            cursor.FetchRaw(-1);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected++;
        }

        rejected += Task.Run(() =>
        {
            try
            {
                cursor.FetchRaw(1);
                return 0;
            }
            catch (InvalidOperationException)
            {
                return 1;
            }
        }).GetAwaiter().GetResult();
        using SpiRawResult batch = cursor.FetchRaw(1);
        PgDatum value = batch[0][0];
        cursor.Dispose();
        int result = value.Read<int>();
        try
        {
            cursor.FetchRaw(1);
        }
        catch (ObjectDisposedException)
        {
            rejected++;
        }

        batch.Dispose();
        try
        {
            value.DangerousGetBits();
        }
        catch (ObjectDisposedException)
        {
            rejected++;
        }

        return rejected.ToString(CultureInfo.InvariantCulture) + "|" + result.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Verifies a failed fetch releases its raw result context and permits subsequent backend operations.
    /// </summary>
    /// <returns>The native error, count of leaked result contexts, and successful scalar.</returns>
    [PgFunction]
    public static string RawCursorRecover()
    {
        const string contexts = "SELECT count(*) FROM ankus_test_memory.contexts WHERE ident = 'Ankus raw SPI result'";
        long before = Spi.ExecuteScalar<long>(contexts);
        string error = "unexpected success";
        using (SpiCursor cursor = Spi.OpenCursor("SELECT 1 / (3 - n) FROM generate_series(1, 3) AS s(n)"))
        {
            try
            {
                using SpiRawResult batch = cursor.FetchRaw(3);
            }
            catch (PgException exception)
            {
                error = exception.SqlState;
            }
        }

        long leaked = Spi.ExecuteScalar<long>(contexts) - before;
        return error + "|" + leaked.ToString(CultureInfo.InvariantCulture) + "|" + Spi.ExecuteScalar<int>("SELECT 42").ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Opens a cursor and releases its session and prepared plan before returning it.
    /// </summary>
    /// <param name="api">The SPI owner selection.</param>
    /// <param name="sql">The cursor query.</param>
    /// <returns>The independent cursor.</returns>
    private static SpiCursor Open(int api, string sql)
    {
        if (api == 0)
        {
            return Spi.OpenCursor(sql);
        }

        if (api == 1)
        {
            return Spi.Connect(session => session.OpenCursor(sql));
        }

        if (api == 2)
        {
            using SpiPreparedStatement plan = Spi.Prepare(sql);
            return plan.OpenCursor();
        }

        return Spi.Connect(session =>
        {
            using SpiPreparedStatement plan = session.Prepare(sql);
            return plan.OpenCursor();
        });
    }

    /// <summary>
    /// Formats every row after its cursor has released the original tuple table.
    /// </summary>
    /// <param name="batch">The live raw batch.</param>
    /// <returns>The server-formatted cells with explicit NULL markers.</returns>
    private static string Values(SpiRawResult batch)
        => string.Join(',', batch.Select(static row => string.Join('|', row.Select(static value => value.ToPostgresString() ?? "<null>"))));
}
