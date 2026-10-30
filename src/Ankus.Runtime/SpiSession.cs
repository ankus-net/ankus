using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Provides a scoped SPI connection during a synchronous Spi.Connect callback.
/// Operations require the owning backend callback and the innermost active session.
/// </summary>
public sealed partial class SpiSession
{
    private readonly nint _backend;
    private readonly int _callbackDepth;

    /// <summary>
    /// Creates a session owner before the native connection is opened.
    /// </summary>
    /// <param name="backend">The native binding.</param>
    /// <param name="callbackDepth">The owning managed dispatcher depth.</param>
    internal SpiSession(nint backend, int callbackDepth)
    {
        _backend = backend;
        _callbackDepth = callbackDepth;
    }

    /// <summary>
    /// Gets or sets the native session identity. Zero denotes an inactive scope.
    /// </summary>
    internal long Identity
    {
        get;
        set;
    }

    /// <summary>
    /// Plans one SQL statement within this session and returns its owned JSON EXPLAIN output.
    /// </summary>
    /// <param name="commandText">The SQL statement.</param>
    /// <param name="parameters">The typed positional parameters.</param>
    /// <returns>The JSON query plan.</returns>
    public PgJson Explain(string commandText, params ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        return NativeBackend.Explain(commandText, parameters, this);
    }

    /// <summary>
    /// Executes commands within this session and returns the final command's processed-row count.
    /// </summary>
    /// <param name="commandText">The SQL command text.</param>
    /// <param name="parameters">The typed positional parameters.</param>
    /// <returns>The processed-row count.</returns>
    /// <remarks>
    /// Like pgrx's writable SPI helpers, the call first assigns the transaction an ID, even for a <c>SELECT</c>. A hot
    /// standby can't assign one, and a later <c>DROP INDEX CONCURRENTLY</c> in the same transaction fails, as it needs a
    /// transaction without one. <c>Select</c> reads without assigning an ID.
    /// </remarks>
    public long Execute(string commandText, params ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        return NativeBackend.Run(commandText, parameters, readOnly: false, limit: 0, SpiResultMode.None, this).RowsAffected;
    }

    /// <summary>
    /// Executes a query and returns owned rows and metadata that remain valid after the session ends.
    /// </summary>
    /// <param name="commandText">The SQL query.</param>
    /// <param name="parameters">The typed positional parameters.</param>
    /// <returns>The independently owned managed result.</returns>
    /// <remarks>
    /// Like pgrx's writable SPI helpers, the call first assigns the transaction an ID, even for a <c>SELECT</c>. A hot
    /// standby can't assign one, and a later <c>DROP INDEX CONCURRENTLY</c> in the same transaction fails, as it needs a
    /// transaction without one. <c>Select</c> reads without assigning an ID.
    /// </remarks>
    public SpiResult Query(string commandText, params ReadOnlySpan<SpiParameter> parameters)
        => Query(commandText, readOnly: false, limit: 0, parameters);

    /// <summary>
    /// Executes a query with explicit read-only mode and row limit.
    /// </summary>
    /// <param name="commandText">The SQL query.</param>
    /// <param name="readOnly">Whether to use read-only SPI execution; false first assigns the transaction an ID.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="parameters">The typed positional parameters.</param>
    /// <returns>The independently owned managed result.</returns>
    public SpiResult Query(string commandText, bool readOnly, int limit, params ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        return NativeBackend.Run(commandText, parameters, readOnly, limit, SpiResultMode.All, this);
    }

    /// <summary>
    /// Selects rows with a read-only snapshot until the transaction becomes writable, then uses fresh writable snapshots.
    /// </summary>
    /// <param name="commandText">The SQL query.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>Owned rows and metadata that remain valid after the session ends.</returns>
    public SpiResult Select(string commandText, params ReadOnlySpan<SpiParameter> parameters)
        => Select(commandText, limit: 0, parameters);

    /// <summary>
    /// Selects rows using PostgreSQL's transaction state and an explicit row limit.
    /// </summary>
    /// <param name="commandText">The SQL query.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>The independently owned managed result.</returns>
    public SpiResult Select(string commandText, int limit, params ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        return NativeBackend.Run(commandText, parameters, readOnly: null, limit, SpiResultMode.All, this);
    }

    /// <summary>
    /// Selects native values using PostgreSQL's transaction state without requiring managed type mappings.
    /// </summary>
    /// <param name="commandText">The SQL query.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>An owned result that survives this session and expires on disposal or callback-context cleanup.</returns>
    public SpiRawResult SelectRaw(string commandText, params ReadOnlySpan<SpiParameter> parameters)
        => SelectRaw(commandText, limit: 0, parameters);

    /// <summary>
    /// Selects native values using transaction-aware snapshots and an explicit row limit.
    /// </summary>
    /// <param name="commandText">The SQL query.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>An owned result to dispose before leaving the backend callback.</returns>
    public SpiRawResult SelectRaw(string commandText, int limit, params ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        return NativeBackend.RunRaw(commandText, parameters, readOnly: null, limit, this);
    }

    /// <summary>
    /// Copies native result values without requiring a managed mapping for their PostgreSQL types.
    /// </summary>
    /// <param name="commandText">The SQL command.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>An owned result that survives this session and expires on disposal or callback-context cleanup.</returns>
    /// <remarks>
    /// Like pgrx's writable SPI helpers, the call first assigns the transaction an ID, even for a <c>SELECT</c>. A hot
    /// standby can't assign one, and a later <c>DROP INDEX CONCURRENTLY</c> in the same transaction fails, as it needs a
    /// transaction without one. <c>Select</c> reads without assigning an ID.
    /// </remarks>
    public SpiRawResult QueryRaw(string commandText, params ReadOnlySpan<SpiParameter> parameters)
        => QueryRaw(commandText, readOnly: false, limit: 0, parameters);

    /// <summary>
    /// Copies raw native results with explicit snapshot mode and row limit.
    /// </summary>
    /// <param name="commandText">The SQL command.</param>
    /// <param name="readOnly">Whether to use read-only SPI execution; false first assigns the transaction an ID.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="parameters">Typed positional parameters.</param>
    /// <returns>A result to dispose before leaving the backend callback.</returns>
    public SpiRawResult QueryRaw(string commandText, bool readOnly, int limit, params ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        return NativeBackend.RunRaw(commandText, parameters, readOnly, limit, this);
    }

    /// <summary>
    /// Reads the first result cell without limiting a command's write effects.
    /// </summary>
    /// <typeparam name="T">The expected managed result type.</typeparam>
    /// <param name="commandText">The SQL command.</param>
    /// <param name="parameters">The typed positional parameters.</param>
    /// <returns>The scalar value.</returns>
    /// <remarks>
    /// Like pgrx's writable SPI helpers, the call first assigns the transaction an ID, even for a <c>SELECT</c>. A hot
    /// standby can't assign one, and a later <c>DROP INDEX CONCURRENTLY</c> in the same transaction fails, as it needs a
    /// transaction without one. <c>Select</c> reads without assigning an ID.
    /// </remarks>
    public T ExecuteScalar<[PgConvertible] T>(string commandText, params ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        SpiScalarResult result = SpiScalarResult.Run(commandText, parameters, SpiResultMode.Scalar, SpiScalarResult.RequiresRaw<T>(), this);
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
    /// <remarks>
    /// Like pgrx's writable SPI helpers, the call first assigns the transaction an ID, even for a <c>SELECT</c>. A hot
    /// standby can't assign one, and a later <c>DROP INDEX CONCURRENTLY</c> in the same transaction fails, as it needs a
    /// transaction without one. <c>Select</c> reads without assigning an ID.
    /// </remarks>
    public (TFirst First, TSecond Second) ExecuteScalars<[PgConvertible] TFirst, [PgConvertible] TSecond>(
        string commandText, params ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        SpiScalarResult result = SpiScalarResult.Run(commandText, parameters, SpiResultMode.Pair,
            SpiScalarResult.RequiresRaw<TFirst, TSecond>(), this);
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
    /// <remarks>
    /// Like pgrx's writable SPI helpers, the call first assigns the transaction an ID, even for a <c>SELECT</c>. A hot
    /// standby can't assign one, and a later <c>DROP INDEX CONCURRENTLY</c> in the same transaction fails, as it needs a
    /// transaction without one. <c>Select</c> reads without assigning an ID.
    /// </remarks>
    public (TFirst First, TSecond Second, TThird Third) ExecuteScalars<[PgConvertible] TFirst, [PgConvertible] TSecond,
        [PgConvertible] TThird>(string commandText, params ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        SpiScalarResult result = SpiScalarResult.Run(commandText, parameters, SpiResultMode.Triple,
            SpiScalarResult.RequiresRaw<TFirst, TSecond, TThird>(), this);
        return result.Read(static values => (values.Get<TFirst>(0), values.Get<TSecond>(1), values.Get<TThird>(2)));
    }

    /// <summary>
    /// Prepares a statement owned by this session. Call Keep on the statement to retain it beyond the callback.
    /// </summary>
    /// <param name="commandText">The SQL command text.</param>
    /// <param name="parameterTypes">The declared CLR parameter types.</param>
    /// <returns>A session-bound prepared statement.</returns>
    public SpiPreparedStatement Prepare(string commandText, [PgConvertible] params ReadOnlySpan<Type> parameterTypes)
    {
        CheckAccess();
        return NativeBackend.Prepare(commandText, parameterTypes, this);
    }

    /// <summary>
    /// Prepares a session-owned statement with explicit PostgreSQL parameter identities, including named composites and domains.
    /// </summary>
    /// <param name="commandText">The SQL command text.</param>
    /// <param name="parameterTypeOids">The nonzero catalog type OIDs for positional parameters.</param>
    /// <returns>A session-bound statement; Keep transfers it to independent ownership.</returns>
    public SpiPreparedStatement PrepareWithTypeOids(string commandText, params ReadOnlySpan<uint> parameterTypeOids)
    {
        CheckAccess();
        return NativeBackend.PrepareWithTypeOids(commandText, parameterTypeOids, this);
    }

    /// <summary>
    /// Opens a transaction-bound cursor whose portal remains valid after this session ends.
    /// Uses a read-only snapshot until the transaction becomes writable, then uses a fresh writable snapshot.
    /// </summary>
    /// <param name="commandText">The SQL query.</param>
    /// <param name="parameters">The typed positional parameters.</param>
    /// <returns>An owned cursor.</returns>
    public SpiCursor OpenCursor(string commandText, params ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        return NativeBackend.OpenCursor(commandText, parameters, readOnly: null, this);
    }

    /// <summary>
    /// Opens a cursor with explicit read-only execution mode.
    /// </summary>
    /// <param name="commandText">The SQL query.</param>
    /// <param name="readOnly">Whether to use read-only SPI execution; false first assigns the transaction an ID.</param>
    /// <param name="parameters">The typed positional parameters.</param>
    /// <returns>An owned transaction-bound cursor.</returns>
    public SpiCursor OpenCursor(string commandText, bool readOnly, params ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        return NativeBackend.OpenCursor(commandText, parameters, readOnly, this);
    }

    /// <summary>
    /// Rejects expired, worker-thread, nested-session, and reentrant-callback access before entering native code.
    /// </summary>
    internal void CheckAccess()
    {
        ObjectDisposedException.ThrowIf(Identity == 0, this);
        NativeBackend.CheckAccess(_backend);
        NativeBackend.CheckSession(this, _callbackDepth);
    }
}
