namespace Ankus;

/// <summary>
/// Describes a row that <see cref="PgRelationWriter.Find(PgHeapTuple)"/> found and locked, and the transaction that
/// wrote it.
/// </summary>
/// <param name="Row">The row's values.</param>
/// <param name="CommitOrigin">The replication origin of the transaction that wrote the row, or zero when it was
/// written locally or PostgreSQL does not track commit timestamps.</param>
/// <param name="CommitTime">The time that transaction committed, or null when <c>track_commit_timestamp</c> is off or
/// the time is no longer known.</param>
/// <param name="TransactionId">The ID of that transaction.</param>
public sealed record PgFoundRow(PgHeapTuple Row, ushort CommitOrigin, PgTimestampTz? CommitTime, uint TransactionId);
