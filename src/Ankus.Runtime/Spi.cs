using System;
using System.Globalization;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Executes SQL inside the current PostgreSQL backend through the Server Programming Interface.
/// </summary>
public static partial class Spi
{
    /// <summary>
    /// Quotes a single SQL identifier using PostgreSQL's keyword rules and quote_all_identifiers setting.
    /// </summary>
    /// <param name="identifier">One identifier; dots are part of its name.</param>
    /// <returns>The quoted identifier or the original spelling when quoting is unnecessary.</returns>
    /// <remarks>
    /// The quoting helpers need no transaction, so a background worker can also use them between transactions, for
    /// example to build replication commands.
    /// </remarks>
    public static string QuoteIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return NativeBackend.HasBinding ? NativeBackend.Quote(SpiOperation.QuoteIdentifier, SpiParameter.Create(identifier))
            : NativeQuote.Quote(1, identifier, null);
    }

    /// <summary>
    /// Quotes a schema or other qualifier and an identifier independently, joining them with a dot.
    /// </summary>
    /// <param name="qualifier">The qualifier, or null to omit it.</param>
    /// <param name="identifier">The identifier.</param>
    /// <returns>The qualified SQL name.</returns>
    public static string QuoteQualifiedIdentifier(string? qualifier, string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return NativeBackend.HasBinding
            ? NativeBackend.Quote(SpiOperation.QuoteQualifiedIdentifier, SpiParameter.Create(qualifier), SpiParameter.Create(identifier))
            : NativeQuote.Quote(2, identifier, qualifier);
    }

    /// <summary>
    /// Quotes a text literal with escaping that is valid regardless of standard_conforming_strings.
    /// </summary>
    /// <param name="value">The literal text.</param>
    /// <returns>The quoted SQL literal.</returns>
    public static string QuoteLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return NativeBackend.HasBinding ? NativeBackend.Quote(SpiOperation.QuoteLiteral, SpiParameter.Create(value))
            : NativeQuote.Quote(3, value, null);
    }

    /// <summary>
    /// Writes a reference to a positional parameter, such as <c>$1</c>, for a command whose number of parameters is known
    /// only at run time.
    /// </summary>
    /// <param name="number">The parameter's position, starting at 1.</param>
    /// <returns>The parameter reference.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> is less than 1.</exception>
    public static string ParameterReference(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        return "$" + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Plans one SQL statement and returns PostgreSQL's JSON EXPLAIN output. The statement is not executed by EXPLAIN ANALYZE.
    /// </summary>
    /// <param name="commandText">The statement to explain.</param>
    /// <param name="parameters">The typed positional parameters.</param>
    /// <returns>The owned JSON query plan.</returns>
    public static PgJson Explain(string commandText, params ReadOnlySpan<SpiParameter> parameters)
        => NativeBackend.Explain(commandText, parameters);

    /// <summary>
    /// Runs a synchronous callback using one scoped SPI connection. Session-bound plans expire when the callback exits.
    /// </summary>
    /// <param name="action">The synchronous backend-thread callback.</param>
    public static void Connect(Action<SpiSession> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeBackend.Connect(session =>
        {
            action(session);
            return 0;
        });
    }

    /// <summary>
    /// Runs a synchronous callback using one scoped SPI connection and returns its managed result.
    /// </summary>
    /// <typeparam name="TResult">The callback result type.</typeparam>
    /// <param name="action">The synchronous backend-thread callback. It must not start asynchronous session operations.</param>
    /// <returns>The callback result, whose owned rows and retained plans can outlive the session.</returns>
    public static TResult Connect<TResult>(Func<SpiSession, TResult> action) => NativeBackend.Connect(action);

    /// <summary>
    /// Opens a transaction-bound cursor with optional typed positional parameters.
    /// Uses a read-only snapshot until the transaction becomes writable, then uses a fresh writable snapshot.
    /// </summary>
    /// <param name="commandText">One SQL command that returns rows.</param>
    /// <param name="parameters">The positional parameter values.</param>
    /// <returns>An owned cursor that should be disposed or detached.</returns>
    public static SpiCursor OpenCursor(string commandText, params ReadOnlySpan<SpiParameter> parameters)
        => NativeBackend.OpenCursor(commandText, parameters, readOnly: null);

    /// <summary>
    /// Opens a cursor with explicit read-only SPI execution mode.
    /// </summary>
    /// <param name="commandText">One SQL command that returns rows.</param>
    /// <param name="readOnly">Whether PostgreSQL should use read-only execution.</param>
    /// <param name="parameters">The positional parameter values.</param>
    /// <returns>An owned transaction-bound cursor.</returns>
    public static SpiCursor OpenCursor(string commandText, bool readOnly, params ReadOnlySpan<SpiParameter> parameters)
        => NativeBackend.OpenCursor(commandText, parameters, readOnly);

    /// <summary>
    /// Takes ownership of an existing PostgreSQL cursor by its exact portal name.
    /// </summary>
    /// <param name="name">The portal name, including names returned by SpiCursor.Detach.</param>
    /// <returns>An owned cursor.</returns>
    public static SpiCursor FindCursor(string name) => NativeBackend.FindCursor(name);

    /// <summary>
    /// Prepares a reusable statement with explicitly declared managed parameter types.
    /// The statement survives SPI calls and transaction boundaries until disposed on the owning backend thread.
    /// </summary>
    /// <param name="commandText">The SQL commands to prepare.</param>
    /// <param name="parameterTypes">The declared CLR types of $1, $2, and subsequent parameters.</param>
    /// <returns>A statement that must be disposed from an extension callback on its owning backend.</returns>
    public static SpiPreparedStatement Prepare(string commandText, params ReadOnlySpan<Type> parameterTypes)
        => NativeBackend.Prepare(commandText, parameterTypes);

    /// <summary>
    /// Prepares a reusable statement with explicit PostgreSQL parameter type identities, including named composites and domains.
    /// </summary>
    /// <param name="commandText">The SQL commands to prepare.</param>
    /// <param name="parameterTypeOids">The nonzero catalog type OIDs for $1, $2, and subsequent parameters.</param>
    /// <returns>A statement that must be disposed on its owning backend.</returns>
    public static SpiPreparedStatement PrepareWithTypeOids(string commandText, params ReadOnlySpan<uint> parameterTypeOids)
        => NativeBackend.PrepareWithTypeOids(commandText, parameterTypeOids);

    /// <summary>
    /// Executes SQL and returns the rows processed by its final statement.
    /// The call uses an internal subtransaction: success retains changes in the enclosing transaction;
    /// failure rolls back this call's changes before throwing a managed PostgreSQL error.
    /// </summary>
    /// <param name="commandText">The SQL commands to execute.</param>
    /// <param name="parameters">Typed positional parameters referenced as $1, $2, and so on.</param>
    /// <returns>The number of rows processed by the final statement.</returns>
    /// <exception cref="PgException">PostgreSQL rejected the command.</exception>
    /// <exception cref="InvalidOperationException">The caller is not on the active PostgreSQL backend thread.</exception>
    public static long Execute(string commandText, params ReadOnlySpan<SpiParameter> parameters)
        => NativeBackend.Run(commandText, parameters, readOnly: false, limit: 0, SpiResultMode.None).RowsAffected;

    /// <summary>
    /// Executes SQL and copies all result rows and metadata into managed objects.
    /// </summary>
    /// <param name="commandText">The SQL command.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>The materialized result.</returns>
    public static SpiResult Query(string commandText, params ReadOnlySpan<SpiParameter> parameters)
        => Query(commandText, readOnly: false, limit: 0, parameters);

    /// <summary>
    /// Executes SQL with explicit snapshot mode and a row limit, copying result data into managed memory.
    /// </summary>
    /// <param name="commandText">The SQL command.</param>
    /// <param name="readOnly">Whether PostgreSQL should use read-only SPI execution.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>The materialized result.</returns>
    public static SpiResult Query(string commandText, bool readOnly, int limit, params ReadOnlySpan<SpiParameter> parameters)
        => NativeBackend.Run(commandText, parameters, readOnly, limit, SpiResultMode.All);

    /// <summary>
    /// Selects rows with a read-only snapshot until the transaction becomes writable, then uses fresh writable snapshots.
    /// </summary>
    /// <param name="commandText">The SQL query.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>The materialized rows and metadata.</returns>
    public static SpiResult Select(string commandText, params ReadOnlySpan<SpiParameter> parameters)
        => Select(commandText, limit: 0, parameters);

    /// <summary>
    /// Selects rows using PostgreSQL's transaction state and an explicit row limit.
    /// Writes through other SPI sessions or the caller also select writable snapshots.
    /// </summary>
    /// <param name="commandText">The SQL query.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>The materialized rows and metadata.</returns>
    public static SpiResult Select(string commandText, int limit, params ReadOnlySpan<SpiParameter> parameters)
        => NativeBackend.Run(commandText, parameters, readOnly: null, limit, SpiResultMode.All);

    /// <summary>
    /// Selects owned native values using PostgreSQL's transaction state without requiring managed type mappings.
    /// </summary>
    /// <param name="commandText">The SQL query.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>An owned result to dispose before leaving the backend callback.</returns>
    public static SpiRawResult SelectRaw(string commandText, params ReadOnlySpan<SpiParameter> parameters)
        => SelectRaw(commandText, limit: 0, parameters);

    /// <summary>
    /// Selects owned native values using transaction-aware snapshots and an explicit row limit.
    /// </summary>
    /// <param name="commandText">The SQL query.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>A result whose datums expire on disposal or callback-context cleanup.</returns>
    public static SpiRawResult SelectRaw(string commandText, int limit, params ReadOnlySpan<SpiParameter> parameters)
        => NativeBackend.RunRaw(commandText, parameters, readOnly: null, limit);

    /// <summary>
    /// Copies native result values without requiring a managed mapping for their PostgreSQL types.
    /// </summary>
    /// <param name="commandText">The SQL command.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>A result to dispose before leaving the backend callback.</returns>
    public static SpiRawResult QueryRaw(string commandText, params ReadOnlySpan<SpiParameter> parameters)
        => QueryRaw(commandText, readOnly: false, limit: 0, parameters);

    /// <summary>
    /// Copies raw native results with explicit snapshot mode and row limit.
    /// </summary>
    /// <param name="commandText">The SQL command.</param>
    /// <param name="readOnly">Whether PostgreSQL should use read-only SPI execution.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>An owned result whose datums expire on disposal or callback-context cleanup.</returns>
    public static SpiRawResult QueryRaw(string commandText, bool readOnly, int limit, params ReadOnlySpan<SpiParameter> parameters)
        => NativeBackend.RunRaw(commandText, parameters, readOnly, limit);

    /// <summary>
    /// Reads the first column of the first result row without implicit type conversion.
    /// SQL NULL and an empty result return null for nullable or reference types and reject non-nullable value types.
    /// </summary>
    /// <typeparam name="T">The expected managed result type.</typeparam>
    /// <param name="commandText">The SQL command.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>The scalar result.</returns>
    public static T ExecuteScalar<T>(string commandText, params ReadOnlySpan<SpiParameter> parameters)
    {
        SpiScalarResult result = SpiScalarResult.Run(commandText, parameters, SpiResultMode.Scalar, SpiScalarResult.RequiresRaw<T>());
        return result.Read(static values => values.Get<T>(0, allowMissing: true));
    }

    /// <summary>
    /// Reads the first two columns of the first row without limiting command execution.
    /// SQL NULL or an empty result requires nullable value types or reference types.
    /// </summary>
    /// <typeparam name="TFirst">The first column's managed type.</typeparam>
    /// <typeparam name="TSecond">The second column's managed type.</typeparam>
    /// <param name="commandText">The SQL commands to execute.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>The first two values from the final statement, without implicit type conversions.</returns>
    /// <exception cref="InvalidOperationException">A returned row has fewer than two columns, or SQL NULL is read as a non-nullable value type.</exception>
    /// <exception cref="InvalidCastException">A column cannot be read as its requested managed type.</exception>
    public static (TFirst First, TSecond Second) ExecuteScalars<TFirst, TSecond>(
        string commandText, params ReadOnlySpan<SpiParameter> parameters)
    {
        SpiScalarResult result = SpiScalarResult.Run(commandText, parameters, SpiResultMode.Pair,
            SpiScalarResult.RequiresRaw<TFirst, TSecond>());
        return result.Read(static values => (values.Get<TFirst>(0), values.Get<TSecond>(1)));
    }

    /// <summary>
    /// Reads the first three columns of the first row without limiting command execution.
    /// SQL NULL or an empty result requires nullable value types or reference types.
    /// </summary>
    /// <typeparam name="TFirst">The first column's managed type.</typeparam>
    /// <typeparam name="TSecond">The second column's managed type.</typeparam>
    /// <typeparam name="TThird">The third column's managed type.</typeparam>
    /// <param name="commandText">The SQL commands to execute.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>The first three values from the final statement, without implicit type conversions.</returns>
    /// <exception cref="InvalidOperationException">A returned row has fewer than three columns, or SQL NULL is read as a non-nullable value type.</exception>
    /// <exception cref="InvalidCastException">A column cannot be read as its requested managed type.</exception>
    public static (TFirst First, TSecond Second, TThird Third) ExecuteScalars<TFirst, TSecond, TThird>(
        string commandText, params ReadOnlySpan<SpiParameter> parameters)
    {
        SpiScalarResult result = SpiScalarResult.Run(commandText, parameters, SpiResultMode.Triple,
            SpiScalarResult.RequiresRaw<TFirst, TSecond, TThird>());
        return result.Read(static values => (values.Get<TFirst>(0), values.Get<TSecond>(1), values.Get<TThird>(2)));
    }
}
