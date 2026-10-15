using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Invalid prefix literals retain fresh diagnostic trees while cached empty output repairs normally.
    /// </summary>
    /// <param name="expression">The C# expression containing an invalid native literal.</param>
    /// <param name="expected">The independently required transport error.</param>
    [TestMethod]
    [DataRow("null", "ANKUS347")]
    [DataRow("\"bad\\0prefix\"", "ANKUS348")]
    [DataRow("\"\\uD800\"", "ANKUS349")]
    public void GucPrefixDiagnosticsFollowCurrentTreesAndRecover(string expression, string expected)
    {
        string attribute = "Ankus.PgGucPrefix(" + expression + ")";
        string source = "[assembly: " + attribute + "]";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _,
            out ImmutableArray<Diagnostic> first, context.CancellationToken);
        Assert.AreEqual(expected, Assert.ContainsSingle(first).Id);
        SyntaxTree current = CSharpSyntaxTree.ParseText(source, path: "CurrentPrefix.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation moved = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), current);
        driver = driver.RunGeneratorsAndUpdateCompilation(moved, out Compilation invalid, out ImmutableArray<Diagnostic> second, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(second);

        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual("https://ankus.net/configuration/#prefix-declaration-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreEqual(expression, current.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "GucPrefixEmission"));
        Assert.DoesNotContain("ankus_reserve_guc_prefix(", ManifestValue(invalid, "Ankus.NativeSource"));
        CSharpCompilation repaired = moved.ReplaceSyntaxTree(current, CSharpSyntaxTree.ParseText("[assembly: Ankus.PgGucPrefix(\"fixed\")]",
            path: "CurrentPrefix.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Modified, ModuleStep(driver, "GucPrefixEmission"));
        Assert.Contains("ankus_reserve_guc_prefix(\"\\146\\151\\170\\145\\144\");", ManifestValue(output, "Ankus.NativeSource"));
        AssertGucCompilation(output, []);
    }

    /// <summary>
    /// Duplicate-name diagnostics, repaired sorted registration and successive removals use the current inventory.
    /// </summary>
    [TestMethod]
    public void GucDuplicatesAndRemovalUseCurrentInventory()
    {
        const string Marker = "[assembly: Ankus.PgModule]\n";
        string firstSource = GucCacheSource("integer");
        string secondSource = GucCacheSource("integer").Replace("Settings", "Other", StringComparison.Ordinal)
            .Replace("Mode", "OtherMode", StringComparison.Ordinal).Replace("demo.value", "demo.VALUE", StringComparison.Ordinal)
            .Replace("int Value {", "int OtherValue {", StringComparison.Ordinal);
        CSharpCompilation initial = ModuleCompilation(Marker + firstSource + secondSource);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS202", error.Id);
        Assert.AreEqual("\"demo.VALUE\"", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        string survivor = secondSource.Replace("demo.VALUE", "demo.other", StringComparison.Ordinal);
        CSharpCompilation repaired = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            Marker + firstSource + survivor, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation both);
        (GucEmission first, IncrementalStepRunReason firstReason) = TrackedGucEmission(driver, "demo.value");
        (GucEmission other, IncrementalStepRunReason otherReason) = TrackedGucEmission(driver, "demo.other");

        Assert.AreEqual(IncrementalStepRunReason.Cached, firstReason);
        Assert.AreEqual(IncrementalStepRunReason.Modified, otherReason);
        Assert.AreSequenceEqual(["ankus_guc_register(&" + other.Symbol + ");", "ankus_guc_register(&" + first.Symbol + ");"],
            ManifestValue(both, "Ankus.NativeSource").Split('\n').Select(static line => line.Trim())
                .Where(static line => line.StartsWith("ankus_guc_register(&", StringComparison.Ordinal)));
        CSharpCompilation removed = repaired.ReplaceSyntaxTree(repaired.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            Marker + survivor, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, removed, out Compilation one);
        Assert.DoesNotContain(first.Symbol, ManifestValue(one, "Ankus.NativeSource"));
        Assert.Contains(other.Symbol, ManifestValue(one, "Ankus.NativeSource"));
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedGucEmission(driver, "demo.other").Reason);
        Assert.AreEqual(other, TrackedGucEmission(driver, "demo.other").Emission);
        CSharpCompilation empty = removed.ReplaceSyntaxTree(removed.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            Marker, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, empty, out Compilation none);
        Assert.AreEqual("Pg_magic_func", ManifestValue(none, "Ankus.Exports").Trim());
        Assert.DoesNotContain(other.Symbol, ManifestValue(none, "Ankus.NativeSource"));
        Assert.DoesNotContain("ankus_guc_register(", ManifestValue(none, "Ankus.NativeSource"));
        Assert.IsEmpty(Assert.ContainsSingle(driver.GetRunResult().Results).GeneratedSources
            .Where(static source => source.HintName == "GucProperties.g.cs"));
        AssertGucCompilation(none, []);
    }
}
