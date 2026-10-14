using System.Globalization;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises parameterized interpolation through each SPI transport in a native backend.
/// </summary>
public static class SpiCommandFunctions
{
    /// <summary>
    /// Executes one command role with declared nullable values and the selected connection owner.
    /// </summary>
    /// <param name="mode">The execution, row, scalar, plan or cursor role.</param>
    /// <param name="scoped">Whether to use a scoped SPI connection.</param>
    /// <param name="number">A nullable integer binding.</param>
    /// <param name="text">A nullable text binding.</param>
    /// <param name="bytes">A nullable bytea binding.</param>
    /// <returns>The independently observed result, or the JSON plan for planning.</returns>
    [PgFunction]
    public static string CommandRole(int mode, bool scoped, int? number, string? text, byte[]? bytes)
    {
        // codeql[cs/call-to-object-tostring] Spi.Sql binds each interpolated value as a parameter; nothing is formatted as text.
        SpiCommand command = Spi.Sql($"SELECT {number} AS value, {text} AS label, {bytes} AS bytes FROM generate_series(1,3)");
        return scoped ? Spi.Connect(session => ReadCommand(mode, command, session)) : ReadCommand(mode, command, null);
    }

    /// <summary>
    /// Checks write rollback, explicit read-only rejection and same-callback recovery.
    /// </summary>
    /// <param name="scoped">Whether to use a scoped connection.</param>
    /// <returns>The exact SQLSTATEs, surviving table sum and recovered scalar.</returns>
    [PgFunction]
    public static string CommandRecovery(bool scoped)
    {
        Spi.Execute("CREATE TEMP TABLE ankus_command_values(value int); INSERT INTO ankus_command_values VALUES (40)");
        return scoped ? Spi.Connect(RecoverCommand) : RecoverCommand(null);
    }

    /// <summary>
    /// Swallows managed cancellation to prove the native entry boundary still rejects success.
    /// </summary>
    /// <returns>An unreachable success marker when cancellation fires.</returns>
    [PgFunction]
    public static int CommandSwallowCancellation()
    {
        try
        {
            Spi.Execute(Spi.Sql($"SELECT pg_sleep({10.0})"));
        }
        catch (PgQueryCanceledException)
        {
            return 99;
        }

        return 0;
    }

    /// <summary>
    /// Preserves custom codecs, declared mapped base types and nullable array elements.
    /// </summary>
    /// <param name="scoped">Whether to use a scoped connection.</param>
    /// <param name="nullValues">Whether the three values are SQL NULL.</param>
    /// <returns>The independently read values and PostgreSQL type names.</returns>
    [PgFunction]
    public static string CommandTypedValues(bool scoped, bool nullValues)
    {
        CustomOperatorFunctions.Key? key = nullValues ? null : new("key☃", 42);
        MappedText? mapped = nullValues ? null : new DerivedMappedText("mapped☃");
        int?[]? numbers = nullValues ? null : [1, null, 3];
        // codeql[cs/call-to-object-tostring] Spi.Sql binds each interpolated value as a parameter; nothing is formatted as text.
        SpiCommand values = Spi.Sql($"SELECT {key}, {mapped}, {numbers}");
        // codeql[cs/call-to-object-tostring] Spi.Sql binds each interpolated value as a parameter; nothing is formatted as text.
        SpiCommand identities = Spi.Sql($"SELECT pg_typeof({key})::text, pg_typeof({mapped})::text, pg_typeof({numbers})::text");
        return scoped ? Spi.Connect(session => ReadTypedValues(values, identities, session)) : ReadTypedValues(values, identities, null);
    }

    /// <summary>
    /// Rebinding raw values copies result storage but does not extend the source parameter's owner.
    /// </summary>
    /// <param name="sql">The independently constructed source query.</param>
    /// <param name="scoped">Whether to rebind on a scoped connection.</param>
    /// <returns>The rebound type/value/NULL, stale rejection and same-callback recovery.</returns>
    [PgFunction]
    public static string CommandRawOwner(string sql, bool scoped)
    {
        using SpiRawResult source = Spi.QueryRaw(sql);
        PgDatum datum = source[0][0];
        // codeql[cs/call-to-object-tostring] Spi.Sql binds each interpolated value as a parameter; nothing is formatted as text.
        SpiCommand command = Spi.Sql($"SELECT {datum}");
        using SpiRawResult rebound = scoped ? Spi.Connect(session => session.QueryRaw(command)) : Spi.QueryRaw(command);
        source.Dispose();
        string stale = "missing";
        try
        {
            if (scoped)
            {
                using SpiRawResult rejected = Spi.Connect(session => session.QueryRaw(command));
            }
            else
            {
                using SpiRawResult rejected = Spi.QueryRaw(command);
            }
        }
        catch (ObjectDisposedException)
        {
            stale = nameof(ObjectDisposedException);
        }

        PgDatum copied = rebound[0][0];
        int recovered = Spi.ExecuteScalar<int>(Spi.Sql($"SELECT {40} + {2}"));
        return $"{copied.TypeOid}|{copied.ToPostgresString() ?? "<null>"}|{copied.IsNull}|{stale}|{recovered}";
    }

    /// <summary>
    /// Selects after optional writable intent using the same command APIs as extension authors.
    /// </summary>
    /// <param name="scoped">Whether to use a scoped connection.</param>
    /// <param name="writable">Whether a scalar read establishes writable intent first.</param>
    /// <returns>The transaction identity, selected value and observed write visibility.</returns>
    [PgFunction]
    public static string CommandSnapshot(bool scoped, bool writable)
        => scoped ? Spi.Connect(session => ReadSnapshot(writable, session)) : ReadSnapshot(writable, null);

    /// <summary>
    /// Attempts every command role on a connection whose callback has ended.
    /// </summary>
    /// <param name="mode">The role to attempt.</param>
    /// <returns>The exact rejected lifetime contract and a subsequent result.</returns>
    [PgFunction]
    public static string CommandExpiredSession(int mode)
    {
        SpiSession expired = Spi.Connect(static session => session);
        string error = "missing";
        try
        {
            // codeql[cs/call-to-object-tostring] Spi.Sql binds each interpolated value as a parameter; nothing is formatted as text.
            ReadCommand(mode, Spi.Sql($"SELECT {42}, {"label"}, {new byte[] { 42 }}"), expired);
        }
        catch (ObjectDisposedException)
        {
            error = nameof(ObjectDisposedException);
        }

        return error + "|" + Spi.ExecuteScalar<int>(Spi.Sql($"SELECT {42}")).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Dispatches commands while keeping scoped results inside their connection callback.
    /// </summary>
    /// <param name="mode">The command role.</param>
    /// <param name="command">The declared bindings.</param>
    /// <param name="session">The active connection, or null for standalone SPI.</param>
    /// <returns>The observed values and metadata.</returns>
    private static string ReadCommand(int mode, SpiCommand command, SpiSession? session)
    {
        switch (mode)
        {
            case 0:
                return (session is null ? Spi.Execute(command) : session.Execute(command)).ToString(CultureInfo.InvariantCulture);
            case 1:
            case 2:
                SpiResult rows = mode == 1
                    ? session is null ? Spi.Query(command, true, 2) : session.Query(command, true, 2)
                    : session is null ? Spi.Select(command, 2) : session.Select(command, 2);
                return $"{rows.Count}|{Format(rows[0].Get<int?>(0), rows[0].Get<string?>(1), rows[0].Get<byte[]?>(2))}|" +
                    $"{rows.Columns[0].TypeOid}|{rows.Columns[1].TypeOid}|{rows.Columns[2].TypeOid}";
            case 3:
            case 4:
                using (SpiRawResult raw = mode == 3
                    ? session is null ? Spi.QueryRaw(command, true, 2) : session.QueryRaw(command, true, 2)
                    : session is null ? Spi.SelectRaw(command, 2) : session.SelectRaw(command, 2))
                {
                    return $"{raw.Count}|{Format(raw[0].Get<int?>(0), raw[0].Get<string?>(1), raw[0].Get<byte[]?>(2))}|" +
                        $"{raw.Columns[0].TypeOid}|{raw.Columns[1].TypeOid}|{raw.Columns[2].TypeOid}";
                }
            case 5:
                int? scalar = session is null ? Spi.ExecuteScalar<int?>(command) : session.ExecuteScalar<int?>(command);
                return scalar?.ToString(CultureInfo.InvariantCulture) ?? "<null>";
            case 6:
                (int? first, string? second) = session is null
                    ? Spi.ExecuteScalars<int?, string?>(command) : session.ExecuteScalars<int?, string?>(command);
                return (first?.ToString(CultureInfo.InvariantCulture) ?? "<null>") + "|" + (second ?? "<null>");
            case 7:
                (int? number, string? text, byte[]? bytes) = session is null
                    ? Spi.ExecuteScalars<int?, string?, byte[]?>(command) : session.ExecuteScalars<int?, string?, byte[]?>(command);
                return Format(number, text, bytes);
            case 8:
                return (session is null ? Spi.Explain(command) : session.Explain(command)).Text;
            case 9:
            case 10:
                using (SpiCursor cursor = mode == 9
                    ? session is null ? Spi.OpenCursor(command) : session.OpenCursor(command)
                    : session is null ? Spi.OpenCursor(command, true) : session.OpenCursor(command, true))
                {
                    SpiResult firstBatch = cursor.Fetch(2);
                    SpiResult lastBatch = cursor.Fetch(2);
                    SpiResult empty = cursor.Fetch(1);
                    return $"{firstBatch.Count}|{lastBatch.Count}|{empty.Count}|" +
                        Format(firstBatch[0].Get<int?>(0), lastBatch[0].Get<string?>(1), firstBatch[1].Get<byte[]?>(2));
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>
    /// Reads custom values and names using exact declared result types.
    /// </summary>
    /// <param name="values">The value bindings.</param>
    /// <param name="identities">The independent type-name query.</param>
    /// <param name="session">The optional scoped owner.</param>
    /// <returns>The values and catalog identities.</returns>
    private static string ReadTypedValues(SpiCommand values, SpiCommand identities, SpiSession? session)
    {
        (CustomOperatorFunctions.Key? key, MappedText? mapped, int?[]? numbers) = session is null
            ? Spi.ExecuteScalars<CustomOperatorFunctions.Key?, MappedText?, int?[]?>(values)
            : session.ExecuteScalars<CustomOperatorFunctions.Key?, MappedText?, int?[]?>(values);
        (string customType, string mappedType, string arrayType) = session is null
            ? Spi.ExecuteScalars<string, string, string>(identities) : session.ExecuteScalars<string, string, string>(identities);
        string keyText = key is null ? "<null>" : key.Name + ":" + key.Metadata.ToString(CultureInfo.InvariantCulture);
        string arrayText = numbers is null ? "<null>" : string.Join(",", numbers.Select(static value =>
            value?.ToString(CultureInfo.InvariantCulture) ?? "<null>"));
        return $"{keyText}|{mapped?.Value ?? "<null>"}|{arrayText}|{customType}|{mappedType}|{arrayType}";
    }

    /// <summary>
    /// Observes transaction-aware read-only selection and writable snapshot visibility.
    /// </summary>
    /// <param name="writable">Whether to establish writable intent.</param>
    /// <param name="session">The optional scoped owner.</param>
    /// <returns>The initial state, locking outcome and final table sum.</returns>
    private static string ReadSnapshot(bool writable, SpiSession? session)
    {
        if (writable)
        {
            SpiCommand scalar = Spi.Sql($"SELECT {42}");
            if (session is null)
            {
                Spi.ExecuteScalar<int>(scalar);
            }
            else
            {
                session.ExecuteScalar<int>(scalar);
            }
        }

        SpiCommand state = Spi.Sql($"SELECT pg_current_xact_id_if_assigned() IS NOT NULL");
        bool assigned = (session is null ? Spi.Select(state) : session.Select(state))[0].Get<bool>(0);
        string locking;
        SpiCommand locked = Spi.Sql($"SELECT value FROM ankus_command_snapshot WHERE value = {40} FOR UPDATE");
        try
        {
            int value = (session is null ? Spi.Select(locked) : session.Select(locked))[0].Get<int>(0);
            locking = value.ToString(CultureInfo.InvariantCulture);
        }
        catch (PgException error)
        {
            locking = error.SqlState;
        }

        if (writable)
        {
            SpiCommand write = Spi.Sql($"INSERT INTO ankus_command_snapshot VALUES ({2})");
            if (session is null)
            {
                Spi.Execute(write);
            }
            else
            {
                session.Execute(write);
            }
        }

        SpiCommand sum = Spi.Sql($"SELECT sum(value)::int FROM ankus_command_snapshot WHERE value >= {0}");
        int total = (session is null ? Spi.Select(sum) : session.Select(sum))[0].Get<int>(0);
        return $"{assigned}|{locking}|{total}";
    }

    /// <summary>
    /// Captures recoverable errors and reads the unmodified table with the same owner.
    /// </summary>
    /// <param name="session">The scoped owner, or null for standalone SPI.</param>
    /// <returns>The failures and exact subsequent results.</returns>
    private static string RecoverCommand(SpiSession? session)
    {
        string arithmetic = "missing";
        string readOnly = "missing";
        SpiCommand failing = Spi.Sql($"INSERT INTO ankus_command_values VALUES ({99}); SELECT {1} / {0}");
        try
        {
            if (session is null)
            {
                Spi.Execute(failing);
            }
            else
            {
                session.Execute(failing);
            }
        }
        catch (PgException error)
        {
            arithmetic = error.SqlState;
        }

        SpiCommand write = Spi.Sql($"INSERT INTO ankus_command_values VALUES ({2}) RETURNING value");
        try
        {
            if (session is null)
            {
                Spi.Query(write, true);
            }
            else
            {
                session.Query(write, true);
            }
        }
        catch (PgException error)
        {
            readOnly = error.SqlState;
        }

        SpiCommand total = Spi.Sql($"SELECT sum(value)::int FROM ankus_command_values WHERE value >= {0}");
        int sum = session is null ? Spi.ExecuteScalar<int>(total) : session.ExecuteScalar<int>(total);
        SpiCommand next = Spi.Sql($"SELECT {40} + {2}");
        int recovered = session is null ? Spi.ExecuteScalar<int>(next) : session.ExecuteScalar<int>(next);
        return $"{arithmetic}|{readOnly}|{sum}|{recovered}";
    }

    /// <summary>
    /// Makes SQL NULL and binary contents observable without locale-dependent conversions.
    /// </summary>
    /// <param name="number">The first cell.</param>
    /// <param name="text">The second cell.</param>
    /// <param name="bytes">The third cell.</param>
    /// <returns>The values with explicit NULL markers.</returns>
    private static string Format(int? number, string? text, byte[]? bytes)
        => (number?.ToString(CultureInfo.InvariantCulture) ?? "<null>") + "|" + (text ?? "<null>") + "|" +
            (bytes is null ? "<null>" : Convert.ToHexString(bytes));
}
