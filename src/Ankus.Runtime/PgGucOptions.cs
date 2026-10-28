using System;

namespace Ankus;

/// <summary>
/// Selects PostgreSQL configuration behaviors independently of version-specific native flag bits.
/// </summary>
[Flags]
public enum PgGucOptions
{
    /// <summary>
    /// Uses the ordinary configuration behavior.
    /// </summary>
    None = 0,

    /// <summary>
    /// Omits the setting from SHOW ALL and generated sample configuration.
    /// </summary>
    NoShowAll = 1,

    /// <summary>
    /// Excludes the setting from RESET ALL.
    /// </summary>
    NoResetAll = 2,

    /// <summary>
    /// Reports changes to the client.
    /// </summary>
    Report = 4,

    /// <summary>
    /// Applies PostgreSQL's GUC_DISALLOW_IN_FILE flag, including its ALTER SYSTEM restriction.
    /// </summary>
    DisallowInFile = 8,

    /// <summary>
    /// Restricts visibility to privileged users under PostgreSQL's rules.
    /// </summary>
    SuperuserOnly = 16,

    /// <summary>
    /// Limits a string setting to PostgreSQL's identifier length.
    /// </summary>
    IsName = 32,

    /// <summary>
    /// Rejects changes during security-restricted operations.
    /// </summary>
    NotWhileSecurityRestricted = 64,

    /// <summary>
    /// Rejects the setting in ALTER SYSTEM's automatic configuration file.
    /// </summary>
    DisallowInAutoFile = 128,

    /// <summary>
    /// Includes the setting in relevant EXPLAIN output.
    /// </summary>
    Explain = 256,

    /// <summary>
    /// Marks a runtime-computed setting on PostgreSQL 15 or later.
    /// </summary>
    RuntimeComputed = 512,
}
