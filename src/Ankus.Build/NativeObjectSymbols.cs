using System.Buffers.Binary;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Reads generated entry-point imports from relocatable native objects without loading or executing them.
/// </summary>
internal static partial class NativeObjectSymbols
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    /// <summary>
    /// Retains undefined external names with the requested C prefix and their measured object target.
    /// </summary>
    /// <param name="image">
    /// One complete relocatable object. Executables, archives and universal binaries are not supported.
    /// </param>
    /// <param name="prefix">The generated C namespace to select after removing platform C-name decoration.</param>
    /// <returns>Ordinal, unique imports together with the actual object format, architecture and byte order.</returns>
    internal static NativeObjectImports Read(ReadOnlySpan<byte> image, string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        var reader = new Reader(image, s_utf8.GetBytes(prefix));
        return reader.Read();
    }

    /// <summary>
    /// Finds exact COFF import names with no remaining relocations after removing backend-only probe sections.
    /// </summary>
    /// <param name="image">A complete relocatable COFF object.</param>
    /// <returns>Undefined symbols whose removal cannot change any remaining relocation.</returns>
    internal static NativeObjectImports ReadUnreferencedCoffImports(ReadOnlySpan<byte> image)
    {
        var reader = new Reader(image, [], unreferencedOnly: true);
        return reader.ReadCoff();
    }

    /// <summary>
    /// Gives discarded header unwind sections a distinct name without changing live unwind records.
    /// </summary>
    /// <param name="image">The selected compiler's complete COFF object.</param>
    internal static void IdentifyCoffProbeUnwindSections(Span<byte> image)
    {
        var reader = new Reader(image, [], unreferencedOnly: true);
        reader.ReadCoff();
        foreach (int offset in reader.CoffProbeUnwindSections())
        {
            "ankus_eh"u8.CopyTo(image.Slice(offset, 8));
        }
    }

    private ref partial struct Reader(ReadOnlySpan<byte> image, ReadOnlySpan<byte> prefix, bool unreferencedOnly = false)
    {
        private readonly ReadOnlySpan<byte> _image = image;
        private readonly ReadOnlySpan<byte> _prefix = prefix;
        private readonly bool _unreferencedOnly = unreferencedOnly;
        private readonly SortedSet<string> _symbols = new(StringComparer.Ordinal);
        private bool _littleEndian = true;

        /// <summary>
        /// Selects the object container from its header and validates the import metadata before returning it.
        /// </summary>
        internal NativeObjectImports Read()
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(Slice(0, 4));
            return magic switch
            {
                0x464c457f => ReadElf(),
                0xfeedface or 0xfeedfacf or 0xcefaedfe or 0xcffaedfe => ReadMach(magic),
                _ => ReadCoff(),
            };
        }

        private readonly NativeObjectImports Result(string format, string architecture)
            => new(format, architecture, _littleEndian, Array.AsReadOnly(_symbols.ToArray()));

        private readonly ReadOnlySpan<byte> Slice(ulong offset, ulong length)
        {
            Require(offset <= (ulong)_image.Length && length <= (ulong)_image.Length - offset, "Native object range exceeds its file.");
            return _image.Slice((int)offset, (int)length);
        }

        private readonly ReadOnlySpan<byte> Table(ulong offset, ulong count, int width)
        {
            Require(count <= (ulong)(_image.Length / width), "Native object table count exceeds its file.");
            return Slice(offset, count * (ulong)width);
        }

        private readonly ushort UInt16(ReadOnlySpan<byte> value)
            => _littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(value) : BinaryPrimitives.ReadUInt16BigEndian(value);

        private readonly uint UInt32(ReadOnlySpan<byte> value)
            => _littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(value) : BinaryPrimitives.ReadUInt32BigEndian(value);

        private readonly ulong UInt64(ReadOnlySpan<byte> value)
            => _littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(value) : BinaryPrimitives.ReadUInt64BigEndian(value);

        private static ReadOnlySpan<byte> Name(ReadOnlySpan<byte> strings, uint index)
        {
            Require(index < strings.Length, "Native object symbol name exceeds its string table.");
            ReadOnlySpan<byte> tail = strings[(int)index..];
            int end = tail.IndexOf((byte)0);
            Require(end >= 0, "Native object symbol name has no terminator.");
            return tail[..end];
        }

        private readonly void Add(ReadOnlySpan<byte> name, bool decorated)
        {
            if (decorated)
            {
                if (name.IsEmpty || name[0] != (byte)'_')
                {
                    return;
                }

                name = name[1..];
            }

            if (!name.StartsWith(_prefix))
            {
                return;
            }

            try
            {
                _symbols.Add(s_utf8.GetString(name));
            }
            catch (DecoderFallbackException error)
            {
                throw new FormatException("Generated native import is not valid UTF-8.", error);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new FormatException(message);
            }
        }
    }
}

/// <summary>
/// Retains object identity separately from its selected undefined C names.
/// </summary>
/// <param name="Format">The relocatable container: elf, coff or mach-o.</param>
/// <param name="Architecture">The processor encoded in the object header.</param>
/// <param name="IsLittleEndian">Whether object integers use little-endian encoding.</param>
/// <param name="Symbols">Unique, ordinally sorted names; generated-namespace selection removes platform C decoration.</param>
internal sealed record NativeObjectImports(string Format, string Architecture, bool IsLittleEndian, IReadOnlyList<string> Symbols);
