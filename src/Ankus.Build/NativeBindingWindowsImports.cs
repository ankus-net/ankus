using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Decides how a Windows extension reaches each server global from the import library it links against.
/// </summary>
/// <remarks>
/// An import library gives a variable exported as data only its <c>__imp_</c> address slot. A declaration marked
/// <c>PGDLLIMPORT</c> compiles to a load through that slot, while an unmarked one refers to the bare name, which
/// nothing defines. PostgreSQL 13 and 14 export some variables that their headers leave unmarked, and every version
/// declares some variables that its <c>postgres.exe</c> does not export, such as those of optional features.
/// </remarks>
internal static class NativeBindingWindowsImports
{
    private const int HeaderSize = 60;
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    /// <summary>
    /// Gives each extern global of a Windows target the linkage that the installation's <c>postgres.lib</c> supports.
    /// </summary>
    /// <param name="headers">The selected headers' declarations.</param>
    /// <param name="installation">The installation whose import library the extension links against.</param>
    /// <param name="cancellationToken">Cancels reading the import library.</param>
    /// <returns>The same declarations, with the linkage of each Windows global decided.</returns>
    internal static async Task<NativeHeaderCatalog> ApplyAsync(NativeHeaderCatalog headers, PostgresInstallation installation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(installation);
        if (!IsWindows(headers.Target))
        {
            return headers;
        }

        byte[] library = await File.ReadAllBytesAsync(LibraryPath(installation), cancellationToken);
        return headers with { Symbols = Apply(headers.Symbols, headers.Target, ReadSymbols(library)) };
    }

    /// <summary>
    /// Names the import library that a Windows extension links against.
    /// </summary>
    /// <param name="installation">The selected PostgreSQL installation.</param>
    /// <returns>The full path of its <c>postgres.lib</c>.</returns>
    internal static string LibraryPath(PostgresInstallation installation)
        => Path.Join(installation.TargetInfo?.MapPath(installation.LibraryDirectory) ?? installation.LibraryDirectory, "postgres.lib");

    /// <summary>
    /// Assigns each extern global the linkage that the server's exports support.
    /// </summary>
    /// <param name="symbols">The selected headers' declarations.</param>
    /// <param name="target">The selected compiler target.</param>
    /// <param name="imports">Every public symbol of the server's import library.</param>
    /// <returns>Declarations in ordinal order, with unchanged functions and non-extern globals.</returns>
    internal static IReadOnlyDictionary<string, NativeHeaderSymbol> Apply(IReadOnlyDictionary<string, NativeHeaderSymbol> symbols,
        NativeHeaderTarget target, IReadOnlySet<string> imports)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(imports);
        if (!IsWindows(target))
        {
            return symbols;
        }

        var linked = new SortedDictionary<string, NativeHeaderSymbol>(StringComparer.Ordinal);
        foreach ((string name, NativeHeaderSymbol symbol) in symbols)
        {
            linked.Add(name, symbol.IsFunction || symbol.StorageClass != "extern" ? symbol : symbol with
            {
                Linkage = symbol.IsThreadLocal || !imports.Contains(ImportName(symbol.NativeName, target)) ? NativeHeaderLinkage.Unavailable
                    : symbol.Attributes.Contains("DLLImportAttr") ? NativeHeaderLinkage.Direct
                    : NativeHeaderLinkage.ImportSlot,
            });
        }

        return new ReadOnlyDictionary<string, NativeHeaderSymbol>(linked);
    }

    /// <summary>
    /// Spells the C identifier whose decorated name is a variable's import address slot.
    /// </summary>
    /// <param name="name">The variable's C name.</param>
    /// <param name="target">The selected compiler target.</param>
    /// <returns>An identifier for a pointer to the variable.</returns>
    internal static string SlotIdentifier(string name, NativeHeaderTarget target)
        => (target.RuntimeIdentifier == "win-x86" ? "_imp__" : "__imp_") + name;

    /// <summary>
    /// Reads every public symbol from the first linker member of a COFF archive, such as an import library.
    /// </summary>
    /// <param name="library">The complete archive.</param>
    /// <returns>The archive's symbol names.</returns>
    internal static IReadOnlySet<string> ReadSymbols(ReadOnlySpan<byte> library)
    {
        if (!library.StartsWith("!<arch>\n"u8) || library.Length < 8 + HeaderSize)
        {
            throw Invalid("is not a COFF archive");
        }

        ReadOnlySpan<byte> header = library.Slice(8, HeaderSize);
        if (!header[..16].SequenceEqual("/               "u8) || !header[58..].SequenceEqual("`\n"u8) ||
            !int.TryParse(header.Slice(48, 10).TrimEnd((byte)' '), NumberStyles.None, CultureInfo.InvariantCulture, out int size) ||
            size < 4 || size > library.Length - 8 - HeaderSize)
        {
            throw Invalid("has no symbol table");
        }

        ReadOnlySpan<byte> member = library.Slice(8 + HeaderSize, size);
        uint count = BinaryPrimitives.ReadUInt32BigEndian(member);
        if (count > (uint)(member.Length - 4) / 4)
        {
            throw Invalid("has a truncated symbol table");
        }

        ReadOnlySpan<byte> names = member[(4 + (int)count * 4)..];
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        for (uint index = 0; index < count; index++)
        {
            int end = names.IndexOf((byte)0);
            if (end <= 0)
            {
                throw Invalid("has a malformed symbol name");
            }

            _ = symbols.Add(s_utf8.GetString(names[..end]));
            names = names[(end + 1)..];
        }

        return symbols.Any(static symbol => symbol.StartsWith("__imp_", StringComparison.Ordinal))
            ? symbols
            : throw Invalid("exports nothing");
    }

    private static bool IsWindows(NativeHeaderTarget target) => target.RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal);

    /// <summary>
    /// Spells a variable's import slot as the import library names it: the C name with 32-bit Windows's underscore,
    /// after <c>__imp_</c>. Clang's mangled name already carries the host's decoration, such as macOS's underscore.
    /// </summary>
    private static string ImportName(string name, NativeHeaderTarget target)
        => (target.RuntimeIdentifier == "win-x86" ? "__imp__" : "__imp_") + name;

    private static FormatException Invalid(string problem) => new($"The PostgreSQL import library {problem}.");
}
