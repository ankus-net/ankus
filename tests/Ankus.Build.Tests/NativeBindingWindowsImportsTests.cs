using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Ankus.Build.Tests;

/// <summary>
/// Reads PostgreSQL's Windows import library and decides how an extension reaches each server global (#116).
/// </summary>
[TestClass]
public sealed class NativeBindingWindowsImportsTests
{
    /// <summary>
    /// Every name in the archive's first linker member is read, including the data and code import slots.
    /// </summary>
    [TestMethod]
    public void ImportLibrarySymbolsAreRead()
    {
        string[] names = ["__IMPORT_DESCRIPTOR_postgres", "__imp_work_mem", "__imp_pq_sendbytes", "pq_sendbytes", "\u007fpostgres_NULL_THUNK_DATA"];
        Assert.IsTrue(NativeBindingWindowsImports.ReadSymbols(Archive(names)).SetEquals(names));
    }

    /// <summary>
    /// A file that is not an import library with a complete symbol table fails instead of reporting no exports.
    /// </summary>
    /// <param name="kind">The defect to introduce.</param>
    /// <param name="message">The expected error.</param>
    [TestMethod]
    [DataRow("signature", "The PostgreSQL import library is not a COFF archive.")]
    [DataRow("member", "The PostgreSQL import library has no symbol table.")]
    [DataRow("size", "The PostgreSQL import library has no symbol table.")]
    [DataRow("count", "The PostgreSQL import library has a truncated symbol table.")]
    [DataRow("terminator", "The PostgreSQL import library has a malformed symbol name.")]
    [DataRow("exports", "The PostgreSQL import library exports nothing.")]
    public void MalformedImportLibrariesAreRejected(string kind, string message)
    {
        string[] names = kind == "exports" ? ["pq_sendbytes"] : ["__imp_work_mem"];
        byte[] library = kind switch
        {
            "member" => Archive(names, member: "/<ECSYMBOLS>/"),
            "size" => Archive(names, size: 4096),
            "count" => Archive(names, count: 1000),
            "terminator" => Archive(names, terminate: false),
            _ => Archive(names),
        };
        if (kind == "signature")
        {
            library[0] = (byte)'?';
        }

        FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingWindowsImports.ReadSymbols(library));
        Assert.AreEqual(message, error.Message);
    }

    /// <summary>
    /// Builds an archive whose first linker member lists the given names, with an optional defect.
    /// </summary>
    private static byte[] Archive(string[] names, string member = "/", int? size = null, uint? count = null, bool terminate = true)
    {
        // A symbol count, then one member offset for each name, which reading the names does not need.
        byte[] table = new byte[4 + names.Length * 4];
        BinaryPrimitives.WriteUInt32BigEndian(table, count ?? (uint)names.Length);
        var body = new List<byte>(table);
        foreach (string name in names)
        {
            body.AddRange(Encoding.UTF8.GetBytes(name));
            if (terminate)
            {
                body.Add(0);
            }
        }

        string header = member.PadRight(16) + "0".PadRight(12) + new string(' ', 12) + "0".PadRight(8) +
            (size ?? body.Count).ToString(CultureInfo.InvariantCulture).PadRight(10) + "`\n";
        return [.. "!<arch>\n"u8, .. Encoding.ASCII.GetBytes(header), .. body];
    }
}
