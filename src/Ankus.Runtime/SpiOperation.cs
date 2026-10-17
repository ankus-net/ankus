namespace Ankus;

/// <summary>
/// Selects an operation inside the native SPI subtransaction guard.
/// </summary>
internal enum SpiOperation : byte
{
    /// <summary>
    /// Executes SQL text with optional positional parameters.
    /// </summary>
    Execute,

    /// <summary>
    /// Prepares and retains a PostgreSQL plan beyond the current SPI connection.
    /// </summary>
    Prepare,

    /// <summary>
    /// Executes a retained plan.
    /// </summary>
    ExecutePlan,

    /// <summary>
    /// Releases a retained plan on its owning backend thread.
    /// </summary>
    FreePlan,

    /// <summary>
    /// Opens a transaction-bound cursor from command text.
    /// </summary>
    OpenCursor,

    /// <summary>
    /// Opens a transaction-bound cursor from a retained plan.
    /// </summary>
    OpenPlanCursor,

    /// <summary>
    /// Fetches the next batch from a live cursor.
    /// </summary>
    FetchCursor,

    /// <summary>
    /// Closes a cursor if its portal is still alive.
    /// </summary>
    CloseCursor,

    /// <summary>
    /// Resolves a cursor by its PostgreSQL portal name.
    /// </summary>
    FindCursor,

    /// <summary>
    /// Opens a scoped native SPI connection.
    /// </summary>
    OpenSession,

    /// <summary>
    /// Closes the current scoped SPI connection.
    /// </summary>
    CloseSession,

    /// <summary>
    /// Retains a session-bound plan beyond its connection's lifetime.
    /// </summary>
    KeepPlan,

    /// <summary>
    /// Quotes one identifier using PostgreSQL's keyword and configuration rules.
    /// </summary>
    QuoteIdentifier,

    /// <summary>
    /// Quotes a qualifier and identifier as two separate SQL name components.
    /// </summary>
    QuoteQualifiedIdentifier,

    /// <summary>
    /// Quotes a string literal using PostgreSQL's escape rules.
    /// </summary>
    QuoteLiteral,

    /// <summary>
    /// Explains exactly one parsed SQL statement, returning its JSON plan.
    /// </summary>
    Explain,

    /// <summary>
    /// Reports a nonterminal PostgreSQL diagnostic.
    /// </summary>
    Report,

    /// <summary>
    /// Queries PostgreSQL's reporting thresholds.
    /// </summary>
    IsLogEnabled,

    /// <summary>
    /// Calls an allowlisted temporal routine without opening an SPI connection.
    /// </summary>
    Temporal,

    /// <summary>
    /// Calls an allowlisted numeric routine without opening an SPI connection.
    /// </summary>
    Numeric,

    /// <summary>
    /// Calls allowlisted network input functions without opening an SPI connection.
    /// </summary>
    Network,

    /// <summary>
    /// Calls allowlisted geometric input functions without opening an SPI connection.
    /// </summary>
    Geometry,

    /// <summary>
    /// Calls allowlisted range routines without opening an SPI connection.
    /// </summary>
    Range,
    /// <summary>
    /// Resolves live enum catalog identities without opening an SPI connection.
    /// </summary>
    Enum,

    /// <summary>
    /// Copies tuple descriptors and constructs composite values through the native guard.
    /// </summary>
    Tuple,

    /// <summary>
    /// Reads generated configuration backing storage through the native guard without a subtransaction.
    /// </summary>
    GucRead,

    /// <summary>
    /// Installs the managed transaction and subtransaction callback dispatchers.
    /// </summary>
    TransactionCallbacks,

    /// <summary>
    /// Reads PostgreSQL's next full transaction ID for wrap-aware xid expansion.
    /// </summary>
    TransactionId,

    /// <summary>
    /// Reads, formats, or copies a checked raw PostgreSQL datum.
    /// </summary>
    Datum,

    /// <summary>
    /// Copies a live function call's metadata and raw SQL arguments.
    /// </summary>
    FunctionContext,

    /// <summary>
    /// Resolves and invokes a scalar PostgreSQL function by name, OID, or an explicit native entry point.
    /// </summary>
    FunctionCall,

    /// <summary>
    /// Resolves the current identity of a generated variable-length base type.
    /// </summary>
    CustomType,

    /// <summary>
    /// Resolves the current identity of a concrete manually mapped PostgreSQL type.
    /// </summary>
    DatumType,

    /// <summary>
    /// Constructs an exactly typed raw array from shape metadata and checked element datums.
    /// </summary>
    Array,

    /// <summary>
    /// Resolves type syntax or exact qualified operator names without retaining catalog or native string storage.
    /// </summary>
    Lookup,

    /// <summary>
    /// Opens, inspects, and releases checked native relation references.
    /// </summary>
    Relation,

    /// <summary>
    /// Runs a synchronous managed callback inside an explicitly requested recovery boundary.
    /// </summary>
    Subtransaction,

    /// <summary>
    /// Defines a configuration parameter whose name and metadata are supplied at run time.
    /// </summary>
    GucDefine,

    /// <summary>
    /// Runs a synchronous managed callback with configuration settings saved and restored around it.
    /// </summary>
    Settings,
}
