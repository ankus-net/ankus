namespace Ankus;

/// <summary>
/// Identifies a PostgreSQL predefined memory context.
/// </summary>
public enum PgMemoryContextKind
{
    /// <summary>
    /// The context that is current at the native callback boundary.
    /// </summary>
    Current,

    /// <summary>
    /// PostgreSQL's top-level context.
    /// </summary>
    Top,

    /// <summary>
    /// The active portal context, when a portal is active.
    /// </summary>
    Portal,

    /// <summary>
    /// PostgreSQL's error context.
    /// </summary>
    Error,

    /// <summary>
    /// PostgreSQL's postmaster context.
    /// </summary>
    Postmaster,

    /// <summary>
    /// PostgreSQL's catalog cache context.
    /// </summary>
    Cache,

    /// <summary>
    /// PostgreSQL's message context.
    /// </summary>
    Message,

    /// <summary>
    /// The active top-transaction context, or none outside a transaction, including on PostgreSQL 18 and later, which
    /// keep the context allocated between transactions.
    /// </summary>
    TopTransaction,

    /// <summary>
    /// The active subtransaction context, when one exists.
    /// </summary>
    CurTransaction,
}
