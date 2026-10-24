using System.Buffers.Binary;
using System.Text;

namespace Ankus;

/// <summary>
/// Connects this backend to a PostgreSQL server for logical replication and SQL, through PostgreSQL's own
/// <c>libpqwalreceiver</c>, as the built-in logical replication subscriber connects.
/// </summary>
/// <remarks>
/// The connection is a logical replication connection to the database the connection string names, which also runs
/// ordinary SQL. It belongs to the backend that opened it, outlives transactions, and closes when disposed or when the
/// backend exits. Waits wake when this backend's latch is set and honor query cancellation. Connection and protocol
/// failures raise PostgreSQL errors in this backend; a failed command on the server throws
/// <see cref="PgRemoteException"/> and leaves this backend's transaction untouched.
/// </remarks>
public sealed unsafe class PgReplicationConnection : IDisposable
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly int _thread = Environment.CurrentManagedThreadId;

    private nint _id;

    private byte[] _message = new byte[256];

    private PgReplicationConnection(nint id, int serverVersion)
    {
        _id = id;
        ServerVersion = serverVersion;
    }

    /// <summary>
    /// Gets the server's version number, such as 180006 for PostgreSQL 18.6.
    /// </summary>
    public int ServerVersion { get; }

    /// <summary>
    /// Opens a logical replication connection.
    /// </summary>
    /// <param name="connectionString">A libpq connection string naming the server and database.</param>
    /// <param name="applicationName">The name the server shows for this connection, such as in
    /// <c>pg_stat_replication</c>.</param>
    /// <returns>The open connection.</returns>
    /// <exception cref="PgException">The server cannot be reached or rejects the connection.</exception>
    public static PgReplicationConnection Connect(string connectionString, string applicationName)
        => Connect(connectionString, applicationName, replication: true);

    /// <summary>
    /// Opens a logical replication connection, or an ordinary connection for SQL and copies.
    /// </summary>
    /// <param name="connectionString">A libpq connection string naming the server and database.</param>
    /// <param name="applicationName">The name the server shows for this connection, such as in
    /// <c>pg_stat_activity</c>.</param>
    /// <param name="replication">Whether to open a logical replication connection. An ordinary connection needs no
    /// <c>REPLICATION</c> attribute and no WAL sender; it runs SQL and <c>COPY</c> but not replication commands.</param>
    /// <returns>The open connection.</returns>
    /// <exception cref="PgException">The server cannot be reached or rejects the connection.</exception>
    public static PgReplicationConnection Connect(string connectionString, string applicationName, bool replication)
    {
        byte[] connection = Encode(connectionString, nameof(connectionString));
        byte[] application = Encode(applicationName, nameof(applicationName));
        fixed (byte* connectionAddress = connection, applicationAddress = application)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.Replication,
                _flags = 1,
                _pointer = (nint)connectionAddress,
                _data = (nint)applicationAddress,
                _value = replication ? 0 : 1,
            };
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            return new PgReplicationConnection(result._context, checked((int)result._value));
        }
    }

    /// <summary>
    /// Runs a command that returns no rows, such as <c>SET</c>, <c>BEGIN</c> or <c>DROP_REPLICATION_SLOT</c>.
    /// </summary>
    /// <param name="command">The command text.</param>
    /// <exception cref="PgRemoteException">The command failed on the server.</exception>
    /// <exception cref="PgException">The command returned rows or started a copy, or the connection failed.</exception>
    public void Execute(string command)
    {
        int status = Run(command, 0, out _);
        if (status != 1)
        {
            throw new PgException(PgSqlStates.ProtocolViolation, $"The replication command returned status {status}; a completed command was expected.");
        }
    }

    /// <summary>
    /// Runs a query and returns its rows as text, as the server formats each value.
    /// </summary>
    /// <param name="query">The query text, such as <c>SELECT</c>, <c>IDENTIFY_SYSTEM</c> or
    /// <c>CREATE_REPLICATION_SLOT</c>.</param>
    /// <param name="columns">The number of columns the query returns; a different number is an error.</param>
    /// <returns>Each row's values, with null for SQL NULL.</returns>
    /// <exception cref="PgRemoteException">The query failed on the server.</exception>
    /// <exception cref="PgException">The query returned another number of columns, or the connection failed.</exception>
    /// <remarks>
    /// Rows need a transaction, because PostgreSQL looks up their column types in the catalog; a background worker runs
    /// such queries inside <see cref="PgBackgroundWorker.RunTransaction(Action)"/>. Commands without rows, streaming and
    /// copies work outside transactions.
    /// </remarks>
    public IReadOnlyList<string?[]> Query(string query, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        int status = Run(query, columns, out ReadOnlySpan<byte> payload);
        if (status == 1)
        {
            return [];
        }

        if (status != 2)
        {
            throw new PgException(PgSqlStates.ProtocolViolation, $"The replication query returned status {status}; a row result was expected.");
        }

        int width = ReadInt(ref payload);
        int count = ReadInt(ref payload);
        if (width != columns || count < 0)
        {
            throw new InvalidOperationException("Invalid replication query result.");
        }

        string?[][] rows = new string?[count][];
        for (int row = 0; row < count; row++)
        {
            string?[] values = new string?[width];
            for (int column = 0; column < width; column++)
            {
                values[column] = ReadText(ref payload);
            }

            rows[row] = values;
        }

        return Array.AsReadOnly(rows);
    }

    /// <summary>
    /// Starts a copy: <c>COPY ... TO STDOUT</c>, whose rows <see cref="Receive"/> then returns, or
    /// <c>START_REPLICATION</c>, which streams changes and accepts <see cref="Send"/>.
    /// </summary>
    /// <param name="command">The command text.</param>
    /// <exception cref="PgRemoteException">The command failed on the server.</exception>
    /// <exception cref="PgException">The command did not start a copy, or the connection failed.</exception>
    public void StartCopy(string command)
    {
        int status = Run(command, 0, out _);
        if (status is not (4 or 5))
        {
            throw new PgException(PgSqlStates.ProtocolViolation, $"The replication command returned status {status}; a copy was expected.");
        }
    }

    /// <summary>
    /// Receives the next message of a copy, waiting up to <paramref name="timeout"/> for one.
    /// </summary>
    /// <param name="timeout">How long to wait, <see cref="TimeSpan.Zero"/> to only check, or
    /// <see cref="Timeout.InfiniteTimeSpan"/> to wait until a message arrives or the latch is set.</param>
    /// <param name="message">The message, valid until the connection's next call.</param>
    /// <returns>Whether a message arrived, the wait timed out, the latch woke it, or the copy ended.</returns>
    /// <exception cref="PgException">The connection failed, or the query was canceled.</exception>
    public PgReplicationReceiveStatus Receive(TimeSpan timeout, out ReadOnlySpan<byte> message)
    {
        long milliseconds = timeout == Timeout.InfiniteTimeSpan ? -1 : (long)timeout.TotalMilliseconds;
        if (milliseconds is < -1 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.Replication,
            _flags = 3,
            _context = Id,
            _value = (nint)milliseconds,
        };
        NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
        message = default;
        if (result._value < 0)
        {
            return PgReplicationReceiveStatus.Ended;
        }

        if (result._value == 0)
        {
            return result._context == 1 ? PgReplicationReceiveStatus.Woken : PgReplicationReceiveStatus.TimedOut;
        }

        int length = checked((int)result._value);
        if (length > _message.Length)
        {
            _message = new byte[Math.Max(length, _message.Length * 2)];
        }

        // The server's buffer is freed by the connection's next call, so the message is copied first.
        new ReadOnlySpan<byte>((void*)result._data, length).CopyTo(_message);
        message = _message.AsSpan(0, length);
        return PgReplicationReceiveStatus.Message;
    }

    /// <summary>
    /// Sends a message during a streaming copy, such as a standby status update.
    /// </summary>
    /// <param name="message">The message bytes.</param>
    /// <exception cref="PgException">The connection failed.</exception>
    public void Send(ReadOnlySpan<byte> message)
    {
        fixed (byte* address = message)
        {
            byte empty = 0;
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.Replication,
                _flags = 4,
                _context = Id,
                _data = address == null ? (nint)(&empty) : (nint)address,
                _length = (nuint)message.Length,
            };
            NativeMemoryContext.Invoke(ref request, out _);
        }
    }

    /// <summary>
    /// Reads the rows of a <c>COPY ... TO STDOUT</c> started with <see cref="StartCopy"/> into a local table, as the
    /// built-in subscriber's table synchronization does.
    /// </summary>
    /// <param name="relationOid">The local table's OID.</param>
    /// <param name="columns">The table's columns that receive the copy's columns, in order, or null for all of them.</param>
    /// <returns>The number of rows copied.</returns>
    /// <remarks>
    /// The copy must use COPY's text format. The rows go through PostgreSQL's own <c>COPY FROM</c>, which checks
    /// constraints, fills indexes, routes rows to partitions and fires triggers as <c>session_replication_role</c>
    /// selects. It needs <c>INSERT</c> on the table and, like the built-in subscriber, refuses a table whose row-level
    /// security applies to the user. The connection runs commands again afterward. If the copy fails, the server's copy
    /// is left unfinished, so dispose the connection.
    /// </remarks>
    /// <exception cref="PgException">A row is rejected, the user lacks the privilege, or the connection failed.</exception>
    public long CopyInto(uint relationOid, IReadOnlyList<string>? columns = null)
    {
        byte[]? names = null;
        if (columns is { Count: > 0 })
        {
            int size = 0;
            byte[][] encoded = new byte[columns.Count][];
            for (int index = 0; index < columns.Count; index++)
            {
                encoded[index] = Encode(columns[index], nameof(columns));
                size += encoded[index].Length;
            }

            names = new byte[size];
            int offset = 0;
            foreach (byte[] name in encoded)
            {
                name.CopyTo(names, offset);
                offset += name.Length;
            }
        }

        fixed (byte* address = names)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.Replication,
                _flags = 7,
                _context = Id,
                _other = (nint)relationOid,
                _data = (nint)address,
                _value = columns?.Count ?? 0,
            };
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            return checked((long)(ulong)result._value);
        }
    }

    /// <summary>
    /// Ends a streaming copy started with <c>START_REPLICATION</c>, so the connection runs commands again.
    /// </summary>
    /// <exception cref="PgException">The connection failed.</exception>
    public void EndStreaming()
    {
        NativeMemoryRequest request = new() { _operation = NativeMemoryOperation.Replication, _flags = 5, _context = Id };
        NativeMemoryContext.Invoke(ref request, out _);
    }

    /// <summary>
    /// Closes the connection.
    /// </summary>
    public void Dispose()
    {
        if (_id == 0)
        {
            return;
        }

        NativeMemoryRequest request = new() { _operation = NativeMemoryOperation.Replication, _flags = 6, _context = Id };
        _id = 0;
        NativeMemoryContext.Invoke(ref request, out _);
    }

    private nint Id
    {
        get
        {
            ObjectDisposedException.ThrowIf(_id == 0, this);
            if (Environment.CurrentManagedThreadId != _thread)
            {
                throw new InvalidOperationException("A replication connection belongs to the backend thread that opened it.");
            }

            return _id;
        }
    }

    /// <summary>
    /// Runs one command and returns its status, with a remote failure thrown and the rest of the result borrowed.
    /// </summary>
    private int Run(string text, int columns, out ReadOnlySpan<byte> payload)
    {
        byte[] encoded = Encode(text, nameof(text));
        NativeMemoryResult result;
        fixed (byte* address = encoded)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.Replication,
                _flags = 2,
                _context = Id,
                _pointer = (nint)address,
                _value = columns,
            };
            NativeMemoryContext.Invoke(ref request, out result);
        }

        payload = new ReadOnlySpan<byte>((void*)result._data, checked((int)result._length));
        int status = ReadInt(ref payload);
        if (status == 0)
        {
            string? sqlState = ReadText(ref payload);
            string message = ReadText(ref payload) ?? "The command failed on the server.";
            throw new PgRemoteException(sqlState, PrimaryMessage(message));
        }

        return status;
    }

    /// <summary>
    /// Removes the severity label libpq puts before a server's message, such as <c>ERROR:  </c>, and trailing lines.
    /// </summary>
    private static string PrimaryMessage(string message)
    {
        int label = message.IndexOf(":  ", StringComparison.Ordinal);
        if (label > 0 && message.AsSpan(0, label).IndexOfAnyExceptInRange('A', 'Z') < 0)
        {
            message = message[(label + 3)..];
        }

        return message.TrimEnd();
    }

    private static byte[] Encode(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Replication text cannot contain a zero character.", name);
        }

        byte[] bytes = new byte[s_utf8.GetByteCount(value) + 1];
        s_utf8.GetBytes(value, bytes);
        return bytes;
    }

    private static int ReadInt(ref ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            throw new InvalidOperationException("Truncated replication result.");
        }

        int value = BinaryPrimitives.ReadInt32BigEndian(payload);
        payload = payload[4..];
        return value;
    }

    private static string? ReadText(ref ReadOnlySpan<byte> payload)
    {
        int length = ReadInt(ref payload);
        if (length == -1)
        {
            return null;
        }

        if (length < 0 || length > payload.Length)
        {
            throw new InvalidOperationException("Truncated replication result.");
        }

        string text = s_utf8.GetString(payload[..length]);
        payload = payload[length..];
        return text;
    }
}
