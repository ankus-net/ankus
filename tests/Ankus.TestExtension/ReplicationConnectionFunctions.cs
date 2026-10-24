using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Ankus.TestExtension;

/// <summary>
/// Opens replication connections from a backend to the cluster it runs in, as a logical replication worker would.
/// </summary>
public static class ReplicationConnectionFunctions
{
    /// <summary>
    /// Runs a query over a replication connection and returns its rows.
    /// </summary>
    /// <param name="connection">The connection string.</param>
    /// <param name="query">The query.</param>
    /// <param name="columns">The query's column count.</param>
    /// <returns>Rows separated by semicolons and values by bars, with NULL shown as <c>&lt;null&gt;</c>, after the
    /// server version.</returns>
    [PgFunction]
    public static string ReplicationQuery(string connection, string query, int columns)
    {
        using PgReplicationConnection replication = PgReplicationConnection.Connect(connection, "ankus_test");
        return replication.ServerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Format(replication.Query(query, columns));
    }

    /// <summary>
    /// Runs a query over an ordinary connection and returns its rows.
    /// </summary>
    /// <param name="connection">The connection string.</param>
    /// <param name="query">The query.</param>
    /// <param name="columns">The query's column count.</param>
    /// <returns>Rows separated by semicolons and values by bars, with NULL shown as <c>&lt;null&gt;</c>.</returns>
    [PgFunction]
    public static string OrdinaryQuery(string connection, string query, int columns)
    {
        using PgReplicationConnection ordinary = PgReplicationConnection.Connect(connection, "ankus_ordinary", replication: false);
        return Format(ordinary.Query(query, columns));
    }

    /// <summary>
    /// Copies a table's rows out over an ordinary connection.
    /// </summary>
    /// <param name="connection">The connection string.</param>
    /// <param name="table">The table to copy.</param>
    /// <returns>The copied lines separated by semicolons.</returns>
    [PgFunction]
    public static string OrdinaryCopy(string connection, string table)
    {
        using PgReplicationConnection ordinary = PgReplicationConnection.Connect(connection, "ankus_ordinary", replication: false);
        ordinary.StartCopy("COPY " + table + " TO STDOUT");
        var lines = new List<string>();
        while (true)
        {
            PgReplicationReceiveStatus status = ordinary.Receive(TimeSpan.FromSeconds(30), out ReadOnlySpan<byte> message);
            if (status == PgReplicationReceiveStatus.Ended)
            {
                break;
            }

            if (status != PgReplicationReceiveStatus.Message)
            {
                throw new InvalidOperationException("The copy stopped without ending: " + status);
            }

            lines.Add(Encoding.UTF8.GetString(message).TrimEnd('\n'));
        }

        return string.Join(';', lines);
    }

    /// <summary>
    /// Runs a failing query, then shows the connection still works.
    /// </summary>
    /// <param name="connection">The connection string.</param>
    /// <returns>The remote SQLSTATE, message and a later query's result.</returns>
    [PgFunction]
    public static string ReplicationRemoteError(string connection)
    {
        using PgReplicationConnection replication = PgReplicationConnection.Connect(connection, "ankus_test");
        try
        {
            _ = replication.Query("SELECT 1 / 0", 1);
            return "no error";
        }
        catch (PgRemoteException exception)
        {
            return (exception.SqlState ?? "none") + "|" + exception.Message + "|" + Format(replication.Query("SELECT 42", 1));
        }
    }

    /// <summary>
    /// Copies a table's rows out over a replication connection, then runs a query on the same connection.
    /// </summary>
    /// <param name="connection">The connection string.</param>
    /// <param name="table">The table to copy.</param>
    /// <returns>The copied lines separated by semicolons, then the query's result.</returns>
    [PgFunction]
    public static string ReplicationCopy(string connection, string table)
    {
        using PgReplicationConnection replication = PgReplicationConnection.Connect(connection, "ankus_test");
        replication.StartCopy("COPY " + table + " TO STDOUT");
        var lines = new List<string>();
        while (true)
        {
            PgReplicationReceiveStatus status = replication.Receive(TimeSpan.FromSeconds(30), out ReadOnlySpan<byte> message);
            if (status == PgReplicationReceiveStatus.Ended)
            {
                break;
            }

            if (status != PgReplicationReceiveStatus.Message)
            {
                throw new InvalidOperationException("The copy stopped without ending: " + status);
            }

            lines.Add(Encoding.UTF8.GetString(message).TrimEnd('\n'));
        }

        return string.Join(';', lines) + "|" + Format(replication.Query("SELECT 'after'", 1));
    }

    /// <summary>
    /// Streams a logical slot's changes until enough rows arrive, confirming them with a status update.
    /// </summary>
    /// <param name="connection">The connection string.</param>
    /// <param name="slot">A slot that uses the <c>test_decoding</c> plugin.</param>
    /// <param name="changes">The number of change lines to collect.</param>
    /// <returns>The change lines, separated by semicolons.</returns>
    [PgFunction]
    public static string ReplicationStream(string connection, string slot, int changes)
    {
        using PgReplicationConnection replication = PgReplicationConnection.Connect(connection, "ankus_test");
        replication.StartCopy("START_REPLICATION SLOT \"" + slot + "\" LOGICAL 0/0 (\"include-xids\" '0', \"skip-empty-xacts\" '1')");
        var lines = new List<string>();
        long position = 0;
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (lines.Count < changes)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The stream sent " + lines.Count + " of " + changes + " changes.");
            }

            if (replication.Receive(TimeSpan.FromSeconds(1), out ReadOnlySpan<byte> message) != PgReplicationReceiveStatus.Message)
            {
                continue;
            }

            if (message[0] == (byte)'w')
            {
                // XLogData: the start and end of the change's WAL, the send time, then the plugin's output.
                position = BinaryPrimitives.ReadInt64BigEndian(message[9..]);
                string text = Encoding.UTF8.GetString(message[25..]);
                if (text.StartsWith("table ", StringComparison.Ordinal))
                {
                    lines.Add(text);
                }
            }
            else if (message[0] == (byte)'k' && message[17] == 1)
            {
                SendStatus(replication, position);
            }
        }

        SendStatus(replication, position);
        replication.EndStreaming();
        return string.Join(';', lines);
    }

    /// <summary>
    /// Waits on an idle stream for a message that never comes, so the statement's own timeout ends the call.
    /// </summary>
    /// <param name="connection">The connection string.</param>
    /// <param name="slot">A slot with nothing to send.</param>
    /// <returns>Never returns normally.</returns>
    [PgFunction]
    public static string ReplicationWaitForever(string connection, string slot)
    {
        using PgReplicationConnection replication = PgReplicationConnection.Connect(connection, "ankus_test");
        replication.StartCopy("START_REPLICATION SLOT \"" + slot + "\" LOGICAL 0/0");
        while (true)
        {
            if (replication.Receive(Timeout.InfiniteTimeSpan, out ReadOnlySpan<byte> message) == PgReplicationReceiveStatus.Ended)
            {
                return "ended";
            }

            if (message.Length > 0 && message[0] == (byte)'w')
            {
                return "change";
            }
        }
    }

    /// <summary>
    /// Shows that a closed connection refuses further use.
    /// </summary>
    /// <param name="connection">The connection string.</param>
    /// <returns>The exception's type name.</returns>
    [PgFunction]
    public static string ReplicationUseAfterDispose(string connection)
    {
        using PgReplicationConnection replication = PgReplicationConnection.Connect(connection, "ankus_test");
        replication.Dispose();
        replication.Dispose();
        try
        {
            _ = replication.Query("SELECT 1", 1);
            return "no error";
        }
        catch (ObjectDisposedException exception)
        {
            return exception.GetType().Name;
        }
    }

    /// <summary>
    /// Copies a remote table into a local one and runs a query on the same connection afterward.
    /// </summary>
    /// <param name="connection">The connection string.</param>
    /// <param name="command">The <c>COPY ... TO STDOUT</c> command.</param>
    /// <param name="table">The local table's OID.</param>
    /// <param name="columns">The local columns as a comma-separated list, or null for all.</param>
    /// <returns>The row count and the follow-up query's value.</returns>
    [PgFunction]
    public static string ReplicationCopyInto(string connection, string command, uint table, string? columns)
    {
        using PgReplicationConnection replication = PgReplicationConnection.Connect(connection, "ankus_test");
        replication.StartCopy(command);
        long rows = replication.CopyInto(table, columns?.Split(','));
        return rows.ToString(CultureInfo.InvariantCulture) + "|" + replication.Query("SELECT 'ready'", 1)[0][0];
    }

    /// <summary>
    /// Sends a standby status update that confirms everything up to <paramref name="position"/>.
    /// </summary>
    private static void SendStatus(PgReplicationConnection replication, long position)
    {
        Span<byte> update = stackalloc byte[34];
        update[0] = (byte)'r';
        BinaryPrimitives.WriteInt64BigEndian(update[1..], position);
        BinaryPrimitives.WriteInt64BigEndian(update[9..], position);
        BinaryPrimitives.WriteInt64BigEndian(update[17..], position);
        BinaryPrimitives.WriteInt64BigEndian(update[25..], 0);
        update[33] = 0;
        replication.Send(update);
    }

    private static string Format(IReadOnlyList<string?[]> rows)
        => string.Join(';', rows.Select(static row => string.Join('|', row.Select(static value => value ?? "<null>"))));
}
