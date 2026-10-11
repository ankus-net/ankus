using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Worker boundaries remain cached across body edits and source movement without changing native or linker contracts.
    /// </summary>
    /// <param name="move">Whether the declaration moves rather than changing its implementation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WorkerEmissionRemainsCachedAcrossImplementationEdits(bool move)
    {
        string source = WorkerCacheSource("Run");
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        BackgroundWorkerEmission before = TrackedWorkerEmission(driver, "Run").Emission;
        string replacement = move ? "\n\n" + source : source.Replace("{ }", "{ _ = argument + 1; }", StringComparison.Ordinal);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            replacement, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedWorkerEmission(driver, "Run").Reason);
        Assert.AreEqual(before, TrackedWorkerEmission(driver, "Run").Emission);
        Assert.AreEqual("Run", before.EntryPoint);
        Assert.Contains("global::Workers.@Run(argument);", before.Managed);
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init", "Run"], ManifestValue(second, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(second).ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// An assembly rename refreshes managed callback symbols, while a new assembly version keeps them; either way the
    /// public native export is retained.
    /// </summary>
    /// <param name="version">Whether the assembly version changes instead of its name.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WorkerEmissionTracksAssemblyIdentity(bool version)
    {
        CSharpCompilation initial = ModuleCompilation(WorkerCacheSource("Run")).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")]", path: "Version.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        BackgroundWorkerEmission before = TrackedWorkerEmission(driver, "Run").Emission;
        CSharpCompilation edited = version ? initial.ReplaceSyntaxTree(initial.SyntaxTrees.Last(), CSharpSyntaxTree.ParseText(
            "[assembly: System.Reflection.AssemblyVersion(\"2.0.0.0\")]", path: "Version.cs", cancellationToken: context.CancellationToken)) : initial.WithAssemblyName("Renamed");
        driver = RunModule(driver, edited, out Compilation output);
        (BackgroundWorkerEmission emission, IncrementalStepRunReason reason) = TrackedWorkerEmission(driver, "Run");

        Assert.AreEqual(before.EntryPoint, emission.EntryPoint);
        Assert.AreEqual(before.Exports, emission.Exports);
        Assert.Contains(emission.Native.ReplaceLineEndings("\n"), ManifestValue(output, "Ankus.NativeSource"));
        if (version)
        {
            Assert.AreEqual(before.Managed, emission.Managed);
            Assert.AreEqual(before.Native, emission.Native);
        }
        else
        {
            Assert.AreEqual(IncrementalStepRunReason.Modified, reason);
            Assert.AreNotEqual(before.Managed, emission.Managed);
            Assert.AreNotEqual(before.Native, emission.Native);
            Assert.DoesNotContain(before.Native.ReplaceLineEndings("\n"), ManifestValue(output, "Ankus.NativeSource"));
        }

        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init", "Run"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Adding initialization composes its current managed callback without rerendering an unchanged worker entry.
    /// </summary>
    [TestMethod]
    public void WorkerEmissionComposesCurrentInitialization()
    {
        CSharpCompilation initial = ModuleCompilation(WorkerCacheSource("Run"));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        BackgroundWorkerEmission before = TrackedWorkerEmission(driver, "Run").Emission;
        CSharpCompilation edited = initial.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "public static class Lifecycle { [Ankus.PgInitialize] public static void Ready() { } }", path: "Lifecycle.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedWorkerEmission(driver, "Run").Reason);
        Assert.AreEqual(before, TrackedWorkerEmission(driver, "Run").Emission);
        Assert.Contains(before.Native.ReplaceLineEndings("\n"), ManifestValue(first, "Ankus.NativeSource"));
        Assert.Contains(before.Native.ReplaceLineEndings("\n"), ManifestValue(second, "Ankus.NativeSource"));
        Assert.DoesNotContain("global::Lifecycle.@Ready();", string.Join("\n", first.SyntaxTrees.Select(static tree => tree.ToString())));
        Assert.Contains("global::Lifecycle.@Ready();", string.Join("\n", second.SyntaxTrees.Select(static tree => tree.ToString())));
        Assert.AreEqual(ManifestValue(first, "Ankus.Exports"), ManifestValue(second, "Ankus.Exports"));
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
    }

    /// <summary>
    /// Referenced export constants invalidate only the dependent worker and remove its old native symbol.
    /// </summary>
    [TestMethod]
    public void WorkerEmissionTracksDependentExportNames()
    {
        CSharpCompilation initial = ModuleCompilation(WorkerCacheSource("Run", "EntryPoint = Names.Export"))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(WorkerCacheSource("Other"), path: "Other.cs", cancellationToken: context.CancellationToken),
                CSharpSyntaxTree.ParseText("public static class Names { public const string Export = \"Original\"; }", path: "Names.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Last(), CSharpSyntaxTree.ParseText(
            "public static class Names { public const string Export = \"Current\"; }", path: "Names.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);

        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedWorkerEmission(driver, "Run").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedWorkerEmission(driver, "Other").Reason);
        Assert.AreEqual("Current", TrackedWorkerEmission(driver, "Run").Emission.EntryPoint);
        Assert.Contains("PGDLLEXPORT void Current(Datum argument)", ManifestValue(output, "Ankus.NativeSource"));
        Assert.DoesNotContain("PGDLLEXPORT void Original(Datum argument)", ManifestValue(output, "Ankus.NativeSource"));
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init", "Other", "Current"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(output).ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// Managed renames update invocation and callback symbols while preserving the authored native entry.
    /// </summary>
    [TestMethod]
    public void WorkerEmissionTracksInvocationRenames()
    {
        string source = WorkerCacheSource("Run", "EntryPoint = \"Selected\"");
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        BackgroundWorkerEmission before = TrackedWorkerEmission(driver, "Run").Emission;
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            source.Replace("Run(", "Renamed(", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);
        (BackgroundWorkerEmission emission, IncrementalStepRunReason reason) = TrackedWorkerEmission(driver, "Renamed");

        Assert.AreEqual(IncrementalStepRunReason.Modified, reason);
        Assert.AreEqual("Selected", emission.EntryPoint);
        Assert.Contains("global::Workers.@Renamed(argument);", emission.Managed);
        Assert.DoesNotContain("global::Workers.@Run(argument);", emission.Managed);
        Assert.AreNotEqual(before.Native, emission.Native);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init", "Selected"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Invalid worker diagnostics use current physical/mapped source coordinates and disappear after signature repair.
    /// </summary>
    /// <param name="edit">The body, movement or line-mapping edit.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void WorkerDiagnosticsFollowCurrentTreesAndRecover(int edit)
    {
        string source = WorkerCacheSource("Run").Replace("nuint", "long", StringComparison.Ordinal);
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _, out ImmutableArray<Diagnostic> original, context.CancellationToken);
        Diagnostic previous = Assert.ContainsSingle(original);
        string replacement = edit switch
        {
            1 => "\n\n" + source,
            2 => source.Replace("#nullable enable\n", "#nullable enable\n#line 101 \"WorkerDefinition.cs\"\n", StringComparison.Ordinal),
            _ => source.Replace("{ }", "{ _ = argument; }", StringComparison.Ordinal),
        };
        SyntaxTree current = CSharpSyntaxTree.ParseText(replacement, path: "Current.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), current);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual("ANKUS260", error.Id);
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreEqual("long", current.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual(edit == 2 ? "WorkerDefinition.cs" : "Current.cs", error.Location.GetMappedLineSpan().Path);
        Assert.AreEqual(edit switch { 1 => 3, 2 => 100, _ => 1 }, error.Location.GetMappedLineSpan().StartLinePosition.Line);
        Assert.AreEqual(previous.GetMessage(CultureInfo.InvariantCulture), error.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "WorkerEmission"));

        CSharpCompilation repaired = edited.ReplaceSyntaxTree(current, CSharpSyntaxTree.ParseText(
            replacement.Replace("long", "nuint", StringComparison.Ordinal), path: "Current.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedWorkerEmission(driver, "Run").Reason);
        Assert.Contains("Run", ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Global export conflicts remain current after boundary cache hits, then clear when the dependent export constant changes.
    /// </summary>
    [TestMethod]
    public void WorkerDuplicateExportsFollowCurrentTreesAndRecover()
    {
        string second = WorkerCacheSource("Last", "EntryPoint = Names.Export");
        CSharpCompilation initial = ModuleCompilation(WorkerCacheSource("First", "EntryPoint = \"Shared\""))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(second, path: "Last.cs", cancellationToken: context.CancellationToken),
                CSharpSyntaxTree.ParseText("public static class Names { public const string Export = \"Shared\"; }", path: "Names.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _, out ImmutableArray<Diagnostic> original, context.CancellationToken);
        Assert.AreEqual("ANKUS275", Assert.ContainsSingle(original).Id);
        SyntaxTree current = CSharpSyntaxTree.ParseText("\n\n" + second, path: "Current.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.ElementAt(1), current);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out Compilation conflicted, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual("ANKUS275", error.Id);
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreEqual("Last", current.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual(3, error.Location.GetLineSpan().StartLinePosition.Line);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedWorkerEmission(driver, "First").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedWorkerEmission(driver, "Last").Reason);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init", "Shared"], ManifestValue(conflicted, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);

        CSharpCompilation repaired = edited.ReplaceSyntaxTree(edited.SyntaxTrees.Last(), CSharpSyntaxTree.ParseText(
            "public static class Names { public const string Export = \"Separate\"; }", path: "Names.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedWorkerEmission(driver, "First").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedWorkerEmission(driver, "Last").Reason);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init", "Shared", "Separate"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Removing a worker removes only its native/export contract and retains exact survivors across positional changes.
    /// </summary>
    /// <param name="removeFirst">Whether removal shifts the surviving worker's input position.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WorkerRemovalPreservesSurvivorContracts(bool removeFirst)
    {
        CSharpCompilation initial = ModuleCompilation(WorkerCacheSource("First")).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            WorkerCacheSource("Last"), path: "Last.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        string survivorName = removeFirst ? "Last" : "First";
        string removedName = removeFirst ? "First" : "Last";
        BackgroundWorkerEmission survivor = TrackedWorkerEmission(driver, survivorName).Emission;
        driver = RunModule(driver, initial.RemoveSyntaxTrees(removeFirst ? initial.SyntaxTrees.First() : initial.SyntaxTrees.Last()), out Compilation output);

        Assert.AreEqual(survivor, TrackedWorkerEmission(driver, survivorName).Emission);
        Assert.AreEqual(removeFirst ? IncrementalStepRunReason.Modified : IncrementalStepRunReason.Cached, TrackedWorkerEmission(driver, survivorName).Reason);
        Assert.DoesNotContain($"PGDLLEXPORT void {removedName}(Datum argument)", ManifestValue(output, "Ankus.NativeSource"));
        Assert.Contains($"PGDLLEXPORT void {survivorName}(Datum argument)", ManifestValue(output, "Ankus.NativeSource"));
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init", survivorName], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(output).ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// Partial implementations keep one normalized worker and update validity after body/async transitions.
    /// </summary>
    /// <param name="implementationMarker">Whether the marker is authored on the implementation rather than definition.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WorkerPartialImplementationsTrackValidity(bool implementationMarker)
    {
        string definition = "public static partial class Workers { " + (implementationMarker ? string.Empty : "[Ankus.PgBackgroundWorker]") +
            "public static partial void Run(nuint argument); }";
        string implementation = "public static partial class Workers { " + (implementationMarker ? "[Ankus.PgBackgroundWorker]" : string.Empty) +
            "public static partial void Run(nuint argument) { } }";
        CSharpCompilation initial = ModuleCompilation(definition).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            implementation, path: "Implementation.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Last(), CSharpSyntaxTree.ParseText(
            implementation.Replace("{ }", "{ _ = argument + 1; }", StringComparison.Ordinal), path: "Implementation.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedWorkerEmission(driver, "Run").Reason);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init", "Run"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));

        CSharpCompilation invalid = edited.ReplaceSyntaxTree(edited.SyntaxTrees.Last(), CSharpSyntaxTree.ParseText(
            implementation.Replace("public static partial void", "public static async partial void", StringComparison.Ordinal)
                .Replace("{ }", "{ await System.Threading.Tasks.Task.Yield(); }", StringComparison.Ordinal),
            path: "Implementation.cs", cancellationToken: context.CancellationToken));
        driver = driver.RunGeneratorsAndUpdateCompilation(invalid, out _, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS252", error.Id);
        Assert.AreSame(invalid.SyntaxTrees.Last(), error.Location.SourceTree);
        Assert.AreEqual("async", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);

        driver = RunModule(driver, edited, out Compilation repaired);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedWorkerEmission(driver, "Run").Reason);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init", "Run"], ManifestValue(repaired, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Removing the final worker retains unrelated SQL and removes worker-driven native initialization from its current entry.
    /// </summary>
    [TestMethod]
    public void WorkerRemovalPreservesOrdinarySql()
    {
        CSharpCompilation initial = ModuleCompilation(WorkerCacheSource("Run")).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "public static class Functions { [Ankus.PgFunction] public static int Answer() => 42; }", path: "Function.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        FunctionEmission original = TrackedFunctionEmission(driver, "answer").Emission;
        driver = RunModule(driver, initial.RemoveSyntaxTrees(initial.SyntaxTrees.First()), out Compilation second);

        Assert.AreEqual(original, TrackedFunctionEmission(driver, "answer").Emission);
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.Contains((original.Native.Header + "    ankus_ensure_initialized();").ReplaceLineEndings("\n"), ManifestValue(first, "Ankus.NativeSource"));
        Assert.Contains((original.Native.Header + original.Native.Body).ReplaceLineEndings("\n"), ManifestValue(second, "Ankus.NativeSource"));
        Assert.DoesNotContain("PGDLLEXPORT void Run(Datum argument)", ManifestValue(second, "Ankus.NativeSource"));
        Assert.AreSequenceEqual(["Pg_magic_func", original.NativeName, "pg_finfo_" + original.NativeName], ManifestValue(second, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Supplies a canonical worker declaration with optional authored native identity.
    /// </summary>
    /// <param name="name">The managed method name.</param>
    /// <param name="options">The optional attribute arguments.</param>
    /// <returns>A complete nullable-enabled worker declaration.</returns>
    private static string WorkerCacheSource(string name, string options = "")
        => "#nullable enable\npublic static partial class Workers { [Ankus.PgBackgroundWorker(" + options + ")] public static void " + name + "(nuint argument) { } }";

    /// <summary>
    /// Reads the production worker renderer's actual artifact and incremental reuse decision.
    /// </summary>
    /// <param name="driver">The executed production generator.</param>
    /// <param name="name">The exact managed invocation leaf name.</param>
    /// <returns>The immutable worker emission and tracked cache decision.</returns>
    private static (BackgroundWorkerEmission Emission, IncrementalStepRunReason Reason) TrackedWorkerEmission(GeneratorDriver driver, string name)
    {
        (object value, IncrementalStepRunReason reason) = Assert.ContainsSingle(Assert.ContainsSingle(driver.GetRunResult().Results)
            .TrackedSteps["WorkerEmission"].SelectMany(static step => step.Outputs).Where(output =>
                output.Reason != IncrementalStepRunReason.Removed && output.Value is BackgroundWorkerEmission emission &&
                emission.Managed.Contains(".@" + name + "(argument);", StringComparison.Ordinal)));
        return (Assert.IsInstanceOfType<BackgroundWorkerEmission>(value), reason);
    }
}
