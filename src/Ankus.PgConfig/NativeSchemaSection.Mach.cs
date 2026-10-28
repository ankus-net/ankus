using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Ankus.PgConfig;

internal static partial class NativeSchemaSection
{
    private sealed partial class Reader
    {
        private string ReadMach()
        {
            byte[] header = Bytes(0, 32);
            uint cpu = UInt32(header.AsSpan(4));
            Require(cpu is 0x1000007 or 0x100000c && UInt32(header.AsSpan(12)) == 6, "Expected an x64 or ARM64 Mach-O dynamic library.");
            uint count = UInt32(header.AsSpan(16));
            uint size = UInt32(header.AsSpan(20));
            Require(count <= 65536 && count <= size / 8, "Invalid Mach-O load command count.");
            Range(32, size);
            ulong cursor = 32;
            ulong end = 32UL + size;
            for (uint index = 0; index < count; index++)
            {
                Require(cursor <= end - 8, "Mach-O load command exceeds its table.");
                byte[] command = Bytes(cursor, 8);
                uint commandSize = UInt32(command.AsSpan(4));
                Require(commandSize >= 8 && commandSize % 8 == 0 && commandSize <= end - cursor,
                    "Invalid Mach-O load command size.");
                if (UInt32(command) == 0x19)
                {
                    ReadSegment(cursor, commandSize);
                }

                cursor += commandSize;
            }

            Require(cursor == end, "Mach-O load commands do not fill their declared table.");
            return cpu == 0x1000007 ? "osx-x64" : "osx-arm64";
        }

        private void ReadSegment(ulong cursor, uint commandSize)
        {
            Require(commandSize >= 72, "Truncated Mach-O segment.");
            byte[] segment = Bytes(cursor, 72);
            uint count = UInt32(segment.AsSpan(64));
            Require(72UL + count * 80UL == commandSize, "Invalid Mach-O section table size.");
            ulong fileOffset = UInt64(segment.AsSpan(40));
            ulong fileSize = UInt64(segment.AsSpan(48));
            Range(fileOffset, fileSize);
            for (uint index = 0; index < count; index++)
            {
                byte[] section = Bytes(cursor + 72 + index * 80UL, 80);
                if (Name(section.AsSpan(0, 16), "__ankusc"u8))
                {
                    Require(Name(segment.AsSpan(8, 16), "__DATA"u8) && Name(section.AsSpan(16, 16), "__DATA"u8),
                        "Mach-O schema is in an unexpected segment.");
                    uint flags = UInt32(section.AsSpan(64));
                    Require((flags & 0xff) == 0 && (flags & 0x80000400) == 0,
                        "Mach-O schema must contain regular, non-executable file data.");
                    ulong offset = UInt32(section.AsSpan(48));
                    ulong size = UInt64(section.AsSpan(40));
                    Require(offset >= fileOffset && offset - fileOffset <= fileSize && size <= fileSize - (offset - fileOffset),
                        "Mach-O schema exceeds its containing segment.");
                    Section(offset, size);
                }
            }
        }

        private (string RuntimeIdentifier, byte[] Data) ReadUniversal(bool wide, string? runtimeIdentifier)
        {
            Require(runtimeIdentifier is "osx-x64" or "osx-arm64", "Select osx-x64 or osx-arm64 to read a universal native library.");
            uint expected = runtimeIdentifier == "osx-x64" ? 0x1000007U : 0x100000cU;
            uint count = BinaryPrimitives.ReadUInt32BigEndian(Bytes(4, 4));
            Require(count is > 0 and <= 64, "Invalid universal Mach-O architecture count.");
            int width = wide ? 32 : 20;
            ulong tableEnd = 8 + count * (ulong)width;
            Range(0, tableEnd);
            var slices = new List<(ulong Offset, ulong Size)>();
            (ulong Offset, ulong Size)? selected = null;
            for (uint index = 0; index < count; index++)
            {
                byte[] entry = Bytes(8 + index * (ulong)width, width);
                uint cpu = BinaryPrimitives.ReadUInt32BigEndian(entry);
                ulong offset = wide ? BinaryPrimitives.ReadUInt64BigEndian(entry.AsSpan(8)) : BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(8));
                ulong size = wide ? BinaryPrimitives.ReadUInt64BigEndian(entry.AsSpan(16)) : BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(12));
                uint alignment = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(wide ? 24 : 16));
                Require(offset >= tableEnd && size >= 32 && alignment <= 63 && offset % (1UL << (int)alignment) == 0,
                    "Invalid universal Mach-O slice range or alignment.");
                Range(offset, size);
                Require(slices.All(slice => offset >= slice.Offset + slice.Size || offset + size <= slice.Offset),
                    "Universal Mach-O architecture slices overlap.");
                slices.Add((offset, size));
                if (cpu == expected)
                {
                    Require(selected is null, "Universal Mach-O contains duplicate matching architectures.");
                    selected = (offset, size);
                }
            }

            Require(selected.HasValue, "Universal Mach-O does not contain the requested architecture.");
            (ulong start, ulong sliceSize) = selected!.Value;
            var sliceReader = new Reader(stream, origin + start, sliceSize);
            Require(UInt32(sliceReader.Bytes(0, 4)) == 0xfeedfacf, "Universal Mach-O slice is not a thin Mach-O64 image.");
            (string actual, byte[] data) = sliceReader.Read(runtimeIdentifier);
            Require(actual == runtimeIdentifier, "Universal Mach-O slice disagrees with its architecture table.");
            return (actual, data);
        }
    }
}
