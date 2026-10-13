using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.CompilerServices;

/// <summary>
/// Carries converted values between generated native wrappers and managed dispatchers.
/// Input buffers are borrowed for the duration of a call. Output buffers have an explicit matching allocator callback.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[StructLayout(LayoutKind.Sequential)]
public unsafe partial struct NativeValue
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [ThreadStatic]
    private static byte* s_resultText;

    [ThreadStatic]
    private static int s_resultTextCapacity;

    /// <summary>
    /// Gets whether the native value carries an enum label and its exact PostgreSQL type identity.
    /// </summary>
    internal readonly bool IsEnum => _auxiliary1 == -3 && _data != null;

    private long _integer;
    private int _auxiliary1;
    private int _auxiliary2;
    private int _temporalInfinity;
    private byte* _data;
    private int _length;
    private byte _isNull;
    private delegate* unmanaged[Cdecl]<void*, void> _release;

    /// <summary>
    /// Gets or sets the integer, Boolean, OID, floating-point bits, or opaque numeric datum length.
    /// </summary>
    public long Integral
    {
        readonly get => _integer;
        set => _integer = value;
    }

    /// <summary>
    /// Gets or sets whether the value represents SQL NULL, using a one-byte C flag.
    /// </summary>
    public byte IsNull
    {
        readonly get => _isNull;
        set => _isNull = value;
    }

    /// <summary>
    /// Reads a validated PostgreSQL date from the scalar transport.
    /// </summary>
    /// <returns>The date.</returns>
    public readonly PgDate ReadDate() => new(checked((int)_integer));

    /// <summary>
    /// Reads a validated PostgreSQL time from the scalar transport.
    /// </summary>
    /// <returns>The time.</returns>
    public readonly PgTime ReadTime() => new(_integer);

    /// <summary>
    /// Reads a time and the transport's east-of-UTC offset without depending on native struct padding.
    /// </summary>
    /// <returns>The time with a fixed offset.</returns>
    public readonly PgTimeTz ReadTimeTz() => new(new PgTime(_integer), _auxiliary1);

    /// <summary>
    /// Reads a timezone-free PostgreSQL timestamp.
    /// </summary>
    /// <returns>The timestamp.</returns>
    public readonly PgTimestamp ReadTimestamp() => new(_integer);

    /// <summary>
    /// Reads a PostgreSQL UTC timestamp.
    /// </summary>
    /// <returns>The timestamp.</returns>
    public readonly PgTimestampTz ReadTimestampTz() => new(_integer);

    /// <summary>
    /// Reads independent interval components without native padding or normalization.
    /// </summary>
    /// <returns>The interval.</returns>
    public readonly PgInterval ReadInterval() => _temporalInfinity switch
    {
        -1 => PgInterval.NegativeInfinity,
        0 => new(_auxiliary2, _auxiliary1, _integer),
        1 => PgInterval.PositiveInfinity,
        _ => throw new InvalidOperationException("Invalid interval infinity discriminator."),
    };

    /// <summary>
    /// Reads a PostgreSQL transaction ID from the scalar transport.
    /// </summary>
    /// <returns>The transaction ID.</returns>
    public readonly PgTransactionId ReadTransactionId() => new(checked((uint)_integer));

    /// <summary>
    /// Writes a PostgreSQL date without allocating a buffer.
    /// </summary>
    /// <param name="value">The date.</param>
    /// <returns>The scalar transport.</returns>
    public static NativeValue FromDate(PgDate value) => new() { _integer = value.DaysSinceEpoch };

    /// <summary>
    /// Writes a PostgreSQL time without allocating a buffer.
    /// </summary>
    /// <param name="value">The time.</param>
    /// <returns>The scalar transport.</returns>
    public static NativeValue FromTime(PgTime value) => new() { _integer = value.Microseconds };

    /// <summary>
    /// Writes a time and east-of-UTC offset as independent transport fields.
    /// </summary>
    /// <param name="value">The time with an offset.</param>
    /// <returns>The scalar transport.</returns>
    public static NativeValue FromTimeTz(PgTimeTz value)
        => new()
        {
            _integer = value.Time.Microseconds,
            _auxiliary1 = value.OffsetSeconds
        };

    /// <summary>
    /// Writes a timezone-free timestamp without allocating a buffer.
    /// </summary>
    /// <param name="value">The timestamp.</param>
    /// <returns>The scalar transport.</returns>
    public static NativeValue FromTimestamp(PgTimestamp value) => new() { _integer = value.MicrosecondsSinceEpoch };

    /// <summary>
    /// Writes a UTC timestamp without allocating a buffer.
    /// </summary>
    /// <param name="value">The timestamp.</param>
    /// <returns>The scalar transport.</returns>
    public static NativeValue FromTimestampTz(PgTimestampTz value) => new() { _integer = value.MicrosecondsSinceEpoch };

    /// <summary>
    /// Writes an interval's exact components without copying a platform-specific struct.
    /// </summary>
    /// <param name="value">The interval.</param>
    /// <returns>The scalar transport.</returns>
    public static NativeValue FromInterval(PgInterval value)
        => new()
        {
            _integer = value.Microseconds,
            _auxiliary1 = value.Days,
            _auxiliary2 = value.Months,
            _temporalInfinity = value.Infinity,
        };

    /// <summary>
    /// Writes a PostgreSQL transaction ID. PostgreSQL's invalid transaction ID maps to SQL NULL.
    /// </summary>
    /// <param name="value">The transaction ID.</param>
    /// <returns>The scalar transport.</returns>
    public static NativeValue FromTransactionId(PgTransactionId value)
        => new()
        {
            _integer = value.Value,
            _isNull = value.IsValid ? (byte)0 : (byte)1
        };

    /// <summary>
    /// Copies a borrowed UTF-8 input buffer into a managed string, rejecting malformed UTF-8.
    /// </summary>
    /// <returns>The decoded string.</returns>
    public readonly string ReadString() => s_utf8.GetString(new ReadOnlySpan<byte>(_data, _length));

    /// <summary>
    /// Reads character text holding exactly one Unicode scalar value.
    /// </summary>
    /// <returns>The scalar value.</returns>
    /// <exception cref="InvalidCastException">The text is empty or holds more than one scalar value.</exception>
    public readonly Rune ReadRune() => PgRuneText.Parse(ReadString());

    /// <summary>
    /// Encodes one Unicode scalar value as character text.
    /// </summary>
    /// <param name="value">The scalar value.</param>
    /// <returns>An owned output value.</returns>
    public static NativeValue FromRune(Rune value) => FromString(value.ToString());

    /// <summary>
    /// Copies an optional UTF-8 buffer, preserving the distinction between absent and empty diagnostics.
    /// </summary>
    /// <returns>The decoded string, or null if no buffer was supplied.</returns>
    internal readonly string? ReadOptionalString() => _data == null ? null : ReadString();

    /// <summary>
    /// Copies a borrowed bytea input buffer into a managed byte array.
    /// </summary>
    /// <returns>The binary value.</returns>
    public readonly byte[] ReadBytes() => new ReadOnlySpan<byte>(_data, _length).ToArray();

    /// <summary>
    /// Reads PostgreSQL's sixteen network-order UUID bytes without .NET's mixed-endian byte-array convention.
    /// </summary>
    /// <returns>The managed UUID.</returns>
    public readonly Guid ReadGuid() => new(new ReadOnlySpan<byte>(_data, _length), bigEndian: true);

    /// <summary>
    /// Reads an owned JSON value from the UTF-8 transport.
    /// </summary>
    /// <returns>The JSON value.</returns>
    public readonly PgJson ReadJson() => new(ReadString());

    /// <summary>
    /// Reads an owned JSONB text representation from the UTF-8 transport.
    /// </summary>
    /// <returns>The JSONB value.</returns>
    public readonly PgJsonb ReadJsonb() => new(ReadString());

    /// <summary>
    /// Copies opaque PostgreSQL numeric datum and portable binary bytes into an owned value.
    /// </summary>
    /// <returns>The full-range numeric.</returns>
    public readonly PgNumeric ReadNumeric()
    {
        if (_isNull != 0 || _auxiliary1 != -8 || _data == null || _length < 8 || _integer < 0 || _integer > _length)
        {
            throw new InvalidOperationException("Invalid native numeric transport.");
        }

        return PgNumeric.FromBinary(new ReadOnlySpan<byte>(_data, _length), checked((int)_integer));
    }

    /// <summary>
    /// Copies an owned numeric's opaque datum and portable bytes into a matching-allocator native buffer.
    /// </summary>
    /// <param name="value">The exact numeric, including display scale.</param>
    /// <returns>The owned numeric transport, which the native boundary must release.</returns>
    public static NativeValue FromNumeric(PgNumeric value)
    {
        NativeValue result = FromBytes(value.Storage.Buffer);
        result._integer = value.Storage.RawLength;
        result._auxiliary1 = -8;
        return result;
    }

    /// <summary>
    /// Copies a UUID into PostgreSQL's network-order sixteen-byte representation.
    /// </summary>
    /// <param name="value">The managed UUID.</param>
    /// <returns>The owned transport value.</returns>
    public static NativeValue FromGuid(Guid value)
    {
        NativeValue result = Allocate(16);
        value.TryWriteBytes(new Span<byte>(result._data, 16), bigEndian: true, out _);
        return result;
    }

    /// <summary>
    /// Encodes a managed string into an owned UTF-8 output buffer. PostgreSQL text cannot contain a zero character.
    /// The native wrapper invokes the supplied release callback after copying or if PostgreSQL raises an error.
    /// </summary>
    /// <param name="value">The managed string.</param>
    /// <returns>An owned output value.</returns>
    public static NativeValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("PostgreSQL text cannot contain a zero character.", nameof(value));
        }

        int length = s_utf8.GetByteCount(value);
        NativeValue result = Allocate(length);
        try
        {
            s_utf8.GetBytes(value, new Span<byte>(result._data, length));
            return result;
        }
        catch
        {
            NativeMemory.Free(result._data);
            throw;
        }
    }

    /// <summary>
    /// Encodes a scalar function's text result into a buffer this thread reuses, so the result needs no allocation or
    /// release of its own. PostgreSQL text cannot contain a zero character.
    /// </summary>
    /// <param name="value">The managed string.</param>
    /// <returns>A value that borrows the thread's result buffer.</returns>
    /// <remarks>
    /// The native wrapper copies the bytes into PostgreSQL memory as soon as the managed call returns, before any
    /// managed code can write another result. Values that wait, such as the rows of a set, use <see cref="FromString"/>.
    /// </remarks>
    public static NativeValue FromResultString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("PostgreSQL text cannot contain a zero character.", nameof(value));
        }

        // A buffer of three bytes per character holds any string in one encoding pass; a long string is counted
        // first, so the buffer does not grow to three times its size.
        int length = value.Length <= 0x55555 ? (value.Length * 3) + 1 : s_utf8.GetByteCount(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 0x3FFFFFFF - 4, nameof(value));
        if (length > s_resultTextCapacity || (s_resultTextCapacity > 65536 && length <= 65536))
        {
            // A large result does not keep its buffer for the rest of the backend's life.
            NativeMemory.Free(s_resultText);
            s_resultText = null;
            s_resultTextCapacity = 0;
            int capacity = Math.Max(length, 256);
            s_resultText = (byte*)NativeMemory.Alloc((nuint)capacity + 1);
            s_resultTextCapacity = capacity;
        }

        int written = s_utf8.GetBytes(value, new Span<byte>(s_resultText, s_resultTextCapacity));
        s_resultText[written] = 0;
        return new NativeValue { _data = s_resultText, _length = written };
    }

    /// <summary>
    /// Copies a managed binary value into an owned native output buffer with a matching release callback.
    /// </summary>
    /// <param name="value">The binary value.</param>
    /// <returns>An owned output value.</returns>
    public static NativeValue FromBytes(ReadOnlySpan<byte> value)
    {
        NativeValue result = Allocate(value.Length);
        value.CopyTo(new Span<byte>(result._data, result._length));
        return result;
    }

    /// <summary>
    /// Releases an owned transport buffer through its matching allocator and clears this value.
    /// </summary>
    internal void Release()
    {
        if (_release != null)
        {
            _release(_data);
        }

        this = default;
    }

    private static NativeValue Allocate(int length)
    {
        // A varlena includes a four-byte header and must fit PostgreSQL's MaxAllocSize.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 0x3FFFFFFF - 4);
        byte* data = (byte*)NativeMemory.Alloc((nuint)length + 1);
        data[length] = 0;
        return new NativeValue { _data = data, _length = length, _release = &ReleaseBuffer };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseBuffer(void* data) => NativeMemory.Free(data);
}
