using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Independent enum declaration failures identify their exact cause and preserve unrelated generated contracts.
    /// </summary>
    /// <param name="source">The invalid declaration.</param>
    /// <param name="expected">The specific diagnostic ID.</param>
    /// <param name="highlight">The exact highlighted source text.</param>
    [TestMethod]
    [DataRow("[System.Flags, Ankus.PgEnum] public enum Mood { First = 1, Last = 2 }", "ANKUS279", "System.Flags")]
    [DataRow("public class Container<T> { [Ankus.PgEnum] public enum Mood { Ready } }", "ANKUS280", "Container")]
    [DataRow("file class Container { [Ankus.PgEnum] public enum Mood { Ready } }", "ANKUS281", "Container")]
    [DataRow("[Ankus.PgEnum] file enum Mood { Ready }", "ANKUS281", "Mood")]
    [DataRow("public class Container { [Ankus.PgEnum] private enum Mood { Ready } }", "ANKUS282", "Mood")]
    [DataRow("public class Outer { private class Container { [Ankus.PgEnum] public enum Mood { Ready } } }", "ANKUS282", "Container")]
    [DataRow("[Ankus.PgEnum(Name = \"\")] public enum Mood { Ready }", "ANKUS283", "\"\"")]
    [DataRow("[Ankus.PgEnum(Name = \"a\\0b\")] public enum Mood { Ready }", "ANKUS283", "\"a\\0b\"")]
    [DataRow("[Ankus.PgEnum(Name = \"\\ud800\")] public enum Mood { Ready }", "ANKUS283", "\"\\ud800\"")]
    [DataRow("[Ankus.PgEnum(Schema = \"\")] public enum Mood { Ready }", "ANKUS284", "\"\"")]
    [DataRow("[Ankus.PgEnum(Schema = \"a\\0b\")] public enum Mood { Ready }", "ANKUS284", "\"a\\0b\"")]
    [DataRow("[Ankus.PgEnum(Schema = \"\\udc00\")] public enum Mood { Ready }", "ANKUS284", "\"\\udc00\"")]
    [DataRow("[Ankus.PgEnum] public enum Mood { [Ankus.PgEnumLabel(null!)] Ready }", "ANKUS286", "null!")]
    [DataRow("[Ankus.PgEnum] public enum Mood { [Ankus.PgEnumLabel(\"a\\0b\")] Ready }", "ANKUS287", "\"a\\0b\"")]
    [DataRow("[Ankus.PgEnum] public enum Mood { [Ankus.PgEnumLabel(\"\\ud800\")] Ready }", "ANKUS287", "\"\\ud800\"")]
    [DataRow("[Ankus.PgEnum] public enum Mood { [Ankus.PgEnumLabel(\"\\udc00\")] Ready }", "ANKUS287", "\"\\udc00\"")]
    [DataRow("[Ankus.PgEnum] public enum Mood { [Ankus.PgEnumLabel(\"same\")] First, [Ankus.PgEnumLabel(\"same\")] Last }", "ANKUS289", "\"same\"")]
    [DataRow("[Ankus.PgEnum] public enum Mood { First = 7, Last = 7 }", "ANKUS290", "Last")]
    public void EnumFailuresHaveSpecificDiagnostics(string source, string expected, string highlight)
    {
        const string Sibling = "\n[Ankus.PgEnum] public enum Healthy { Working }\npublic static class Other { [Ankus.PgFunction] public static int Value() => 7; }";
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source + Sibling);
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(highlight, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/enums/#declaration-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.Contains("'Mood'", error.GetMessage(CultureInfo.InvariantCulture));
        if (expected == "ANKUS289")
        {
            Assert.AreEqual(source.LastIndexOf(highlight, StringComparison.Ordinal), error.Location.SourceSpan.Start);
        }

        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        Assert.Contains("CREATE TYPE \"healthy\" AS ENUM (E'Working');", InstallationBody(output));
        Assert.DoesNotContain("CREATE TYPE \"mood\"", InstallationBody(output));
        Assert.ContainsSingle(output.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers().OfType<IMethodSymbol>()
            .Where(static method => method.GetAttributes().Any(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute")));
    }

    /// <summary>
    /// Enum identifier and label limits report the authored overlength literal, while their adjacent valid boundaries retain all bytes.
    /// </summary>
    /// <param name="role">The declaration component.</param>
    /// <param name="bytes">Its exact UTF-8 size.</param>
    [TestMethod]
    [DataRow("Name", 62)]
    [DataRow("Name", 63)]
    [DataRow("Name", 64)]
    [DataRow("Schema", 62)]
    [DataRow("Schema", 63)]
    [DataRow("Schema", 64)]
    [DataRow("Label", 62)]
    [DataRow("Label", 63)]
    [DataRow("Label", 64)]
    public void EnumDiagnosticByteBoundariesRemainExact(string role, int bytes)
    {
        string value = new('é', bytes / 2);
        value += bytes % 2 == 0 ? string.Empty : "x";
        string literal = SymbolDisplay.FormatLiteral(value, true);
        string attribute = role == "Label" ? "[Ankus.PgEnum]" : "[Ankus.PgEnum(" + role + "=" + literal + ")]";
        string field = role == "Label" ? "[Ankus.PgEnumLabel(" + literal + ")] Ready" : "Ready";
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(attribute + " public enum Mood { " + field + " }");
        if (bytes == 64)
        {
            Diagnostic error = Assert.ContainsSingle(errors);
            Assert.AreEqual(role switch { "Name" => "ANKUS283", "Schema" => "ANKUS284", _ => "ANKUS288" }, error.Id);
            Assert.AreEqual(literal, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
            Assert.DoesNotContain("CREATE TYPE", InstallationBody(output));
            return;
        }

        Assert.IsEmpty(errors);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
        string name = role == "Name" ? value : "mood";
        string schema = role == "Schema" ? "\"" + value + "\"." : string.Empty;
        string label = role == "Label" ? value : "Ready";
        Assert.AreEqual("CREATE TYPE " + schema + "\"" + name + "\" AS ENUM (E'" + label + "');\n", InstallationBody(output));
    }

    /// <summary>
    /// A non-enum attribute misuse retains the compiler's usage error and reports its own declaration requirement without generating a type.
    /// </summary>
    [TestMethod]
    public void PgEnumMisuseHasSpecificDiagnostic()
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate("[Ankus.PgEnum] public class Mood { }");
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual("ANKUS278", error.Id);
        Assert.AreEqual("Ankus.PgEnum", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual("CS0592", Assert.ContainsSingle(output.GetDiagnostics(context.CancellationToken)
            .Where(static value => value.Severity == DiagnosticSeverity.Error)).Id);
        Assert.DoesNotContain("CREATE TYPE", InstallationBody(output));
    }

    /// <summary>
    /// Invalid inherited schemas identify the enclosing attribute rather than blaming the enum name.
    /// </summary>
    /// <param name="expression">The exact schema constructor value.</param>
    /// <param name="expected">The dedicated enum schema diagnostic.</param>
    /// <param name="explicitNull">Whether PgEnum explicitly selects the default inherited schema.</param>
    [TestMethod]
    [DataRow("null!", "ANKUS285", false)]
    [DataRow("null!", "ANKUS285", true)]
    [DataRow("\"\"", "ANKUS284", false)]
    [DataRow("\"\"", "ANKUS284", true)]
    [DataRow("\"a\\0b\"", "ANKUS284", false)]
    [DataRow("\"a\\0b\"", "ANKUS284", true)]
    [DataRow("\"\\ud800\"", "ANKUS284", false)]
    [DataRow("\"\\ud800\"", "ANKUS284", true)]
    public void EnumInheritedSchemaDiagnosticHighlightsItsCause(string expression, string expected, bool explicitNull)
    {
        string enumAttribute = explicitNull ? "[Ankus.PgEnum(Schema = null)]" : "[Ankus.PgEnum]";
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate("[Ankus.PgSchema(" + expression + ")] public static class Container { " + enumAttribute + " public enum Mood { Ready } }");
        Diagnostic error = Assert.ContainsSingle(errors.Where(diagnostic => diagnostic.Id == expected));
        Assert.AreEqual(expression, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual("https://ankus.net/enums/#declaration-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.Contains("'Mood'", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreSequenceEqual(new[] { "ANKUS050", expected }.Order(StringComparer.Ordinal), errors.Select(static item => item.Id).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("CREATE TYPE", InstallationBody(output));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Accessible containers, empty labels and null installation schemas remain valid enum contracts.
    /// </summary>
    /// <param name="source">The valid declaration.</param>
    /// <param name="sql">Its exact installation statement.</param>
    [TestMethod]
    [DataRow("[Ankus.PgSchema(\"selected\")] public class Container { [Ankus.PgEnum(Schema = null)] public enum Mood { Ready } }", "CREATE SCHEMA IF NOT EXISTS \"selected\";\nCREATE TYPE \"selected\".\"mood\" AS ENUM (E'Ready');\n")]
    [DataRow("[Ankus.PgEnum] public enum Mood { [Ankus.PgEnumLabel(\"\")] Ready }", "CREATE TYPE \"mood\" AS ENUM (E'');\n")]
    [DataRow("[Ankus.PgEnum(Schema = null)] public enum Mood { Ready }", "CREATE TYPE \"mood\" AS ENUM (E'Ready');\n")]
    [DataRow("[Ankus.PgEnum(Name = null)] public enum Mood { Ready }", "CREATE TYPE \"mood\" AS ENUM (E'Ready');\n")]
    [DataRow("internal class Container { [Ankus.PgEnum] internal enum Mood { Ready } }", "CREATE TYPE \"mood\" AS ENUM (E'Ready');\n")]
    [DataRow("public class Container { [Ankus.PgEnum] protected internal enum Mood { Ready } }", "CREATE TYPE \"mood\" AS ENUM (E'Ready');\n")]
    public void PreciseEnumDiagnosticControlsPreserveValidContracts(string source, string sql)
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source);
        Assert.IsEmpty(errors);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
        Assert.AreEqual(sql, InstallationBody(output));
        Assert.IsNotEmpty(EmitDatumMappingImage(output));
    }

    /// <summary>
    /// A moved cached alias diagnostic resolves its current member, preserves a sibling enum and disappears after the value is repaired.
    /// </summary>
    [TestMethod]
    public void PreciseEnumDiagnosticsMoveAndRepairIndependently()
    {
        const string Source = "[Ankus.PgEnum] public enum Mood { First = 7, Last = 7 }";
        CSharpCompilation input = ModuleCompilation(Source).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "[Ankus.PgEnum] public enum Healthy { Working }", path: "Healthy.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out _, out ImmutableArray<Diagnostic> initial, context.CancellationToken);
        Assert.AreEqual("ANKUS290", Assert.ContainsSingle(initial).Id);
        SyntaxTree movedTree = CSharpSyntaxTree.ParseText("\n\n" + Source, path: "Module.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation moved = input.ReplaceSyntaxTree(input.SyntaxTrees.First(), movedTree);
        driver = driver.RunGeneratorsAndUpdateCompilation(moved, out Compilation failed, out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual("ANKUS290", error.Id);
        Assert.AreSame(movedTree, error.Location.SourceTree);
        Assert.AreEqual("Last", movedTree.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual(Assert.ContainsSingle(initial).Location.SourceSpan.Start + 2, error.Location.SourceSpan.Start);
        Assert.AreEqual(IncrementalStepRunReason.Cached, EnumEmissionReason(driver, "healthy"));
        Assert.AreEqual("CREATE TYPE \"healthy\" AS ENUM (E'Working');\n", InstallationBody(failed));
        CSharpCompilation repaired = moved.ReplaceSyntaxTree(movedTree, CSharpSyntaxTree.ParseText(
            Source.Replace("Last = 7", "Last = 8", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Cached, EnumEmissionReason(driver, "healthy"));
        Assert.AreEqual("CREATE TYPE \"healthy\" AS ENUM (E'Working');\nCREATE TYPE \"mood\" AS ENUM (E'First', E'Last');\n", InstallationBody(output));
        Assert.IsNotEmpty(EmitDatumMappingImage(output));
    }
}
