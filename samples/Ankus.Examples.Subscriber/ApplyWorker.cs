using System.Buffers.Binary;

namespace Ankus.Examples.Subscriber;

/// <summary>
/// Runs one subscription in a background worker: copies the publication's tables in the new slot's snapshot, then
/// streams the provider's changes through <c>pgoutput</c> and applies each remote transaction in one local
/// transaction, recording its commit position with the subscription's replication origin.
/// </summary>
/// <param name="name">The subscription's name.</param>
/// <param name="provider">The provider's connection string.</param>
/// <param name="publication">The provider's publication.</param>
/// <param name="slot">The provider's replication slot.</param>
internal sealed class ApplyWorker(string name, string provider, string publication, string slot)
{
    private static readonly TimeSpan s_feedbackInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<uint, RemoteRelation> _relations = [];

    private PgLsn _received;

    private DateTime _feedback = DateTime.MinValue;

    /// <summary>
    /// Gets the replication origin's name for a subscription.
    /// </summary>
    /// <param name="subscription">The subscription's name.</param>
    /// <returns>The origin's name.</returns>
    public static string OriginName(string subscription) => "ankus_subscriber_" + subscription;

    /// <summary>
    /// Sets up the session, then copies or resumes and applies changes until PostgreSQL asks the worker to stop.
    /// </summary>
    public void Run()
    {
        PgBackgroundWorker.RunTransaction(() =>
        {
            // As in the built-in subscriber, only triggers enabled for replicas fire for applied changes.
            Spi.Execute("SET session_replication_role = replica");
            string origin = OriginName(name);
            PgReplicationOrigin.SetupSession(PgReplicationOrigin.Find(origin) ?? PgReplicationOrigin.Create(origin));
        });
        using PgReplicationConnection connection = PgReplicationConnection.Connect(provider, OriginName(name));
        PgLsn start = PgReplicationOrigin.GetSessionProgress();
        if (!start.IsValid)
        {
            start = CopyTables(connection);
        }

        _received = start;
        connection.StartCopy($"START_REPLICATION SLOT {Spi.QuoteIdentifier(slot)} LOGICAL {start} " +
            $"(proto_version '1', publication_names {Spi.QuoteLiteral(Spi.QuoteIdentifier(publication))})");
        while (PgBackgroundWorker.CanContinue)
        {
            PgReplicationReceiveStatus status = connection.Receive(s_feedbackInterval, out ReadOnlySpan<byte> message);
            if (status == PgReplicationReceiveStatus.Ended)
            {
                return;
            }

            if (status == PgReplicationReceiveStatus.Message && Handle(connection, message, null))
            {
                PgBackgroundWorker.RunTransaction(() => ApplyTransaction(connection));
            }

            SendFeedback(connection, force: false);
        }
    }

    /// <summary>
    /// Creates the slot and copies every published table in its snapshot, in one local transaction that also records
    /// the slot's starting position, so a failed copy starts over. Queries that return rows run inside the local
    /// transaction because PostgreSQL reads their column types from the catalog.
    /// </summary>
    private PgLsn CopyTables(PgReplicationConnection connection)
    {
        return PgBackgroundWorker.RunTransaction(() =>
        {
            // A copy that failed before committing left its slot behind; the copy starts over from a new snapshot.
            DropSlot(connection, slot);
            connection.Execute("BEGIN READ ONLY ISOLATION LEVEL REPEATABLE READ");
            string?[] created = connection.Query($"CREATE_REPLICATION_SLOT {Spi.QuoteIdentifier(slot)} LOGICAL pgoutput USE_SNAPSHOT", 4)[0];
            PgLsn consistent = PgLsn.Parse(created[1]!);
            IReadOnlyList<string?[]> tables = connection.Query(
                $"SELECT schemaname, tablename FROM pg_catalog.pg_publication_tables WHERE pubname = {Spi.QuoteLiteral(publication)}", 2);
            foreach (string?[] table in tables)
            {
                string qualified = Spi.QuoteQualifiedIdentifier(table[0], table[1]!);
                string[] columns = [.. connection.Query(
                    "SELECT attname FROM pg_catalog.pg_attribute WHERE attrelid = " + Spi.QuoteLiteral(qualified) +
                    "::regclass AND attnum > 0 AND NOT attisdropped AND attgenerated = '' ORDER BY attnum", 1).Select(static row => row[0]!)];
                uint local = Spi.ExecuteScalar<uint?>("SELECT to_regclass($1)::oid", SpiParameter.Create(qualified))
                    ?? throw new InvalidOperationException($"The subscriber has no table {qualified}.");
                connection.StartCopy($"COPY {qualified} ({string.Join(", ", columns.Select(Spi.QuoteIdentifier))}) TO STDOUT");
                _ = connection.CopyInto(local, columns);
            }

            // The commit records the slot's position with the copied rows; the local commit time is the copy's.
            PgReplicationOrigin.SetTransactionOrigin(consistent, null);
            connection.Execute("COMMIT");
            return consistent;
        });
    }

    /// <summary>
    /// Drops a slot on the provider if it exists. PostgreSQL 13 does not report a remote error's SQLSTATE, so the slot
    /// is looked up rather than its drop error recognized.
    /// </summary>
    /// <param name="connection">The provider connection, inside a local transaction.</param>
    /// <param name="slot">The slot's name.</param>
    public static void DropSlot(PgReplicationConnection connection, string slot)
    {
        if (connection.Query($"SELECT count(*) FROM pg_catalog.pg_replication_slots WHERE slot_name = {Spi.QuoteLiteral(slot)}", 1)[0][0] != "0")
        {
            connection.Execute("DROP_REPLICATION_SLOT " + Spi.QuoteIdentifier(slot));
        }
    }

    /// <summary>
    /// Applies one remote transaction after its begin message, then commits with the remote commit's position and
    /// time. An error or a stop request rolls it back, and the stream resumes from the last committed transaction.
    /// </summary>
    private void ApplyTransaction(PgReplicationConnection connection)
    {
        using TransactionApplier applier = new(_relations);
        while (true)
        {
            PgReplicationReceiveStatus status = connection.Receive(s_feedbackInterval, out ReadOnlySpan<byte> message);
            if (!PgBackgroundWorker.CanContinue)
            {
                throw new OperationCanceledException("The subscriber is stopping inside a remote transaction.");
            }

            if (status == PgReplicationReceiveStatus.Ended)
            {
                throw new InvalidDataException("The provider ended the stream inside a transaction.");
            }

            if (status == PgReplicationReceiveStatus.Message && Handle(connection, message, applier))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Handles one copy message: a keepalive, or a change the applier applies when a transaction is open.
    /// </summary>
    /// <returns>Outside a transaction, whether the message began one; inside, whether it committed it.</returns>
    private bool Handle(PgReplicationConnection connection, ReadOnlySpan<byte> message, TransactionApplier? applier)
    {
        ProtocolReader reader = new(message);
        byte kind = reader.ReadByte();
        if (kind == (byte)'k')
        {
            Received(reader.ReadLsn());
            _ = reader.ReadInt64();
            SendFeedback(connection, force: reader.ReadByte() != 0);
            return false;
        }

        if (kind != (byte)'w')
        {
            throw new InvalidDataException($"The provider sent an unknown copy message '{(char)kind}'.");
        }

        _ = reader.ReadLsn();
        Received(reader.ReadLsn());
        _ = reader.ReadInt64();
        kind = reader.ReadByte();
        switch (kind)
        {
            case (byte)'B':
                return applier is null ? true : throw new InvalidDataException("A transaction began inside another.");
            case (byte)'R':
                RemoteRelation relation = RemoteRelation.Read(ref reader, out uint id);
                _relations[id] = relation;
                return false;
            case (byte)'Y':
            case (byte)'O':
                return false;
        }

        if (applier is null)
        {
            throw new InvalidDataException($"The provider sent a '{(char)kind}' message outside a transaction.");
        }

        switch (kind)
        {
            case (byte)'I':
                applier.Insert(ref reader);
                return false;
            case (byte)'U':
                applier.Update(ref reader);
                return false;
            case (byte)'D':
                applier.Delete(ref reader);
                return false;
            case (byte)'T':
                applier.Truncate(ref reader);
                return false;
            case (byte)'C':
                _ = reader.ReadByte();
                _ = reader.ReadLsn();
                PgLsn end = reader.ReadLsn();
                PgTimestampTz time = reader.ReadTimestamp();

                // Writers close before commit so their AFTER triggers run inside the transaction.
                applier.Dispose();
                PgReplicationOrigin.SetTransactionOrigin(end, time);
                return true;
            default:
                throw new InvalidDataException($"The provider sent an unknown change '{(char)kind}'.");
        }
    }

    private void Received(PgLsn position)
    {
        if (position > _received)
        {
            _received = position;
        }
    }

    /// <summary>
    /// Reports the positions received and durably applied, which lets the provider release older WAL, when asked or
    /// once a second.
    /// </summary>
    private void SendFeedback(PgReplicationConnection connection, bool force)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && now - _feedback < s_feedbackInterval)
        {
            return;
        }

        PgLsn applied = PgReplicationOrigin.GetSessionProgress(flush: true);
        Span<byte> update = stackalloc byte[34];
        update[0] = (byte)'r';
        BinaryPrimitives.WriteUInt64BigEndian(update[1..], _received.Value);
        BinaryPrimitives.WriteUInt64BigEndian(update[9..], applied.Value);
        BinaryPrimitives.WriteUInt64BigEndian(update[17..], applied.Value);
        BinaryPrimitives.WriteInt64BigEndian(update[25..], (now - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / 10);
        update[33] = 0;
        connection.Send(update);
        _feedback = now;
    }
}
