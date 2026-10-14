using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Carries a SPI operation and its borrowed arguments through the native guard.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSpiRequest
{
    /// <summary>
    /// Encodes an explicit snapshot choice or transaction-aware selection without changing the native request layout.
    /// </summary>
    /// <param name="readOnly">The explicit choice, or null to select from PostgreSQL's transaction state.</param>
    /// <returns>Zero for writable, one for read-only or two for transaction-aware selection.</returns>
    internal static byte GetReadMode(bool? readOnly) => readOnly switch
    {
        null => 2,
        true => 1,
        _ => 0,
    };

    /// <summary>
    /// Points to null-terminated UTF-8 command text, when required by the operation.
    /// </summary>
    internal byte* _command;

    /// <summary>
    /// Points to typed parameters, or type-only entries when preparing a statement.
    /// </summary>
    internal NativeSpiParameter* _parameters;

    /// <summary>
    /// Contains the retained plan handle, updated when preparing or freeing a plan.
    /// </summary>
    internal nint _plan;

    /// <summary>
    /// Carries a managed callback entry point for a native registration operation.
    /// </summary>
    internal nint _callback;

    /// <summary>
    /// Contains the cursor identity, which is never a dereferenceable PostgreSQL pointer.
    /// </summary>
    internal long _cursorId;

    /// <summary>
    /// Contains the scoped SPI connection identity, or zero for an independent operation.
    /// </summary>
    internal long _sessionId;

    /// <summary>
    /// Contains the UTF-8 command length, excluding its terminator.
    /// </summary>
    internal int _commandLength;

    /// <summary>
    /// Contains the positional parameter count.
    /// </summary>
    internal int _parameterCount;

    /// <summary>
    /// Contains the maximum returned rows, or zero for no limit.
    /// </summary>
    internal int _limit;

    /// <summary>
    /// Selects the guarded operation.
    /// </summary>
    internal SpiOperation _operation;

    /// <summary>
    /// Selects which result cells to copy.
    /// </summary>
    internal SpiResultMode _resultMode;

    /// <summary>
    /// Selects writable execution (zero), forced read-only execution (one), or transaction-aware selection (two).
    /// </summary>
    internal byte _readOnly;

    /// <summary>
    /// Selects forward cursor movement using a one-byte C flag.
    /// </summary>
    internal byte _forward;

    /// <summary>
    /// Points to borrowed managed diagnostic transport during a reporting operation.
    /// </summary>
    internal NativeCallError* _diagnostic;

    /// <summary>
    /// Contains the version-independent reporting level.
    /// </summary>
    internal PgLogLevel _logLevel;

    /// <summary>
    /// Selects a function within the requested scalar operation family.
    /// Catalog FunctionCall modes zero/one check compatible/exact result OIDs; two/three check compatible/exact array elements.
    /// </summary>
    internal int _scalarOperation;

    /// <summary>
    /// Contains the expected scalar result type, or the exact array type for a raw array constructor.
    /// </summary>
    internal uint _scalarResultOid;

    /// <summary>
    /// Requests resource release without opening a subtransaction during executor abort cleanup.
    /// </summary>
    internal byte _cleanupOnly;

    /// <summary>
    /// Identifies the destination context for raw result copies, or zero for managed materialization.
    /// </summary>
    internal nint _resultContext;

    /// <summary>
    /// Contains the destination context's captured reset generation.
    /// </summary>
    internal nuint _resultGeneration;

    /// <summary>
    /// Borrows a generated callback's FunctionCallInfo only during synchronous snapshot capture.
    /// </summary>
    internal nint _functionCall;

    /// <summary>
    /// Selects a catalog function directly, or zero for name resolution.
    /// </summary>
    internal uint _functionOid;

    /// <summary>
    /// Supplies an explicit input collation when requested.
    /// </summary>
    internal uint _collationOid;

    /// <summary>
    /// Points to one default-expression flag per function argument.
    /// </summary>
    internal byte* _argumentDefaults;

    /// <summary>
    /// Distinguishes an explicit zero collation from inferred argument collation.
    /// </summary>
    internal byte _hasCollation;

    /// <summary>
    /// Requests SQL VARIADIC array binding for a named function call.
    /// </summary>
    internal byte _variadic;

    /// <summary>
    /// Supplies a caller-validated PostgreSQL version-1 entry point for explicit raw invocation.
    /// </summary>
    internal nint _nativeFunction;

    /// <summary>
    /// Borrows the managed state handle for a synchronous subtransaction callback.
    /// </summary>
    internal nint _callbackState;

    /// <summary>
    /// Borrows an array cursor only after validating its bookkeeping context and the array's lifetime.
    /// </summary>
    internal nint _arrayIterator;

    /// <summary>
    /// Requests actual input-error rollback where PostgreSQL permits subtransactions.
    /// </summary>
    internal byte _recoverInput;
}
