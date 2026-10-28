using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

namespace Ankus.PgConfig;

/// <summary>
/// Extracts the schema section from a published native library without loading executable code.
/// </summary>
internal static partial class NativeSchemaSection
{
    /// <summary>
    /// Bounds schema allocations independently of the size of the native library.
    /// </summary>
    internal const int MaximumLength = 64 * 1024 * 1024;

    /// <summary>
    /// Reads a supported native library, selecting an explicit architecture for universal Mach-O images.
    /// </summary>
    /// <param name="stream">The readable, seekable library stream, left open after reading.</param>
    /// <param name="runtimeIdentifier">An optional required RID; universal libraries require an explicit macOS RID.</param>
    /// <returns>The measured native RID and exact section bytes, including any native alignment padding.</returns>
    internal static (string RuntimeIdentifier, byte[] Data) Read(Stream stream, string? runtimeIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("Reading a native schema requires a readable, seekable stream.", nameof(stream));
        }

        var reader = new Reader(stream, 0, (ulong)stream.Length);
        (string actual, byte[] data) = reader.Read(runtimeIdentifier);
        Require(runtimeIdentifier is null || runtimeIdentifier == actual, "Native library does not match the requested runtime identifier.");
        return (actual, data);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new FormatException(message);
        }
    }

    private sealed partial class Reader(Stream stream, ulong origin, ulong length)
    {
        private ulong? _sectionOffset;
        private ulong _sectionLength;

        /// <summary>
        /// Validates the image format before locating one unambiguous schema section.
        /// </summary>
        /// <param name="runtimeIdentifier">The optional expected runtime identifier.</param>
        /// <returns>The actual image identity and file-backed section data.</returns>
        internal (string RuntimeIdentifier, byte[] Data) Read(string? runtimeIdentifier)
        {
            uint magic = UInt32(Bytes(0, 4));
            if (magic is 0xbebafeca or 0xbfbafeca)
            {
                return ReadUniversal(magic == 0xbfbafeca, runtimeIdentifier);
            }

            string identity = magic switch
            {
                0x464c457f => ReadElf(),
                0xfeedfacf => ReadMach(),
                _ when (magic & 0xffff) == 0x5a4d => ReadPe(),
                _ => throw new FormatException("Unsupported native library format."),
            };
            Require(_sectionOffset.HasValue, "Native library has no Ankus schema section.");
            return (identity, Bytes(_sectionOffset!.Value, (int)_sectionLength));
        }

        private byte[] Bytes(ulong offset, int count)
        {
            Range(offset, (ulong)count);
            byte[] bytes = new byte[count];
            stream.Position = checked((long)(origin + offset));
            try
            {
                stream.ReadExactly(bytes);
            }
            catch (EndOfStreamException error)
            {
                throw new FormatException("Native library ended inside a declared range.", error);
            }

            return bytes;
        }

        private void Range(ulong offset, ulong count)
            => Require(offset <= length && count <= length - offset, "Native library range exceeds its file.");

        private void Section(ulong offset, ulong count)
        {
            Require(!_sectionOffset.HasValue, "Native library contains duplicate Ankus schema sections.");
            Require(count is > 0 and <= MaximumLength, "Native schema section exceeds the supported size or is empty.");
            Range(offset, count);
            _sectionOffset = offset;
            _sectionLength = count;
        }

        private static ushort UInt16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes);

        private static uint UInt32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);

        private static ulong UInt64(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt64LittleEndian(bytes);

        private static bool Name(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> expected)
        {
            int end = bytes.IndexOf((byte)0);
            return (end < 0 ? bytes : bytes[..end]).SequenceEqual(expected);
        }
    }
}
