using System.Buffers.Binary;
using System.Text;

namespace Ankus.Examples.Subscriber;

/// <summary>
/// Reads the big-endian fields of one replication protocol message.
/// </summary>
/// <param name="data">The message.</param>
internal ref struct ProtocolReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _data = data;

    /// <summary>
    /// Reads one byte.
    /// </summary>
    /// <returns>The byte.</returns>
    public byte ReadByte() => Take(1)[0];

    /// <summary>
    /// Reads a 16-bit integer.
    /// </summary>
    /// <returns>The integer.</returns>
    public short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(Take(2));

    /// <summary>
    /// Reads a 32-bit integer.
    /// </summary>
    /// <returns>The integer.</returns>
    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    /// <summary>
    /// Reads a 64-bit integer.
    /// </summary>
    /// <returns>The integer.</returns>
    public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));

    /// <summary>
    /// Reads a write-ahead log position.
    /// </summary>
    /// <returns>The position.</returns>
    public PgLsn ReadLsn() => new(BinaryPrimitives.ReadUInt64BigEndian(Take(8)));

    /// <summary>
    /// Reads a timestamp in microseconds since 2000.
    /// </summary>
    /// <returns>The timestamp.</returns>
    public PgTimestampTz ReadTimestamp() => new(ReadInt64());

    /// <summary>
    /// Reads bytes.
    /// </summary>
    /// <param name="count">The number of bytes.</param>
    /// <returns>The bytes, borrowed from the message.</returns>
    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count);

    /// <summary>
    /// Reads a zero-terminated name, which the sample's UTF-8 databases send as UTF-8.
    /// </summary>
    /// <returns>The name.</returns>
    public string ReadString()
    {
        int end = _data.IndexOf((byte)0);
        if (end < 0)
        {
            throw new InvalidDataException("A replication message's name has no terminator.");
        }

        string value = Encoding.UTF8.GetString(_data[..end]);
        _data = _data[(end + 1)..];
        return value;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _data.Length < count)
        {
            throw new InvalidDataException("A replication message ended early.");
        }

        ReadOnlySpan<byte> value = _data[..count];
        _data = _data[count..];
        return value;
    }
}
