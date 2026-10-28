using System;

namespace Ankus;

/// <summary>
/// Selects PostgreSQL allocation initialization and size policies.
/// </summary>
[Flags]
public enum PgAllocationOptions
{
    /// <summary>
    /// Allocates uninitialized storage within PostgreSQL's ordinary allocation limit.
    /// </summary>
    None = 0,

    /// <summary>
    /// Clears every byte of a newly allocated region.
    /// </summary>
    Zeroed = 1,

    /// <summary>
    /// Permits PostgreSQL's larger huge-allocation size limit without guaranteeing available memory.
    /// </summary>
    Huge = 4,
}
