using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Mapping metadata errors distinguish contracts and identify the value the author must correct.
    /// </summary>
    /// <param name="options">The malformed mapping arguments.</param>
    /// <param name="id">The exact diagnostic contract.</param>
    /// <param name="span">The authored syntax requiring correction.</param>
    [TestMethod]
    [DataRow("null, typeof(Converter)", "ANKUS138", "null")]
    [DataRow("\"\", typeof(Converter)", "ANKUS138", "\"\"")]
    [DataRow("name: \"\", converter: typeof(Converter)", "ANKUS138", "\"\"")]
    [DataRow("converter: typeof(Converter), name: \"\"", "ANKUS138", "\"\"")]
    [DataRow("\"int4\", typeof(Converter), Schema = \"\"", "ANKUS139", "\"\"")]
    [DataRow("\"int4\", typeof(Converter), Origin = (Ankus.PgTypeOrigin)17", "ANKUS140", "(Ankus.PgTypeOrigin)17")]
    [DataRow("\"int4\", typeof(Converter), Origin = Ankus.PgTypeOrigin.External", "ANKUS141", "Ankus.PgTypeOrigin.External")]
    [DataRow("\"int4\", typeof(Converter[])", "ANKUS142", "typeof(Converter[])")]
    [DataRow("\"int4\", typeof(System.DayOfWeek)", "ANKUS142", "typeof(System.DayOfWeek)")]
    [DataRow("converter: typeof(Converter[]), name: \"int4\"", "ANKUS142", "typeof(Converter[])")]
    [DataRow("name: \"int4\", typeof(Converter[])", "ANKUS142", "typeof(Converter[])")]
    public void DatumMappingMetadataDiagnosticsIdentifyAuthoredValues(string options, string id, string span)
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(
            "[Ankus.PgDatumType(" + options + ")] public struct Value { } " + DatumConverter());
        Diagnostic error = Assert.ContainsSingle(errors);
        AssertDatumDiagnosticLocation(error, id, span);
        Assert.IsNull(output.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers"));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Conversion direction and override errors point to the consuming signature or conflicting attribute.
    /// </summary>
    /// <param name="reader">Whether only the input capability is available.</param>
    /// <param name="method">The invalid consuming signature.</param>
    /// <param name="id">The exact diagnostic contract.</param>
    /// <param name="span">The authored syntax requiring correction.</param>
    [TestMethod]
    [DataRow(false, "public static int Read(Value value) => 1;", "ANKUS152", "Value")]
    [DataRow(true, "public static Value Write() => default;", "ANKUS153", "Value")]
    [DataRow(false, "public static int Read(Value?[] value) => 1;", "ANKUS152", "Value?[]")]
    [DataRow(true, "public static int Read(Value[][] value) => 1;", "ANKUS154", "Value[][]")]
    [DataRow(true, "public static int Read([Ankus.PgSqlType(\"text\")] Value value) => 1;", "ANKUS151", "Ankus.PgSqlType(\"text\")")]
    public void DatumMappingSignatureDiagnosticsIdentifyConsumedTypes(bool reader, string method, string id, string span)
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(DatumMappingSource(reader: reader, writer: !reader) +
            "public static class Functions { [Ankus.PgFunction] " + method + " }");
        AssertDatumDiagnosticLocation(Assert.ContainsSingle(errors), id, span);
        Assert.IsNull(output.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers"));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// A private carrier gets an accessibility diagnostic independently of its valid converter contract.
    /// </summary>
    [TestMethod]
    public void DatumMappingCarrierAccessibilityHasItsOwnDiagnostic()
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate("""
            public static class Holder
            {
                [Ankus.PgDatumType("int4", typeof(Converter), Origin = Ankus.PgTypeOrigin.External, Schema = "pg_catalog")]
                private struct Value;
                private sealed class Converter : Ankus.IPgDatumReader<Value>
                {
                    public Value Read(Ankus.PgDatum value) => default;
                }
            }
            """);
        AssertDatumDiagnosticLocation(Assert.ContainsSingle(errors), "ANKUS135", "Value");
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Duplicate mappings point at the second declaration instead of the valid original or carrier name.
    /// </summary>
    /// <param name="exact">Whether the mapping selects an explicit closed type.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DuplicateDatumMappingDiagnosticsIdentifyTheSecondDeclaration(bool exact)
    {
        string attribute = "Ankus.PgDatumType(" + (exact ? "typeof(Value), " : string.Empty) +
            "\"int4\", typeof(Converter), Origin = Ankus.PgTypeOrigin.External, Schema = \"pg_catalog\")";
        string source = "[" + attribute + "]\n[" + attribute + "] public struct Value { } " + DatumConverter();
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source);
        Diagnostic error = Assert.ContainsSingle(errors);

        AssertDatumDiagnosticLocation(error, exact ? "ANKUS157" : "ANKUS155", attribute);
        Assert.AreEqual(source.LastIndexOf(attribute, StringComparison.Ordinal), error.Location.SourceSpan.Start);
        Assert.IsNull(output.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers"));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// An inferred constraint failure points at the converter expression and names the inferred converter and violated compiler rule.
    /// </summary>
    [TestMethod]
    public void DatumConverterConstraintDiagnosticsIdentifyTheAuthoredConverter()
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate("""
            [Ankus.PgDatumType(typeof(Box<string>), "int4", typeof(Converter<>), Origin = Ankus.PgTypeOrigin.External, Schema = "pg_catalog")]
            public readonly record struct Box<T>(T Value);
            public sealed class Converter<T> : Ankus.IPgDatumReader<Box<T>> where T : struct
            {
                public Box<T> Read(Ankus.PgDatum value) => default;
            }
            """);
        Diagnostic error = Assert.ContainsSingle(errors);
        AssertDatumDiagnosticLocation(error, "ANKUS149", "typeof(Converter<>)");
        AssertConverterConstraintDiagnostic(error, "typeof(Converter<>)", "Converter<string>", "CS0453");
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// A cached inferred-constraint failure follows its converter after an unrelated earlier edit and file insertion.
    /// </summary>
    [TestMethod]
    public void DatumConverterConstraintDiagnosticsRemainCachedAcrossSourceMovement()
    {
        string source = "public static class Earlier { public static int Body() => 1; }\n" + """
            [Ankus.PgDatumType(typeof(Box<string>), "int4", typeof(Converter<>), Origin = Ankus.PgTypeOrigin.External, Schema = "pg_catalog")]
            public readonly record struct Box<T>(T Value);
            public sealed class Converter<T> : Ankus.IPgDatumReader<Box<T>> where T : struct
            {
                public Box<T> Read(Ankus.PgDatum value) => default;
            }
            """;
        CSharpCompilation input = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out _,
            out ImmutableArray<Diagnostic> initial, context.CancellationToken);
        Diagnostic previous = Assert.ContainsSingle(initial);
        SyntaxTree current = CSharpSyntaxTree.ParseText(source.Replace("=> 1;", "=> 10000;", StringComparison.Ordinal), path: "Module.cs",
            cancellationToken: context.CancellationToken);
        SyntaxTree unrelated = CSharpSyntaxTree.ParseText("internal static class Unrelated;", path: "Unrelated.cs", cancellationToken: context.CancellationToken);
        driver = driver.RunGeneratorsAndUpdateCompilation(input.RemoveAllSyntaxTrees().AddSyntaxTrees(unrelated, current), out _,
            out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);

        AssertConverterConstraintDiagnostic(error, "typeof(Converter<>)", "Converter<string>", "CS0453");
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "DatumOutputs"));
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreEqual(previous.Location.SourceSpan.Start + 4, error.Location.SourceSpan.Start);
    }

    /// <summary>
    /// An earlier body edit and file insertion preserve cached errors while moving navigation to the current attribute.
    /// </summary>
    [TestMethod]
    public void DatumMappingValueDiagnosticsRemainCachedAcrossSourceMovement()
    {
        string source = "public static class Earlier { public static int Body() => 1; }\n" +
            "[Ankus.PgDatumType(\"\", typeof(Converter))] public struct Value { } " + DatumConverter();
        CSharpCompilation input = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out _,
            out ImmutableArray<Diagnostic> initial, context.CancellationToken);
        Diagnostic previous = Assert.ContainsSingle(initial);
        string replacement = source.Replace("=> 1;", "=> 10000;", StringComparison.Ordinal);
        SyntaxTree current = CSharpSyntaxTree.ParseText(replacement, path: "Module.cs", cancellationToken: context.CancellationToken);
        SyntaxTree unrelated = CSharpSyntaxTree.ParseText("internal static class Unrelated;", path: "Unrelated.cs",
            cancellationToken: context.CancellationToken);
        CSharpCompilation edited = input.RemoveAllSyntaxTrees().AddSyntaxTrees(unrelated, current);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);

        AssertDatumDiagnosticLocation(error, "ANKUS138", "\"\"");
        Assert.AreEqual(IncrementalStepRunReason.Unchanged, ModuleStep(driver, "DatumAnalysis"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "DatumOutputs"));
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreNotEqual(previous.Location.SourceSpan.Start, error.Location.SourceSpan.Start);
    }

    /// <summary>
    /// Verifies diagnostic identity, severity, help destination and exact authored navigation.
    /// </summary>
    private void AssertDatumDiagnosticLocation(Diagnostic error, string id, string span)
    {
        Assert.AreEqual(id, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual("https://ankus.net/raw-values/#mapping-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.IsTrue(error.Location.IsInSource);
        Assert.AreEqual(span, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }
}
