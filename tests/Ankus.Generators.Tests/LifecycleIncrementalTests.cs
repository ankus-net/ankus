using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Both phase renderers remain cached across implementation edits and source movement with exact stable artifacts.
    /// </summary>
    /// <param name="moduleLoad">Whether the phase performs immediate module registration.</param>
    /// <param name="move">Whether the declaring source moves instead of changing its body.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void LifecycleEmissionPreservesImplementationEdits(bool moduleLoad, bool move)
    {
        string source = LifecycleSource(moduleLoad, "Run");
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        LifecycleEmission before = TrackedLifecycleEmission(driver, moduleLoad, "Run").Emission;
        string replacement = move ? "\n\n" + source : source.Replace("{ }", "{ System.GC.KeepAlive(null); }", StringComparison.Ordinal);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            replacement, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedLifecycleEmission(driver, moduleLoad, "Run").Reason);
        Assert.AreEqual(before, TrackedLifecycleEmission(driver, moduleLoad, "Run").Emission);
        Assert.Contains("global::Lifecycle.@Run();", before.Managed);
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        Assert.AreEqual(ManifestValue(first, "Ankus.Exports"), ManifestValue(second, "Ankus.Exports"));
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(second, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(second));
    }

    /// <summary>
    /// Managed renames refresh invocation and native dispatcher identity without rerendering the other phase.
    /// </summary>
    /// <param name="moduleLoad">Whether the renamed callback performs immediate registration.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LifecycleEmissionTracksInvocationIdentity(bool moduleLoad)
    {
        string source = LifecycleSource(moduleLoad, "Run");
        CSharpCompilation initial = ModuleCompilation(source).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            LifecycleSource(!moduleLoad, "Other"), path: "Other.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        LifecycleEmission before = TrackedLifecycleEmission(driver, moduleLoad, "Run").Emission;
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.First(), CSharpSyntaxTree.ParseText(
            source.Replace("Run()", "Renamed()", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);
        (LifecycleEmission emission, IncrementalStepRunReason reason) = TrackedLifecycleEmission(driver, moduleLoad, "Renamed");

        Assert.AreEqual(IncrementalStepRunReason.Modified, reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedLifecycleEmission(driver, !moduleLoad, "Other").Reason);
        Assert.AreNotEqual(before.Declaration.Callback, emission.Declaration.Callback);
        Assert.Contains("global::Lifecycle.@Renamed();", emission.Managed);
        Assert.Contains(emission.Declaration.Callback + "(AnkusError *", ManifestValue(output, "Ankus.NativeSource"));
        Assert.DoesNotContain(before.Declaration.Callback, ManifestValue(output, "Ankus.NativeSource"));
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// An assembly rename refreshes phase callback symbols, while a new assembly version keeps them, so an upgraded library
    /// still exports the symbols an installed version's catalog entries name.
    /// </summary>
    /// <param name="moduleLoad">Whether this callback performs immediate registration.</param>
    /// <param name="version">Whether the version changes instead of the assembly name.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void LifecycleEmissionTracksAssemblyIdentity(bool moduleLoad, bool version)
    {
        CSharpCompilation initial = ModuleCompilation(LifecycleSource(moduleLoad, "Run")).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")]", path: "Version.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        LifecycleEmission before = TrackedLifecycleEmission(driver, moduleLoad, "Run").Emission;
        CSharpCompilation edited = version ? initial.ReplaceSyntaxTree(initial.SyntaxTrees.Last(), CSharpSyntaxTree.ParseText(
            "[assembly: System.Reflection.AssemblyVersion(\"2.0.0.0\")]", path: "Version.cs", cancellationToken: context.CancellationToken)) : initial.WithAssemblyName("Renamed");
        driver = RunModule(driver, edited, out Compilation output);
        (LifecycleEmission emission, IncrementalStepRunReason reason) = TrackedLifecycleEmission(driver, moduleLoad, "Run");

        Assert.AreEqual(before.Declaration.Target, emission.Declaration.Target);
        Assert.Contains(emission.Declaration.Callback + "(AnkusError *", ManifestValue(output, "Ankus.NativeSource"));
        if (version)
        {
            Assert.AreEqual(before.Declaration.Callback, emission.Declaration.Callback);
        }
        else
        {
            Assert.AreEqual(IncrementalStepRunReason.Modified, reason);
            Assert.AreNotEqual(before.Declaration.Callback, emission.Declaration.Callback);
            Assert.DoesNotContain(before.Declaration.Callback, ManifestValue(output, "Ankus.NativeSource"));
        }

        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Cached invalid models retain current diagnostic trees and source coordinates, then recover after signature repair.
    /// </summary>
    /// <param name="moduleLoad">Whether the invalid callback performs immediate registration.</param>
    /// <param name="mapped">Whether diagnostics use a current mapped source path and line.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void LifecycleDiagnosticsFollowCurrentTreesAndRecover(bool moduleLoad, bool mapped)
    {
        string valid = LifecycleSource(moduleLoad, "Run");
        string source = valid.Replace("static void", "static int", StringComparison.Ordinal).Replace("{ }", "=> 1;", StringComparison.Ordinal);
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _, out ImmutableArray<Diagnostic> previous, context.CancellationToken);
        string editedSource = mapped ? "#line 100 \"CurrentMapped.cs\"\n" + source : "\n\n" + source;
        SyntaxTree current = CSharpSyntaxTree.ParseText(editedSource, path: "Current.cs", cancellationToken: context.CancellationToken);
        driver = driver.RunGeneratorsAndUpdateCompilation(initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), current),
            out _, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual("ANKUS238", error.Id);
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreEqual("int", current.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual(mapped ? "CurrentMapped.cs" : "Current.cs", error.Location.GetMappedLineSpan().Path);
        Assert.AreEqual(mapped ? 100 : 3, error.Location.GetMappedLineSpan().StartLinePosition.Line);
        Assert.AreEqual(Assert.ContainsSingle(previous).GetMessage(CultureInfo.InvariantCulture), error.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, (moduleLoad ? "ModuleLoad" : "Initialize") + "Emission"));
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);

        CSharpCompilation repaired = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            valid, path: "Current.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedLifecycleEmission(driver, moduleLoad, "Run").Reason);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A conflicting pair of phase markers reports once after canonical discovery and recovers when one marker is removed.
    /// </summary>
    [TestMethod]
    public void LifecycleConflictingMarkersNormalizeAndRecover()
    {
        string valid = LifecycleSource(false, "Run");
        string source = valid.Replace("[Ankus.PgInitialize]", "[Ankus.PgInitialize, Ankus.PgModuleLoad]", StringComparison.Ordinal);
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out Compilation rejected,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);

        Assert.AreEqual("ANKUS230", Assert.ContainsSingle(diagnostics).Id);
        Assert.DoesNotContain("_PG_init", ManifestValue(rejected, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        driver = RunModule(driver, initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            valid, path: "Module.cs", cancellationToken: context.CancellationToken)), out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedLifecycleEmission(driver, false, "Run").Reason);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Duplicate phases diagnose the current second definition and recover without rerendering unaffected phase artifacts.
    /// </summary>
    /// <param name="moduleLoad">Whether the duplicated phase performs immediate registration.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LifecycleDuplicatePhasesFollowCurrentTreesAndRecover(bool moduleLoad)
    {
        string marker = moduleLoad ? "PgModuleLoad" : "PgInitialize";
        string source = LifecycleSource(moduleLoad, "First") + "\n" + LifecycleSource(moduleLoad, "Last");
        CSharpCompilation initial = ModuleCompilation(source).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            LifecycleSource(!moduleLoad, "Other"), path: "Other.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _, out ImmutableArray<Diagnostic> previous, context.CancellationToken);
        SyntaxTree current = CSharpSyntaxTree.ParseText("\n\n" + source, path: "Current.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation moved = initial.ReplaceSyntaxTree(initial.SyntaxTrees.First(), current);
        driver = driver.RunGeneratorsAndUpdateCompilation(moved, out Compilation rejected, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual("ANKUS249", error.Id);
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreEqual("Last", current.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual(Assert.ContainsSingle(previous).GetMessage(CultureInfo.InvariantCulture), error.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedLifecycleEmission(driver, moduleLoad, "First").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedLifecycleEmission(driver, moduleLoad, "Last").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedLifecycleEmission(driver, !moduleLoad, "Other").Reason);
        Assert.AreSequenceEqual(["Pg_magic_func"], ManifestValue(rejected, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));

        string repaired = source.Replace("[Ankus." + marker + "] public static void Last", "public static void Last", StringComparison.Ordinal);
        driver = RunModule(driver, moved.ReplaceSyntaxTree(current, CSharpSyntaxTree.ParseText(
            repaired, path: "Current.cs", cancellationToken: context.CancellationToken)), out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedLifecycleEmission(driver, moduleLoad, "First").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedLifecycleEmission(driver, !moduleLoad, "Other").Reason);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Removing one phase retains exact surviving artifacts across positional changes and removes stale registration calls.
    /// </summary>
    /// <param name="moduleLoad">Whether the removed callback performs immediate registration.</param>
    /// <param name="removeFirst">Whether source removal changes the surviving tree's positional input.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void LifecycleRemovalPreservesSurvivors(bool moduleLoad, bool removeFirst)
    {
        CSharpCompilation initial = ModuleCompilation(LifecycleSource(removeFirst ? moduleLoad : !moduleLoad, removeFirst ? "Run" : "Other"))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(LifecycleSource(removeFirst ? !moduleLoad : moduleLoad, removeFirst ? "Other" : "Run"),
                path: "Other.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        LifecycleEmission removed = TrackedLifecycleEmission(driver, moduleLoad, "Run").Emission;
        LifecycleEmission survivor = TrackedLifecycleEmission(driver, !moduleLoad, "Other").Emission;
        driver = RunModule(driver, initial.RemoveSyntaxTrees(removeFirst ? initial.SyntaxTrees.First() : initial.SyntaxTrees.Last()), out Compilation output);

        Assert.AreEqual(removeFirst ? IncrementalStepRunReason.New : IncrementalStepRunReason.Cached, TrackedLifecycleEmission(driver, !moduleLoad, "Other").Reason);
        Assert.AreEqual(survivor, TrackedLifecycleEmission(driver, !moduleLoad, "Other").Emission);
        Assert.Contains(survivor.Declaration.Callback + "(AnkusError *", ManifestValue(output, "Ankus.NativeSource"));
        Assert.DoesNotContain(removed.Declaration.Callback, ManifestValue(output, "Ankus.NativeSource"));
        if (moduleLoad)
        {
            Assert.DoesNotContain("ankus_ensure_module_loaded", ManifestValue(output, "Ankus.NativeSource"));
        }

        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Removing the final phase retains ordinary function contracts and removes their current initialization requirement.
    /// </summary>
    /// <param name="moduleLoad">Whether the removed phase performs immediate registration.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LifecycleRemovalPreservesOrdinarySql(bool moduleLoad)
    {
        CSharpCompilation initial = ModuleCompilation(LifecycleSource(moduleLoad, "Run")).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "public static class Functions { [Ankus.PgFunction] public static int Answer() => 42; }", path: "Functions.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        FunctionEmission function = TrackedFunctionEmission(driver, "answer").Emission;
        driver = RunModule(driver, initial.RemoveSyntaxTrees(initial.SyntaxTrees.First()), out Compilation second);

        Assert.AreEqual(function, TrackedFunctionEmission(driver, "answer").Emission);
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.Contains((function.Native.Header + "    ankus_ensure_initialized();").ReplaceLineEndings("\n"), ManifestValue(first, "Ankus.NativeSource"));
        Assert.Contains((function.Native.Header + function.Native.Body).ReplaceLineEndings("\n"), ManifestValue(second, "Ankus.NativeSource"));
        Assert.DoesNotContain("ankus_ensure_initialized", ManifestValue(second, "Ankus.NativeSource"));
        Assert.AreSequenceEqual(["Pg_magic_func", function.NativeName, "pg_finfo_" + function.NativeName], ManifestValue(second, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Removing the only phase returns an ordinary assembly to an empty generated inventory.
    /// </summary>
    /// <param name="moduleLoad">Whether the removed phase performs immediate registration.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LifecycleRemovalRestoresEmptyInventory(bool moduleLoad)
    {
        CSharpCompilation initial = ModuleCompilation(LifecycleSource(moduleLoad, "Run"));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            "public static partial class Lifecycle { public static void Run() { } }", path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);

        Assert.IsEmpty(Assert.ContainsSingle(driver.GetRunResult().Results).GeneratedSources);
        Assert.HasCount(1, output.SyntaxTrees);
        Assert.DoesNotContain(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute", output.Assembly.GetAttributes());
    }

    /// <summary>
    /// Partial implementations normalize to one canonical phase, reuse rendering after body edits and reject async implementations.
    /// </summary>
    /// <param name="moduleLoad">Whether the phase performs immediate registration.</param>
    /// <param name="implementationMarker">Whether the marker appears on the implementation rather than the definition.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void LifecyclePartialImplementationsTrackValidity(bool moduleLoad, bool implementationMarker)
    {
        string marker = "[Ankus." + (moduleLoad ? "PgModuleLoad" : "PgInitialize") + "] ";
        string definition = "public static partial class Lifecycle { " + (implementationMarker ? string.Empty : marker) + "public static partial void Run(); }";
        string implementation = "public static partial class Lifecycle { " + (implementationMarker ? marker : string.Empty) + "public static partial void Run() { } }";
        CSharpCompilation initial = ModuleCompilation(definition).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            implementation, path: "Implementation.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        LifecycleEmission before = TrackedLifecycleEmission(driver, moduleLoad, "Run").Emission;
        string bodyEdit = implementation.Replace("{ }", "{ System.GC.KeepAlive(null); }", StringComparison.Ordinal);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Last(), CSharpSyntaxTree.ParseText(
            bodyEdit, path: "Implementation.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedLifecycleEmission(driver, moduleLoad, "Run").Reason);
        Assert.AreEqual(before, TrackedLifecycleEmission(driver, moduleLoad, "Run").Emission);
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(second, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        CSharpCompilation invalid = edited.ReplaceSyntaxTree(edited.SyntaxTrees.Last(), CSharpSyntaxTree.ParseText(
            bodyEdit.Replace("static partial void", "static async partial void", StringComparison.Ordinal)
                .Replace("{ System.GC.KeepAlive(null); }", "{ await System.Threading.Tasks.Task.Yield(); }", StringComparison.Ordinal),
            path: "Implementation.cs", cancellationToken: context.CancellationToken));
        driver = driver.RunGeneratorsAndUpdateCompilation(invalid, out _, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual("ANKUS233", error.Id);
        Assert.AreSame(invalid.SyntaxTrees.Last(), error.Location.SourceTree);
        Assert.AreEqual("async", invalid.SyntaxTrees.Last().GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        driver = RunModule(driver, edited, out Compilation repaired);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedLifecycleEmission(driver, moduleLoad, "Run").Reason);
        Assert.AreEqual(before, TrackedLifecycleEmission(driver, moduleLoad, "Run").Emission);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(repaired, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Supplies a complete phase declaration with a canonical partial containing type.
    /// </summary>
    /// <param name="moduleLoad">Whether the method performs immediate registration.</param>
    /// <param name="name">The managed method name.</param>
    /// <returns>The complete nullable-enabled phase source.</returns>
    private static string LifecycleSource(bool moduleLoad, string name)
        => "#nullable enable\npublic static partial class Lifecycle { [Ankus." + (moduleLoad ? "PgModuleLoad" : "PgInitialize") + "] public static void " + name + "() { } }";

    /// <summary>
    /// Reads an actual production phase artifact and tracked incremental reuse decision.
    /// </summary>
    /// <param name="driver">The executed production generator.</param>
    /// <param name="moduleLoad">Whether to inspect the immediate registration stage.</param>
    /// <param name="name">The exact managed method name.</param>
    /// <returns>The independently cached artifacts and actual cache decision.</returns>
    private static (LifecycleEmission Emission, IncrementalStepRunReason Reason) TrackedLifecycleEmission(GeneratorDriver driver, bool moduleLoad, string name)
    {
        (object value, IncrementalStepRunReason reason) = Assert.ContainsSingle(Assert.ContainsSingle(driver.GetRunResult().Results)
            .TrackedSteps[(moduleLoad ? "ModuleLoad" : "Initialize") + "Emission"].SelectMany(static step => step.Outputs).Where(output =>
                output.Reason != IncrementalStepRunReason.Removed && output.Value is LifecycleEmission emission &&
                emission.Declaration.Target.EndsWith(".@" + name, StringComparison.Ordinal)));
        return (Assert.IsInstanceOfType<LifecycleEmission>(value), reason);
    }
}
