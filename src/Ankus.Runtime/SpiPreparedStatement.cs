using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Owns a reusable PostgreSQL SPI plan. Execution and disposal require the owning backend thread.
/// Use a using declaration for local statements; explicitly dispose cached statements when replacing them.
/// Statements created by SpiSession expire with that session unless Keep transfers ownership out of the scope.
/// </summary>
public sealed class SpiPreparedStatement : IDisposable
{
    private readonly nint _backend;
    private readonly uint[] _parameterTypes;
    private int _activeExecutions;
    private SpiSession? _session;

    /// <summary>
    /// Creates managed ownership before the native plan is allocated.
    /// </summary>
    /// <param name="commandText">The prepared command text.</param>
    /// <param name="parameterTypes">The positional PostgreSQL type OIDs.</param>
    /// <param name="backend">The owning native backend binding.</param>
    /// <param name="session">The owning scoped session, or null for a retained plan.</param>
    internal SpiPreparedStatement(string commandText, uint[] parameterTypes, nint backend, SpiSession? session = null)
    {
        CommandText = commandText;
        _parameterTypes = parameterTypes;
        _backend = backend;
        _session = session;
    }

    /// <summary>
    /// Gets the SQL text used to prepare this statement.
    /// </summary>
    public string CommandText { get; }

    /// <summary>
    /// Gets the number of positional parameters required by this statement.
    /// </summary>
    public int ParameterCount => _parameterTypes.Length;

    /// <summary>
    /// Retains this statement beyond its session's lifetime and returns the same explicitly disposable owner.
    /// Calling Keep on an already retained statement is harmless.
    /// </summary>
    /// <returns>This statement, now independent of its original session.</returns>
    public SpiPreparedStatement Keep()
    {
        CheckAccess();
        CheckNotExecuting();
        if (_session is not null)
        {
            nint plan = Handle;
            try
            {
                NativeBackend.KeepPlan(ref plan, _session);
                _session = null;
            }
            finally
            {
                Handle = plan;
            }
        }

        return this;
    }

    /// <summary>
    /// Gets or sets the native plan handle. Zero means no native plan is owned.
    /// </summary>
    internal nint Handle
    {
        get;
        set;
    }

    /// <summary>
    /// Opens a transaction-bound cursor from this plan. The cursor remains valid after the plan is disposed.
    /// Uses a read-only snapshot until the transaction becomes writable, then uses a fresh writable snapshot.
    /// </summary>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>An owned cursor.</returns>
    public SpiCursor OpenCursor(params ReadOnlySpan<SpiParameter> parameters)
        => OpenCursorCore(readOnly: null, parameters);

    /// <summary>
    /// Opens a cursor from this plan with explicit read-only execution mode.
    /// </summary>
    /// <param name="readOnly">Whether PostgreSQL should use read-only execution.</param>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>An owned cursor independent of the prepared statement's lifetime.</returns>
    public SpiCursor OpenCursor(bool readOnly, params ReadOnlySpan<SpiParameter> parameters)
        => OpenCursorCore(readOnly, parameters);

    /// <summary>
    /// Opens a cursor while retaining access, parameter and reentrancy checks for either explicit or transaction-aware snapshots.
    /// </summary>
    /// <param name="readOnly">The explicit snapshot choice, or null to follow PostgreSQL's transaction state.</param>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>An owned cursor independent of the statement's lifetime.</returns>
    private SpiCursor OpenCursorCore(bool? readOnly, ReadOnlySpan<SpiParameter> parameters)
    {
        ValidateParameters(parameters);
        _activeExecutions++;
        try
        {
            return NativeBackend.OpenPlanCursor(Handle, parameters, readOnly, _session);
        }
        finally
        {
            _activeExecutions--;
        }
    }

    /// <summary>
    /// Executes the statement and returns the final command's processed-row count.
    /// </summary>
    /// <param name="parameters">Values whose PostgreSQL types must match the declared parameter types.</param>
    /// <returns>The processed-row count.</returns>
    public long Execute(params ReadOnlySpan<SpiParameter> parameters)
        => Run(parameters, readOnly: false, limit: 0, SpiResultMode.None).RowsAffected;

    /// <summary>
    /// Executes the statement and materializes its result rows and metadata.
    /// </summary>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>The managed result.</returns>
    public SpiResult Query(params ReadOnlySpan<SpiParameter> parameters)
        => Query(readOnly: false, limit: 0, parameters);

    /// <summary>
    /// Executes the statement with explicit read-only mode and row limit.
    /// </summary>
    /// <param name="readOnly">Whether PostgreSQL should use read-only SPI execution.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>The managed result.</returns>
    public SpiResult Query(bool readOnly, int limit, params ReadOnlySpan<SpiParameter> parameters)
        => Run(parameters, readOnly, limit, SpiResultMode.All);

    /// <summary>
    /// Selects rows with a read-only snapshot until the transaction becomes writable, then uses fresh writable snapshots.
    /// </summary>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>The independently owned managed result.</returns>
    public SpiResult Select(params ReadOnlySpan<SpiParameter> parameters)
        => Select(limit: 0, parameters);

    /// <summary>
    /// Selects rows from this plan using PostgreSQL's transaction state and an explicit row limit.
    /// </summary>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>The independently owned managed result.</returns>
    public SpiResult Select(int limit, params ReadOnlySpan<SpiParameter> parameters)
        => Run(parameters, readOnly: null, limit, SpiResultMode.All);

    /// <summary>
    /// Selects native values from this plan using PostgreSQL's transaction state without requiring managed type mappings.
    /// </summary>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>An owned result that survives this plan and expires on disposal or callback-context cleanup.</returns>
    public SpiRawResult SelectRaw(params ReadOnlySpan<SpiParameter> parameters)
        => SelectRaw(limit: 0, parameters);

    /// <summary>
    /// Selects native values from this plan using transaction-aware snapshots and an explicit row limit.
    /// </summary>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>An owned result to dispose before leaving the backend callback.</returns>
    public SpiRawResult SelectRaw(int limit, params ReadOnlySpan<SpiParameter> parameters)
        => RunRaw(parameters, readOnly: null, limit);

    /// <summary>
    /// Copies native result values without requiring a managed mapping for their PostgreSQL types.
    /// </summary>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>An owned result that survives this plan and expires on disposal or callback-context cleanup.</returns>
    public SpiRawResult QueryRaw(params ReadOnlySpan<SpiParameter> parameters)
        => QueryRaw(readOnly: false, limit: 0, parameters);

    /// <summary>
    /// Copies raw native results with explicit snapshot mode and row limit.
    /// </summary>
    /// <param name="readOnly">Whether PostgreSQL should use read-only SPI execution.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>A result to dispose before leaving the backend callback.</returns>
    public SpiRawResult QueryRaw(bool readOnly, int limit, params ReadOnlySpan<SpiParameter> parameters)
        => RunRaw(parameters, readOnly, limit);

    /// <summary>
    /// Executes native result transport with the plan's parameter, ownership and reentrancy checks intact.
    /// </summary>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <param name="readOnly">The explicit snapshot choice, or null to follow PostgreSQL's transaction state.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <returns>The owned native result.</returns>
    private SpiRawResult RunRaw(ReadOnlySpan<SpiParameter> parameters, bool? readOnly, int limit)
    {
        ValidateParameters(parameters);
        _activeExecutions++;
        try
        {
            return NativeBackend.RunRawPlan(Handle, parameters, readOnly, limit, _session);
        }
        finally
        {
            _activeExecutions--;
        }
    }

    /// <summary>
    /// Reads the first cell without limiting command execution or applying implicit type conversions.
    /// SQL NULL or an absent cell requires a nullable value type or reference type.
    /// </summary>
    /// <typeparam name="T">The expected managed result type.</typeparam>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>The scalar value.</returns>
    public T ExecuteScalar<[PgConvertible] T>(params ReadOnlySpan<SpiParameter> parameters)
    {
        SpiScalarResult result = RunScalars(parameters, SpiResultMode.Scalar, SpiScalarResult.RequiresRaw<T>());
        return result.Read(static values => values.Get<T>(0, allowMissing: true));
    }

    /// <summary>
    /// Reads the first two columns of the first row without limiting command execution.
    /// SQL NULL or an empty result requires nullable value types or reference types.
    /// </summary>
    /// <typeparam name="TFirst">The first column's managed type.</typeparam>
    /// <typeparam name="TSecond">The second column's managed type.</typeparam>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>The first two values from the final statement, without implicit type conversions.</returns>
    /// <exception cref="InvalidOperationException">A returned row has fewer than two columns, or SQL NULL is read as a non-nullable value type.</exception>
    /// <exception cref="InvalidCastException">A column cannot be read as its requested managed type.</exception>
    public (TFirst First, TSecond Second) ExecuteScalars<[PgConvertible] TFirst, [PgConvertible] TSecond>(
        params ReadOnlySpan<SpiParameter> parameters)
    {
        SpiScalarResult result = RunScalars(parameters, SpiResultMode.Pair, SpiScalarResult.RequiresRaw<TFirst, TSecond>());
        return result.Read(static values => (values.Get<TFirst>(0), values.Get<TSecond>(1)));
    }

    /// <summary>
    /// Reads the first three columns of the first row without limiting command execution.
    /// SQL NULL or an empty result requires nullable value types or reference types.
    /// </summary>
    /// <typeparam name="TFirst">The first column's managed type.</typeparam>
    /// <typeparam name="TSecond">The second column's managed type.</typeparam>
    /// <typeparam name="TThird">The third column's managed type.</typeparam>
    /// <param name="parameters">Values matching the declared parameter types.</param>
    /// <returns>The first three values from the final statement, without implicit type conversions.</returns>
    /// <exception cref="InvalidOperationException">A returned row has fewer than three columns, or SQL NULL is read as a non-nullable value type.</exception>
    /// <exception cref="InvalidCastException">A column cannot be read as its requested managed type.</exception>
    public (TFirst First, TSecond Second, TThird Third) ExecuteScalars<[PgConvertible] TFirst, [PgConvertible] TSecond,
        [PgConvertible] TThird>(params ReadOnlySpan<SpiParameter> parameters)
    {
        SpiScalarResult result = RunScalars(parameters, SpiResultMode.Triple,
            SpiScalarResult.RequiresRaw<TFirst, TSecond, TThird>());
        return result.Read(static values => (values.Get<TFirst>(0), values.Get<TSecond>(1), values.Get<TThird>(2)));
    }

    /// <summary>
    /// Frees the native plan through a guarded backend call. Repeated disposal is harmless.
    /// PostgreSQL APIs cannot run on the finalizer thread. Independent plans require explicit disposal;
    /// session-owned plans are also released when their session ends.
    /// </summary>
    public void Dispose()
    {
        if (Handle == 0)
        {
            return;
        }

        if (_session is { Identity: 0 })
        {
            Handle = 0;
            return;
        }

        NativeBackend.CheckDisposalAccess(_backend);
        _session?.CheckAccess();
        CheckNotExecuting();
        nint plan = Handle;
        try
        {
            NativeBackend.FreePlan(ref plan, _session);
        }
        finally
        {
            Handle = plan;
        }
    }

    private SpiResult Run(ReadOnlySpan<SpiParameter> parameters, bool? readOnly, int limit, SpiResultMode resultMode)
    {
        ValidateParameters(parameters);
        _activeExecutions++;
        try
        {
            return NativeBackend.RunPlan(Handle, parameters, readOnly, limit, resultMode, _session);
        }
        finally
        {
            _activeExecutions--;
        }
    }

    /// <summary>
    /// Preserves plan access and reentrancy checks while choosing native or managed scalar storage.
    /// </summary>
    /// <param name="parameters">The bound values.</param>
    /// <param name="mode">The selected first-row columns.</param>
    /// <param name="polymorphic">Whether a requested column requires its actual native identity.</param>
    /// <returns>The temporary scalar conversion owner.</returns>
    private SpiScalarResult RunScalars(ReadOnlySpan<SpiParameter> parameters, SpiResultMode mode, bool polymorphic)
    {
        ValidateParameters(parameters);
        _activeExecutions++;
        try
        {
            return polymorphic
                ? new(null, NativeBackend.RunRawPlan(Handle, parameters, false, 0, _session, mode))
                : new(NativeBackend.RunPlan(Handle, parameters, false, 0, mode, _session), null);
        }
        finally
        {
            _activeExecutions--;
        }
    }

    private void ValidateParameters(ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        if (parameters.Length != _parameterTypes.Length)
        {
            throw new ArgumentException($"The statement requires {_parameterTypes.Length} parameters, but received {parameters.Length}.",
                nameof(parameters));
        }

        for (int index = 0; index < parameters.Length; index++)
        {
            if (parameters[index].TypeOid != _parameterTypes[index])
            {
                throw new ArgumentException($"Parameter {index + 1} must have PostgreSQL type OID {_parameterTypes[index]}.",
                    nameof(parameters));
            }
        }
    }

    private void CheckAccess()
    {
        ObjectDisposedException.ThrowIf(Handle == 0 || _session is { Identity: 0 }, this);
        NativeBackend.CheckAccess(_backend);
        _session?.CheckAccess();
    }

    private void CheckNotExecuting()
    {
        if (_activeExecutions != 0)
        {
            throw new InvalidOperationException("A prepared statement cannot be disposed or retained while it is executing.");
        }
    }
}
