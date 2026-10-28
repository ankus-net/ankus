using System;
using System.Text;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Manages PostgreSQL's replication origins, which record how far a subscriber has applied each source's changes and
/// mark the commits it makes on that source's behalf.
/// </summary>
/// <remarks>
/// A worker that applies one source's changes sets up a session origin, then, in each transaction, records the
/// source's commit position and time before committing. PostgreSQL stores that position with the commit, so the
/// origin's progress survives a crash, and logical decoding can filter changes by the origin that made them. An
/// origin's numeric ID is what <see cref="PgFoundRow.CommitOrigin"/> reports. Each operation makes the checks its
/// <c>pg_replication_origin_*</c> SQL function makes, except privileges: an extension decides who may call the functions
/// that use these operations. Creating, finding, dropping and advancing origins read or change the catalog, so they
/// need a transaction.
/// </remarks>
public static unsafe class PgReplicationOrigin
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Gets the session's origin, or zero when none is set up.
    /// </summary>
    public static ushort SessionOrigin => checked((ushort)Invoke(10)._value);

    /// <summary>
    /// Creates an origin in the current transaction.
    /// </summary>
    /// <param name="name">The origin's name.</param>
    /// <returns>The new origin's ID.</returns>
    /// <exception cref="PgException">An origin has the name, the name is reserved, or there is no transaction.</exception>
    /// <remarks>Names starting with <c>pg_</c> are reserved, and from PostgreSQL 16 so are <c>any</c> and <c>none</c>.</remarks>
    public static ushort Create(string name) => checked((ushort)Invoke(1, name)._value);

    /// <summary>
    /// Looks up an origin by name.
    /// </summary>
    /// <param name="name">The origin's name.</param>
    /// <returns>The origin's ID, or null when no origin has the name.</returns>
    public static ushort? Find(string name)
    {
        nint origin = Invoke(2, name)._value;
        return origin == 0 ? null : checked((ushort)origin);
    }

    /// <summary>
    /// Drops an origin.
    /// </summary>
    /// <param name="name">The origin's name.</param>
    /// <param name="missingOk">Whether a missing origin is ignored; otherwise it is an error.</param>
    /// <exception cref="PgException">The origin does not exist, or a session is using it.</exception>
    public static void Drop(string name, bool missingOk = false) => Invoke(3, name, value: missingOk ? 1 : 0);

    /// <summary>
    /// Makes an origin this session's, so its commits are marked with it and advance its progress.
    /// </summary>
    /// <param name="origin">The origin's ID.</param>
    /// <exception cref="PgException">The session already has an origin, or another session uses this one.</exception>
    public static void SetupSession(ushort origin) => Invoke(4, value: origin);

    /// <summary>
    /// Releases the session's origin.
    /// </summary>
    public static void ResetSession() => Invoke(5);

    /// <summary>
    /// Records the source's commit position and time for the current transaction, which PostgreSQL stores with its
    /// commit and uses to advance the session origin's progress.
    /// </summary>
    /// <param name="remoteCommit">The position of the source's commit.</param>
    /// <param name="commitTime">The time the source committed, or null to record the local commit time.</param>
    /// <remarks>
    /// PostgreSQL records the time as the commit timestamp in place of the local time. It keeps both values for the
    /// session's later commits until they are set again or the session is reset, so an apply worker sets them in every
    /// transaction it applies. PostgreSQL treats a time of zero, the start of 2000 UTC, as no time.
    /// </remarks>
    /// <exception cref="PgException">The session has no origin, or there is no transaction.</exception>
    public static void SetTransactionOrigin(PgLsn remoteCommit, PgTimestampTz? commitTime)
        => Invoke(6, value: unchecked((nint)remoteCommit.Value), data: (nint)(commitTime?.MicrosecondsSinceEpoch ?? 0));

    /// <summary>
    /// Gets how far the session's origin has applied.
    /// </summary>
    /// <param name="flush">Whether to wait until the position is durable on disk.</param>
    /// <returns>The last source commit position applied.</returns>
    /// <exception cref="PgException">The session has no origin.</exception>
    public static PgLsn GetSessionProgress(bool flush = false) => new(unchecked((ulong)Invoke(7, value: flush ? 1 : 0)._value));

    /// <summary>
    /// Gets how far an origin has applied.
    /// </summary>
    /// <param name="origin">The origin's ID.</param>
    /// <param name="flush">Whether to wait until the position is durable on disk.</param>
    /// <returns>The last source commit position applied, or <see cref="PgLsn.Invalid"/> when there is none.</returns>
    public static PgLsn GetProgress(ushort origin, bool flush = false)
        => new(unchecked((ulong)Invoke(8, value: flush ? 1 : 0, context: origin)._value));

    /// <summary>
    /// Moves an origin's progress, as <c>pg_replication_origin_advance</c> does.
    /// </summary>
    /// <param name="origin">The origin's ID.</param>
    /// <param name="remoteCommit">The source commit position the origin has applied.</param>
    /// <param name="localCommit">The local commit that applied it, or <see cref="PgLsn.Invalid"/>.</param>
    /// <param name="goBackward">Whether the progress may move to an earlier position.</param>
    /// <param name="walLog">Whether to write the change to the write-ahead log so it survives a crash.</param>
    /// <remarks>
    /// Without <paramref name="goBackward"/>, an earlier position leaves the progress where it is. The SQL function
    /// always allows moving backward.
    /// </remarks>
    /// <exception cref="PgException">The origin does not exist, or there is no transaction.</exception>
    public static void Advance(ushort origin, PgLsn remoteCommit, PgLsn localCommit = default, bool goBackward = false, bool walLog = true)
        => Invoke(9, value: unchecked((nint)remoteCommit.Value), data: unchecked((nint)localCommit.Value), context: origin,
            length: (nuint)((goBackward ? 1 : 0) | (walLog ? 2 : 0)));

    private static NativeMemoryResult Invoke(int operation, string? name = null, nint value = 0, nint data = 0, nint context = 0, nuint length = 0)
    {
        byte[]? encoded = null;
        if (name is not null)
        {
            if (name.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("An origin name cannot contain a zero character.", nameof(name));
            }

            encoded = new byte[s_utf8.GetByteCount(name) + 1];
            s_utf8.GetBytes(name, encoded);
        }

        fixed (byte* address = encoded)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.ReplicationOrigin,
                _flags = operation,
                _pointer = (nint)address,
                _value = value,
                _data = data,
                _context = context,
                _length = length,
            };
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            return result;
        }
    }
}
