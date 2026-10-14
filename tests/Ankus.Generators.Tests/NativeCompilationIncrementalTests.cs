using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies immutable native compiler metadata and independent ABI render caches against current compiled consumers.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Independent implementation, source, unrelated metadata and assembly edits retain unchanged ABI renderers.
    /// </summary>
    /// <param name="edit">The independent compilation change.</param>
    [TestMethod]
    [DataRow("body")]
    [DataRow("move")]
    [DataRow("extra")]
    [DataRow("assembly")]
    public void NativeCompilationCachesPreserveIndependentEdits(string edit)
    {
        string source = NativeCompilationSource();
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        NativeCompilationModel before = NativeCompilerModel(driver);
        string changed = edit switch
        {
            "body" => source.Replace("=> 42;", "=> 43;", StringComparison.Ordinal),
            "move" => "\n\n" + source,
            "extra" => source.Replace("Unused = 1", "Unused = 2", StringComparison.Ordinal),
            _ => source,
        };
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            changed, path: "Changed.cs", cancellationToken: context.CancellationToken));
        if (edit == "assembly")
        {
            edited = edited.WithAssemblyName("ChangedExtension");
        }

        driver = RunModule(driver, edited, out Compilation second);
        NativeCompilationModel after = NativeCompilerModel(driver);
        Assert.AreEqual(before with { Assembly = after.Assembly }, after);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "NativeBindingEmission"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "NativeNodeLayoutEmission"));
        Assert.AreEqual(edit == "assembly" ? IncrementalStepRunReason.Modified : IncrementalStepRunReason.Unchanged,
            ModuleStep(driver, "NativeCompilationAnalysis"));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(first));
        Assert.AreEqual(edit == "body" ? 43 : 42, InvokeSqlReferenceAnswer(second));
        if (edit == "assembly")
        {
            Assert.AreNotEqual(before.Assembly, after.Assembly);
            Assert.AreNotEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        }
        else
        {
            Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
            Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        }
    }

    /// <summary>
    /// Binding identity and node layout edits invalidate only the corresponding renderer and reach current native output.
    /// </summary>
    /// <param name="layouts">Whether the edit changes node layouts instead of binding identity.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeCompilationMetadataInvalidatesOnlyDependentEmission(bool layouts)
    {
        string source = NativeCompilationSource();
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        string changed = layouts ? source.Replace("7:8:4", "7:12:4", StringComparison.Ordinal) :
            source.Replace("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", new string('F', 64), StringComparison.Ordinal);
        driver = RunModule(driver, initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            changed, path: "Module.cs", cancellationToken: context.CancellationToken)), out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Modified, ModuleStep(driver, layouts ? "NativeNodeLayoutEmission" : "NativeBindingEmission"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, layouts ? "NativeBindingEmission" : "NativeNodeLayoutEmission"));
        string native = ManifestValue(second, "Ankus.NativeSource");
        Assert.Contains(layouts ? "[7] = {12, 4}," :
            "static const char ankus_binding_identity[] = \"" + new string('F', 64) + "\";", native);
        Assert.AreNotEqual(ManifestValue(first, "Ankus.NativeSource"), native);
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(second));
    }

    /// <summary>
    /// Added and removed real assembly metadata updates callback dispatch without invalidating measured ABI fragments.
    /// </summary>
    [TestMethod]
    public void NativeCompilationReferencedCallbacksTrackActualMetadata()
    {
        CSharpCompilation initial = ModuleCompilation(NativeCompilationSource());
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        PortableExecutableReference reference = NativeCompilerReference("CallbackCapability",
            "[assembly: System.Reflection.AssemblyMetadata(\"Ankus.NativeCallbacks\", \"1\")] public sealed class Provider;");
        CSharpCompilation selected = initial.AddReferences(reference);
        driver = RunModule(driver, selected, out Compilation second);

        Assert.IsTrue(NativeCompilerModel(driver).ReferencedCallbacks);
        Assert.AreEqual(IncrementalStepRunReason.Modified, ModuleStep(driver, "NativeCompilationAnalysis"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "NativeBindingEmission"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "NativeNodeLayoutEmission"));
        Assert.DoesNotContain("ankus_dispatch_native_callback(", ManifestValue(first, "Ankus.NativeSource"));
        Assert.Contains("ankus_dispatch_native_callback(", ManifestValue(second, "Ankus.NativeSource"));
        Assert.AreEqual("1", ManifestValue(second, "Ankus.NativeCallbacks"));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(second));

        driver = RunModule(driver, selected.RemoveReferences(reference), out Compilation removed);
        Assert.IsFalse(NativeCompilerModel(driver).ReferencedCallbacks);
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(removed, "Ankus.NativeSource"));
        Assert.AreEqual(InstallationBody(first), InstallationBody(removed));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(removed));
    }

    /// <summary>
    /// Duplicate or nonconstant editor fields cannot select an arbitrary ABI or crash generation; correction restores it.
    /// </summary>
    /// <param name="duplicate">Whether the editor declaration duplicates the field instead of removing const.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeCompilationIncompleteBindingFailsClosedAndRecovers(bool duplicate)
    {
        string source = NativeCompilationSource();
        string changed = duplicate ? source.Replace("public const int Unused = 1;", "public const string Identity = \"duplicate\";", StringComparison.Ordinal) :
            source.Replace("public const string Identity", "public static readonly string Identity", StringComparison.Ordinal);
        CSharpCompilation initial = ModuleCompilation(changed);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out Compilation first,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);

        Assert.IsEmpty(diagnostics);
        Assert.IsNull(NativeCompilerModel(driver).Binding);
        Assert.Contains("static const char ankus_binding_identity[] = \"\";", ManifestValue(first, "Ankus.NativeSource"));
        Assert.Contains("[7] = {8, 4},", ManifestValue(first, "Ankus.NativeSource"));
        if (duplicate)
        {
            Assert.AreEqual("CS0102", Assert.ContainsSingle(first.GetDiagnostics(context.CancellationToken)
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).Id);
        }
        else
        {
            Assert.AreEqual(42, InvokeSqlReferenceAnswer(first));
        }

        driver = RunModule(driver, initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            source, path: "Module.cs", cancellationToken: context.CancellationToken)), out Compilation repaired);
        Assert.IsNotNull(NativeCompilerModel(driver).Binding);
        Assert.AreEqual(IncrementalStepRunReason.Modified, ModuleStep(driver, "NativeBindingEmission"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "NativeNodeLayoutEmission"));
        Assert.Contains("static const char ankus_binding_identity[] = \"0123456789ABCDEF", ManifestValue(repaired, "Ankus.NativeSource"));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(repaired));
    }

    /// <summary>
    /// Ambiguous emitted companions fail closed and removing one selects the remaining measured reference.
    /// </summary>
    [TestMethod]
    public void NativeCompilationAmbiguousCompanionsRequireUniqueResolution()
    {
        const string Companion = """
            namespace Ankus.Postgres
            {
                public static class NativeBinding
                {
                    public const string Identity = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
                    public const string NodeLayouts = "7:8:4;11:16:8";
                }
            }
            """;
        PortableExecutableReference first = NativeCompilerReference("FirstCompanion", Companion);
        PortableExecutableReference second = NativeCompilerReference("SecondCompanion", Companion);
        CSharpCompilation initial = ModuleCompilation("public static class Functions { [Ankus.PgFunction] public static int Answer() => 42; }")
            .AddReferences(first, second);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation ambiguous);
        Assert.IsNull(NativeCompilerModel(driver).Binding);
        Assert.IsNull(NativeCompilerModel(driver).Layouts);
        Assert.Contains("static const char ankus_binding_identity[] = \"\";", ManifestValue(ambiguous, "Ankus.NativeSource"));
        Assert.Contains("this extension has no measured node layout contract", ManifestValue(ambiguous, "Ankus.NativeSource"));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(ambiguous));

        driver = RunModule(driver, initial.RemoveReferences(second), out Compilation unique);
        Assert.IsNotNull(NativeCompilerModel(driver).Binding);
        Assert.IsNotNull(NativeCompilerModel(driver).Layouts);
        Assert.Contains("[7] = {8, 4},", ManifestValue(unique, "Ankus.NativeSource"));
        Assert.DoesNotContain("this extension has no measured node layout contract", ManifestValue(unique, "Ankus.NativeSource"));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(unique));
    }

    /// <summary>
    /// Reads the actual tracked immutable analysis result.
    /// </summary>
    private static NativeCompilationModel NativeCompilerModel(GeneratorDriver driver)
        => Assert.IsInstanceOfType<NativeCompilationModel>(Assert.ContainsSingle(Assert.ContainsSingle(
            Assert.ContainsSingle(driver.GetRunResult().Results).TrackedSteps["NativeCompilationAnalysis"]).Outputs).Value);

    /// <summary>
    /// Emits a real dependency without running a generator or loading its assembly.
    /// </summary>
    private PortableExecutableReference NativeCompilerReference(string name, string source)
    {
        CSharpCompilation compilation = ModuleCompilation(source).WithAssemblyName(name);
        using var bytes = new MemoryStream();
        Assert.IsTrue(compilation.Emit(bytes, cancellationToken: context.CancellationToken).Success);
        return MetadataReference.CreateFromImage(bytes.ToArray());
    }

    /// <summary>
    /// Supplies exact measured constants and an independently callable extension function.
    /// </summary>
    private static string NativeCompilationSource()
        => """
            namespace Ankus.Postgres
            {
                public static class NativeBinding
                {
                    public const string Identity = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
                    public const string NodeLayouts = "7:8:4;11:16:8";
                    public const int Unused = 1;
                }
            }
            public static class Functions { [Ankus.PgFunction] public static int Answer() => 42; }
            """;
}
