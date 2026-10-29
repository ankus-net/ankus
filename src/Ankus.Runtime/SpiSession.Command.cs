namespace Ankus;

public sealed partial class SpiSession
{
    /// <summary>
    /// Executes a parameterized command on this active scoped session.
    /// </summary>
    /// <param name="command">SQL and its declared bindings.</param>
    /// <returns>The final statement's processed row count.</returns>
    public long Execute(SpiCommand command) => Execute(command.CommandText, command.Parameters);

    /// <summary>
    /// Executes a parameterized command with explicit snapshot mode and a row limit.
    /// </summary>
    /// <param name="command">SQL and its declared bindings.</param>
    /// <param name="readOnly">Whether to enforce read-only SPI execution.</param>
    /// <param name="limit">The maximum returned rows, or zero for unlimited.</param>
    /// <returns>Owned managed rows and column metadata.</returns>
    public SpiResult Query(SpiCommand command, bool readOnly = false, int limit = 0)
        => Query(command.CommandText, readOnly, limit, command.Parameters);

    /// <summary>
    /// Selects a parameterized command using PostgreSQL's transaction-aware snapshot policy.
    /// </summary>
    /// <param name="command">SQL and its declared bindings.</param>
    /// <param name="limit">The maximum returned rows, or zero for unlimited.</param>
    /// <returns>Owned managed rows and column metadata.</returns>
    public SpiResult Select(SpiCommand command, int limit = 0)
        => Select(command.CommandText, limit, command.Parameters);

    /// <summary>
    /// Selects parameterized native values using PostgreSQL's transaction-aware snapshot policy.
    /// </summary>
    /// <param name="command">SQL and its declared bindings.</param>
    /// <param name="limit">The maximum returned rows, or zero for unlimited.</param>
    /// <returns>An owned native result to dispose before leaving the backend callback.</returns>
    public SpiRawResult SelectRaw(SpiCommand command, int limit = 0)
        => SelectRaw(command.CommandText, limit, command.Parameters);

    /// <summary>
    /// Executes a parameterized command with owned native results and explicit snapshot mode.
    /// </summary>
    /// <param name="command">SQL and its declared bindings.</param>
    /// <param name="readOnly">Whether to enforce read-only SPI execution.</param>
    /// <param name="limit">The maximum returned rows, or zero for unlimited.</param>
    /// <returns>An owned native result to dispose before leaving the backend callback.</returns>
    public SpiRawResult QueryRaw(SpiCommand command, bool readOnly = false, int limit = 0)
        => QueryRaw(command.CommandText, readOnly, limit, command.Parameters);

    /// <summary>
    /// Reads the first parameterized result cell without implicit conversion or limiting command effects.
    /// </summary>
    /// <typeparam name="T">The expected managed result type.</typeparam>
    /// <param name="command">SQL and its declared bindings.</param>
    /// <returns>The scalar value with the existing SQL NULL and empty-result rules.</returns>
    public T ExecuteScalar<[PgConvertible] T>(SpiCommand command) => ExecuteScalar<T>(command.CommandText, command.Parameters);

    /// <summary>
    /// Reads two parameterized result cells without implicit conversion or limiting command effects.
    /// </summary>
    /// <typeparam name="TFirst">The first column's managed type.</typeparam>
    /// <typeparam name="TSecond">The second column's managed type.</typeparam>
    /// <param name="command">SQL and its declared bindings.</param>
    /// <returns>The first row's two values with the existing SQL NULL and empty-result rules.</returns>
    public (TFirst First, TSecond Second) ExecuteScalars<[PgConvertible] TFirst, [PgConvertible] TSecond>(SpiCommand command)
        => ExecuteScalars<TFirst, TSecond>(command.CommandText, command.Parameters);

    /// <summary>
    /// Reads three parameterized result cells without implicit conversion or limiting command effects.
    /// </summary>
    /// <typeparam name="TFirst">The first column's managed type.</typeparam>
    /// <typeparam name="TSecond">The second column's managed type.</typeparam>
    /// <typeparam name="TThird">The third column's managed type.</typeparam>
    /// <param name="command">SQL and its declared bindings.</param>
    /// <returns>The first row's three values with the existing SQL NULL and empty-result rules.</returns>
    public (TFirst First, TSecond Second, TThird Third) ExecuteScalars<[PgConvertible] TFirst, [PgConvertible] TSecond,
        [PgConvertible] TThird>(SpiCommand command)
        => ExecuteScalars<TFirst, TSecond, TThird>(command.CommandText, command.Parameters);

    /// <summary>
    /// Plans a parameterized statement without executing EXPLAIN ANALYZE.
    /// </summary>
    /// <param name="command">One SQL statement and its declared bindings.</param>
    /// <returns>The owned JSON query plan.</returns>
    public PgJson Explain(SpiCommand command) => Explain(command.CommandText, command.Parameters);

    /// <summary>
    /// Opens a parameterized cursor using PostgreSQL's transaction-aware snapshot policy.
    /// </summary>
    /// <param name="command">One row-producing statement and its declared bindings.</param>
    /// <returns>An owned transaction-bound cursor.</returns>
    public SpiCursor OpenCursor(SpiCommand command) => OpenCursor(command.CommandText, command.Parameters);

    /// <summary>
    /// Opens a parameterized cursor with explicit read-only SPI execution mode.
    /// </summary>
    /// <param name="command">One row-producing statement and its declared bindings.</param>
    /// <param name="readOnly">Whether to enforce read-only SPI execution.</param>
    /// <returns>An owned transaction-bound cursor.</returns>
    public SpiCursor OpenCursor(SpiCommand command, bool readOnly)
        => OpenCursor(command.CommandText, readOnly, command.Parameters);
}
