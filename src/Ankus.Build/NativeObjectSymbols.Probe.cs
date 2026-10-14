using System.Globalization;

namespace Ankus.Build;

internal static partial class NativeObjectSymbols
{
    private ref partial struct Reader
    {
        /// <summary>
        /// Locates independent unwind tables belonging exclusively to discarded header implementations.
        /// </summary>
        internal readonly List<int> CoffProbeUnwindSections()
        {
            bool big = UInt16(_image) == 0;
            int header = big ? 56 : 20;
            int width = big ? 20 : 18;
            uint count = big ? UInt32(_image[44..]) : UInt16(_image[2..]);
            uint symbolOffset = UInt32(_image[(big ? 48 : 8)..]);
            uint symbolCount = UInt32(_image[(big ? 52 : 12)..]);
            ReadOnlySpan<byte> sections = Table((uint)header, count, 40);
            if (symbolOffset == 0)
            {
                return [];
            }

            ReadOnlySpan<byte> symbols = Table(symbolOffset, symbolCount, width);
            ulong stringOffset = symbolOffset + (ulong)symbols.Length;
            ReadOnlySpan<byte> strings = Slice(stringOffset, UInt32(Slice(stringOffset, 4)));
            string[] names = new string[count];
            HashSet<int>[] targets = new HashSet<int>[count];
            HashSet<int>[] sources = new HashSet<int>[count];
            var removed = new HashSet<int>();
            for (int index = 0; index < names.Length; index++)
            {
                ReadOnlySpan<byte> section = sections.Slice(index * 40, 40);
                ReadOnlySpan<byte> name = section[..8];
                int end = name.IndexOf((byte)0);
                name = end < 0 ? name : name[..end];
                if (name.StartsWith("/"u8))
                {
                    Require(uint.TryParse(s_utf8.GetString(name[1..]), NumberStyles.None, CultureInfo.InvariantCulture, out uint position) && position >= 4,
                        "Invalid COFF section-name offset.");
                    name = Name(strings, position);
                }

                names[index] = s_utf8.GetString(name);
                targets[index] = [];
                sources[index] = [];
                if (names[index].StartsWith("ankus_header_", StringComparison.Ordinal))
                {
                    removed.Add(index);
                }
            }

            for (int index = 0; index < names.Length; index++)
            {
                ReadOnlySpan<byte> section = sections.Slice(index * 40, 40);
                uint position = UInt32(section[24..]);
                bool overflow = (UInt32(section[36..]) & 0x01000000) != 0;
                uint relocations = overflow ? UInt32(Slice(position, 10)) : UInt16(section[32..]);
                ReadOnlySpan<byte> table = Table(position, relocations, 10);
                for (int offset = overflow ? 10 : 0; offset < table.Length; offset += 10)
                {
                    uint symbol = UInt32(table[(offset + 4)..]);
                    ReadOnlySpan<byte> record = symbols.Slice(checked((int)symbol * width), width);
                    uint target = big ? UInt32(record[12..]) : UInt16(record[12..]);
                    // Undefined, absolute and debug symbols do not identify another section.
                    if (target is > 0 && target <= count)
                    {
                        int destination = checked((int)target - 1);
                        targets[index].Add(destination);
                        sources[destination].Add(index);
                    }
                    else
                    {
                        targets[index].Add(-1);
                    }
                }
            }

            return RemovableUnwindTables(names, targets, sources, removed, header);
        }

        /// <summary>
        /// Finds each unwind table that only describes removed header code: a .pdata table whose relocations reach only
        /// that code or its unwind data, and then each .xdata table that only removed tables use.
        /// </summary>
        /// <returns>The header offsets of the tables to remove.</returns>
        private static List<int> RemovableUnwindTables(string[] names, HashSet<int>[] targets, HashSet<int>[] sources,
            HashSet<int> removed, int header)
        {
            var offsets = new List<int>();
            for (int index = 0; index < names.Length; index++)
            {
                // Clang's explicit code sections have separate, non-associative
                // unwind tables. Never remove a table containing any live code.
                if (names[index] == ".pdata" && targets[index].Any(target => target >= 0 && names[target] == "ankus_header_code") &&
                    targets[index].All(target => target >= 0 && names[target] is "ankus_header_code" or ".xdata"))
                {
                    removed.Add(index);
                    offsets.Add(header + index * 40);
                }
            }

            bool changed;
            do
            {
                changed = false;
                for (int index = 0; index < names.Length; index++)
                {
                    if (names[index] == ".xdata" && !removed.Contains(index) && sources[index].Count > 0 && sources[index].All(removed.Contains))
                    {
                        removed.Add(index);
                        offsets.Add(header + index * 40);
                        changed = true;
                    }
                }
            }
            while (changed);

            return offsets;
        }
    }
}
