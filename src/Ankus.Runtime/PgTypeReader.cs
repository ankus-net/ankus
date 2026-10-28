using System;
using System.ComponentModel;
using System.Formats.Cbor;
using System.Globalization;
using System.Text.Json;

namespace Ankus;

/// <summary>
/// Reads bounded JSON or CBOR tokens for generated type contracts without runtime reflection.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public ref struct PgTypeReader
{
    private Utf8JsonReader _json;
    private readonly CborReader? _cbor;
    private readonly ReadOnlyMemory<byte> _binaryInput;
    private bool _hasJsonToken;
    private int _depth;

    /// <summary>
    /// Creates a cursor over one payload; binary input is copied into the CBOR reader's owned memory.
    /// </summary>
    internal PgTypeReader(ReadOnlySpan<byte> input, bool json)
    {
        _json = json ? new Utf8JsonReader(input, new JsonReaderOptions { MaxDepth = 64 }) : default;
        _binaryInput = json ? default : input.ToArray();
        _cbor = json ? null : new CborReader(_binaryInput, CborConformanceMode.Strict);
        _hasJsonToken = json && _json.Read();
        if (input.IsEmpty || (json && !_hasJsonToken))
        {
            throw new FormatException("Expected one serialized value.");
        }
    }

    /// <summary>
    /// Creates an independent binary cursor over already owned input at its enclosing contract depth.
    /// </summary>
    private PgTypeReader(ReadOnlyMemory<byte> input, int depth)
    {
        _binaryInput = input;
        _cbor = new CborReader(input, CborConformanceMode.Strict);
        _depth = depth;
    }

    /// <summary>
    /// Inspects a complete object's discriminator without consuming the original cursor.
    /// </summary>
    /// <param name="name">The exact case-sensitive discriminator property name.</param>
    /// <returns>The string or signed 32-bit integer discriminator, or null when the property is absent.</returns>
    public readonly object? PeekDiscriminator(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        PgTypeReader probe = _cbor is null
            ? this
            : new PgTypeReader(_binaryInput.Slice(_binaryInput.Length - _cbor.BytesRemaining), _depth);
        probe.ReadStartObject();
        bool found = false;
        object? discriminator = null;
        while (probe.ReadPropertyName() is { } property)
        {
            if (!string.Equals(name, property, StringComparison.Ordinal))
            {
                probe.Skip();
                continue;
            }

            if (found)
            {
                throw new FormatException("Duplicate custom-type discriminator property.");
            }

            found = true;
            discriminator = probe.ReadDiscriminator();
        }

        return discriminator;
    }

    /// <summary>
    /// Reads only the exact token kinds supported by declared polymorphic discriminators.
    /// </summary>
    private object ReadDiscriminator()
    {
        if (_cbor is null)
        {
            return _json.TokenType switch
            {
                JsonTokenType.String => ReadString(),
                JsonTokenType.Number => checked((int)ReadInt64()),
                _ => throw new FormatException("A custom-type discriminator must be a string or signed 32-bit integer."),
            };
        }

        return _cbor.PeekState() switch
        {
            CborReaderState.TextString or CborReaderState.StartIndefiniteLengthTextString => ReadString(),
            CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger => checked((int)ReadInt64()),
            _ => throw new FormatException("A custom-type discriminator must be a string or signed 32-bit integer."),
        };
    }

    /// <summary>
    /// Consumes a null token if present.
    /// </summary>
    /// <returns>Whether a null was consumed.</returns>
    public bool ReadNull()
    {
        if (_cbor is not null)
        {
            if (_cbor.PeekState() != CborReaderState.Null)
            {
                return false;
            }

            _cbor.ReadNull();
            return true;
        }

        if (!_hasJsonToken || _json.TokenType != JsonTokenType.Null)
        {
            return false;
        }

        Next();
        return true;
    }

    /// <summary>
    /// Reads an exact Boolean token.
    /// </summary>
    /// <returns>The value.</returns>
    public bool ReadBoolean()
    {
        if (_cbor is not null)
        {
            return _cbor.ReadBoolean();
        }

        bool value = _json.GetBoolean();
        Next();
        return value;
    }

    /// <summary>
    /// Reads an exact signed integer without accepting a floating-point token.
    /// </summary>
    /// <returns>The value.</returns>
    public long ReadInt64()
    {
        if (_cbor is not null)
        {
            return _cbor.ReadInt64();
        }

        long value = _json.GetInt64();
        Next();
        return value;
    }

    /// <summary>
    /// Reads an exact unsigned integer.
    /// </summary>
    /// <returns>The value.</returns>
    public ulong ReadUInt64()
    {
        if (_cbor is not null)
        {
            return _cbor.ReadUInt64();
        }

        ulong value = _json.GetUInt64();
        Next();
        return value;
    }

    /// <summary>
    /// Reads a binary32 value, keeping NaN payload bits and rejecting wider precision or payload loss and JSON overflow.
    /// </summary>
    /// <returns>The value.</returns>
    public float ReadSingle()
    {
        if (_cbor is not null)
        {
            if (_cbor.PeekState() == CborReaderState.SinglePrecisionFloat)
            {
                return _cbor.ReadSingle();
            }

            double number = ReadDouble();
            float result = (float)number;
            if (BitConverter.DoubleToInt64Bits(result) != BitConverter.DoubleToInt64Bits(number))
            {
                throw new FormatException("The CBOR value is not exactly representable as Single.");
            }

            return result;
        }

        float value = _json.GetSingle();
        if (!float.IsFinite(value) || (value is 0 && HasNonzeroJsonSignificand()))
        {
            throw new FormatException("JSON number exceeds the Single range.");
        }

        Next();
        return value;
    }

    /// <summary>
    /// Reads a binary64 value, keeping NaN payload bits and rejecting integer precision loss and JSON overflow.
    /// </summary>
    /// <returns>The value.</returns>
    public double ReadDouble()
    {
        if (_cbor is not null)
        {
            if (_cbor.PeekState() == CborReaderState.UnsignedInteger)
            {
                ulong integer = _cbor.ReadUInt64();
                double converted = integer;
                if (new System.Numerics.BigInteger(converted) != integer)
                {
                    throw new FormatException("The CBOR integer is not exactly representable as Double.");
                }

                return converted;
            }

            if (_cbor.PeekState() == CborReaderState.NegativeInteger)
            {
                System.Numerics.BigInteger integer = -1 - new System.Numerics.BigInteger(_cbor.ReadCborNegativeIntegerRepresentation());
                double converted = (double)integer;
                if (new System.Numerics.BigInteger(converted) != integer)
                {
                    throw new FormatException("The CBOR integer is not exactly representable as Double.");
                }

                return converted;
            }

            return _cbor.ReadDouble();
        }

        double value = _json.GetDouble();
        if (!double.IsFinite(value) || (value is 0 && HasNonzeroJsonSignificand()))
        {
            throw new FormatException("JSON number exceeds the Double range.");
        }

        Next();
        return value;
    }

    /// <summary>
    /// Reads an exact decimal and scale using the CBOR decimal-fraction tag or a JSON number; rejects negative zero.
    /// </summary>
    /// <returns>The value.</returns>
    public decimal ReadDecimal()
    {
        // A decimal counts as a nesting level in both formats, as the writer counts it.
        if (_depth >= 64)
        {
            throw new FormatException("Custom-type nesting exceeds 64 levels.");
        }

        if (_cbor is not null)
        {
            return _cbor.PeekState() switch
            {
                CborReaderState.UnsignedInteger => _cbor.ReadUInt64(),
                CborReaderState.NegativeInteger => -1m - _cbor.ReadCborNegativeIntegerRepresentation(),
                _ => ReadCborDecimal(),
            };
        }

        decimal value = _json.GetDecimal();
        if (value == 0 && _json.ValueSpan[0] == '-')
        {
            throw new FormatException("Negative decimal zero cannot be represented losslessly in custom-type storage.");
        }

        if (NormalizeDecimal(PgSerializationText.Utf8.GetString(_json.ValueSpan)) != NormalizeDecimal(value.ToString(CultureInfo.InvariantCulture)))
        {
            throw new FormatException("The JSON number is not exactly representable as Decimal.");
        }

        Next();
        return value;
    }

    /// <summary>
    /// Reconstructs a decimal fraction directly so zero retains its encoded scale.
    /// </summary>
    private readonly decimal ReadCborDecimal()
    {
        CborReader reader = _cbor!;
        if (reader.ReadTag() != CborTag.DecimalFraction || reader.ReadStartArray() != 2)
        {
            throw new FormatException("Expected a CBOR decimal fraction with an exponent and mantissa.");
        }

        long exponent = reader.ReadInt64();
        decimal mantissa = reader.PeekState() switch
        {
            CborReaderState.UnsignedInteger => reader.ReadUInt64(),
            CborReaderState.NegativeInteger => -1m - reader.ReadCborNegativeIntegerRepresentation(),
            CborReaderState.Tag when reader.PeekTag() is CborTag.UnsignedBigNum or CborTag.NegativeBigNum => (decimal)reader.ReadBigInteger(),
            _ => throw new FormatException("Expected an integral CBOR decimal mantissa."),
        };
        reader.ReadEndArray();
        if (exponent is < -28 or > 28)
        {
            throw new FormatException("The CBOR decimal exponent is outside the representable range.");
        }

        if (exponent >= 0)
        {
            for (int position = 0; position < exponent; position++)
            {
                mantissa *= 10;
            }

            return mantissa;
        }

        Span<int> bits = stackalloc int[4];
        decimal.GetBits(mantissa, bits);
        return new decimal(bits[0], bits[1], bits[2], decimal.IsNegative(mantissa), (byte)-exponent);
    }

    /// <summary>
    /// Reads a UUID from System.Text.Json's text, or from a CBOR UUID (tag 37) of exactly 16 bytes in network order.
    /// </summary>
    /// <returns>The value.</returns>
    public Guid ReadGuid()
    {
        if (_cbor is not null)
        {
            ExpectTag(PgSerializationTags.Uuid);
            byte[] bytes = _cbor.ReadByteString();
            return bytes.Length == 16 ? new Guid(bytes, bigEndian: true) : throw new FormatException("A CBOR UUID holds exactly 16 bytes.");
        }

        Guid value = _json.GetGuid();
        Next();
        return value;
    }

    /// <summary>
    /// Reads a date written exactly as <c>yyyy-MM-dd</c>, tagged as a CBOR full-date (tag 1004) in binary storage.
    /// </summary>
    /// <returns>The value.</returns>
    public DateOnly ReadDateOnly()
    {
        if (_cbor is not null)
        {
            ExpectTag(PgSerializationTags.FullDate);
        }

        return DateOnly.TryParseExact(ReadString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly value)
            ? value
            : throw new FormatException("Expected a date in yyyy-MM-dd form.");
    }

    /// <summary>
    /// Reads a time of day in the constant format, rejecting negative values and values of a day or more.
    /// </summary>
    /// <returns>The value.</returns>
    public TimeOnly ReadTimeOnly()
    {
        TimeSpan value = ReadTimeSpan();
        return value >= TimeSpan.Zero && value < TimeSpan.FromDays(1)
            ? new TimeOnly(value.Ticks)
            : throw new FormatException("A time of day must be at least zero and less than one day.");
    }

    /// <summary>
    /// Reads a duration in the constant format, <c>[-][d.]hh:mm:ss[.fffffff]</c>.
    /// </summary>
    /// <returns>The value.</returns>
    public TimeSpan ReadTimeSpan()
        => TimeSpan.TryParseExact(ReadString(), "c", CultureInfo.InvariantCulture, out TimeSpan value)
            ? value
            : throw new FormatException("Expected a duration in the constant [-][d.]hh:mm:ss[.fffffff] form.");

    /// <summary>
    /// Reads a date and time with its kind from System.Text.Json's ISO 8601 text, or from round-trip <c>O</c> text in binary storage.
    /// </summary>
    /// <returns>The value.</returns>
    public DateTime ReadDateTime()
    {
        if (_cbor is not null)
        {
            return DateTime.TryParseExact(_cbor.ReadTextString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out DateTime stored) ? stored : throw new FormatException("Expected a round-trip date and time.");
        }

        DateTime value = _json.GetDateTime();
        Next();
        return value;
    }

    /// <summary>
    /// Reads a date and time with its offset from System.Text.Json's ISO 8601 text, or from a CBOR date/time string (tag 0).
    /// </summary>
    /// <returns>The value.</returns>
    public DateTimeOffset ReadDateTimeOffset()
    {
        if (_cbor is not null)
        {
            return _cbor.ReadDateTimeOffset();
        }

        DateTimeOffset value = _json.GetDateTimeOffset();
        Next();
        return value;
    }

    /// <summary>
    /// Reads an owned Unicode string and rejects null.
    /// </summary>
    /// <returns>The value.</returns>
    public string ReadString()
    {
        if (_cbor is not null)
        {
            return _cbor.ReadTextString();
        }

        string value = _json.GetString() ?? throw new FormatException("Expected a non-null string.");
        Next();
        return value;
    }

    /// <summary>
    /// Starts a map whose keys are member names or dictionary keys.
    /// </summary>
    public void ReadStartObject()
    {
        Enter();
        if (_cbor is not null)
        {
            _cbor.ReadStartMap();
        }
        else
        {
            Expect(JsonTokenType.StartObject);
            Next();
        }
    }

    /// <summary>
    /// Reads a key or consumes the end of the current map.
    /// </summary>
    /// <returns>The key, or null when the map ended.</returns>
    public string? ReadPropertyName()
    {
        if (_cbor is not null)
        {
            if (_cbor.PeekState() != CborReaderState.EndMap)
            {
                return _cbor.ReadTextString();
            }

            _cbor.ReadEndMap();
        }
        else
        {
            if (_json.TokenType != JsonTokenType.EndObject)
            {
                Expect(JsonTokenType.PropertyName);
                return ReadString();
            }

            Next();
        }

        _depth--;
        return null;
    }

    /// <summary>
    /// Starts a sequence without allocating from untrusted declared lengths.
    /// </summary>
    public void ReadStartArray()
    {
        Enter();
        if (_cbor is not null)
        {
            _cbor.ReadStartArray();
        }
        else
        {
            Expect(JsonTokenType.StartArray);
            Next();
        }
    }

    /// <summary>
    /// Consumes a sequence end when reached.
    /// </summary>
    /// <returns>Whether the sequence ended.</returns>
    public bool ReadEndArray()
    {
        if (_cbor is not null)
        {
            if (_cbor.PeekState() != CborReaderState.EndArray)
            {
                return false;
            }

            _cbor.ReadEndArray();
        }
        else
        {
            if (!_hasJsonToken || _json.TokenType != JsonTokenType.EndArray)
            {
                return false;
            }

            Next();
        }

        _depth--;
        return true;
    }

    /// <summary>
    /// Skips an unknown member while still checking syntax, Unicode and nesting limits.
    /// </summary>
    public void Skip()
    {
        if (_cbor is null)
        {
            switch (_json.TokenType)
            {
                case JsonTokenType.StartObject:
                    ReadStartObject();
                    while (ReadPropertyName() is not null)
                    {
                        Skip();
                    }

                    break;
                case JsonTokenType.StartArray:
                    ReadStartArray();
                    while (!ReadEndArray())
                    {
                        Skip();
                    }

                    break;
                case JsonTokenType.String:
                    ReadString();
                    break;
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    Next();
                    break;
                default:
                    throw new FormatException("Expected an unknown member value.");
            }

            return;
        }

        SkipCbor(0);
    }

    /// <summary>
    /// Rejects truncated or trailing root data.
    /// </summary>
    internal readonly void Complete()
    {
        if (_depth != 0 || (_cbor is null ? _hasJsonToken : _cbor.BytesRemaining != 0))
        {
            throw new FormatException("Expected exactly one complete serialized value.");
        }
    }

    /// <summary>
    /// Applies the same recursion limit to known and unknown binary members.
    /// </summary>
    private readonly void SkipCbor(int extraDepth)
    {
        CborReader reader = _cbor!;
        while (reader.PeekState() == CborReaderState.Tag)
        {
            if (reader.PeekTag() == CborTag.DecimalFraction)
            {
                if (_depth + extraDepth >= 64)
                {
                    throw new FormatException("Custom-type nesting exceeds 64 levels.");
                }

                SkipCborDecimal(reader);
                return;
            }

            reader.ReadTag();
        }

        CborReaderState state = reader.PeekState();
        if (_depth + extraDepth >= 64 && state is CborReaderState.StartArray or CborReaderState.StartMap)
        {
            throw new FormatException("Custom-type nesting exceeds 64 levels.");
        }

        switch (state)
        {
            case CborReaderState.StartArray:
                reader.ReadStartArray();
                while (reader.PeekState() != CborReaderState.EndArray)
                {
                    SkipCbor(extraDepth + 1);
                }

                reader.ReadEndArray();
                break;
            case CborReaderState.StartMap:
                reader.ReadStartMap();
                while (reader.PeekState() != CborReaderState.EndMap)
                {
                    SkipCbor(extraDepth + 1);
                    SkipCbor(extraDepth + 1);
                }

                reader.ReadEndMap();
                break;
            case CborReaderState.TextString:
            case CborReaderState.StartIndefiniteLengthTextString:
                reader.ReadTextString();
                break;
            default:
                reader.SkipValue();
                break;
        }
    }

    /// <summary>
    /// Validates an unknown decimal fraction as one scalar without narrowing its exponent or mantissa.
    /// </summary>
    private static void SkipCborDecimal(CborReader reader)
    {
        reader.ReadTag();
        int? length = reader.ReadStartArray();
        if (length is not null and not 2)
        {
            throw new FormatException("Expected a CBOR decimal fraction with an exponent and mantissa.");
        }

        SkipCborInteger(reader);
        if (reader.PeekState() == CborReaderState.Tag && reader.PeekTag() is CborTag.UnsignedBigNum or CborTag.NegativeBigNum)
        {
            reader.ReadTag();
            if (reader.PeekState() is not (CborReaderState.ByteString or CborReaderState.StartIndefiniteLengthByteString))
            {
                throw new FormatException("Expected a byte string for the CBOR decimal mantissa.");
            }

            reader.SkipValue();
        }
        else
        {
            SkipCborInteger(reader);
        }

        reader.ReadEndArray();
    }

    /// <summary>
    /// Consumes the full CBOR integer range without converting to a narrower signed representation.
    /// </summary>
    private static void SkipCborInteger(CborReader reader)
    {
        switch (reader.PeekState())
        {
            case CborReaderState.UnsignedInteger:
                reader.ReadUInt64();
                break;
            case CborReaderState.NegativeInteger:
                reader.ReadCborNegativeIntegerRepresentation();
                break;
            default:
                throw new FormatException("Expected an integral CBOR decimal component.");
        }
    }

    /// <summary>
    /// Compares exact base-ten values without rounding long significands or exponentiating untrusted exponents.
    /// </summary>
    private static (string Coefficient, long Exponent) NormalizeDecimal(string text)
    {
        int exponentIndex = text.AsSpan().IndexOfAny('e', 'E');
        ReadOnlySpan<char> significand = exponentIndex < 0 ? text.AsSpan() : text.AsSpan(0, exponentIndex);
        int pointIndex = significand.IndexOf('.');
        int fractionalDigits = pointIndex < 0 ? 0 : significand.Length - pointIndex - 1;
        string digits = significand.ToString().Replace(".", "", StringComparison.Ordinal).TrimStart('-').TrimStart('0');
        if (digits.Length == 0)
        {
            return ("0", 0);
        }

        string coefficient = digits.TrimEnd('0');
        long exponent = exponentIndex < 0 ? 0 : long.Parse(text.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        exponent = checked(exponent - fractionalDigits + digits.Length - coefficient.Length);
        return (text[0] == '-' ? "-" + coefficient : coefficient, exponent);
    }

    /// <summary>
    /// Distinguishes exact zero from a nonzero JSON number that underflowed during conversion.
    /// </summary>
    private readonly bool HasNonzeroJsonSignificand()
    {
        foreach (byte character in _json.ValueSpan)
        {
            if (character is (byte)'e' or (byte)'E')
            {
                break;
            }

            if (character is >= (byte)'1' and <= (byte)'9')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks depth before recursively entering generated code.
    /// </summary>
    private void Enter()
    {
        if (++_depth > 64)
        {
            throw new FormatException("Custom-type nesting exceeds 64 levels.");
        }
    }

    /// <summary>
    /// Advances after a complete JSON token.
    /// </summary>
    private void Next() => _hasJsonToken = _json.Read();

    /// <summary>
    /// Consumes the semantic tag that identifies a CBOR value's representation.
    /// </summary>
    private readonly void ExpectTag(CborTag tag)
    {
        if (_cbor!.ReadTag() != tag)
        {
            throw new FormatException("The CBOR value does not carry its expected semantic tag.");
        }
    }

    /// <summary>
    /// Requires the structural token selected by the generated contract.
    /// </summary>
    private readonly void Expect(JsonTokenType type)
    {
        if (!_hasJsonToken || _json.TokenType != type)
        {
            throw new FormatException($"Expected JSON {type}.");
        }
    }
}
