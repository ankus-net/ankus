namespace Ankus.TestExtension;

/// <summary>
/// Exercises transaction-aware SPI selection across connections, plans, raw results and cursors.
/// </summary>
public static class SpiSelectionFunctions
{
    private static SpiPreparedStatement? s_selectionPlan;

    /// <summary>
    /// Returns empty selection metadata through each managed or raw owner.
    /// </summary>
    /// <param name="mode">The selection transport.</param>
    /// <returns>The empty row counts and retained column identity.</returns>
    [PgFunction]
    public static string SelectionEmptyRows(int mode)
    {
        const string sql = "SELECT $1::int AS value, $2::text AS label WHERE false";
        SpiParameter first = SpiParameter.Create(40);
        SpiParameter second = SpiParameter.Create<string?>(null);
        if (mode < 4)
        {
            SpiResult rows = mode switch
            {
                0 => Spi.Select(sql, first, second),
                1 => Spi.Connect(session => session.Select(sql, first, second)),
                2 => SelectRowsPlan(sql, 0, null, first, second),
                _ => Spi.Connect(session => SelectRowsPlan(sql, 0, session, first, second)),
            };
            return $"{rows.Count}|{rows.RowsAffected}|{rows.Columns[0].Name}:{rows.Columns[0].TypeOid}|" +
                $"{rows.Columns[1].Name}:{rows.Columns[1].TypeOid}";
        }

        using SpiRawResult raw = mode switch
        {
            4 => Spi.SelectRaw(sql, first, second),
            5 => Spi.Connect(session => session.SelectRaw(sql, first, second)),
            6 => SelectRawPlan(sql, 0, null, first, second),
            _ => Spi.Connect(session => SelectRawPlan(sql, 0, session, first, second)),
        };
        return $"{raw.Count}|{raw.RowsAffected}|{raw.Columns[0].Name}:{raw.Columns[0].TypeOid}|" +
            $"{raw.Columns[1].Name}:{raw.Columns[1].TypeOid}";
    }

    /// <summary>
    /// Retains a session plan for transaction-aware selection after that connection and transaction end.
    /// </summary>
    [PgFunction]
    public static void SelectionCachePlan()
    {
        s_selectionPlan?.Dispose();
        s_selectionPlan = Spi.Connect(static session => session.Prepare("SELECT $1::int + 2 AS value", typeof(int)).Keep());
    }

    /// <summary>
    /// Reads a retained plan in a later callback without assigning a new transaction identity.
    /// </summary>
    /// <param name="value">The bound integer.</param>
    /// <returns>The parameterized result and transaction state.</returns>
    [PgFunction]
    public static string SelectionCachedValue(int value)
    {
        SpiPreparedStatement plan = s_selectionPlan ?? throw new InvalidOperationException("No selection plan was retained.");
        return $"{plan.Select(SpiParameter.Create(value))[0].Get<int>(0)}|{HasTransactionId()}";
    }

    /// <summary>
    /// Releases the retained selection plan on its owning backend.
    /// </summary>
    [PgFunction]
    public static void SelectionDisposePlan()
    {
        s_selectionPlan?.Dispose();
        s_selectionPlan = null;
    }

    /// <summary>
    /// Checks immutable transactions without allocating an identity, including recoverable locking and write errors.
    /// </summary>
    /// <param name="mode">The selection transport.</param>
    /// <returns>The transaction state, exact errors and recovered value.</returns>
    [PgFunction]
    public static string SelectionImmutable(int mode)
    {
        bool before = HasTransactionId();
        int value = SelectNumber("SELECT 42", mode);
        string locking = SelectionError("SELECT value FROM pg_temp.ankus_selection_values FOR UPDATE", mode);
        string writing = SelectionError("INSERT INTO pg_temp.ankus_selection_values VALUES (99) RETURNING value", mode % 8);
        string arithmetic = SelectionError("SELECT 1 / 0", mode % 8);
        int total = SelectNumber("SELECT sum(value)::int FROM pg_temp.ankus_selection_values", mode);
        return $"{before}|{value}|{locking}|{writing}|{arithmetic}|{total}|{HasTransactionId()}";
    }

    /// <summary>
    /// Checks writable intent, visibility after mutation and explicit read-only restrictions.
    /// </summary>
    /// <param name="mode">The selection transport.</param>
    /// <param name="markWritable">Whether a scalar read should establish writable intent before selection.</param>
    /// <returns>The locked value, newly visible sum and explicit read-only error.</returns>
    [PgFunction]
    public static string SelectionWritable(int mode, bool markWritable)
    {
        if (markWritable)
        {
            Spi.ExecuteScalar<int>("SELECT 42");
        }

        bool assigned = HasTransactionId();
        int locked = SelectNumber("SELECT value FROM pg_temp.ankus_selection_values ORDER BY value LIMIT 1 FOR UPDATE", mode);
        Spi.Execute("INSERT INTO pg_temp.ankus_selection_values VALUES (2)");
        int total = SelectNumber("SELECT sum(value)::int FROM pg_temp.ankus_selection_values", mode);
        string explicitError;
        try
        {
            Spi.Query("INSERT INTO pg_temp.ankus_selection_values VALUES (99)", readOnly: true, limit: 0);
            explicitError = "unexpected success";
        }
        catch (PgException error)
        {
            explicitError = error.SqlState;
        }

        return $"{assigned}|{locked}|{total}|{explicitError}|{HasTransactionId()}";
    }

    /// <summary>
    /// Reads through an unassigned recovery child without allocating a child transaction identity.
    /// </summary>
    /// <param name="mode">The selection transport.</param>
    /// <returns>The outer identity, child identities and selected value.</returns>
    [PgFunction]
    public static string SelectionPreservesChildIdentity(int mode)
        => PgTransaction.RunInSubtransaction(() =>
        {
            unsafe
            {
                bool outerAssigned = Ankus.Postgres.NativeMethods.GetTopTransactionIdIfAny() != 0;
                bool before = Ankus.Postgres.NativeMethods.GetCurrentTransactionIdIfAny() != 0;
                int value = SelectNumber("SELECT 42", mode);
                bool after = Ankus.Postgres.NativeMethods.GetCurrentTransactionIdIfAny() != 0;
                return $"{outerAssigned}|{before}|{value}|{after}";
            }
        });

    /// <summary>
    /// Confirms a scoped recovery child observes the caller's earlier mutation and rollback does not hide that intent.
    /// </summary>
    /// <returns>The two visible values and recovered error state.</returns>
    [PgFunction]
    public static string SelectionNested()
        => Spi.Connect(outer =>
        {
            int before = outer.Select("SELECT sum(value)::int FROM pg_temp.ankus_selection_values")[0].Get<int>(0);
            string state;
            try
            {
                PgTransaction.RunInSubtransaction(() =>
                {
                    Spi.Connect(inner => inner.Select("INSERT INTO pg_temp.ankus_selection_values VALUES (99) RETURNING value"));
                    throw new InvalidOperationException("Roll back the nested mutation.");
                });
                state = "unexpected success";
            }
            catch (InvalidOperationException error)
            {
                state = error.Message;
            }

            int after = outer.Select("SELECT sum(value)::int FROM pg_temp.ankus_selection_values")[0].Get<int>(0);
            return $"{before}|{after}|{state}";
        });

    /// <summary>
    /// Transports typed parameters, SQL NULL, row limits and copied metadata through each selection owner.
    /// </summary>
    /// <param name="mode">The managed or raw selection transport.</param>
    /// <param name="limit">The requested row limit.</param>
    /// <returns>The exact row, metadata and native lifetime observations.</returns>
    [PgFunction]
    public static string SelectionRows(int mode, int limit)
    {
        const string sql = "SELECT $1::int + n AS value, $2::text AS label FROM generate_series(0, 2) n ORDER BY n";
        SpiParameter first = SpiParameter.Create(40);
        SpiParameter second = SpiParameter.Create<string?>(null);
        if (mode < 4)
        {
            SpiResult result = mode switch
            {
                0 => Spi.Select(sql, limit, first, second),
                1 => Spi.Connect(session => session.Select(sql, limit, first, second)),
                2 => SelectRowsPlan(sql, limit, null, first, second),
                _ => Spi.Connect(session => SelectRowsPlan(sql, limit, session, first, second)),
            };
            return $"{result.Count}|{result.RowsAffected}|{result.Columns[0].Name}:{result.Columns[0].TypeOid}|" +
                $"{result.Columns[1].Name}:{result.Columns[1].TypeOid}|" +
                string.Join(',', result.Select(static row => $"{row.Get<int>(0)}:{row.Get<string?>(1) ?? "NULL"}"));
        }

        using SpiRawResult raw = mode switch
        {
            4 => Spi.SelectRaw(sql, limit, first, second),
            5 => Spi.Connect(session => session.SelectRaw(sql, limit, first, second)),
            6 => SelectRawPlan(sql, limit, null, first, second),
            _ => Spi.Connect(session => SelectRawPlan(sql, limit, session, first, second)),
        };
        PgDatum captured = raw[0][0];
        string values = string.Join(',', raw.Select(static row => $"{row.Get<int>(0)}:{(row[1].IsNull ? "NULL" : "present")}"));
        string metadata = $"{raw.Count}|{raw.RowsAffected}|{raw.Columns[0].Name}:{raw.Columns[0].TypeOid}|" +
            $"{raw.Columns[1].Name}:{raw.Columns[1].TypeOid}|{values}";
        raw.Dispose();
        try
        {
            captured.Read<int>();
            return metadata + "|unexpected live datum";
        }
        catch (ObjectDisposedException)
        {
            return metadata + "|expired";
        }
    }

    /// <summary>
    /// Attempts to swallow cancellation from selection to verify the native boundary remains authoritative.
    /// </summary>
    [PgFunction]
    public static void SelectionSwallowCancellation()
    {
        try
        {
            Spi.Select("SELECT pg_sleep(5)");
        }
        catch (PgQueryCanceledException)
        {
            // Deliberately swallowed: the integration test requires PostgreSQL to re-raise cancellation.
        }
    }

    /// <summary>
    /// Returns whether the transaction has already received a real PostgreSQL identity.
    /// </summary>
    /// <returns>The observed identity state without assigning one.</returns>
    private static bool HasTransactionId()
        => Spi.Select("SELECT pg_current_xact_id_if_assigned() IS NOT NULL")[0].Get<bool>(0);

    /// <summary>
    /// Reads a single integer through each independent selection transport.
    /// </summary>
    /// <param name="sql">The query.</param>
    /// <param name="mode">The transport.</param>
    /// <returns>The exact first value.</returns>
    private static int SelectNumber(string sql, int mode)
        => mode switch
        {
            0 => Spi.Select(sql)[0].Get<int>(0),
            1 => Spi.Connect(session => session.Select(sql)[0].Get<int>(0)),
            2 => ReadPlan(sql, null, raw: false, cursor: false),
            3 => Spi.Connect(session => ReadPlan(sql, session, raw: false, cursor: false)),
            4 => ReadRaw(Spi.SelectRaw(sql)),
            5 => Spi.Connect(session => ReadRaw(session.SelectRaw(sql))),
            6 => ReadPlan(sql, null, raw: true, cursor: false),
            7 => Spi.Connect(session => ReadPlan(sql, session, raw: true, cursor: false)),
            8 => ReadCursor(Spi.OpenCursor(sql)),
            9 => Spi.Connect(session => ReadCursor(session.OpenCursor(sql))),
            10 => ReadPlan(sql, null, raw: false, cursor: true),
            11 => Spi.Connect(session => ReadPlan(sql, session, raw: false, cursor: true)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    /// <summary>
    /// Executes a selection expected to fail and preserves its PostgreSQL error identity.
    /// </summary>
    /// <param name="sql">The rejected query.</param>
    /// <param name="mode">The transport.</param>
    /// <returns>The SQLSTATE or an explicit unexpected-success marker.</returns>
    private static string SelectionError(string sql, int mode)
    {
        try
        {
            SelectNumber(sql, mode);
            return "unexpected success";
        }
        catch (PgException error)
        {
            return error.SqlState;
        }
    }

    /// <summary>
    /// Executes a prepared selection while preserving its scoped or retained ownership.
    /// </summary>
    /// <param name="sql">The query.</param>
    /// <param name="session">The optional scoped connection.</param>
    /// <param name="raw">Whether to copy native datums.</param>
    /// <param name="cursor">Whether to open a cursor.</param>
    /// <returns>The selected integer.</returns>
    private static int ReadPlan(string sql, SpiSession? session, bool raw, bool cursor)
    {
        using SpiPreparedStatement plan = session is null ? Spi.Prepare(sql) : session.Prepare(sql);
        if (cursor)
        {
            return ReadCursor(plan.OpenCursor());
        }

        return raw ? ReadRaw(plan.SelectRaw()) : plan.Select()[0].Get<int>(0);
    }

    /// <summary>
    /// Reads and disposes an independently owned native result.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <returns>The selected integer.</returns>
    private static int ReadRaw(SpiRawResult result)
    {
        using (result)
        {
            return result[0].Get<int>(0);
        }
    }

    /// <summary>
    /// Reads and closes an owned cursor.
    /// </summary>
    /// <param name="cursor">The cursor.</param>
    /// <returns>The selected integer.</returns>
    private static int ReadCursor(SpiCursor cursor)
    {
        using (cursor)
        {
            return cursor.Fetch(1)[0].Get<int>(0);
        }
    }

    /// <summary>
    /// Selects detached rows from a scoped or retained typed plan.
    /// </summary>
    /// <param name="sql">The query.</param>
    /// <param name="limit">The row limit.</param>
    /// <param name="session">The optional scoped connection.</param>
    /// <param name="first">The integer parameter.</param>
    /// <param name="second">The nullable text parameter.</param>
    /// <returns>The detached rows.</returns>
    private static SpiResult SelectRowsPlan(string sql, int limit, SpiSession? session, SpiParameter first, SpiParameter second)
    {
        using SpiPreparedStatement plan = session is null ? Spi.Prepare(sql, typeof(int), typeof(string)) : session.Prepare(sql, typeof(int), typeof(string));
        return plan.Select(limit, first, second);
    }

    /// <summary>
    /// Selects native rows whose storage survives disposal of their original plan or session.
    /// </summary>
    /// <param name="sql">The query.</param>
    /// <param name="limit">The row limit.</param>
    /// <param name="session">The optional scoped connection.</param>
    /// <param name="first">The integer parameter.</param>
    /// <param name="second">The nullable text parameter.</param>
    /// <returns>The independently owned native rows.</returns>
    private static SpiRawResult SelectRawPlan(string sql, int limit, SpiSession? session, SpiParameter first, SpiParameter second)
    {
        using SpiPreparedStatement plan = session is null ? Spi.Prepare(sql, typeof(int), typeof(string)) : session.Prepare(sql, typeof(int), typeof(string));
        return plan.SelectRaw(limit, first, second);
    }
}
