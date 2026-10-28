using System;

namespace Ankus.CompilerServices;

/// <summary>
/// Preserves native error routing and distinguishes rethrows from newly authored managed errors.
/// </summary>
[Flags]
internal enum NativeErrorFlags
{
    /// <summary>
    /// Identifies diagnostics captured from a previously reported PostgreSQL error.
    /// </summary>
    Rethrow = 1,

    /// <summary>
    /// Preserves server-log delivery of a native error.
    /// </summary>
    OutputToServer = 2,

    /// <summary>
    /// Preserves client delivery of a native error.
    /// </summary>
    OutputToClient = 4,

    /// <summary>
    /// Omits the statement from the server log when requested by PostgreSQL.
    /// </summary>
    HideStatement = 8,

    /// <summary>
    /// Omits context from the server log when requested by PostgreSQL.
    /// </summary>
    HideContext = 16,

    /// <summary>
    /// Indicates at least one diagnostic could not be transported completely.
    /// </summary>
    Incomplete = 32,

    /// <summary>
    /// Preserves PostgreSQL 13's function-name display flag.
    /// </summary>
    ShowFunction = 64,

    /// <summary>
    /// Indicates that PostgreSQL still requires transaction rollback before further backend work.
    /// </summary>
    Unrecovered = 128,
}
