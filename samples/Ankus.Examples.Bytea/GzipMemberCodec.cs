using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;

namespace Ankus.Examples.Bytea;

/// <summary>
/// Frames gzip members around the platform's raw DEFLATE codec without losing first-member boundaries.
/// </summary>
internal static class GzipMemberCodec
{
    /// <summary>
    /// Defines the fixed literal alphabet in RFC 1951, section 3.2.6.
    /// </summary>
    private static readonly Huffman s_fixedLiterals = Huffman.Create([.. Enumerable.Range(0, 288)
        .Select(static symbol => (byte)(symbol < 144 ? 8 : symbol < 256 ? 9 : symbol < 280 ? 7 : 8))]);

    /// <summary>
    /// Defines the fixed distance alphabet in RFC 1951, section 3.2.6.
    /// </summary>
    private static readonly Huffman s_fixedDistances = Huffman.Create([.. Enumerable.Repeat((byte)5, 32)]);

    /// <summary>
    /// Orders dynamic code lengths as specified by RFC 1951, section 3.2.7.
    /// </summary>
    private static readonly byte[] s_lengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    /// <summary>
    /// Writes a complete gzip header, DEFLATE payload and CRC/length trailer for every input, including empty input.
    /// </summary>
    /// <param name="input">The exact owned payload.</param>
    /// <returns>One complete gzip member.</returns>
    internal static byte[] Encode(byte[] input)
    {
        using var output = new MemoryStream();
        output.Write([0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 255]);
        if (input.Length == 0)
        {
            // A final stored block represents empty DEFLATE input independently of legacy stream-disposal behavior.
            output.Write([1, 0, 0, 255, 255]);
        }
        else
        {
            using var encoder = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true);
            encoder.Write(input);
        }

        Span<byte> trailer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, Crc32(input));
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], (uint)input.Length);
        output.Write(trailer);
        return output.ToArray();
    }

    /// <summary>
    /// Decodes only the first member and verifies its complete trailer and data CRC, matching pgrx's libflate Decoder.
    /// </summary>
    /// <param name="bytes">The first gzip member and any subsequent content.</param>
    /// <returns>The exact first member's payload.</returns>
    /// <exception cref="InvalidDataException">The header, DEFLATE payload or checksum is invalid or incomplete.</exception>
    internal static byte[] Decode(byte[] bytes)
    {
        int header = HeaderLength(bytes);
        int body = DeflateLength(bytes.AsSpan(header));
        int trailer = checked(header + body);
        Require(bytes, trailer, 8);
        using var input = new MemoryStream(bytes, header, body, writable: false);
        using var decoder = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        byte[] payload = output.ToArray();
        if (Crc32(payload) != BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(trailer)))
        {
            throw new InvalidDataException("The gzip data checksum does not match its payload.");
        }

        // libflate consumes all eight trailer bytes but does not compare ISIZE; later members are separate inputs.
        return payload;
    }

    /// <summary>
    /// Reads the same optional extra fields, zero-terminated metadata and reconstructed header checksum as libflate.
    /// </summary>
    /// <param name="bytes">The complete candidate input.</param>
    /// <returns>The exact first DEFLATE byte offset.</returns>
    private static int HeaderLength(ReadOnlySpan<byte> bytes)
    {
        Require(bytes, 0, 10);
        if (bytes[0] != 0x1f || bytes[1] != 0x8b || bytes[2] != 8)
        {
            throw new InvalidDataException("The input does not have a gzip DEFLATE header.");
        }

        int offset = 10;
        byte flags = bytes[3];
        if ((flags & 4) != 0)
        {
            Require(bytes, offset, 2);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
            offset += 2;
            Require(bytes, offset, length);
            int end = offset + length;
            while (offset < end)
            {
                Require(bytes[..end], offset, 4);
                int subfield = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 2)..]);
                offset += 4;
                Require(bytes[..end], offset, subfield);
                offset += subfield;
            }
        }

        // The file name (FNAME, 8) and comment (FCOMMENT, 16) are each zero-terminated (RFC 1952, section 2.3.1).
        int terminated = BitOperations.PopCount((uint)(flags & (8 | 16)));
        for (int field = 0; field < terminated; field++)
        {
            int end = bytes[offset..].IndexOf((byte)0);
            if (end < 0)
            {
                throw new InvalidDataException("The gzip metadata is not terminated.");
            }

            offset += end + 1;
        }

        if ((flags & 2) != 0)
        {
            Require(bytes, offset, 2);
            if ((Crc32(bytes[..offset], reconstructHeader: true) & 0xffff) !=
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]))
            {
                throw new InvalidDataException("The gzip header checksum does not match its metadata.");
            }

            offset += 2;
        }

        return offset;
    }

    /// <summary>
    /// Finds the first final DEFLATE block without decoding data or reading its trailer as another member.
    /// </summary>
    /// <param name="input">The payload, trailer and optional subsequent content.</param>
    /// <returns>The complete compressed payload's byte count, including final padding.</returns>
    private static int DeflateLength(ReadOnlySpan<byte> input)
    {
        var bits = new Bits(input);
        bool final;
        do
        {
            final = bits.Read(1) != 0;
            int kind = bits.Read(2);
            if (kind == 0)
            {
                bits.Align();
                int length = bits.Read(16);
                if ((length ^ bits.Read(16)) != 0xffff)
                {
                    throw new InvalidDataException("The stored DEFLATE block has inconsistent lengths.");
                }

                bits.Skip(length * 8L);
                continue;
            }

            (Huffman literals, Huffman distances) = kind switch
            {
                1 => (s_fixedLiterals, s_fixedDistances),
                2 => DynamicTables(ref bits),
                _ => throw new InvalidDataException("The DEFLATE block type is reserved."),
            };
            while (true)
            {
                int symbol = literals.Read(ref bits);
                if (symbol == 256)
                {
                    break;
                }

                if (symbol < 256)
                {
                    continue;
                }

                if (symbol > 285)
                {
                    throw new InvalidDataException("The DEFLATE length code is reserved.");
                }

                bits.Skip(symbol < 265 || symbol == 285 ? 0 : (symbol - 261) / 4);
                int distance = distances.Read(ref bits);
                if (distance > 29)
                {
                    throw new InvalidDataException("The DEFLATE distance code is reserved.");
                }

                bits.Skip(distance < 4 ? 0 : distance / 2 - 1);
            }
        }
        while (!final);

        return bits.Offset;
    }

    /// <summary>
    /// Reads bounded dynamic alphabets without permitting repeat codes to escape their declared tables.
    /// </summary>
    /// <param name="bits">The current compressed-bit reader.</param>
    /// <returns>The literal/length and distance alphabets.</returns>
    private static (Huffman Literals, Huffman Distances) DynamicTables(ref Bits bits)
    {
        int literalCount = bits.Read(5) + 257;
        int distanceCount = bits.Read(5) + 1;
        int codeCount = bits.Read(4) + 4;
        if (literalCount > 286)
        {
            throw new InvalidDataException("The DEFLATE literal alphabet is too large.");
        }

        byte[] codeLengths = new byte[19];
        for (int index = 0; index < codeCount; index++)
        {
            codeLengths[s_lengthOrder[index]] = (byte)bits.Read(3);
        }

        Huffman codes = Huffman.Create(codeLengths);
        byte[] lengths = new byte[literalCount + distanceCount];
        int next = 0;
        while (next < lengths.Length)
        {
            int symbol = codes.Read(ref bits);
            if (symbol < 16)
            {
                lengths[next++] = (byte)symbol;
                continue;
            }

            if (symbol == 16 && next == 0)
            {
                throw new InvalidDataException("The DEFLATE repeat code has no preceding length.");
            }

            int count = symbol switch
            {
                16 => bits.Read(2) + 3,
                17 => bits.Read(3) + 3,
                18 => bits.Read(7) + 11,
                _ => throw new InvalidDataException("The DEFLATE code-length symbol is invalid."),
            };
            if (count > lengths.Length - next)
            {
                throw new InvalidDataException("The DEFLATE repeat exceeds its declared alphabet.");
            }

            byte repeated = symbol == 16 ? lengths[next - 1] : (byte)0;
            lengths.AsSpan(next, count).Fill(repeated);
            next += count;
        }

        if (lengths[256] == 0)
        {
            throw new InvalidDataException("The DEFLATE alphabet has no end-of-block symbol.");
        }

        return (Huffman.Create(lengths.AsSpan(0, literalCount)), Huffman.Create(lengths.AsSpan(literalCount)));
    }

    /// <summary>
    /// Computes RFC 1952 CRC-32 and optionally reproduces libflate's reconstructed header flags.
    /// </summary>
    /// <param name="input">The exact bytes covered by the checksum.</param>
    /// <param name="reconstructHeader">Reproduces the flags and compression level retained by libflate's header reader.</param>
    /// <returns>The complete unsigned data checksum.</returns>
    private static uint Crc32(ReadOnlySpan<byte> input, bool reconstructHeader = false)
    {
        uint crc = uint.MaxValue;
        for (int index = 0; index < input.Length; index++)
        {
            uint value = input[index];
            if (reconstructHeader && index == 3)
            {
                value &= 0x1c;
            }
            else if (reconstructHeader && index == 8 && value is not (2 or 4))
            {
                value = 0;
            }

            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = crc >> 1 ^ (crc & 1) * 0xedb88320;
            }
        }

        return ~crc;
    }

    /// <summary>
    /// Rejects missing header or trailer bytes before slicing their input.
    /// </summary>
    /// <param name="bytes">The complete input region.</param>
    /// <param name="offset">The next required byte offset.</param>
    /// <param name="length">The required byte count.</param>
    private static void Require(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (offset > bytes.Length || length > bytes.Length - offset)
        {
            throw new InvalidDataException("The gzip member is incomplete.");
        }
    }

    /// <summary>
    /// Reads bounded least-significant-first physical bits without allocating decompressed data.
    /// </summary>
    /// <param name="data">The retained compressed input.</param>
    private ref struct Bits(ReadOnlySpan<byte> data)
    {
        /// <summary>
        /// Retains only the caller-owned span while scanning one member.
        /// </summary>
        private readonly ReadOnlySpan<byte> _data = data;

        /// <summary>
        /// Counts consumed bits independently of the platform's stream buffering.
        /// </summary>
        private long _bit;

        /// <summary>
        /// Gets the first trailer byte after the final block's padding.
        /// </summary>
        internal readonly int Offset => checked((int)((_bit + 7) / 8));

        /// <summary>
        /// Reads a bounded integer in DEFLATE's physical bit order.
        /// </summary>
        /// <param name="count">The number of bits required.</param>
        /// <returns>The exact unsigned field value.</returns>
        internal int Read(int count)
        {
            RequireBits(count);
            int result = 0;
            for (int shift = 0; shift < count; shift++, _bit++)
            {
                result |= (_data[(int)(_bit >> 3)] >> (int)(_bit & 7) & 1) << shift;
            }

            return result;
        }

        /// <summary>
        /// Skips stored bytes and length/distance extra bits without examining their payload.
        /// </summary>
        /// <param name="count">The nonnegative bit count.</param>
        internal void Skip(long count)
        {
            RequireBits(count);
            _bit += count;
        }

        /// <summary>
        /// Moves to the next whole-byte boundary for an uncompressed block.
        /// </summary>
        internal void Align() => _bit = (_bit + 7) & ~7L;

        /// <summary>
        /// Rejects missing compressed input before every bit-level access.
        /// </summary>
        /// <param name="count">The required remaining bits.</param>
        private readonly void RequireBits(long count)
        {
            if (count > _data.Length * 8L - _bit)
            {
                throw new InvalidDataException("The DEFLATE payload is incomplete.");
            }
        }
    }

    /// <summary>
    /// Stores bounded canonical Huffman alphabets without retaining syntax, native memory or decoding output.
    /// </summary>
    /// <param name="counts">The symbol count at each code length.</param>
    /// <param name="first">The first canonical code at each length.</param>
    /// <param name="starts">The first symbol-array offset at each length.</param>
    /// <param name="symbols">The symbols ordered by length and original ordinal.</param>
    private sealed class Huffman(int[] counts, int[] first, int[] starts, int[] symbols)
    {
        /// <summary>
        /// Constructs a canonical alphabet and rejects oversubscribed code lengths.
        /// </summary>
        /// <param name="lengths">The bounded code length of every possible symbol.</param>
        /// <returns>The detached code table.</returns>
        internal static Huffman Create(ReadOnlySpan<byte> lengths)
        {
            int[] counts = new int[16];
            foreach (byte length in lengths)
            {
                if (length > 15)
                {
                    throw new InvalidDataException("The DEFLATE code length is invalid.");
                }

                if (length != 0)
                {
                    counts[length]++;
                }
            }

            int[] first = new int[16];
            int[] starts = new int[16];
            int code = 0;
            int total = 0;
            for (int length = 1; length <= 15; length++)
            {
                code = (code + counts[length - 1]) << 1;
                if (code + counts[length] > 1 << length)
                {
                    throw new InvalidDataException("The DEFLATE alphabet is oversubscribed.");
                }

                first[length] = code;
                starts[length] = total;
                total += counts[length];
            }

            int[] symbols = new int[total];
            int[] positions = [.. starts];
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length != 0)
                {
                    symbols[positions[length]++] = symbol;
                }
            }

            return new(counts, first, starts, symbols);
        }

        /// <summary>
        /// Reads a most-significant-first canonical code from DEFLATE's packed bit stream.
        /// </summary>
        /// <param name="bits">The current compressed-bit reader.</param>
        /// <returns>The exact alphabet symbol.</returns>
        internal int Read(ref Bits bits)
        {
            int code = 0;
            for (int length = 1; length <= 15; length++)
            {
                code = code << 1 | bits.Read(1);
                int ordinal = code - first[length];
                if ((uint)ordinal < (uint)counts[length])
                {
                    return symbols[starts[length] + ordinal];
                }
            }

            throw new InvalidDataException("The DEFLATE code does not belong to its alphabet.");
        }
    }
}
