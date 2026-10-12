using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Excludes benchmark SQL and native exports from ordinary extension publications.
    /// </summary>
    [TestMethod]
    public void OrdinaryPublicationExcludesBenchmarks()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Benchmarks
            {
                [Ankus.PgBenchmark]
                public static void Add(Ankus.PgBencher bencher) => bencher.Iterate(static () => 1 + 2);
            }
            """);

        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(compilation));
        Assert.DoesNotContain("ankus_bench_", ManifestValue(compilation, "Ankus.Exports"));
    }

    /// <summary>
    /// Emits stable run and describe functions with exact authored configuration in benchmark mode.
    /// </summary>
    [TestMethod]
    public void BenchmarkPublicationEmitsConfiguredBackendWrappers()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            namespace Extension;
            public static class Benchmarks
            {
                internal static void Prepare() { }

                [Ankus.PgBenchmark(Setup = nameof(Prepare), Transaction = Ankus.PgBenchmarkTransactionMode.SubtransactionPerBatch,
                    SampleSize = 17, MeasurementTimeMilliseconds = 11, WarmupTimeMilliseconds = 13,
                    ResampleCount = 19, NoiseThreshold = 0.02, SignificanceLevel = 0.03)]
                public static void Add(Ankus.PgBencher bencher) => bencher.Iterate(static () => 1 + 2);
            }
            """, options: new BenchmarkOptions(true, "/work/project"), path: "/work/project/Benchmarks.cs");

        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        string sql = InstallationBody(compilation);
        Assert.Contains("CREATE SCHEMA benches;", sql);
        Assert.Contains("CREATE FUNCTION benches.\"ankus_bench_", sql);
        Assert.Contains("(baseline jsonb DEFAULT NULL)", sql);
        Assert.Contains("_describe\"()", sql);
        string dispatchers = compilation.SyntaxTrees.Single(static tree => tree.FilePath.EndsWith("ExtensionDispatchers.g.cs", StringComparison.Ordinal))
            .GetText(context.CancellationToken).ToString();
        Assert.Contains("global::Extension.Benchmarks.@Prepare()", dispatchers);
        Assert.Contains("global::Extension.Benchmarks.@Add(bencher)", dispatchers);
        Assert.Contains("Benchmarks.cs", dispatchers);
        Assert.Contains("AnkusGeneratedBenchmarkSources.Line", dispatchers);
        Assert.Contains("new global::Ankus.CompilerServices.PgBenchmarkConfiguration(17, 11, 13, 19, 0.02, 0.03)", dispatchers);
        string sourceLines = compilation.SyntaxTrees.Single(static tree =>
            tree.FilePath.EndsWith("BenchmarkSourceLines.g.cs", StringComparison.Ordinal)).GetText(context.CancellationToken).ToString();
        Assert.Contains("=> 9,", sourceLines);
        Assert.HasCount(4, ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static value => value.Contains("ankus_bench_", StringComparison.Ordinal)));
        Assert.AreEqual("false", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Reports each invalid benchmark contract precisely while retaining valid siblings.
    /// </summary>
    [TestMethod]
    public void InvalidBenchmarkDoesNotDropValidSibling()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Benchmarks
            {
                [Ankus.PgBenchmark(SampleSize = 9)]
                public static void Invalid(Ankus.PgBencher bencher) { }

                [Ankus.PgBenchmark(SampleSize = 10, MeasurementTimeMilliseconds = 1,
                    WarmupTimeMilliseconds = 1, NoiseThreshold = 2)]
                public static void Valid(Ankus.PgBencher bencher) => bencher.Iterate(static () => 42);
            }
            """, options: new BenchmarkOptions(true, string.Empty));

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS132", diagnostic.Id);
        Assert.Contains("SampleSize", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken)
            .Where(static value => value.Severity == DiagnosticSeverity.Error));
        string sql = InstallationBody(compilation);
        Assert.Contains("CREATE FUNCTION benches.\"ankus_bench_", sql);
        Assert.AreEqual(2, sql.Split('\n').Count(static line => line.StartsWith("CREATE FUNCTION benches.", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A benchmark's throughput reaches its configuration, and benchmarks without one keep the shorter form.
    /// </summary>
    [TestMethod]
    public void BenchmarkThroughputReachesTheConfiguration()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Benchmarks
            {
                [Ankus.PgBenchmark(Throughput = Ankus.PgBenchmarkThroughput.Elements, ThroughputPerIteration = 4096)]
                public static void Copy(Ankus.PgBencher bencher) => bencher.Iterate(static () => 1 + 2);
            }
            """, options: new BenchmarkOptions(true, string.Empty));

        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        string dispatchers = compilation.SyntaxTrees.Single(static tree => tree.FilePath.EndsWith("ExtensionDispatchers.g.cs", StringComparison.Ordinal))
            .GetText(context.CancellationToken).ToString();
        Assert.Contains("new global::Ankus.CompilerServices.PgBenchmarkConfiguration(100, 5000, 3000, 100000, 0.01, 0.05, " +
            "(global::Ankus.PgBenchmarkThroughput)3, 4096L)", dispatchers);
    }

    /// <summary>
    /// Rejects unsupported benchmark shapes, setup methods and role conflicts with dedicated diagnostics.
    /// </summary>
    /// <param name="source">The invalid declaration source.</param>
    /// <param name="diagnostic">The expected diagnostic identifier.</param>
    [TestMethod]
    [DataRow("public static class B { [Ankus.PgBenchmark(Setup = \"Missing\")] public static void Run(Ankus.PgBencher b) { } }", "ANKUS131")]
    [DataRow("public static class B { [Ankus.PgBenchmark(WarmupTimeMilliseconds = 0)] public static void Run(Ankus.PgBencher b) { } }", "ANKUS132")]
    [DataRow("public static class B { [Ankus.PgBenchmark(Throughput = Ankus.PgBenchmarkThroughput.Bytes)] public static void Run(Ankus.PgBencher b) { } }", "ANKUS132")]
    [DataRow("public static class B { [Ankus.PgBenchmark(ThroughputPerIteration = 8)] public static void Run(Ankus.PgBencher b) { } }", "ANKUS132")]
    [DataRow("public static class B { [Ankus.PgBenchmark(Throughput = (Ankus.PgBenchmarkThroughput)9, ThroughputPerIteration = 8)] public static void Run(Ankus.PgBencher b) { } }", "ANKUS132")]
    [DataRow("public static class B { [Ankus.PgBenchmark, Ankus.PgFunction] public static void Run(Ankus.PgBencher b) { } }", "ANKUS133")]
    public void RejectsInvalidBenchmarkContracts(string source, string diagnostic)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate(source, options: new BenchmarkOptions(true, string.Empty));

        Assert.AreEqual(diagnostic, Assert.ContainsSingle(diagnostics).Id);
    }

    /// <summary>
    /// Each broken benchmark rule has its own diagnostic at the syntax that breaks it: the name, modifier, return type,
    /// parameter list, parameter or containing type.
    /// </summary>
    /// <param name="source">The invalid declaration source.</param>
    /// <param name="id">The expected diagnostic identifier.</param>
    /// <param name="span">The exact source text the diagnostic marks.</param>
    [TestMethod]
    [DataRow("public class B { [Ankus.PgBenchmark] public void Run(Ankus.PgBencher b) { } }", "ANKUS130", "Run")]
    [DataRow("public static class B { [Ankus.PgBenchmark] public static async void Run(Ankus.PgBencher b) { } }", "ANKUS527", "async")]
    [DataRow("public static class B { [Ankus.PgBenchmark] public static void Run<T>(Ankus.PgBencher b) { } }", "ANKUS528", "<T>")]
    [DataRow("public interface B { [Ankus.PgBenchmark] static abstract void Run(Ankus.PgBencher b); }", "ANKUS529", "abstract")]
    [DataRow("public static class B { [Ankus.PgBenchmark] public static int Run(Ankus.PgBencher b) => 1; }", "ANKUS530", "int")]
    [DataRow("public static class B { [Ankus.PgBenchmark] private static void Run(Ankus.PgBencher b) { } }", "ANKUS531", "Run")]
    [DataRow("public static class B { [Ankus.PgBenchmark] public static void Run() { } }", "ANKUS532", "()")]
    [DataRow("public static class B { [Ankus.PgBenchmark] public static void Run(Ankus.PgBencher b, int count) { } }", "ANKUS532",
        "(Ankus.PgBencher b, int count)")]
    [DataRow("public static class B { [Ankus.PgBenchmark] public static void Run(object b) { } }", "ANKUS532", "object")]
    [DataRow("public static class B { [Ankus.PgBenchmark] public static void Run(ref Ankus.PgBencher b) { } }", "ANKUS533", "ref Ankus.PgBencher b")]
    [DataRow("public static class B<T> { [Ankus.PgBenchmark] public static void Run(Ankus.PgBencher b) { } }", "ANKUS534", "B")]
    [DataRow("file static class B { [Ankus.PgBenchmark] public static void Run(Ankus.PgBencher b) { } }", "ANKUS534", "B")]
    [DataRow("public struct B { [Ankus.PgBenchmark] public static void Run(Ankus.PgBencher b) { } }", "ANKUS534", "B")]
    [DataRow("public static class Outer { private static class B { [Ankus.PgBenchmark] public static void Run(Ankus.PgBencher b) { } } }", "ANKUS534", "B")]
    public void BenchmarkDeclarationDiagnosticsMarkTheirRule(string source, string id, string span)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate(source, options: new BenchmarkOptions(true, string.Empty));
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(id, diagnostic.Id);
        Assert.AreEqual(span, diagnostic.Location.SourceTree!.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan));
    }

    /// <summary>
    /// A setup diagnostic states the first rule the named method breaks.
    /// </summary>
    /// <param name="setup">The setup declarations.</param>
    /// <param name="problem">The problem the message states.</param>
    [TestMethod]
    [DataRow("", "'B' has no method named 'Prepare'")]
    [DataRow("public void Prepare() { }", "'Prepare' is not static")]
    [DataRow("public static async void Prepare() => await System.Threading.Tasks.Task.Yield();", "'Prepare' is async")]
    [DataRow("public static void Prepare<T>() { }", "'Prepare' is generic")]
    [DataRow("public static int Prepare() => 0;", "'Prepare' returns int")]
    [DataRow("public static void Prepare(int count) { }", "'Prepare' takes 1 parameter")]
    [DataRow("private static void Prepare() { }", "'Prepare' is not public or internal")]
    [DataRow("public static void Prepare() { } public static void Prepare(int count) { }", "'Prepare' has 2 overloads")]
    public void BenchmarkSetupDiagnosticsStateTheProblem(string setup, string problem)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public class B { [Ankus.PgBenchmark(Setup = \"Prepare\")] public static void Run(Ankus.PgBencher b) { } " + setup + " }",
            options: new BenchmarkOptions(true, string.Empty));
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS131", diagnostic.Id);
        Assert.EndsWith("; " + problem, diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Body edits in one of several declarations retain the complete benchmark model and rendered publication.
    /// </summary>
    [TestMethod]
    public void BenchmarkModelsRemainCachedAcrossBodyEdits()
    {
        const string Source = """
            public static class Benchmarks
            {
                [Ankus.PgBenchmark]
                public static void First(Ankus.PgBencher bencher) => bencher.Iterate(static () => 1);

                [Ankus.PgBenchmark]
                public static void Second(Ankus.PgBencher bencher) => bencher.Iterate(static () => 2);
            }
            """;
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(BenchmarkDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            Source.Replace("static () => 1", "static () => 123456", StringComparison.Ordinal), path: "Module.cs",
            cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "PgBenchmarkAnalysis"));
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        Assert.HasCount(8, ManifestValue(second, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static value => value.Contains("ankus_bench_", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Physical line movement updates only benchmark attribution while semantic composition remains cached.
    /// </summary>
    [TestMethod]
    public void BenchmarkSourceLinesUpdateWithoutRecomposition()
    {
        const string Source = """
            public static class Benchmarks
            {
                [Ankus.PgBenchmark]
                public static void First(Ankus.PgBencher bencher) => bencher.Iterate(static () => 1);

                [Ankus.PgBenchmark]
                public static void Second(Ankus.PgBencher bencher) => bencher.Iterate(static () => 2);
            }
            """;
        string normalizedSource = Source.ReplaceLineEndings("\n");
        CSharpCompilation initial = ModuleCompilation(normalizedSource);
        GeneratorDriver driver = RunModule(BenchmarkDriver(), initial, out Compilation first);
        string movedSource = normalizedSource.Replace("\n    [Ankus.PgBenchmark]\n    public static void Second", "\n\n    [Ankus.PgBenchmark]\n    public static void Second",
            StringComparison.Ordinal);
        CSharpCompilation moved = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            movedSource, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, moved, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "ExtensionComposition"));
        Assert.AreEqual(IncrementalStepRunReason.Modified, ModuleStep(driver, "BenchmarkSourceLineEmission"));
        string before = first.SyntaxTrees.Single(static tree => tree.FilePath.EndsWith("BenchmarkSourceLines.g.cs", StringComparison.Ordinal))
            .GetText(context.CancellationToken).ToString();
        string after = second.SyntaxTrees.Single(static tree => tree.FilePath.EndsWith("BenchmarkSourceLines.g.cs", StringComparison.Ordinal))
            .GetText(context.CancellationToken).ToString();
        Assert.Contains("=> 7,", before);
        Assert.Contains("=> 8,", after);
    }

    private static CSharpGeneratorDriver BenchmarkDriver()
        => CSharpGeneratorDriver.Create([new PgFunctionGenerator().AsSourceGenerator()],
            optionsProvider: new BenchmarkOptions(true, string.Empty),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    private sealed class BenchmarkOptions(bool enabled, string projectDirectory) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new BenchmarkGlobalOptions(enabled, projectDirectory);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class BenchmarkGlobalOptions(bool enabled, string projectDirectory) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = key switch
            {
                "build_property.AnkusIncludeBenchmarks" => enabled ? "true" : "false",
                "build_property.MSBuildProjectDirectory" => projectDirectory,
                _ => string.Empty,
            };
            return value.Length != 0 || key == "build_property.AnkusIncludeBenchmarks";
        }
    }
}
