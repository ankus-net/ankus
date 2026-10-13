using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Formats.Cbor;
using System.Globalization;
using System.Text.Json;

namespace Ankus.CompilerServices;

/// <summary>
/// Writes bounded JSON or CBOR tokens from statically generated type contracts.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PgTypeWriter : IDisposable
{
    /// <summary>
    /// Contains the CBOR initial byte of a binary32 floating-point value.
    /// </summary>
    private const byte SinglePrecisionFloatHeader = 0xFA;

    /// <summary>
    /// Contains the CBOR initial byte of a binary64 floating-point value.
    /// </summary>
    private const byte DoublePrecisionFloatHeader = 0xFB;

    private readonly IBufferWriter<byte> _destination;
    private readonly Utf8JsonWriter? _json;
    private readonly CborWriter? _cbor;
    private int _depth;

    /// <summary>
    /// Selects JSON text or compact, definite-length CBOR storage.
    /// </summary>
    internal PgTypeWriter(IBufferWriter<byte> destination, bool json)
    {
        _destination = destination;
        if (json)
        {
            _json = new Utf8JsonWriter(destination, new JsonWriterOptions { MaxDepth = 64, Encoder = PgJsonTextEncoder.Instance });
        }
        else
        {
            _cbor = new CborWriter(CborConformanceMode.Strict);
        }
    }

    /// <summary>
    /// Writes a null member or element.
    /// </summary>
    public void WriteNull()
    {
        if (_cbor is not null)
        {
            _cbor.WriteNull();
        }
        else
        {
            _json!.WriteNullValue();
        }
    }

    /// <summary>
    /// Writes a Boolean.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteBoolean(bool value)
    {
        if (_cbor is not null)
        {
            _cbor.WriteBoolean(value);
        }
        else
        {
            _json!.WriteBooleanValue(value);
        }
    }

    /// <summary>
    /// Writes an exact signed integer.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteInt64(long value)
    {
        if (_cbor is not null)
        {
            _cbor.WriteInt64(value);
        }
        else
        {
            _json!.WriteNumberValue(value);
        }
    }

    /// <summary>
    /// Writes an exact unsigned integer.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteUInt64(ulong value)
    {
        if (_cbor is not null)
        {
            _cbor.WriteUInt64(value);
        }
        else
        {
            _json!.WriteNumberValue(value);
        }
    }

    /// <summary>
    /// Writes binary32 storage, keeping a NaN's sign and payload bits in a full-width float; JSON rejects non-finite numbers.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteSingle(float value)
    {
        if (_cbor is not null)
        {
            if (float.IsNaN(value))
            {
                Span<byte> encoded = stackalloc byte[1 + sizeof(float)];
                encoded[0] = SinglePrecisionFloatHeader;
                BinaryPrimitives.WriteSingleBigEndian(encoded[1..], value);
                _cbor.WriteEncodedValue(encoded);
            }
            else
            {
                _cbor.WriteSingle(value);
            }
        }
        else
        {
            _json!.WriteNumberValue(value);
        }
    }

    /// <summary>
    /// Writes binary64 storage, keeping a NaN's sign and payload bits in a full-width double; JSON rejects non-finite numbers.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteDouble(double value)
    {
        if (_cbor is not null)
        {
            if (double.IsNaN(value))
            {
                Span<byte> encoded = stackalloc byte[1 + sizeof(double)];
                encoded[0] = DoublePrecisionFloatHeader;
                BinaryPrimitives.WriteDoubleBigEndian(encoded[1..], value);
                _cbor.WriteEncodedValue(encoded);
            }
            else
            {
                _cbor.WriteDouble(value);
            }
        }
        else
        {
            _json!.WriteNumberValue(value);
        }
    }

    /// <summary>
    /// Writes exact decimal-fraction storage or a JSON decimal number; rejects unrepresentable negative zero.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteDecimal(decimal value)
    {
        if (value == 0 && decimal.IsNegative(value))
        {
            throw new ArgumentException("Negative decimal zero cannot be represented losslessly in custom-type storage.", nameof(value));
        }

        // Binary storage writes a decimal as a two-element array, one level below its object, and .NET's CBOR reader
        // reads at most 64 levels. Both formats count it, so every value written can be read back in either.
        if (_depth >= 64)
        {
            throw new InvalidOperationException("Custom-type nesting exceeds 64 levels.");
        }

        if (_cbor is not null)
        {
            _cbor.WriteDecimal(value);
        }
        else
        {
            _json!.WriteNumberValue(value);
        }
    }

    /// <summary>
    /// Writes a UUID as System.Text.Json's lowercase text, or as a CBOR UUID (tag 37) holding its 16 bytes in network order.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteGuid(Guid value)
    {
        if (_cbor is not null)
        {
            Span<byte> bytes = stackalloc byte[16];
            _ = value.TryWriteBytes(bytes, bigEndian: true, out _);
            _cbor.WriteTag(PgSerializationTags.Uuid);
            _cbor.WriteByteString(bytes);
        }
        else
        {
            _json!.WriteStringValue(value);
        }
    }

    /// <summary>
    /// Writes a date as <c>yyyy-MM-dd</c>, tagged as a CBOR full-date (tag 1004) in binary storage.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteDateOnly(DateOnly value)
    {
        string text = value.ToString("O", CultureInfo.InvariantCulture);
        if (_cbor is not null)
        {
            _cbor.WriteTag(PgSerializationTags.FullDate);
            _cbor.WriteTextString(text);
        }
        else
        {
            _json!.WriteStringValue(text);
        }
    }

    /// <summary>
    /// Writes a time of day in System.Text.Json's constant format, <c>HH:mm:ss</c> with seven fractional digits when present.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteTimeOnly(TimeOnly value) => WriteText(value.ToTimeSpan().ToString("c", CultureInfo.InvariantCulture));

    /// <summary>
    /// Writes a duration in System.Text.Json's constant format, <c>[-][d.]hh:mm:ss[.fffffff]</c>.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteTimeSpan(TimeSpan value) => WriteText(value.ToString("c", CultureInfo.InvariantCulture));

    /// <summary>
    /// Writes a date and time with its kind: System.Text.Json's ISO 8601 text, or round-trip <c>O</c> text in binary storage.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteDateTime(DateTime value)
    {
        if (_cbor is not null)
        {
            _cbor.WriteTextString(value.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            _json!.WriteStringValue(value);
        }
    }

    /// <summary>
    /// Writes a date and time with its offset: System.Text.Json's ISO 8601 text, or a CBOR date/time string (tag 0).
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteDateTimeOffset(DateTimeOffset value)
    {
        if (_cbor is not null)
        {
            _cbor.WriteDateTimeOffset(value);
        }
        else
        {
            _json!.WriteStringValue(value);
        }
    }

    /// <summary>
    /// Writes ASCII text produced by a framework formatter as a JSON or CBOR string.
    /// </summary>
    /// <param name="value">The text.</param>
    private void WriteText(string value)
    {
        if (_cbor is not null)
        {
            _cbor.WriteTextString(value);
        }
        else
        {
            _json!.WriteStringValue(value);
        }
    }

    /// <summary>
    /// Writes a non-null Unicode string without replacement of malformed UTF-16.
    /// </summary>
    /// <param name="value">The value.</param>
    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ = PgSerializationText.Utf8.GetByteCount(value);
        if (_cbor is not null)
        {
            _cbor.WriteTextString(value);
        }
        else
        {
            _json!.WriteStringValue(value);
        }
    }

    /// <summary>
    /// Orders dictionary entries by ordinal key so equal dictionaries write identical storage and text.
    /// </summary>
    /// <typeparam name="TValue">The dictionary value type.</typeparam>
    /// <param name="value">The dictionary; its comparer does not affect the order.</param>
    /// <returns>A new array of the entries in ascending ordinal UTF-16 key order.</returns>
    /// <exception cref="InvalidOperationException">The dictionary's comparer admits ordinally equal keys.</exception>
    public static KeyValuePair<string, TValue>[] GetOrderedEntries<TValue>(Dictionary<string, TValue> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        KeyValuePair<string, TValue>[] entries = [.. value];
        Array.Sort(entries, static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        for (int index = 1; index < entries.Length; index++)
        {
            if (string.Equals(entries[index - 1].Key, entries[index].Key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Dictionary keys must be unique under ordinal comparison.");
            }
        }

        return entries;
    }

    /// <summary>
    /// Starts an object with its statically known member count.
    /// </summary>
    /// <param name="count">The number of key/value pairs.</param>
    public void WriteStartObject(int count)
    {
        Enter();
        if (_cbor is not null)
        {
            _cbor.WriteStartMap(count);
        }
        else
        {
            _json!.WriteStartObject();
        }
    }

    /// <summary>
    /// Writes a member or dictionary key.
    /// </summary>
    /// <param name="name">The exact case-sensitive key.</param>
    public void WritePropertyName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _ = PgSerializationText.Utf8.GetByteCount(name);
        if (_cbor is not null)
        {
            _cbor.WriteTextString(name);
        }
        else
        {
            _json!.WritePropertyName(name);
        }
    }

    /// <summary>
    /// Ends the current object.
    /// </summary>
    public void WriteEndObject()
    {
        if (_cbor is not null)
        {
            _cbor.WriteEndMap();
        }
        else
        {
            _json!.WriteEndObject();
        }

        _depth--;
    }

    /// <summary>
    /// Starts a sequence with its known element count.
    /// </summary>
    /// <param name="count">The number of elements.</param>
    public void WriteStartArray(int count)
    {
        Enter();
        if (_cbor is not null)
        {
            _cbor.WriteStartArray(count);
        }
        else
        {
            _json!.WriteStartArray();
        }
    }

    /// <summary>
    /// Ends the current sequence.
    /// </summary>
    public void WriteEndArray()
    {
        if (_cbor is not null)
        {
            _cbor.WriteEndArray();
        }
        else
        {
            _json!.WriteEndArray();
        }

        _depth--;
    }

    /// <summary>
    /// Releases any JSON output buffers.
    /// </summary>
    public void Dispose() => _json?.Dispose();

    /// <summary>
    /// Flushes exactly one complete value to the caller's buffer.
    /// </summary>
    internal void Complete()
    {
        if (_depth != 0 || (_json is not null && _json.BytesCommitted + _json.BytesPending == 0))
        {
            throw new InvalidOperationException("Incomplete custom-type output.");
        }

        if (_cbor is not null)
        {
            Span<byte> destination = _destination.GetSpan(_cbor.BytesWritten);
            int written = _cbor.Encode(destination);
            _destination.Advance(written);
        }
        else
        {
            _json!.Flush();
        }
    }

    /// <summary>
    /// Bounds recursive contracts and cyclic object graphs before descending further.
    /// </summary>
    private void Enter()
    {
        if (++_depth > 64)
        {
            throw new InvalidOperationException("Custom-type nesting exceeds 64 levels.");
        }
    }
}
