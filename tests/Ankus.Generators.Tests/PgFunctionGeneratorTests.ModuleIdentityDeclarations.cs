using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Authored module identity values identify their exact field and independently correctable transport cause.
    /// </summary>
    /// <param name="property">The authored identity field.</param>
    /// <param name="expression">The source-level constant expression.</param>
    /// <param name="expected">The independently required diagnostic identity.</param>
    [TestMethod]
    [DataRow("Name", "\"\\0\"", "ANKUS350")]
    [DataRow("Name", "\"a\\0b\"", "ANKUS350")]
    [DataRow("Name", "\"tail\\0\"", "ANKUS350")]
    [DataRow("Version", "\"\\0\"", "ANKUS352")]
    [DataRow("Version", "\"a\\0b\"", "ANKUS352")]
    [DataRow("Version", "\"tail\\0\"", "ANKUS352")]
    [DataRow("Name", "\"\\uD800\"", "ANKUS351")]
    [DataRow("Name", "\"\\uDC00\"", "ANKUS351")]
    [DataRow("Version", "\"\\uD800\"", "ANKUS353")]
    [DataRow("Version", "\"\\uDC00\"", "ANKUS353")]
    [DataRow("Name", "IdentityLiterals.Zero", "ANKUS350")]
    [DataRow("Version", "IdentityLiterals.Surrogate", "ANKUS353")]
    public void ModuleIdentityFailuresIdentifyFieldAndTransport(string property, string expression, string expected)
    {
        const string Literals = """
            public static class IdentityLiterals
            {
                public const string Zero = "a\0b";
                public const string Surrogate = "\uD800";
            }
            """;
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate("[assembly: Ankus.PgModule(" + property + " = " + expression + ")]\n" + Literals);
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(expression, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/reference/build-settings/#module-identity-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        Assert.DoesNotContain(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            attribute.ConstructorArguments[0].Value is "Ankus.NativeSource", output.Assembly.GetAttributes());
    }

    /// <summary>
    /// Invalid name and version fields retain both distinct errors and exact locations without generating a partial native identity.
    /// </summary>
    /// <param name="name">The invalid name expression.</param>
    /// <param name="version">The invalid version expression.</param>
    /// <param name="nameDiagnostic">The required name transport error.</param>
    /// <param name="versionDiagnostic">The required version transport error.</param>
    [TestMethod]
    [DataRow("\"a\\0b\"", "\"v\\0x\"", "ANKUS350", "ANKUS352")]
    [DataRow("\"a\\0b\"", "\"\\uD800\"", "ANKUS350", "ANKUS353")]
    [DataRow("\"\\uDC00\"", "\"v\\0x\"", "ANKUS351", "ANKUS352")]
    [DataRow("\"\\uDC00\"", "\"\\uD800\"", "ANKUS351", "ANKUS353")]
    public void ModuleIdentityFailuresRetainBothFieldDiagnostics(string name, string version, string nameDiagnostic, string versionDiagnostic)
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate("[assembly: Ankus.PgModule(Name = " + name + ", Version = " + version + ")]");
        Assert.HasCount(2, errors);
        Assert.AreEqual(nameDiagnostic, errors[0].Id);
        Assert.AreEqual(versionDiagnostic, errors[1].Id);
        foreach ((Diagnostic error, string expression) in new[] { (errors[0], name), (errors[1], version) })
        {
            Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
            Assert.AreEqual(expression, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
            Assert.AreEqual("https://ankus.net/reference/build-settings/#module-identity-diagnostics", error.Descriptor.HelpLinkUri);
        }

        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        Assert.DoesNotContain(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            attribute.ConstructorArguments[0].Value is "Ankus.NativeSource", output.Assembly.GetAttributes());
    }

    /// <summary>
    /// Invalid effective project defaults produce their version-specific error without inventing an authored source span.
    /// </summary>
    /// <param name="cause">The exact malformed project-default partition.</param>
    /// <param name="expected">The required default-version transport diagnostic.</param>
    [TestMethod]
    [DataRow("zero", "ANKUS352")]
    [DataRow("high", "ANKUS353")]
    [DataRow("low", "ANKUS353")]
    public void ModuleIdentityProjectDefaultsHavePreciseErrors(string cause, string expected)
    {
        string version = cause switch { "zero" => "v\0x", "high" => "\uD800", _ => "\uDC00" };
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate("[assembly: Ankus.PgModule]", options: new ModuleOptions(version));
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(Location.None, error.Location);
        Assert.AreEqual("https://ankus.net/reference/build-settings/#module-identity-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        Assert.DoesNotContain(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            attribute.ConstructorArguments[0].Value is "Ankus.NativeSource", output.Assembly.GetAttributes());
    }

    /// <summary>
    /// Unicode scalar boundaries remain valid and retain exact independent name/version UTF-8 bytes.
    /// </summary>
    /// <param name="expression">The exact C# constant expression.</param>
    /// <param name="literal">The independently required UTF-8 C literal body.</param>
    [TestMethod]
    [DataRow("\"\\uD7FF\"", "\\xed\\x9f\\xbf")]
    [DataRow("\"\\uE000\"", "\\xee\\x80\\x80")]
    [DataRow("\"\\uD800\\uDC00\"", "\\xf0\\x90\\x80\\x80")]
    [DataRow("\"\\uDBFF\\uDFFF\"", "\\xf4\\x8f\\xbf\\xbf")]
    public void ModuleIdentityUnicodeBoundariesPreserveNativeBytes(string expression, string literal)
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate("[assembly: Ankus.PgModule(Name = " + expression + ", Version = " + expression + ")]");
        Assert.IsEmpty(errors);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        Assert.Contains("PG_MODULE_MAGIC_EXT(\n    .name = \"" + literal + "\",\n    .version = \"" + literal + "\");", ManifestValue(output, "Ankus.NativeSource"));
        Assert.AreSequenceEqual(["Pg_magic_func"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}
