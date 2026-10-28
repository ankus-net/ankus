using System;
using System.Buffers;
using System.ComponentModel;
using System.Formats.Cbor;
using System.Text.Json;

namespace Ankus.CompilerServices;

/// <summary>
/// Implements the format boundary for statically generated custom-type serializers.
/// </summary>
/// <typeparam name="T">The exact serialized contract.</typeparam>
/// <param name="createTextCodec">The optional lazy custom text factory; null selects JSON text.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class PgSerializedTypeCodec<T>(Func<PgTypeTextCodec<T>>? createTextCodec) : PgTypeCodec<T>
{
    private readonly Lazy<PgTypeTextCodec<T>>? _textCodec = createTextCodec is null ? null : new(() => createTextCodec() ??
        throw new InvalidOperationException("A custom text codec factory returned null."));

    /// <summary>
    /// Selects generated JSON text input/output and CBOR storage.
    /// </summary>
    protected PgSerializedTypeCodec() : this(null)
    {
    }

    /// <summary>
    /// Reads one value through generated member and constructor access.
    /// </summary>
    /// <param name="reader">The borrowed input cursor.</param>
    /// <returns>The independent value.</returns>
    protected abstract T ReadValue(ref PgTypeReader reader);

    /// <summary>
    /// Writes one value through generated member access.
    /// </summary>
    /// <param name="writer">The bounded output cursor.</param>
    /// <param name="value">The present value.</param>
    protected abstract void WriteValue(PgTypeWriter writer, T value);

    /// <inheritdoc />
    public sealed override T Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_textCodec is not null)
        {
            return _textCodec.Value.Parse(text) ?? throw new InvalidOperationException("A custom text codec returned null for a present text input.");
        }

        try
        {
            return Decode(PgSerializationText.Utf8.GetBytes(text), json: true);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException or InvalidOperationException or ArgumentException)
        {
            throw new PgException("22P02", "Invalid JSON custom-type value.", detail: exception.Message, innerException: exception);
        }
    }

    /// <inheritdoc />
    public sealed override string Format(T value)
    {
        if (_textCodec is not null)
        {
            if (value is null)
            {
                throw new InvalidOperationException("A present custom-type value cannot be null.");
            }

            return _textCodec.Value.Format(value) ?? throw new InvalidOperationException("A custom text codec returned null text for a present value.");
        }

        var destination = new ArrayBufferWriter<byte>();
        using var writer = new PgTypeWriter(destination, json: true);
        WriteValue(writer, value);
        writer.Complete();
        return PgSerializationText.Utf8.GetString(destination.WrittenSpan);
    }

    /// <inheritdoc />
    public sealed override T Read(ReadOnlySpan<byte> payload)
    {
        try
        {
            return Decode(payload, json: false);
        }
        catch (Exception exception) when (exception is CborContentException or FormatException or OverflowException or InvalidOperationException or ArgumentException)
        {
            throw new PgException("22P03", "Invalid CBOR custom-type value.", detail: exception.Message, innerException: exception);
        }
    }

    /// <inheritdoc />
    public sealed override void Write(T value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new PgTypeWriter(destination, json: false);
        WriteValue(writer, value);
        writer.Complete();
    }

    /// <summary>
    /// Requires one complete non-null value and rejects trailing input.
    /// </summary>
    private T Decode(ReadOnlySpan<byte> input, bool json)
    {
        var reader = new PgTypeReader(input, json);
        T value = ReadValue(ref reader);
        reader.Complete();
        return value is null ? throw new FormatException("A present custom-type value cannot be null.") : value;
    }
}
