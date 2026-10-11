using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies reserved extension-schema paths, relocation policy and independent generator caches.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Reserved schema references remain raw while ordinary names retain exact identifier quoting.
    /// </summary>
    /// <param name="entry">The reserved token authored as a literal or named constant.</param>
    [TestMethod]
    [DataRow("\"@extschema@\"")]
    [DataRow("Ankus.PgSearchPath.ExtensionSchema")]
    public void ExtensionSchemaSearchPathPreservesReservedTokens(string entry)
    {
        RunModule(ModuleDriver(), ModuleCompilation("""
            public static class Functions
            {
                [Ankus.PgFunction(SearchPath = new[] { "pg_catalog", ENTRY, "Mixed \" schema", "$user", "pg_temp" })]
                public static int Answer() => 42;
            }
            """.Replace("ENTRY", entry, StringComparison.Ordinal)), out Compilation output);
        string sql = InstallationBody(output);

        Assert.Contains("SET search_path TO \"pg_catalog\", @extschema@, \"Mixed \"\" schema\", \"$user\", \"pg_temp\"", sql);
        Assert.DoesNotContain("\"@extschema@\"", sql);
        Assert.AreEqual("false", ManifestValue(output, "Ankus.Relocatable"));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(output));
    }

    /// <summary>
    /// Ordinary marker-like names do not acquire substitution semantics or prevent relocation.
    /// </summary>
    /// <param name="entry">A valid literal PostgreSQL schema name.</param>
    [TestMethod]
    [DataRow("@extschema")]
    [DataRow("@EXTSCHEMA@")]
    [DataRow("Mixed schema")]
    public void ExtensionSchemaSearchPathKeepsOrdinaryNamesLiteral(string entry)
    {
        RunModule(ModuleDriver(), ModuleCompilation("public static class Functions { [Ankus.PgFunction(SearchPath = new[] { " +
            SymbolDisplay.FormatLiteral(entry, quote: true) + " })] public static int Answer() => 42; }"), out Compilation output);

        Assert.Contains("SET search_path TO \"" + entry + "\"", InstallationBody(output));
        Assert.AreEqual("true", ManifestValue(output, "Ankus.Relocatable"));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(output));
    }

    /// <summary>
    /// All generated function families share raw schema-token rendering and non-relocatable metadata.
    /// </summary>
    /// <param name="declaration">A valid ordinary, iterator or specialized callback family.</param>
    /// <param name="tests">Whether backend-test SQL is enabled.</param>
    [TestMethod]
    [DataRow("public static class Functions { [Ankus.PgFunction(SearchPath=new[]{\"@extschema@\"})] public static int Value()=>42; }", false)]
    [DataRow("public static class Functions { [Ankus.PgFunction(SearchPath=new[]{\"@extschema@\"})] public static System.Collections.Generic.IEnumerable<int> Values()=>new[]{42}; }", false)]
    [DataRow("public static class Functions { [Ankus.PgTrigger,Ankus.PgFunction(SearchPath=new[]{\"@extschema@\"})] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext call)=>null; }", false)]
    [DataRow("public static class Functions { [Ankus.PgEventTrigger,Ankus.PgFunction(SearchPath=new[]{\"@extschema@\"})] public static void Audit(Ankus.PgEventTriggerContext call){} }", false)]
    [DataRow("public static partial class Functions { [Ankus.PgTest(SearchPath=new[]{\"@extschema@\"})] public static void Verify(){} }", true)]
    [DataRow("[Ankus.PgAggregate(InitialCondition=\"0\")] public sealed class Total:Ankus.IPgAggregate<long,int> { [Ankus.PgFunction(SearchPath=new[]{\"@extschema@\"})] public static long Transition(Ankus.PgAggregateContext owner,long state,int value)=>state+value; }", false)]
    public void ExtensionSchemaSearchPathAppliesToEveryFunctionRole(string declaration, bool tests)
    {
        (Compilation output, ImmutableArray<Diagnostic> diagnostics) = Generate(declaration, options: new BackendOptions(tests ? "true" : "false"));
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        Assert.Contains("SET search_path TO @extschema@", InstallationBody(output));
        Assert.AreEqual("false", ManifestValue(output, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Suppressed or replaced SQL retains its explicit relocation policy rather than an unused generated path.
    /// </summary>
    /// <param name="policy">The SQL generation policy to combine with a reserved search path.</param>
    /// <param name="relocatable">The effective explicit relocation contract.</param>
    [TestMethod]
    [DataRow("GenerateSql=false", "true")]
    [DataRow("Sql=\"SELECT 42;\",SqlRelocatable=true", "true")]
    [DataRow("Sql=\"SELECT 42;\"", "false")]
    public void ExtensionSchemaSearchPathRespectsSqlPolicy(string policy, string relocatable)
    {
        RunModule(ModuleDriver(), ModuleCompilation("public static class Functions { [Ankus.PgFunction(SearchPath=new[]{\"@extschema@\"}," +
            policy + ")] public static int Answer()=>42; }"), out Compilation output);

        Assert.DoesNotContain("@extschema@", InstallationBody(output));
        Assert.AreEqual(relocatable, ManifestValue(output, "Ankus.Relocatable"));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(output));
    }

    /// <summary>
    /// A test-only search path affects publication only when backend test exports are enabled.
    /// </summary>
    /// <param name="enabled">Whether the publication includes backend tests.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExtensionSchemaSearchPathIsOnlyPublishedForEnabledBackendTests(bool enabled)
    {
        (Compilation output, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static partial class Checks
            {
                [Ankus.PgTest(SearchPath=new[]{"@extschema@"})] public static void Verify(){}
            }
            """, options: new BackendOptions(enabled ? "true" : "false"));
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        Assert.AreEqual(enabled ? "false" : "true", ManifestValue(output, "Ankus.Relocatable"));
        if (enabled)
        {
            Assert.Contains("SET search_path TO @extschema@", InstallationBody(output));
        }
        else
        {
            Assert.DoesNotContain("@extschema@", InstallationBody(output));
            Assert.AreSequenceEqual(["Pg_magic_func"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    /// <summary>
    /// Function options on a backend test reach its SQL function, as pgrx's <c>#[pg_test]</c> passes
    /// <c>#[pg_extern]</c> options through, and invalid values are reported at the option.
    /// </summary>
    [TestMethod]
    public void BackendTestFunctionOptionsReachTheirSqlFunction()
    {
        (Compilation output, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static partial class Checks
            {
                [Ankus.PgTest(Volatility = Ankus.PgVolatility.Stable, ParallelSafety = Ankus.PgParallelSafety.Safe, SecurityDefiner = true, Cost = 5)]
                public static void Verify(){}
            }
            """, options: new BackendOptions("true"));
        Assert.IsEmpty(diagnostics);
        Assert.Contains("LANGUAGE c STABLE PARALLEL SAFE STRICT SECURITY DEFINER NOT LEAKPROOF COST 5", InstallationBody(output));

        (_, ImmutableArray<Diagnostic> invalid) = Generate("""
            public static partial class Checks
            {
                [Ankus.PgTest(Cost = 0)] public static void Verify(){}
            }
            """, options: new BackendOptions("true"));
        Diagnostic error = Assert.ContainsSingle(invalid);
        Assert.AreEqual("ANKUS046", error.Id);
        Assert.AreEqual("0", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// Body edits preserve conversion and SQL caches; changing the path updates only SQL and relocation metadata.
    /// </summary>
    [TestMethod]
    public void ExtensionSchemaSearchPathCachesConversionContracts()
    {
        const string Source = "public static class Functions { [Ankus.PgFunction(SearchPath=new[]{\"public\"})] public static int Answer()=>42; }";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        SyntaxTree body = CSharpSyntaxTree.ParseText(Source.Replace("=>42", "=>43", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken);
        driver = RunModule(driver, initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), body), out Compilation second);
        Assert.AreEqual(43, InvokeSqlReferenceAnswer(second));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "FunctionEmission"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "FunctionSqlEmission"));
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        CSharpCompilation changed = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            Source.Replace("\"public\"", "\"@extschema@\"", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, changed, out Compilation third);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "FunctionEmission"));
        Assert.AreEqual(IncrementalStepRunReason.Modified, ModuleStep(driver, "FunctionSqlEmission"));
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(third, "Ankus.NativeSource"));
        Assert.AreEqual("false", ManifestValue(third, "Ankus.Relocatable"));
        Assert.Contains("SET search_path TO @extschema@", InstallationBody(third));
    }
}
