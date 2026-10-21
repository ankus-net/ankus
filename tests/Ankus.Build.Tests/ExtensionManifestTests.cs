using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Ankus.Build.Tests;

/// <summary>
/// Verifies how the build reads generated values that the generator writes in parts.
/// </summary>
/// <param name="context">The active test context.</param>
[TestClass]
public sealed class ExtensionManifestTests(TestContext context)
{
    private readonly string _root = Directory.CreateTempSubdirectory("ankus extension manifest ").FullName;

    /// <summary>
    /// Removes only this test's owned files.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Parts join in index order wherever their attributes appear, and a value without parts reads as before.
    /// </summary>
    [TestMethod]
    public void PartsJoinInIndexOrder()
    {
        ExtensionManifest manifest = ExtensionManifest.Read(Compile(
            ("Ankus.Sql#2", "third"),
            ("Ankus.Sql", "first "),
            ("Ankus.Sql#1", "second "),
            ("Ankus.NativeSource", "native"),
            ("Ankus.Exports", "export")));

        Assert.AreEqual("first second third", manifest.Sql);
        Assert.AreEqual("native", manifest.NativeSource);
        Assert.AreEqual("export", manifest.Exports);
    }

    /// <summary>
    /// A missing, repeated or malformed part fails the read.
    /// </summary>
    /// <param name="first">The first extra SQL part's key.</param>
    /// <param name="second">The second extra SQL part's key.</param>
    /// <param name="message">The expected error text.</param>
    [TestMethod]
    [DataRow("Ankus.Sql#2", "Ankus.Sql#3", "is missing a part")]
    [DataRow("Ankus.Sql#1", "Ankus.Sql#1", "repeats part 1")]
    [DataRow("Ankus.Sql#0", "Ankus.Sql#1", "has an invalid part number")]
    [DataRow("Ankus.Sql#x", "Ankus.Sql#1", "has an invalid part number")]
    public void IncompletePartsAreRejected(string first, string second, string message)
    {
        string assembly = Compile(
            ("Ankus.Sql", "first"),
            (first, "one"),
            (second, "two"),
            ("Ankus.NativeSource", "native"),
            ("Ankus.Exports", "export"));

        BadImageFormatException error = Assert.ThrowsExactly<BadImageFormatException>(() => ExtensionManifest.Read(assembly));
        Assert.Contains(message, error.Message);
    }

    private string Compile(params (string Key, string Value)[] metadata)
    {
        string source = "using System.Reflection;\n" + string.Concat(metadata.Select(static item =>
            $"[assembly: AssemblyMetadata({JsonSerializer.Serialize(item.Key)}, {JsonSerializer.Serialize(item.Value)})]\n"));
        string platformAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        CSharpCompilation compilation = CSharpCompilation.Create("ManifestPartsProbe",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: context.CancellationToken)],
            platformAssemblies.Split(Path.PathSeparator).Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        string assembly = Path.Join(_root, Guid.NewGuid().ToString("N") + ".dll");
        using (FileStream stream = File.Create(assembly))
        {
            EmitResult emitted = compilation.Emit(stream, cancellationToken: context.CancellationToken);
            Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        }

        return assembly;
    }
}
