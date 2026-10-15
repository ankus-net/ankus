using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Each invalid mapped range option identifies its independently correctable argument.
    /// </summary>
    /// <param name="options">The malformed range declaration.</param>
    /// <param name="id">The exact diagnostic contract.</param>
    /// <param name="span">The authored syntax requiring correction.</param>
    [TestMethod]
    [DataRow("null", "ANKUS162", "null")]
    [DataRow("\"\"", "ANKUS162", "\"\"")]
    [DataRow("name: \"\"", "ANKUS162", "\"\"")]
    [DataRow("\"range\", Schema = \"\"", "ANKUS163", "\"\"")]
    [DataRow("\"range\", Schema = \"bad\\0\"", "ANKUS163", "\"bad\\0\"")]
    [DataRow("\"range\", Origin = (Ankus.PgTypeOrigin)18", "ANKUS164", "(Ankus.PgTypeOrigin)18")]
    [DataRow("\"range\", Origin = Ankus.PgTypeOrigin.External", "ANKUS165", "Ankus.PgTypeOrigin.External")]
    [DataRow("typeof(int), \"range\"", "ANKUS160", "typeof(int)")]
    [DataRow("name: \"range\", managedType: typeof(int)", "ANKUS160", "typeof(int)")]
    public void RangeMappingDiagnosticsIdentifyAuthoredValues(string options, string id, string span)
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(DatumRangeSource(options));
        AssertDeclarationDiagnosticLocation(Assert.ContainsSingle(errors), id, span, "ranges/#mapping-diagnostics");
        Assert.IsNull(output.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers"));
        Assert.IsEmpty(output.Assembly.GetAttributes().Where(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute"));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Repeated defaults and exact targets point at the second authored declaration.
    /// </summary>
    /// <param name="attributes">The duplicated range metadata.</param>
    /// <param name="id">The exact diagnostic contract.</param>
    /// <param name="span">The second declaration.</param>
    [TestMethod]
    [DataRow("[Ankus.PgRangeType(\"one\")][Ankus.PgRangeType(\"two\")]", "ANKUS159", "Ankus.PgRangeType(\"two\")")]
    [DataRow("[Ankus.PgRangeType(typeof(Value), \"one\")][Ankus.PgRangeType(typeof(Value), \"two\")]", "ANKUS161",
        "Ankus.PgRangeType(typeof(Value), \"two\")")]
    public void RangeMappingDuplicateDiagnosticsIdentifySecondDeclaration(string attributes, string id, string span)
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(attributes + DatumMappingSource());
        AssertDeclarationDiagnosticLocation(Assert.ContainsSingle(errors), id, span, "ranges/#mapping-diagnostics");
        Assert.IsNull(output.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers"));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Missing or invalid referenced range metadata navigates to the authored consuming signature.
    /// </summary>
    /// <param name="invalid">Whether the referenced mapping has a malformed name rather than no range declaration.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReferencedRangeDiagnosticsIdentifyConsumingSignature(bool invalid)
    {
        MetadataReference dependency = DatumMappingReference("RangeDiagnosticDependency",
            invalid ? DatumRangeSource("\"\"") : DatumMappingSource());
        (Compilation output, ImmutableArray<Diagnostic> errors) = GenerateDatumMappingReference(
            "public static class Functions { [Ankus.PgFunction] public static int Read(Ankus.PgRange<Value> value) => 42; }",
            [dependency]);
        AssertDeclarationDiagnosticLocation(Assert.ContainsSingle(errors), invalid ? "ANKUS162" : "ANKUS168",
            "Ankus.PgRange<Value>", "ranges/#mapping-diagnostics");
        Assert.IsNull(output.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers"));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// An attribute supplied by an incompatible runtime fails before registration generation.
    /// </summary>
    [TestMethod]
    public void RangeMappingDiagnosticsRequireMatchingRuntimeContract()
    {
        string source = """
            namespace Ankus
            {
                [System.AttributeUsage(System.AttributeTargets.Struct)]
                public sealed class PgRangeTypeAttribute(string name) : System.Attribute;
            }
            """ + DatumRangeSource("\"range\"");
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source);
        AssertDeclarationDiagnosticLocation(Assert.ContainsSingle(errors), "ANKUS166", "Ankus.PgRangeType(\"range\")",
            "ranges/#mapping-diagnostics");
        Assert.IsNull(output.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers"));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// A shadowed runtime range identity is rejected before generated code can bind to another type.
    /// </summary>
    [TestMethod]
    public void RangeMappingDiagnosticsRejectShadowedRuntimeIdentity()
    {
        string source = "namespace Ankus { public sealed class PgRange<T>; } " + DatumRangeSource();
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source);
        AssertDeclarationDiagnosticLocation(Assert.ContainsSingle(errors), "ANKUS167", "Value", "ranges/#mapping-diagnostics");
        Assert.IsNull(output.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers"));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Earlier body edits and an inserted source tree keep the range error cached with current navigation.
    /// </summary>
    [TestMethod]
    public void RangeMappingDiagnosticsRemainCachedAcrossSourceMovement()
    {
        string source = "public static class Earlier { public static int Body() => 1; }\n" + DatumRangeSource("\"\"");
        CSharpCompilation input = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out _,
            out ImmutableArray<Diagnostic> initial, context.CancellationToken);
        Diagnostic previous = Assert.ContainsSingle(initial);
        SyntaxTree current = CSharpSyntaxTree.ParseText(source.Replace("=> 1;", "=> 10000;", StringComparison.Ordinal),
            path: "Module.cs", cancellationToken: context.CancellationToken);
        SyntaxTree unrelated = CSharpSyntaxTree.ParseText("internal static class Unrelated;", path: "Unrelated.cs",
            cancellationToken: context.CancellationToken);
        CSharpCompilation edited = input.RemoveAllSyntaxTrees().AddSyntaxTrees(unrelated, current);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);

        AssertDeclarationDiagnosticLocation(error, "ANKUS162", "\"\"", "ranges/#mapping-diagnostics");
        Assert.AreEqual(IncrementalStepRunReason.Unchanged, ModuleStep(driver, "DatumAnalysis"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "DatumOutputs"));
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreNotEqual(previous.Location.SourceSpan.Start, error.Location.SourceSpan.Start);
    }

    /// <summary>
    /// Verifies the independently correctable diagnostic and exact current authored source.
    /// </summary>
    private void AssertDeclarationDiagnosticLocation(Diagnostic error, string id, string span, string help)
    {
        Assert.AreEqual(id, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual("https://ankus.net/" + help, error.Descriptor.HelpLinkUri);
        Assert.IsTrue(error.Location.IsInSource);
        Assert.AreEqual(span, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }
}
