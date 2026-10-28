using System;

namespace Ankus.PgConfig;

internal static partial class NativeSchemaSection
{
    private sealed partial class Reader
    {
        private string ReadPe()
        {
            byte[] dos = Bytes(0, 64);
            uint offset = UInt32(dos.AsSpan(60));
            Require(offset >= 64, "PE header overlaps the DOS header.");
            byte[] header = Bytes(offset, 24);
            Require(UInt32(header) == 0x4550 && UInt16(header.AsSpan(4)) == 0x8664 && (UInt16(header.AsSpan(22)) & 0x2000) != 0,
                "Expected an x64 PE DLL.");
            uint count = UInt16(header.AsSpan(6));
            uint optionalSize = UInt16(header.AsSpan(20));
            Require(count is > 0 and <= 96 && optionalSize >= 112, "Invalid PE section count or optional header size.");
            byte[] optional = Bytes(offset + 24UL, (int)optionalSize);
            Require(UInt16(optional) == 0x20b, "Expected a PE32+ optional header.");
            ulong table = offset + 24UL + optionalSize;
            Range(table, count * 40UL);
            for (uint index = 0; index < count; index++)
            {
                byte[] section = Bytes(table + index * 40UL, 40);
                if (Name(section.AsSpan(0, 8), ".ankusc"u8))
                {
                    uint flags = UInt32(section.AsSpan(36));
                    Require((flags & 0x40) != 0 && (flags & 0x200000a0) == 0,
                        "PE schema must contain initialized, non-executable file data.");
                    uint size = UInt32(section.AsSpan(16));
                    uint virtualSize = UInt32(section.AsSpan(8));
                    Require(virtualSize is > 0 && virtualSize <= size, "PE schema has missing file-backed data.");
                    Section(UInt32(section.AsSpan(20)), size);
                }
            }

            return "win-x64";
        }
    }
}
