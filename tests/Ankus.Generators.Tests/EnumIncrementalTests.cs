using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Moving a declaration or editing an unrelated method retains its independently cached type emission.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EnumEmissionRemainsCachedAcrossUnrelatedEdits(bool move)
    {
        const string Source = "[Ankus.PgEnum] public enum Mood { Happy, Sad }\npublic class Other { public int Value() => 1; }";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        string replacement = move ? "\n\n" + Source : Source.Replace("=> 1", "=> 2", StringComparison.Ordinal);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(replacement, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, EnumEmissionReason(driver, "mood"));
        Assert.AreEqual("CREATE TYPE \"mood\" AS ENUM (E'Happy', E'Sad');\n", InstallationBody(second));
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
    }

    /// <summary>
    /// A label edit invalidates its enum without rendering the other declaration again.
    /// </summary>
    [TestMethod]
    public void EnumEmissionInvalidatesOnlyChangedDeclaration()
    {
        const string Mood = "[Ankus.PgEnum] public enum Mood { [Ankus.PgEnumLabel(\"A\")] Ready }";
        CSharpCompilation initial = ModuleCompilation(Mood).AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("[Ankus.PgEnum] public enum Color { Blue }", path: "Color.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.First(),
            CSharpSyntaxTree.ParseText(Mood.Replace("\"A\"", "\"B\"", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Modified, EnumEmissionReason(driver, "mood"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, EnumEmissionReason(driver, "color"));
        Assert.Contains("CREATE TYPE \"mood\" AS ENUM (E'A');", InstallationBody(first));
        Assert.Contains("CREATE TYPE \"mood\" AS ENUM (E'B');", InstallationBody(second));
        Assert.Contains("CREATE TYPE \"color\" AS ENUM (E'Blue');", InstallationBody(second));
    }

    /// <summary>
    /// Semantic constants update enum labels and inherited schemas even when the enum syntax stays unchanged.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EnumEmissionTracksReferencedConstants(bool schema)
    {
        string source = schema
            ? "[Ankus.PgSchema(Names.Value)] public static class Types { [Ankus.PgEnum] public enum Mood { Ready } }"
            : "[Ankus.PgEnum] public enum Mood { [Ankus.PgEnumLabel(Names.Value)] Ready }";
        CSharpCompilation initial = ModuleCompilation(source).AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("public static class Names { public const string Value = \"A\"; }", path: "Names.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Last(),
            CSharpSyntaxTree.ParseText("public static class Names { public const string Value = \"B\"; }", path: "Names.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Modified, EnumEmissionReason(driver, "mood"));
        Assert.Contains(schema ? "CREATE TYPE \"A\".\"mood\" AS ENUM (E'Ready');" : "CREATE TYPE \"mood\" AS ENUM (E'A');", InstallationBody(first));
        Assert.Contains(schema ? "CREATE TYPE \"B\".\"mood\" AS ENUM (E'Ready');" : "CREATE TYPE \"mood\" AS ENUM (E'B');", InstallationBody(second));
    }

    /// <summary>
    /// Cached validation failures attach to the current tree and disappear after their actual cause is corrected.
    /// </summary>
    [TestMethod]
    public void EnumDiagnosticsFollowCurrentTreeAndRecover()
    {
        const string Source = "[Ankus.PgEnum] public enum Mood { Happy = 1, Sad = 1 }";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _,
            out ImmutableArray<Diagnostic> firstErrors, context.CancellationToken);
        Assert.AreEqual("ANKUS290", Assert.ContainsSingle(firstErrors).Id);
        SyntaxTree current = CSharpSyntaxTree.ParseText(Source + "\n// unrelated edit", path: "Module.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), current);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);

        Assert.AreEqual("ANKUS290", error.Id);
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreEqual(Assert.ContainsSingle(firstErrors).Location.SourceSpan, error.Location.SourceSpan);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "EnumModel"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "EnumEmission"));

        CSharpCompilation repaired = edited.ReplaceSyntaxTree(current,
            CSharpSyntaxTree.ParseText(Source.Replace("Sad = 1", "Sad = 2", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Modified, EnumEmissionReason(driver, "mood"));
        Assert.AreEqual("CREATE TYPE \"mood\" AS ENUM (E'Happy', E'Sad');\n", InstallationBody(output));
    }

    /// <summary>
    /// Removing an enum removes its registration, catalog check and SQL while preserving the remaining enum.
    /// </summary>
    [TestMethod]
    public void EnumRemovalDropsOnlyItsGeneratedContracts()
    {
        CSharpCompilation initial = ModuleCompilation("[Ankus.PgEnum] public enum Mood { Happy }").AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("[Ankus.PgEnum] public enum Color { Blue }", path: "Color.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        Assert.Contains("CREATE TYPE \"mood\"", InstallationBody(first));
        _ = RunModule(driver, initial.RemoveSyntaxTrees(initial.SyntaxTrees.First()), out Compilation second);

        Assert.AreEqual("CREATE TYPE \"color\" AS ENUM (E'Blue');\n", InstallationBody(second));
        Assert.DoesNotContain("global::Mood", string.Join("\n", second.SyntaxTrees.Select(tree => tree.GetText(context.CancellationToken).ToString())));
        Assert.DoesNotContain("\\x6d\\x6f\\x6f\\x64", ManifestValue(second, "Ankus.NativeSource"));
    }

    /// <summary>
    /// SQL graph options change composition without rerendering unchanged enum contracts.
    /// </summary>
    [TestMethod]
    public void EnumGraphOptionsPreserveCachedTypeEmission()
    {
        const string Source = "[Ankus.PgEnum(GenerateSql = true)] public enum Mood { Happy }";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(Source.Replace("true", "false", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, EnumEmissionReason(driver, "mood"));
        Assert.AreEqual("CREATE TYPE \"mood\" AS ENUM (E'Happy');\n", InstallationBody(first));
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(second));
        Assert.Contains("global::Ankus.CompilerServices.PgEnumRegistry.Register<global::Mood>",
            string.Join("\n", second.SyntaxTrees.Select(tree => tree.GetText(context.CancellationToken).ToString())));
    }

    /// <summary>
    /// A detached declaration key cannot resolve an externally aliased type to an identically named local enum.
    /// </summary>
    [TestMethod]
    public void EnumDependencyIdentityPreservesReferencedAssembly()
    {
        CSharpCompilation external = CSharpCompilation.Create("ExternalEnums",
            [CSharpSyntaxTree.ParseText("namespace Shared { public enum Mood { Happy } }", cancellationToken: context.CancellationToken)],
            s_references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = external.Emit(stream, cancellationToken: context.CancellationToken);
        Assert.IsTrue(emitted.Success, string.Join("\n", emitted.Diagnostics));
        PortableExecutableReference reference = MetadataReference.CreateFromImage(stream.ToArray(),
            MetadataReferenceProperties.Assembly.WithAliases(["external"]));
        const string Source = """
            extern alias external;
            [assembly: Ankus.PgSql("after", "SELECT 'after';")]
            [assembly: Ankus.PgRequires(typeof(external::Shared.Mood), DeclarationId = "after")]
            namespace Shared { [Ankus.PgEnum] public enum Mood { Happy } }
            """;
        CSharpCompilation initial = ModuleCompilation(Source).AddReferences(reference);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out Compilation failed,
            out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual("ANKUS480", error.Id);
        Assert.Contains("does not declare a generated SQL object in this extension", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsFalse(failed.Assembly.GetAttributes().Any(static attribute => attribute.ConstructorArguments.Length == 2 &&
            attribute.ConstructorArguments[0].Value is "Ankus.Sql"));
        CSharpCompilation repaired = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(Source.Replace("external::Shared", "global::Shared", StringComparison.Ordinal),
                path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Cached, EnumEmissionReason(driver, "mood"));
        Assert.AreEqual("CREATE TYPE \"mood\" AS ENUM (E'Happy');\nSELECT 'after';\n", InstallationBody(output));
    }

    /// <summary>
    /// Reads the actual per-declaration emission cache decision from the production generator driver.
    /// </summary>
    private static IncrementalStepRunReason EnumEmissionReason(GeneratorDriver driver, string name)
    {
        IEnumerable<(object Value, IncrementalStepRunReason Reason)> outputs = Assert.ContainsSingle(driver.GetRunResult().Results)
            .TrackedSteps["EnumEmission"].SelectMany(static step => step.Outputs);
        return Assert.ContainsSingle(outputs.Where(output => output.Reason != IncrementalStepRunReason.Removed &&
            output.Value is EnumPipeline.EnumEmission emission &&
            emission.Sql.Contains("\"" + name + "\" AS ENUM", StringComparison.Ordinal))).Reason;
    }
}
