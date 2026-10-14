using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Source Link's custom debug information kind in a portable PDB.
    /// </summary>
    private static readonly Guid s_sourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    /// <summary>
    /// Library packages carry their symbols inside each assembly. Native AOT reads an embedded PDB when it compiles a
    /// consumer extension, so the extension's native debug information keeps Ankus source lines. Source Link maps those
    /// lines to the repository when the packages are built from a git checkout; an exported source tree has no
    /// repository to link.
    /// </summary>
    /// <param name="package">The package identity.</param>
    /// <param name="source">A source file compiled into the package's assembly.</param>
    [TestMethod]
    [DataRow("Ankus.Runtime", "PgLog.cs")]
    [DataRow("Ankus.PgConfig", "PostgresInstallation.cs")]
    [DataRow("Ankus.Testing", "PostgresTestCluster.cs")]
    public void LibraryPackagesEmbedTheirSymbols(string package, string source)
    {
        using ZipArchive archive = ZipFile.OpenRead(Path.Join(s_root, "feed", package + "." + s_version + ".nupkg"));
        Assert.IsEmpty(archive.Entries.Where(static entry => entry.FullName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)));
        using var assembly = new MemoryStream();
        using (Stream entry = archive.GetEntry("lib/net10.0/" + package + ".dll")!.Open())
        {
            entry.CopyTo(assembly);
        }

        assembly.Position = 0;
        using var reader = new PEReader(assembly);
        DebugDirectoryEntry embedded = Assert.ContainsSingle(reader.ReadDebugDirectory()
            .Where(static entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb));
        using MetadataReaderProvider provider = reader.ReadEmbeddedPortablePdbDebugDirectoryData(embedded);
        MetadataReader symbols = provider.GetMetadataReader();
        Assert.Contains(name => name.EndsWith("/" + source, StringComparison.Ordinal) || name.EndsWith("\\" + source, StringComparison.Ordinal),
            symbols.Documents.Select(handle => symbols.GetString(symbols.GetDocument(handle).Name)));
        string repository = Path.Join(IntegrationEnvironment.RepositoryRoot, ".git");
        Assert.AreEqual(Directory.Exists(repository) || File.Exists(repository), symbols.CustomDebugInformation
            .Any(handle => symbols.GetGuid(symbols.GetCustomDebugInformation(handle).Kind) == s_sourceLinkKind));
    }
}
