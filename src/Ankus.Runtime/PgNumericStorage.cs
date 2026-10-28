using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Threading;

namespace Ankus;

/// <summary>
/// Owns opaque PostgreSQL numeric datum bytes and their portable binary representation.
/// Only the public numeric_send format is interpreted; private NumericData layouts are never decoded.
/// </summary>
/// <param name="buffer">The privately owned and validated contiguous payload.</param>
/// <param name="rawLength">The number of opaque datum bytes preceding the portable representation.</param>
internal sealed class PgNumericStorage(byte[] buffer, int rawLength)
{
    /// <summary>
    /// Owns a contiguous native datum followed by the network-order numeric_send payload.
    /// Managed-origin values contain only the portable payload until they enter PostgreSQL.
    /// </summary>
    private readonly byte[] _buffer = buffer;

    /// <summary>
    /// Caches formatting on demand without making text the stored numeric representation.
    /// </summary>
    private string? _text;

    /// <summary>
    /// Gets the length of the opaque native datum, or zero for a portable-only value.
    /// </summary>
    internal int RawLength { get; } = rawLength;

    /// <summary>
    /// Gets the immutable transport bytes without exposing their mutable owner.
    /// </summary>
    internal ReadOnlySpan<byte> Buffer => _buffer;

    /// <summary>
    /// Gets the portable network-order representation independent of native varlena layout.
    /// </summary>
    internal ReadOnlySpan<byte> Wire => _buffer.AsSpan(RawLength);

    /// <summary>
    /// Gets PostgreSQL's ordering category without formatting the value.
    /// </summary>
    internal int Kind => BinaryPrimitives.ReadUInt16BigEndian(Wire[4..]) switch
    {
        0xF000 => 0,
        0xD000 => 2,
        0xC000 => 3,
        _ => 1,
    };

    /// <summary>
    /// Gets finite display scale from the portable header, or null for special values.
    /// </summary>
    internal int? Scale => Kind == 1 ? BinaryPrimitives.ReadUInt16BigEndian(Wire[6..]) : null;

    /// <summary>
    /// Gets the exact sign without materializing decimal text.
    /// </summary>
    internal int? Sign
    {
        get
        {
            if (Kind != 1)
            {
                return Kind switch { 0 => -1, 2 => 1, _ => null };
            }

            int count = BinaryPrimitives.ReadUInt16BigEndian(Wire);
            for (int index = 0; index < count; index++)
            {
                if (GetGroup(index) != 0)
                {
                    return BinaryPrimitives.ReadUInt16BigEndian(Wire[4..]) == 0x4000 ? -1 : 1;
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// Gets canonical PostgreSQL output without accessing a backend or its memory contexts.
    /// </summary>
    internal string Text => LazyInitializer.EnsureInitialized(ref _text, Render);

    /// <summary>
    /// Copies a borrowed numeric payload before its native lifetime ends.
    /// </summary>
    internal static PgNumericStorage Copy(ReadOnlySpan<byte> buffer, int rawLength)
    {
        if (rawLength < 0 || rawLength > buffer.Length || rawLength is > 0 and < 4)
        {
            throw new InvalidOperationException("Invalid numeric datum length.");
        }

        ValidateWire(buffer[rawLength..]);
        return new(buffer.ToArray(), rawLength);
    }

    /// <summary>
    /// Encodes a decimal's coefficient, sign and display scale directly into portable binary digits.
    /// </summary>
    /// <param name="value">The exact managed decimal.</param>
    /// <returns>Owned binary storage without an intermediate decimal string.</returns>
    internal static PgNumericStorage FromDecimal(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        int scale = (bits[3] >> 16) & 0xFF;
        UInt128 coefficient = unchecked((uint)bits[0]) | ((UInt128)unchecked((uint)bits[1]) << 32)
            | ((UInt128)unchecked((uint)bits[2]) << 64);
        // Pad the fraction to whole base-10000 groups.
        int fractionalGroups = (scale + 3) / 4;
        coefficient *= (fractionalGroups * 4 - scale) switch
        {
            0 => 1u,
            1 => 10u,
            2 => 100u,
            _ => 1000u,
        };

        Span<ushort> groups = stackalloc ushort[8];
        int count = 0;
        while (coefficient != 0)
        {
            groups[count++] = (ushort)(coefficient % 10000);
            coefficient /= 10000;
        }

        int first = 0;
        while (first < count && groups[first] == 0)
        {
            first++;
        }

        int length = count - first;
        byte[] wire = new byte[8 + length * 2];
        BinaryPrimitives.WriteUInt16BigEndian(wire, checked((ushort)length));
        BinaryPrimitives.WriteInt16BigEndian(wire.AsSpan(2), length == 0 ? (short)0 : checked((short)(count - fractionalGroups - 1)));
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(4), bits[3] < 0 && length != 0 ? (ushort)0x4000 : (ushort)0);
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(6), checked((ushort)scale));
        for (int index = 0; index < length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(8 + index * 2), groups[count - index - 1]);
        }

        return new(wire, 0);
    }

    /// <summary>
    /// Encodes canonical finite or special numeric text into the public binary format.
    /// This is used only for values created without a PostgreSQL backend.
    /// </summary>
    internal static PgNumericStorage FromCanonicalText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ushort sign = text switch { "NaN" => 0xC000, "Infinity" => 0xD000, "-Infinity" => 0xF000, _ => 0 };
        if (sign != 0)
        {
            byte[] special = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(special.AsSpan(4), sign);
            return new(special, 0);
        }

        ReadOnlySpan<char> digits = text;
        if (!digits.IsEmpty && digits[0] == '-')
        {
            sign = 0x4000;
            digits = digits[1..];
        }

        int point = digits.IndexOf('.');
        int integerLength = point < 0 ? digits.Length : point;
        int scale = point < 0 ? 0 : digits.Length - point - 1;
        if (integerLength is < 1 or > 131072 || scale > 16383 || point == digits.Length - 1)
        {
            throw new ArgumentException("Invalid canonical numeric text.", nameof(text));
        }

        ReadOnlySpan<char> fraction = point < 0 ? [] : digits[(point + 1)..];
        if (digits[..integerLength].ContainsAnyExceptInRange('0', '9') || fraction.ContainsAnyExceptInRange('0', '9'))
        {
            throw new ArgumentException("Invalid canonical numeric digit.", nameof(text));
        }

        int integerGroups = (integerLength + 3) / 4;
        int groupCount = integerGroups + (scale + 3) / 4;
        int first = 0;
        while (first < groupCount && ReadTextGroup(digits, point, integerLength, integerGroups, first) == 0)
        {
            first++;
        }

        int end = groupCount;
        while (end > first && ReadTextGroup(digits, point, integerLength, integerGroups, end - 1) == 0)
        {
            end--;
        }

        int count = end - first;
        byte[] wire = new byte[8 + count * 2];
        BinaryPrimitives.WriteUInt16BigEndian(wire, checked((ushort)count));
        BinaryPrimitives.WriteInt16BigEndian(wire.AsSpan(2), count == 0 ? (short)0 : checked((short)(integerGroups - first - 1)));
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(4), count == 0 ? (ushort)0 : sign);
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(6), checked((ushort)scale));
        for (int index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(8 + index * 2),
                ReadTextGroup(digits, point, integerLength, integerGroups, first + index));
        }

        return new(wire, 0);
    }

    /// <summary>
    /// Reads one padded base-10000 group from canonical decimal digits.
    /// </summary>
    private static ushort ReadTextGroup(ReadOnlySpan<char> digits, int point, int integerLength, int integerGroups, int group)
    {
        int start = integerLength - integerGroups * 4 + group * 4;
        int result = 0;
        for (int index = start; index < start + 4; index++)
        {
            int offset = index >= integerLength && point >= 0 ? index + 1 : index;
            result = result * 10 + (offset < 0 || offset >= digits.Length ? 0 : digits[offset] - '0');
        }

        return checked((ushort)result);
    }

    /// <summary>
    /// Rejects truncated, out-of-range or lossy portable numeric representations.
    /// </summary>
    private static void ValidateWire(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < 8 || wire.Length != 8 + BinaryPrimitives.ReadUInt16BigEndian(wire) * 2)
        {
            throw new InvalidOperationException("Invalid numeric binary length.");
        }

        ushort sign = BinaryPrimitives.ReadUInt16BigEndian(wire[4..]);
        int scale = BinaryPrimitives.ReadUInt16BigEndian(wire[6..]);
        if (sign is not (0 or 0x4000 or 0xC000 or 0xD000 or 0xF000) || scale > 16383)
        {
            throw new InvalidOperationException("Invalid numeric binary sign or scale.");
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(wire);
        int weight = BinaryPrimitives.ReadInt16BigEndian(wire[2..]);
        for (int index = 0; index < count; index++)
        {
            int digit = BinaryPrimitives.ReadUInt16BigEndian(wire[(8 + index * 2)..]);
            if (digit >= 10000)
            {
                throw new InvalidOperationException("Invalid numeric binary digit.");
            }

            int hiddenDigits = (index - weight) * 4 - scale;
            if (sign < 0xC000 && hiddenDigits > 0 && digit % (hiddenDigits >= 4 ? 10000 : hiddenDigits == 3 ? 1000 : hiddenDigits == 2 ? 100 : 10) != 0)
            {
                throw new InvalidOperationException("Numeric binary scale would discard nonzero digits.");
            }
        }
    }

    /// <summary>
    /// Renders public base-10000 digits without inspecting opaque native datum bytes.
    /// </summary>
    private string Render()
    {
        ushort sign = BinaryPrimitives.ReadUInt16BigEndian(Wire[4..]);
        if (sign >= 0xC000)
        {
            return sign switch { 0xC000 => "NaN", 0xD000 => "Infinity", _ => "-Infinity" };
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(Wire);
        int weight = BinaryPrimitives.ReadInt16BigEndian(Wire[2..]);
        int scale = BinaryPrimitives.ReadUInt16BigEndian(Wire[6..]);
        int first = 0;
        while (first < count && GetGroup(first) == 0)
        {
            first++;
        }

        int firstWeight = weight - first;
        int firstDigit = first < count ? GetGroup(first) : 0;
        int firstWidth = firstDigit >= 1000 ? 4 : firstDigit >= 100 ? 3 : firstDigit >= 10 ? 2 : 1;
        int integerLength = first < count && firstWeight >= 0 ? firstWeight * 4 + firstWidth : 1;
        int signLength = sign == 0x4000 && first < count ? 1 : 0;
        int length = signLength + integerLength + (scale == 0 ? 0 : 1 + scale);
        return string.Create(length, (Storage: this, First: first, FirstWeight: firstWeight, FirstDigit: firstDigit,
            SignLength: signLength, Scale: scale, Weight: weight, Count: count),
            static (destination, state) =>
            {
                int offset = 0;
                if (state.SignLength != 0)
                {
                    destination[offset++] = '-';
                }

                if (state.First < state.Count && state.FirstWeight >= 0)
                {
                    state.FirstDigit.TryFormat(destination[offset..], out int written, provider: CultureInfo.InvariantCulture);
                    offset += written;
                    for (int group = state.First + 1; group <= state.Weight; group++)
                    {
                        state.Storage.GetGroup(group).TryFormat(destination[offset..], out written, "D4", CultureInfo.InvariantCulture);
                        offset += written;
                    }
                }
                else
                {
                    destination[offset++] = '0';
                }

                if (state.Scale != 0)
                {
                    destination[offset++] = '.';
                    for (int index = 0; index < state.Scale; index++)
                    {
                        int digit = state.Storage.GetGroup(state.Weight + 1 + index / 4);
                        int divisor = (index % 4) switch { 0 => 1000, 1 => 100, 2 => 10, _ => 1 };
                        destination[offset++] = (char)('0' + digit / divisor % 10);
                    }
                }
            });
    }

    /// <summary>
    /// Reads a portable digit, treating absent integer or fractional groups as zero.
    /// </summary>
    private int GetGroup(int index)
        => index < 0 || index >= BinaryPrimitives.ReadUInt16BigEndian(Wire)
            ? 0 : BinaryPrimitives.ReadUInt16BigEndian(Wire[(8 + index * 2)..]);
}
