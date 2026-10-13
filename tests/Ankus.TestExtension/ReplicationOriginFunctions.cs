namespace Ankus.TestExtension;

/// <summary>
/// Manages replication origins through <see cref="PgReplicationOrigin"/>, as a logical replication apply worker would.
/// </summary>
public static class ReplicationOriginFunctions
{
    /// <summary>
    /// Creates an origin.
    /// </summary>
    /// <param name="name">The origin's name.</param>
    /// <returns>The new origin's ID.</returns>
    [PgFunction]
    public static int OriginCreate(string name) => PgReplicationOrigin.Create(name);

    /// <summary>
    /// Looks up an origin.
    /// </summary>
    /// <param name="name">The origin's name.</param>
    /// <returns>The origin's ID, or null when there is none.</returns>
    [PgFunction]
    public static int? OriginFind(string name) => PgReplicationOrigin.Find(name);

    /// <summary>
    /// Drops an origin.
    /// </summary>
    /// <param name="name">The origin's name.</param>
    /// <param name="missingOk">Whether a missing origin is ignored.</param>
    [PgFunction]
    public static void OriginDrop(string name, bool missingOk) => PgReplicationOrigin.Drop(name, missingOk);

    /// <summary>
    /// Makes an origin the session's.
    /// </summary>
    /// <param name="origin">The origin's ID.</param>
    [PgFunction]
    public static void OriginSetupSession(int origin) => PgReplicationOrigin.SetupSession(checked((ushort)origin));

    /// <summary>
    /// Releases the session's origin.
    /// </summary>
    [PgFunction]
    public static void OriginResetSession() => PgReplicationOrigin.ResetSession();

    /// <summary>
    /// Gets the session's origin.
    /// </summary>
    /// <returns>The origin's ID, or zero.</returns>
    [PgFunction]
    public static int OriginSession() => PgReplicationOrigin.SessionOrigin;

    /// <summary>
    /// Inserts a row into a table of <c>(id integer, name text)</c> on a source's behalf, recording the source's commit
    /// position and time for the current transaction.
    /// </summary>
    /// <param name="table">The table's OID.</param>
    /// <param name="remoteCommit">The source's commit position as text.</param>
    /// <param name="commitTime">The source's commit time.</param>
    /// <param name="id">The row's id.</param>
    [PgFunction]
    public static void OriginApply(uint table, string remoteCommit, PgTimestampTz commitTime, int id)
    {
        PgReplicationOrigin.SetTransactionOrigin(PgLsn.Parse(remoteCommit), commitTime);
        using PgRelationWriter writer = PgRelationWriter.Open(table);
        PgHeapTuple row = writer.CreateRow();
        row.Set("id", id);
        row.Set("name", "applied");
        writer.Insert(row);
    }

    /// <summary>
    /// Records a source commit position without a session origin, which fails.
    /// </summary>
    [PgFunction]
    public static void OriginApplyWithoutSession() => PgReplicationOrigin.SetTransactionOrigin(new PgLsn(1), null);

    /// <summary>
    /// Gets how far an origin has applied.
    /// </summary>
    /// <param name="origin">The origin's ID, or zero for the session's.</param>
    /// <returns>The position as text.</returns>
    [PgFunction]
    public static string OriginProgress(int origin)
        => (origin == 0 ? PgReplicationOrigin.GetSessionProgress(flush: true) : PgReplicationOrigin.GetProgress(checked((ushort)origin), flush: true)).ToString();

    /// <summary>
    /// Moves an origin's progress.
    /// </summary>
    /// <param name="origin">The origin's ID.</param>
    /// <param name="remoteCommit">The position as text.</param>
    /// <param name="goBackward">Whether the progress may move back.</param>
    [PgFunction]
    public static void OriginAdvance(int origin, string remoteCommit, bool goBackward)
        => PgReplicationOrigin.Advance(checked((ushort)origin), PgLsn.Parse(remoteCommit), goBackward: goBackward);

    /// <summary>
    /// Finds a row and reports the origin and time of the commit that wrote it.
    /// </summary>
    /// <param name="table">The table's OID.</param>
    /// <param name="id">The row's id.</param>
    /// <returns>The commit time, or null when unknown.</returns>
    [PgFunction]
    public static PgTimestampTz? OriginCommitTime(uint table, int id)
    {
        using PgRelationWriter writer = PgRelationWriter.Open(table);
        PgHeapTuple key = writer.CreateRow();
        key.Set("id", id);
        return writer.Find(key)?.CommitTime;
    }
}
