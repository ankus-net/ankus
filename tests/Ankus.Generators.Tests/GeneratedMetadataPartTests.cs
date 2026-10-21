using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// A long value is written in parts no longer than the limit, a surrogate pair on a boundary moves whole into the
    /// next part, and the parts rejoin to the exact value.
    /// </summary>
    [TestMethod]
    public void LongMetadataSplitsWithoutBreakingSurrogatePairs()
    {
        int limit = PgFunctionGenerator.MetadataPartLength;
        string value = new string('a', limit - 1) + "😀" + new string('b', 2 * limit);
        CSharpCompilation compilation = CompileMetadata(PgFunctionGenerator.Metadata("Ankus.Probe", value));

        string[] parts = [.. compilation.Assembly.GetAttributes().Select(static attribute => (string)attribute.ConstructorArguments[1].Value!)];
        Assert.AreSequenceEqual([new string('a', limit - 1), "😀" + new string('b', limit - 2), new string('b', limit), "bb"], parts);
        Assert.AreEqual(value, ManifestValue(compilation, "Ankus.Probe"));
    }

    /// <summary>
    /// A value that fits, including an empty one, keeps a single attribute under its plain key.
    /// </summary>
    /// <param name="value">The metadata value.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("SELECT 1;")]
    public void ShortMetadataKeepsOneAttribute(string value)
    {
        CSharpCompilation compilation = CompileMetadata(PgFunctionGenerator.Metadata("Ankus.Probe", value));

        AttributeData attribute = Assert.ContainsSingle(compilation.Assembly.GetAttributes());
        Assert.AreEqual("Ankus.Probe", attribute.ConstructorArguments[0].Value);
        Assert.AreEqual(value, attribute.ConstructorArguments[1].Value);
    }

    /// <summary>
    /// Generated extension metadata contains no literal longer than the part limit, and the native source rejoins
    /// from several parts.
    /// </summary>
    [TestMethod]
    public void GeneratedManifestUsesShortLiterals()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgFunction] public static int Answer() => 42;
            }
            """);

        Assert.IsEmpty(diagnostics);
        string[] keys = [.. compilation.Assembly.GetAttributes()
            .Where(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute")
            .Select(static attribute => (Key: (string)attribute.ConstructorArguments[0].Value!, Value: (string)attribute.ConstructorArguments[1].Value!))
            .Where(static item => item.Key.StartsWith("Ankus.", StringComparison.Ordinal))
            .Select(static item => item.Value.Length <= PgFunctionGenerator.MetadataPartLength ? item.Key : "too long: " + item.Key)];
        Assert.DoesNotContain(static key => key.StartsWith("too long: ", StringComparison.Ordinal), keys);
        Assert.Contains("Ankus.NativeSource#1", keys);
        Assert.Contains("PG_MODULE_MAGIC", ManifestValue(compilation, "Ankus.NativeSource"));
    }

    private CSharpCompilation CompileMetadata(string attributes)
    {
        CSharpCompilation compilation = CSharpCompilation.Create("MetadataParts",
            [CSharpSyntaxTree.ParseText(attributes, cancellationToken: context.CancellationToken)], s_references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return compilation;
    }
}
