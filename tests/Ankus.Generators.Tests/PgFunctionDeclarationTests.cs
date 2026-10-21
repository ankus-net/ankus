using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Verifies declaration options, exact defaults, SQL argument names, and fixed-schema metadata are emitted together.
    /// </summary>
    [TestMethod]
    public void FunctionDeclarationsPreserveOptionsAndConstants()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgSchema("Mixed \" schema")]
            public static class Functions
            {
                [Ankus.PgFunction(Volatility = Ankus.PgVolatility.Immutable, ParallelSafety = Ankus.PgParallelSafety.Safe,
                    SecurityDefiner = true, Leakproof = true, CreateOrReplace = true, Cost = 2.5,
                    SearchPath = new[] { "pg_catalog", "Mixed \" schema", "pg_temp" }, SupportFunction = "pg_catalog.text_starts_with_support")]
                public static decimal Value([Ankus.PgParameter(Name = "input count")] int inputCount = -12,
                    decimal amount = 12345678901234567890.123456789m) => amount;
            }
            """);
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static error => error.Severity == DiagnosticSeverity.Error));
        string sql = InstallationBody(compilation);
        Assert.StartsWith("CREATE SCHEMA IF NOT EXISTS \"Mixed \"\" schema\";", sql);
        Assert.Contains("CREATE OR REPLACE FUNCTION \"Mixed \"\" schema\".\"value\"(\"input count\" integer DEFAULT ((-12)::integer), \"amount\" numeric DEFAULT ((12345678901234567890.123456789)::numeric))", sql);
        Assert.Contains("IMMUTABLE PARALLEL SAFE STRICT SECURITY DEFINER LEAKPROOF COST 2.5", sql);
        Assert.Contains("SUPPORT \"pg_catalog\".\"text_starts_with_support\" SET search_path TO \"pg_catalog\", \"Mixed \"\" schema\", \"pg_temp\"", sql);
        Assert.AreEqual("false", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Rejects invalid option values and declaration contracts before native publishing.
    /// </summary>
    /// <param name="method">The invalid function declaration.</param>
    /// <param name="id">The specific declaration diagnostic.</param>
    [TestMethod]
    [DataRow("[Ankus.PgFunction(Cost = 0)] public static int F() => 1;", "ANKUS046")]
    [DataRow("[Ankus.PgFunction(Cost = double.NaN)] public static int F() => 1;", "ANKUS046")]
    [DataRow("[Ankus.PgFunction(Cost = double.PositiveInfinity)] public static int F() => 1;", "ANKUS046")]
    [DataRow("[Ankus.PgFunction(Cost = double.Epsilon)] public static int F() => 1;", "ANKUS046")]
    [DataRow("[Ankus.PgFunction(Cost = double.MaxValue)] public static int F() => 1;", "ANKUS046")]
    [DataRow("[Ankus.PgFunction(Volatility = (Ankus.PgVolatility)3)] public static int F() => 1;", "ANKUS045")]
    [DataRow("[Ankus.PgFunction(ParallelSafety = (Ankus.PgParallelSafety)(-1))] public static int F() => 1;", "ANKUS045")]
    [DataRow("[Ankus.PgFunction(NullInput = (Ankus.PgNullInput)3)] public static int F() => 1;", "ANKUS045")]
    [DataRow("[Ankus.PgFunction(NullInput = Ankus.PgNullInput.CalledOnNull)] public static int F(int value) => value;", "ANKUS049")]
    [DataRow("[Ankus.PgFunction(Schema = \"\")] public static int F() => 1;", "ANKUS050")]
    [DataRow("[Ankus.PgFunction(Schema = \"a\\0b\")] public static int F() => 1;", "ANKUS050")]
    [DataRow("[Ankus.PgFunction(SearchPath = new[] { \"\" })] public static int F() => 1;", "ANKUS055")]
    [DataRow("[Ankus.PgFunction(SearchPath = new string[] { null! })] public static int F() => 1;", "ANKUS055")]
    [DataRow("[Ankus.PgFunction(SupportFunction = \"a.b.c\")] public static int F() => 1;", "ANKUS054")]
    [DataRow("[Ankus.PgFunction] public static int F(int URL, int Url) => URL;", "ANKUS059")]
    [DataRow("[Ankus.PgFunction] public static int F([Ankus.PgParameter(Default = \" \" )] int value) => value;", "ANKUS060")]
    [DataRow("[Ankus.PgFunction] public static int F([Ankus.PgParameter(Default = \"42\")] int value, int other) => value;", "ANKUS062")]
    [DataRow("[Ankus.PgFunction] public static string F(string value = \"\\0\") => value;", "ANKUS061")]
    [DataRow("[Ankus.PgFunction] public static string F(string value = \"\\ud800\") => value;", "ANKUS061")]
    public void InvalidDeclarationOptionsAreRejected(string method, string id)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { " + method + " }");
        Assert.AreEqual(id, Assert.ContainsSingle(diagnostics).Id);
    }

    /// <summary>
    /// Uses the nearest containing schema, supports per-function overrides, and keeps same-name functions in different schemas distinct.
    /// </summary>
    [TestMethod]
    public void SchemaInheritanceAndOverridesUseQualifiedSignatures()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgSchema("outer")]
            public static class Outer
            {
                public static class Nested { [Ankus.PgFunction] public static int F() => 1; }
                [Ankus.PgSchema("inner")]
                public static class Inner { [Ankus.PgFunction] public static int F() => 2; }
                [Ankus.PgFunction(Schema = "override")] public static int F() => 3;
            }

            [Ankus.PgSchema("empty")]
            public static class Empty;
            """);
        Assert.IsEmpty(diagnostics);
        string sql = InstallationBody(compilation);
        Assert.Contains("FUNCTION \"outer\".\"f\"()", sql);
        Assert.Contains("FUNCTION \"inner\".\"f\"()", sql);
        Assert.Contains("FUNCTION \"override\".\"f\"()", sql);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"empty\";", sql);
    }

    /// <summary>
    /// Generates a manifest for an empty declared schema and validates its identifier by UTF-8 byte length.
    /// </summary>
    /// <param name="name">The schema identifier.</param>
    /// <param name="valid">Whether the identifier is valid.</param>
    /// <param name="diagnosticId">The expected failure identifier, or empty for a valid declaration.</param>
    [TestMethod]
    [DataRow("empty", true, "")]
    [DataRow("", false, "ANKUS050")]
    [DataRow(null, false, "ANKUS050")]
    [DataRow("pg_reserved", false, "ANKUS063")]
    [DataRow("PG_Stage", true, "")]
    [DataRow("Pg_stage", true, "")]
    [DataRow("pG_stage", true, "")]
    [DataRow("🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘", true, "")]
    [DataRow("🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘", false, "ANKUS050")]
    public void EmptySchemasHaveValidatedStandaloneMetadata(string? name, bool valid, string diagnosticId)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "[Ankus.PgSchema(" + (name is null ? "null!" : SymbolDisplay.FormatLiteral(name, true)) + ")] public static class Empty;");
        if (valid)
        {
            Assert.IsEmpty(diagnostics);
            Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"" + name + "\";\n", InstallationBody(compilation));
            Assert.AreEqual("false", ManifestValue(compilation, "Ankus.Relocatable"));
        }
        else
        {
            Assert.AreEqual(diagnosticId, Assert.ContainsSingle(diagnostics).Id);
        }
    }

    /// <summary>
    /// Fixed placement does not implicitly create or adopt an existing schema.
    /// </summary>
    [TestMethod]
    public void ExistingSchemasHaveNoCreationStatement()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgSchema("public", Create = false)]
            public static class Functions
            {
                [Ankus.PgFunction] public static int F() => 1;
                [Ankus.PgFunction(Schema = "another")] public static int G() => 2;
            }
            """);
        Assert.IsEmpty(diagnostics);
        string sql = InstallationBody(compilation);
        Assert.DoesNotContain("CREATE SCHEMA", sql);
        Assert.Contains("FUNCTION \"public\".\"f\"()", sql);
        Assert.Contains("FUNCTION \"another\".\"g\"()", sql);
        Assert.AreEqual("false", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Joins a generated metadata value from its parts: the first uses the plain key and later ones append <c>#</c> and
    /// their index.
    /// </summary>
    private static string ManifestValue(Compilation compilation, string key)
    {
        (int Index, string Value)[] parts = [.. compilation.Assembly.GetAttributes()
            .Where(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute")
            .Select(static attribute => ((string)attribute.ConstructorArguments[0].Value!, (string)attribute.ConstructorArguments[1].Value!))
            .Where(item => item.Item1 == key || item.Item1.StartsWith(key + "#", StringComparison.Ordinal))
            .Select(item => (item.Item1 == key ? 0 : int.Parse(item.Item1[(key.Length + 1)..], CultureInfo.InvariantCulture), item.Item2))
            .OrderBy(static item => item.Item1)];
        Assert.IsNotEmpty(parts, "No generated metadata named " + key);
        Assert.AreSequenceEqual(Enumerable.Range(0, parts.Length), parts.Select(static part => part.Index));
        return string.Concat(parts.Select(static part => part.Value));
    }
}
