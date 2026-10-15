namespace Ankus.PgConfig;

internal static partial class NativeSchemaSection
{
    private sealed partial class Reader
    {
        /// <summary>
        /// The ELF machine number of x86-64 libraries.
        /// </summary>
        private const ushort ElfX64 = 62;

        /// <summary>
        /// The ELF machine number of AArch64 libraries.
        /// </summary>
        private const ushort ElfArm64 = 183;

        private string ReadElf()
        {
            byte[] header = Bytes(0, 64);
            Require(header[4] == 2 && header[5] == 1 && header[6] == 1 && UInt32(header.AsSpan(20)) == 1,
                "Expected a little-endian ELF64 library.");
            ushort machine = UInt16(header.AsSpan(18));
            Require(UInt16(header.AsSpan(16)) == 3 && machine is ElfX64 or ElfArm64,
                "Expected an x64 or ARM64 ELF shared library.");
            Require(UInt16(header.AsSpan(52)) == 64 && UInt16(header.AsSpan(58)) == 64,
                "Invalid ELF header or section-header size.");
            ulong table = UInt64(header.AsSpan(40));
            Require(table >= 64, "ELF section table is missing or overlaps its header.");
            byte[] first = Bytes(table, 64);
            ulong count = UInt16(header.AsSpan(60));
            if (count == 0)
            {
                count = UInt64(first.AsSpan(32));
            }

            uint namesIndex = UInt16(header.AsSpan(62));
            if (namesIndex == 0xffff)
            {
                namesIndex = UInt32(first.AsSpan(40));
            }

            Require(count is > 0 and <= 65536 && namesIndex > 0 && namesIndex < count, "Invalid ELF section table count or name index.");
            Range(table, count * 64);
            byte[] namesHeader = Bytes(table + namesIndex * 64UL, 64);
            Require(UInt32(namesHeader.AsSpan(4)) == 3, "ELF section names do not reference a string table.");
            ulong namesSize = UInt64(namesHeader.AsSpan(32));
            Require(namesSize is > 0 and <= MaximumLength, "Invalid ELF section name table size.");
            byte[] names = Bytes(UInt64(namesHeader.AsSpan(24)), (int)namesSize);
            for (ulong index = 1; index < count; index++)
            {
                byte[] section = Bytes(table + index * 64, 64);
                uint nameIndex = UInt32(section);
                Require(nameIndex < names.Length, "ELF section name exceeds its string table.");
                ReadOnlySpan<byte> name = names.AsSpan((int)nameIndex);
                Require(name.IndexOf((byte)0) >= 0, "ELF section name is not terminated.");
                if (Name(name, ".ankusc"u8))
                {
                    Require(UInt32(section.AsSpan(4)) == 1 && (UInt64(section.AsSpan(8)) & 0x804) == 0,
                        "ELF schema must contain uncompressed, non-executable file data.");
                    Section(UInt64(section.AsSpan(24)), UInt64(section.AsSpan(32)));
                }
            }

            return machine == ElfArm64 ? "linux-arm64" : "linux-x64";
        }
    }
}
