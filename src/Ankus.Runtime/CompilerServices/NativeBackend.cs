using System.ComponentModel;
using System.Text;

namespace Ankus.CompilerServices;

/// <summary>
/// Binds guarded PostgreSQL entry points to the current backend thread for the duration of generated managed dispatch.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe partial class NativeBackend
{
    [ThreadStatic]
    private static nint s_execute;

    [ThreadStatic]
    private static int s_callbackDepth;

    [ThreadStatic]
    private static int s_abortCleanupDepth;

    [ThreadStatic]
    private static SpiSession? s_session;

    /// <summary>
    /// Gets the stable guarded entry point for owned resource release from a later memory cleanup callback.
    /// </summary>
    internal static nint CleanupBinding => s_execute;

    /// <summary>
    /// Gets whether the current thread is the backend thread inside a native callback scope, where PostgreSQL calls are permitted.
    /// </summary>
    internal static bool InCallback => s_callbackDepth > 0;

    /// <summary>
    /// Enters a native callback scope, preserving the previous binding for recursive SPI calls.
    /// </summary>
    /// <param name="execute">The native guarded SPI entry point.</param>
    /// <param name="abortCleanup">Whether this scope may only release resources during query abort.</param>
    /// <returns>The previous callback binding, restored by generated code in a finally block.</returns>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// Use the matching dispatcher's live binding and restore the exact previous entry in reverse order.
    /// </remarks>
    [NativeUnsafeAccess]
    public static nint Enter(nint execute, bool abortCleanup = false)
    {
        nint previous = s_execute;
        s_execute = execute;
        s_callbackDepth++;
        if (abortCleanup)
        {
            s_abortCleanupDepth++;
        }

        return previous;
    }

    /// <summary>
    /// Restores the enclosing native callback scope.
    /// </summary>
    /// <param name="previous">The binding saved on entry.</param>
    /// <param name="abortCleanup">Whether the matching entry established an abort-cleanup scope.</param>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// Use the matching dispatcher's live binding and restore the exact previous entry in reverse order.
    /// </remarks>
    [NativeUnsafeAccess]
    public static void Exit(nint previous, bool abortCleanup = false)
    {
        s_execute = previous;
        s_callbackDepth--;
        if (abortCleanup)
        {
            s_abortCleanupDepth--;
        }
    }

    /// <summary>
    /// Runs a synchronous callback in one native SPI connection, closing it on every managed exit path.
    /// </summary>
    /// <typeparam name="TResult">The callback result type.</typeparam>
    /// <param name="action">The synchronous session callback.</param>
    /// <returns>The managed callback result.</returns>
    internal static TResult Connect<TResult>(Func<SpiSession, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        CheckAccess();
        var session = new SpiSession(s_execute, s_callbackDepth);
        SpiSession? previous = s_session;
        var request = new NativeSpiRequest { _operation = SpiOperation.OpenSession };
        NativeSpiResult result = default;
        Invoke(&request, &result);
        session.Identity = request._sessionId;
        s_session = session;
        try
        {
            return action(session);
        }
        finally
        {
            try
            {
                // A raw error may leave deeper PostgreSQL SPI frames on this stack.
                // The enclosing recovery scope will abort and reclaim every owned
                // frame; SPI_finish here could close a different connection.
                if (!NativeSubtransaction.HasFailure)
                {
                    request._operation = SpiOperation.CloseSession;
                    Invoke(&request, &result);
                }
            }
            finally
            {
                session.Identity = 0;
                s_session = previous;
            }
        }
    }

    /// <summary>
    /// Verifies native SPI stack ownership for session-bound operations.
    /// </summary>
    /// <param name="session">The owning session.</param>
    /// <param name="callbackDepth">The session's original dispatcher depth.</param>
    internal static void CheckSession(SpiSession session, int callbackDepth)
    {
        if (s_session != session || s_callbackDepth != callbackDepth)
        {
            throw new InvalidOperationException("SPI sessions require their owning callback and innermost active session scope.");
        }
    }

    /// <summary>
    /// Executes SQL through the native guard and copies requested result data before releasing native allocations.
    /// </summary>
    /// <param name="commandText">The SQL command text.</param>
    /// <param name="parameters">The positional parameters.</param>
    /// <param name="readOnly">The explicit snapshot choice, or null to follow PostgreSQL's transaction state.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="resultMode">The result materialization mode.</param>
    /// <param name="session">The scoped connection, or null for an independent operation.</param>
    /// <returns>The managed query result.</returns>
    internal static SpiResult Run(
        string commandText, ReadOnlySpan<SpiParameter> parameters, bool? readOnly, int limit, SpiResultMode resultMode,
        SpiSession? session = null)
    {
        CheckAccess();
        byte[] sql = EncodeCommand(commandText);
        fixed (byte* text = sql)
        {
            var request = new NativeSpiRequest
            {
                _command = text,
                _commandLength = sql.Length - 1,
                _readOnly = NativeSpiRequest.GetReadMode(readOnly),
                _limit = limit,
                _resultMode = resultMode,
                _sessionId = session?.Identity ?? 0,
            };
            return RunRequest(request, parameters);
        }
    }

    /// <summary>
    /// Creates a retained native plan after validating its command and managed parameter type identities.
    /// </summary>
    /// <param name="commandText">The SQL text to prepare.</param>
    /// <param name="parameterTypes">The declared CLR parameter types.</param>
    /// <param name="session">The owning session, or null to create an independently retained plan.</param>
    /// <returns>The owned prepared statement.</returns>
    internal static SpiPreparedStatement Prepare(string commandText, ReadOnlySpan<Type> parameterTypes, SpiSession? session = null)
    {
        CheckAccess();
        uint[] types = new uint[parameterTypes.Length];
        for (int index = 0; index < types.Length; index++)
        {
            types[index] = SpiType.GetOid(parameterTypes[index]);
        }

        return PrepareWithTypeOids(commandText, types, session);
    }

    /// <summary>
    /// Prepares a retained or session-bound statement with explicit catalog parameter identities.
    /// </summary>
    /// <param name="commandText">The SQL text.</param>
    /// <param name="parameterTypeOids">The nonzero PostgreSQL type OIDs.</param>
    /// <param name="session">The owning session, or null for independent ownership.</param>
    /// <returns>The owned prepared statement.</returns>
    internal static SpiPreparedStatement PrepareWithTypeOids(string commandText, ReadOnlySpan<uint> parameterTypeOids, SpiSession? session = null)
    {
        CheckAccess();
        byte[] sql = EncodeCommand(commandText);
        uint[] types = [.. parameterTypeOids];
        var arguments = new NativeSpiParameter[types.Length];
        for (int index = 0; index < types.Length; index++)
        {
            if (types[index] == 0)
            {
                throw new ArgumentException("Prepared parameter type OIDs must be nonzero.", nameof(parameterTypeOids));
            }

            arguments[index]._typeOid = types[index];
        }

        var statement = new SpiPreparedStatement(commandText, types, s_execute, session);
        fixed (byte* text = sql)
        fixed (NativeSpiParameter* values = arguments)
        {
            var request = new NativeSpiRequest
            {
                _operation = SpiOperation.Prepare,
                _command = text,
                _commandLength = sql.Length - 1,
                _parameters = values,
                _parameterCount = arguments.Length,
                _sessionId = session?.Identity ?? 0,
            };
            NativeSpiResult result = default;
            Invoke(&request, &result);
            statement.Handle = request._plan;
        }

        return statement;
    }

    /// <summary>
    /// Verifies that PostgreSQL is accessible through the active callback and optional owning binding.
    /// </summary>
    /// <param name="owner">The required native binding, or zero to accept any active binding.</param>
    internal static void CheckAccess(nint owner = 0)
    {
        NativeBorrowScope.CheckBackendAccess();
        CheckCallbackAccess(owner);
    }

    /// <summary>
    /// Checks the active callback without entering PostgreSQL, including managed diagnostic unwinding.
    /// </summary>
    internal static void CheckCallbackAccess(nint owner = 0)
    {
        NativeSubtransaction.CheckAccess();
        CheckDisposalAccess(owner);
        if (s_abortCleanupDepth != 0)
        {
            throw new InvalidOperationException("PostgreSQL queries are unavailable during aborted iterator cleanup.");
        }
    }

    /// <summary>
    /// Verifies the owning thread and binding while permitting resource release during abort cleanup.
    /// </summary>
    /// <param name="owner">The required native binding, or zero for any active binding.</param>
    internal static void CheckDisposalAccess(nint owner = 0)
    {
        if (s_execute == 0 || (owner != 0 && s_execute != owner))
        {
            throw new InvalidOperationException("PostgreSQL APIs can only be used on the active PostgreSQL backend thread.");
        }
    }

    /// <summary>
    /// Installs the process-wide native dispatchers used by managed transaction registrations.
    /// </summary>
    /// <param name="callback">The stable managed callback entry point.</param>
    /// <param name="dispatchers">One for outer transactions or two for subtransactions.</param>
    internal static void RegisterTransactionCallbacks(nint callback, int dispatchers)
    {
        CheckAccess();
        var request = new NativeSpiRequest
        {
            _operation = SpiOperation.TransactionCallbacks,
            _callback = callback,
            _scalarOperation = dispatchers,
        };
        NativeSpiResult result = default;
        Invoke(&request, &result);
    }

    /// <summary>
    /// Reads PostgreSQL's next 64-bit transaction ID without assigning one to the current transaction.
    /// </summary>
    /// <returns>The next full transaction ID.</returns>
    internal static ulong ReadNextFullTransactionId()
    {
        CheckAccess();
        var request = new NativeSpiRequest { _operation = SpiOperation.TransactionId };
        NativeSpiResult result = default;
        Invoke(&request, &result);
        return unchecked((ulong)result._rowsAffected);
    }

    /// <summary>
    /// Executes a retained plan through the common guarded parameter/result path.
    /// </summary>
    /// <param name="plan">The owned native plan.</param>
    /// <param name="parameters">The positional values.</param>
    /// <param name="readOnly">The explicit snapshot choice, or null to follow PostgreSQL's transaction state.</param>
    /// <param name="limit">The maximum returned rows, or zero for no limit.</param>
    /// <param name="resultMode">The materialization mode.</param>
    /// <param name="session">The plan's owning session, or null for a retained plan.</param>
    /// <returns>The managed result.</returns>
    internal static SpiResult RunPlan(
        nint plan, ReadOnlySpan<SpiParameter> parameters, bool? readOnly, int limit, SpiResultMode resultMode, SpiSession? session = null)
        => RunRequest(new NativeSpiRequest
        {
            _operation = SpiOperation.ExecutePlan,
            _plan = plan,
            _readOnly = NativeSpiRequest.GetReadMode(readOnly),
            _limit = limit,
            _resultMode = resultMode,
            _sessionId = session?.Identity ?? 0,
        }, parameters);

    /// <summary>
    /// Frees a plan and clears the handle when the native operation consumes it, including on subsequent errors.
    /// </summary>
    /// <param name="plan">The owned handle, updated to reflect native ownership.</param>
    /// <param name="session">The plan's owning session, or null for a retained plan.</param>
    internal static void FreePlan(ref nint plan, SpiSession? session = null)
    {
        var request = new NativeSpiRequest
        {
            _operation = SpiOperation.FreePlan,
            _plan = plan,
            _sessionId = session?.Identity ?? 0,
        };
        NativeSpiResult result = default;
        try
        {
            Invoke(&request, &result);
        }
        finally
        {
            plan = request._plan;
        }
    }

    /// <summary>
    /// Transfers a session-bound saved plan from native session cleanup to the managed statement owner.
    /// </summary>
    /// <param name="plan">The plan handle, cleared if native recovery frees a partially retained plan.</param>
    /// <param name="session">The current owning session.</param>
    internal static void KeepPlan(ref nint plan, SpiSession session)
    {
        var request = new NativeSpiRequest { _operation = SpiOperation.KeepPlan, _plan = plan, _sessionId = session.Identity };
        NativeSpiResult result = default;
        try
        {
            Invoke(&request, &result);
        }
        finally
        {
            plan = request._plan;
        }
    }

    /// <summary>
    /// Opens a cursor from UTF-8 SQL and borrowed typed parameters.
    /// </summary>
    /// <param name="commandText">The SQL command.</param>
    /// <param name="parameters">The bound parameters.</param>
    /// <param name="readOnly">The explicit snapshot choice, or null to follow PostgreSQL's transaction state.</param>
    /// <param name="session">The scoped SPI connection, or null for an independent operation.</param>
    /// <returns>The owned managed cursor.</returns>
    internal static SpiCursor OpenCursor(
        string commandText, ReadOnlySpan<SpiParameter> parameters, bool? readOnly, SpiSession? session = null)
    {
        CheckAccess();
        byte[] sql = EncodeCommand(commandText);
        fixed (byte* text = sql)
        {
            return CreateCursor(new NativeSpiRequest
            {
                _operation = SpiOperation.OpenCursor,
                _command = text,
                _commandLength = sql.Length - 1,
                _readOnly = NativeSpiRequest.GetReadMode(readOnly),
                _sessionId = session?.Identity ?? 0,
            }, parameters);
        }
    }

    /// <summary>
    /// Opens a cursor from a retained plan without transferring plan ownership.
    /// </summary>
    /// <param name="plan">The prepared plan handle.</param>
    /// <param name="parameters">The bound parameters.</param>
    /// <param name="readOnly">The explicit snapshot choice, or null to follow PostgreSQL's transaction state.</param>
    /// <param name="session">The plan's owning session, or null for a retained plan.</param>
    /// <returns>The owned managed cursor.</returns>
    internal static SpiCursor OpenPlanCursor(nint plan, ReadOnlySpan<SpiParameter> parameters, bool? readOnly, SpiSession? session = null)
        => CreateCursor(new NativeSpiRequest
        {
            _operation = SpiOperation.OpenPlanCursor,
            _plan = plan,
            _readOnly = NativeSpiRequest.GetReadMode(readOnly),
            _sessionId = session?.Identity ?? 0,
        }, parameters);

    /// <summary>
    /// Resolves an existing portal by its exact name and returns a managed owner.
    /// </summary>
    /// <param name="name">The PostgreSQL portal name.</param>
    /// <returns>The owned cursor.</returns>
    internal static SpiCursor FindCursor(string name)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A cursor name cannot contain a zero character.", nameof(name));
        }

        byte[] encoded = EncodeUtf8(name);
        fixed (byte* text = encoded)
        {
            return CreateCursor(new NativeSpiRequest
            {
                _operation = SpiOperation.FindCursor,
                _command = text,
                _commandLength = encoded.Length - 1,
            }, []);
        }
    }

    /// <summary>
    /// Fetches and materializes the next cursor batch after native identity validation.
    /// </summary>
    /// <param name="identity">The native cursor identity.</param>
    /// <param name="count">The requested row count.</param>
    /// <param name="forward">Whether to fetch forward rather than backward.</param>
    /// <returns>The managed result batch.</returns>
    internal static SpiResult FetchCursor(long identity, int count, bool forward)
        => RunRequest(new NativeSpiRequest
        {
            _operation = SpiOperation.FetchCursor,
            _cursorId = identity,
            _limit = count,
            _resultMode = SpiResultMode.All,
            _forward = forward ? (byte)1 : (byte)0,
        }, []);

    /// <summary>
    /// Closes a portal if its native identity remains live.
    /// </summary>
    /// <param name="identity">The cursor identity.</param>
    internal static void CloseCursor(long identity)
    {
        CheckDisposalAccess();
        var request = new NativeSpiRequest { _operation = SpiOperation.CloseCursor, _cursorId = identity };
        NativeSpiResult result = default;
        Invoke(&request, &result);
    }

    /// <summary>
    /// Calls PostgreSQL's quoting functions under the native guard and copies the owned text result.
    /// </summary>
    /// <param name="operation">The quoting operation.</param>
    /// <param name="parameters">The individual text arguments.</param>
    /// <returns>The PostgreSQL-quoted SQL fragment.</returns>
    internal static string Quote(SpiOperation operation, params ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        var request = new NativeSpiRequest { _operation = operation };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, parameters, &result);
            return result._text.ReadString();
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    /// <summary>
    /// Explains a single statement after native parser validation, copying its JSON plan out of SPI storage.
    /// </summary>
    /// <param name="commandText">The statement to plan.</param>
    /// <param name="parameters">The positional parameters.</param>
    /// <param name="session">The scoped connection, or null for independent execution.</param>
    /// <returns>The owned JSON plan.</returns>
    internal static PgJson Explain(string commandText, ReadOnlySpan<SpiParameter> parameters, SpiSession? session = null)
    {
        CheckAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        byte[] sql = EncodeCommand("EXPLAIN (FORMAT JSON) " + commandText);
        fixed (byte* text = sql)
        {
            var request = new NativeSpiRequest
            {
                _operation = SpiOperation.Explain,
                _command = text,
                _commandLength = sql.Length - 1,
                _sessionId = session?.Identity ?? 0,
                _resultMode = SpiResultMode.Scalar,
            };
            return RunRequest(request, parameters)[0].Get<PgJson>(0);
        }
    }

    /// <summary>
    /// Defines and registers a configuration parameter whose name and metadata are supplied at run time.
    /// </summary>
    /// <param name="name">The terminated UTF-8 qualified name.</param>
    /// <param name="definition">The definition, whose text remains valid for the call.</param>
    internal static void DefineGuc(byte[] name, NativeGucDefinition* definition)
    {
        CheckAccess();
        NativeSpiResult result = default;
        try
        {
            fixed (byte* text = name)
            {
                var request = new NativeSpiRequest
                {
                    _operation = SpiOperation.GucDefine,
                    _command = text,
                    _commandLength = name.Length - 1,
                    _callback = (nint)definition,
                };
                Invoke(&request, &result);
            }
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    /// <summary>
    /// Reads generated configuration backing storage without issuing SQL or opening a subtransaction.
    /// The caller receives ownership of the scalar transport and must release it after copying.
    /// </summary>
    internal static NativeValue ReadGuc(string name, int kind)
    {
        CheckAccess();
        byte[] bytes = NativeGuc.EncodeName(name);
        NativeSpiResult result = default;
        try
        {
            fixed (byte* text = bytes)
            {
                var request = new NativeSpiRequest
                {
                    _operation = SpiOperation.GucRead,
                    _command = text,
                    _commandLength = bytes.Length - 1,
                    _scalarOperation = kind,
                };
                Invoke(&request, &result);
            }

            NativeValue value = result._text;
            result._text = default;
            return value;
        }
        finally
        {
            result._text.Release();
            ReleaseResult(&result);
        }
    }

    /// <summary>
    /// Calls a native temporal routine and copies its result before releasing per-operation storage.
    /// </summary>
    internal static T Temporal<T>(TemporalOperation operation, ReadOnlySpan<SpiParameter> parameters)
        => Scalar<T>(SpiOperation.Temporal, (int)operation, parameters);

    /// <summary>
    /// Calls a PostgreSQL numeric routine through the guarded scalar boundary.
    /// </summary>
    internal static T Numeric<T>(NumericOperation operation, ReadOnlySpan<SpiParameter> parameters)
        => Scalar<T>(SpiOperation.Numeric, (int)operation, parameters);

    /// <summary>
    /// Parses a network value through PostgreSQL's guarded input functions.
    /// </summary>
    /// <typeparam name="T">The network result type.</typeparam>
    /// <param name="parameters">The validated text input.</param>
    /// <returns>The detached network value.</returns>
    internal static T Network<T>(ReadOnlySpan<SpiParameter> parameters)
        => Scalar<T>(SpiOperation.Network, 0, parameters);

    /// <summary>
    /// Parses a geometry value using PostgreSQL's guarded input functions.
    /// </summary>
    /// <typeparam name="T">The geometric result type.</typeparam>
    /// <param name="parameters">The validated text input.</param>
    /// <returns>The detached geometric value.</returns>
    internal static T Geometry<T>(ReadOnlySpan<SpiParameter> parameters)
        => Scalar<T>(SpiOperation.Geometry, 0, parameters);

    /// <summary>
    /// Calls an allowlisted range routine and copies its result across the native error boundary.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="operation">The range operation.</param>
    /// <param name="parameters">The typed input operands.</param>
    /// <returns>The detached result.</returns>
    internal static T Range<T>(RangeOperation operation, ReadOnlySpan<SpiParameter> parameters)
        => PgDatumRegistry.Find(typeof(T)) is { RangeBound: not null } mapping
            ? MappedRangeScalar<T>(operation, parameters, mapping) : Scalar<T>(SpiOperation.Range, (int)operation, parameters);

    /// <summary>
    /// Resolves an enum in a fixed or current extension schema inside the native guard.
    /// </summary>
    internal static uint ResolveEnum(string name, string? schema, bool missingOk)
        => Scalar<uint>(SpiOperation.Enum, missingOk ? 1 : 0, [SpiParameter.Create(name), SpiParameter.Create(schema)]);

    /// <summary>
    /// Resolves a generated variable-length base type inside the native guard.
    /// </summary>
    internal static uint ResolveCustomType(string name, string? schema, bool missingOk)
        => Scalar<uint>(SpiOperation.CustomType, missingOk ? 1 : 0, [SpiParameter.Create(name), SpiParameter.Create(schema)]);

    /// <summary>
    /// Resolves the array OID of a live enum type inside the native guard.
    /// </summary>
    internal static uint EnumArrayOid(uint oid) => Scalar<uint>(SpiOperation.Enum, 2, [SpiParameter.Create(oid)]);

    /// <summary>
    /// Resolves the array type for an explicit composite element identity.
    /// </summary>
    /// <param name="elementOid">The element type OID.</param>
    /// <returns>The PostgreSQL array type OID.</returns>
    internal static uint TupleArrayOid(uint elementOid) => Scalar<uint>(SpiOperation.Tuple, 2, [SpiParameter.Create(elementOid)]);

    /// <summary>
    /// Loads owned physical attribute metadata for a named composite type or its domain.
    /// </summary>
    /// <param name="name">The SQL type name, or null when resolving by OID.</param>
    /// <param name="oid">The type identity used when name is null.</param>
    /// <returns>The copied tuple descriptor.</returns>
    internal static PgTupleDescriptor LoadTupleDescriptor(string? name, uint oid)
        => ReadTupleOperation(0, [SpiParameter.Create(name), SpiParameter.Create(oid)]).Descriptor;

    /// <summary>
    /// Registers an anonymous tuple shape and returns the normalized, owned tuple.
    /// </summary>
    /// <param name="value">The candidate tuple and metadata.</param>
    /// <returns>The canonical tuple.</returns>
    internal static PgHeapTuple CreateTuple(PgHeapTuple value) => ReadTupleOperation(1, [SpiParameter.Create(value)]);

    /// <summary>
    /// Copies a caller-supplied native tuple descriptor, and a heap tuple of that shape when one is supplied.
    /// </summary>
    /// <param name="descriptor">The native TupleDesc address.</param>
    /// <param name="tuple">The native HeapTuple address, or zero for the descriptor alone.</param>
    /// <returns>The owned tuple; its cells are SQL NULL when no heap tuple is supplied.</returns>
    internal static PgHeapTuple CopyNativeTuple(nint descriptor, nint tuple)
        => ReadTupleOperation(tuple == 0 ? 3 : 4, [SpiParameter.Create((long)descriptor), SpiParameter.Create((long)tuple)]);

    /// <summary>
    /// Forms a native heap tuple from a managed composite in the destination context.
    /// </summary>
    /// <param name="value">The managed composite.</param>
    /// <param name="lifetime">The destination, which owns the heap tuple.</param>
    /// <returns>The native HeapTuple address.</returns>
    internal static nint FormNativeTuple(PgHeapTuple value, PgDatumLifetime lifetime)
    {
        CheckAccess();
        lifetime.Validate();
        var request = new NativeSpiRequest
        {
            _operation = SpiOperation.Tuple,
            _scalarOperation = 5,
            _resultContext = lifetime.ContextId,
            _resultGeneration = lifetime.Generation,
        };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, [SpiParameter.Create(value)], &result);
            return (nint)result._text.Integral;
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    private static PgHeapTuple ReadTupleOperation(int operation, ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        var request = new NativeSpiRequest { _operation = SpiOperation.Tuple, _scalarOperation = operation };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, parameters, &result);
            return result._text.ReadTuple();
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    /// <summary>
    /// Resolves a generated label to its pg_enum datum OID inside the native guard.
    /// </summary>
    internal static uint EnumValueOid(uint typeOid, string label)
        => Scalar<uint>(SpiOperation.Enum, 3, [SpiParameter.Create(typeOid), SpiParameter.Create(label)]);

    /// <summary>
    /// Copies a pg_enum row into an owned catalog value inside the native guard.
    /// </summary>
    internal static PgEnumInfo EnumInfo(uint valueOid)
    {
        CheckAccess();
        var request = new NativeSpiRequest { _operation = SpiOperation.Enum, _scalarOperation = 4 };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, [SpiParameter.Create(valueOid)], &result);
            return result._text.ReadEnumInfo(valueOid);
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    private static T Scalar<T>(SpiOperation family, int operation, ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        uint typeOid = SpiType.GetOid<T>();
        var request = new NativeSpiRequest
        {
            _operation = family,
            _scalarOperation = operation,
            _scalarResultOid = typeOid,
        };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, parameters, &result);
            return SpiRow.Convert<T>(SpiType.FromNative(result._text, typeOid));
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    /// <summary>
    /// Queries the active backend's client and server log thresholds through the native guard.
    /// </summary>
    /// <param name="level">The severity to check.</param>
    /// <returns>Whether PostgreSQL would route a message at that level.</returns>
    internal static bool IsLogEnabled(PgLogLevel level)
    {
        CheckAccess();
        var request = new NativeSpiRequest { _operation = SpiOperation.IsLogEnabled, _logLevel = level };
        NativeSpiResult result = default;
        Invoke(&request, &result);
        return result._rowsAffected != 0;
    }

    /// <summary>
    /// Reports an enabled nonterminal diagnostic through the native guard and releases its transport buffers.
    /// </summary>
    /// <param name="level">The nonterminal reporting severity.</param>
    /// <param name="diagnostic">The validated message and optional diagnostic fields.</param>
    internal static void Report(PgLogLevel level, PgDiagnostic diagnostic)
    {
        CheckAccess();
        if (!IsLogEnabled(level))
        {
            return;
        }

        NativeCallError message = default;
        try
        {
            NativeError.WriteDiagnostic(diagnostic, &message);
            var request = new NativeSpiRequest { _operation = SpiOperation.Report, _logLevel = level, _diagnostic = &message };
            NativeSpiResult result = default;
            Invoke(&request, &result);
        }
        finally
        {
            message.Release();
        }
    }

    /// <summary>
    /// Retains a validated terminal report without running PostgreSQL error reporting across managed frames.
    /// </summary>
    /// <param name="level">FATAL or PANIC.</param>
    /// <param name="report">The borrowed, fully initialized transport.</param>
    internal static void RecordTerminal(PgLogLevel level, NativeCallError* report)
    {
        var request = new NativeSpiRequest { _operation = SpiOperation.Report, _logLevel = level, _diagnostic = report };
        NativeSpiResult result = default;
        Invoke(&request, &result);
    }

    private static SpiCursor CreateCursor(NativeSpiRequest request, ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        var cursor = new SpiCursor(s_execute);
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, parameters, &result);
            cursor.Identity = result._cursorId;
            cursor.Name = result._cursorName.ReadString();
            return cursor;
        }
        catch
        {
            cursor.Dispose();
            throw;
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    private static SpiResult RunRequest(NativeSpiRequest request, ReadOnlySpan<SpiParameter> parameters)
    {
        CheckAccess();
        int limit = request._limit;
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, parameters, &result);
            return result.ToManaged();
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    private static void InvokeParameters(NativeSpiRequest* request, ReadOnlySpan<SpiParameter> parameters, NativeSpiResult* result)
    {
        var arguments = new NativeSpiParameter[parameters.Length];
        try
        {
            for (int index = 0; index < parameters.Length; index++)
            {
                if (parameters[index].TypeOid == 0)
                {
                    throw new ArgumentException("SPI parameters must be created with an explicit managed type.", nameof(parameters));
                }

                arguments[index]._typeOid = parameters[index].TypeOid;
                arguments[index]._value = SpiType.ToNative(parameters[index].Value, parameters[index].CustomMapping,
                    parameters[index].CustomArrayMapping, parameters[index].DatumMapping, parameters[index].TypeOid, parameters[index].DatumArrayMapping);
            }

            fixed (NativeSpiParameter* values = arguments)
            {
                request->_parameters = values;
                request->_parameterCount = arguments.Length;
                Invoke(request, result);
            }
        }
        finally
        {
            foreach (ref NativeSpiParameter parameter in arguments.AsSpan())
            {
                parameter._value.Release();
            }
        }
    }

    private static void ReleaseResult(NativeSpiResult* result)
    {
        if (result->_release != null)
        {
            result->_release(result);
        }
    }

    private static void Invoke(NativeSpiRequest* request, NativeSpiResult* result)
    {
        // Terminal transport only copies owned diagnostics into the native frame.
        // It must remain available so held locks can unwind before PostgreSQL reports the failure.
        bool terminal = request->_operation == SpiOperation.Report && request->_logLevel >= PgLogLevel.Fatal;
        if (!terminal)
        {
            NativeBorrowScope.CheckBackendAccess();
        }

        if (s_abortCleanupDepth != 0 && !terminal)
        {
            bool relationRelease = request->_operation == SpiOperation.Relation && request->_scalarOperation == 0;
            if ((!relationRelease && request->_operation is not (SpiOperation.FreePlan or SpiOperation.CloseCursor)) || request->_sessionId != 0)
            {
                throw new InvalidOperationException("Only owned PostgreSQL resources may be released during aborted iterator cleanup.");
            }

            request->_cleanupOnly = 1;
        }

        request->_recoverInput = NativeInputRecovery.IsActive ? (byte)1 : (byte)0;
        var execute = (delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)s_execute;
        NativeCallError error = default;
        try
        {
            if (execute(request, result, &error) != 0)
            {
                throw error.ToManagedException();
            }
        }
        finally
        {
            error.Release();
        }
    }

    private static byte[] EncodeCommand(string commandText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        if (commandText.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("SQL command text cannot contain a zero character.", nameof(commandText));
        }

        return EncodeUtf8(commandText);
    }

    private static byte[] EncodeUtf8(string text)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        byte[] sql = new byte[encoding.GetByteCount(text) + 1];
        encoding.GetBytes(text, sql);
        return sql;
    }
}
