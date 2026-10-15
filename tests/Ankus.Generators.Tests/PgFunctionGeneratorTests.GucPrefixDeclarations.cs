using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Invalid native prefix text identifies the actual argument and its independently correctable transport contract.
    /// </summary>
    /// <param name="argument">The complete positional or named constructor argument.</param>
    /// <param name="expected">The independently required transport diagnostic.</param>
    /// <param name="highlight">The authored constant expression requiring correction.</param>
    [TestMethod]
    [DataRow("null!", "ANKUS347", "null!")]
    [DataRow("prefix: null!", "ANKUS347", "null!")]
    [DataRow("\"\\0\"", "ANKUS348", "\"\\0\"")]
    [DataRow("\"a\\0b\"", "ANKUS348", "\"a\\0b\"")]
    [DataRow("prefix: \"a\\0b\"", "ANKUS348", "\"a\\0b\"")]
    [DataRow("\"\\uD800\"", "ANKUS349", "\"\\uD800\"")]
    [DataRow("\"\\uDC00\"", "ANKUS349", "\"\\uDC00\"")]
    [DataRow("\"\\uD800x\"", "ANKUS349", "\"\\uD800x\"")]
    [DataRow("\"x\\uDC00\"", "ANKUS349", "\"x\\uDC00\"")]
    [DataRow("\"\\uD800\\uD800\"", "ANKUS349", "\"\\uD800\\uD800\"")]
    [DataRow("prefix: \"\\uDC00\"", "ANKUS349", "\"\\uDC00\"")]
    [DataRow("PrefixLiterals.Missing", "ANKUS347", "PrefixLiterals.Missing")]
    [DataRow("PrefixLiterals.Zero", "ANKUS348", "PrefixLiterals.Zero")]
    [DataRow("PrefixLiterals.Surrogate", "ANKUS349", "PrefixLiterals.Surrogate")]
    public void GucPrefixFailuresHaveSpecificArgumentDiagnostics(string argument, string expected, string highlight)
    {
        const string Literals = """
            public static class PrefixLiterals
            {
                public const string Missing = null!;
                public const string Zero = "a\0b";
                public const string Surrogate = "\uD800";
            }
            """;
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate("[assembly: Ankus.PgGucPrefix(" + argument + ")]\n" + Literals);
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(highlight, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/configuration/#prefix-declaration-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        Assert.DoesNotContain("ankus_reserve_guc_prefix(", ManifestValue(output, "Ankus.NativeSource"));
        Assert.AreSequenceEqual(["Pg_magic_func"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Unicode boundaries and a literal backslash retain exact native bytes instead of treating valid text as a transport error.
    /// </summary>
    /// <param name="expression">The source-level constant containing the boundary value.</param>
    /// <param name="literal">The independently expected UTF-8 C literal.</param>
    [TestMethod]
    [DataRow("\"\\uD7FF\"", "\"\\355\\237\\277\"")]
    [DataRow("\"\\uE000\"", "\"\\356\\200\\200\"")]
    [DataRow("\"\\uD800\\uDC00\"", "\"\\360\\220\\200\\200\"")]
    [DataRow("\"\\uDBFF\\uDFFF\"", "\"\\364\\217\\277\\277\"")]
    [DataRow("\"\\\\0\"", "\"\\134\\060\"")]
    public void GucPrefixUnicodeBoundariesPreserveExactNativeText(string expression, string literal)
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate("[assembly: Ankus.PgGucPrefix(" + expression + ")]");
        AssertGucCompilation(output, errors);
        Assert.Contains("ankus_reserve_guc_prefix(" + literal + ");", ManifestValue(output, "Ankus.NativeSource"));
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Each invalid transport partition leaves valid sibling declarations and their complete compiled native manifest intact.
    /// </summary>
    /// <param name="expression">The invalid sibling prefix expression.</param>
    /// <param name="expected">The independently required sibling diagnostic.</param>
    [TestMethod]
    [DataRow("null!", "ANKUS347")]
    [DataRow("\"bad\\0prefix\"", "ANKUS348")]
    [DataRow("\"\\uD800\"", "ANKUS349")]
    public void GucPrefixInvalidSiblingRetainsValidNativeManifest(string expression, string expected)
    {
        const string Valid = "[assembly: Ankus.PgGucPrefix(\"demo\")]";
        (Compilation valid, ImmutableArray<Diagnostic> validErrors) = Generate(Valid);
        AssertGucCompilation(valid, validErrors);
        (Compilation mixed, ImmutableArray<Diagnostic> errors) = Generate(Valid + "[assembly: Ankus.PgGucPrefix(" + expression + ")]");
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(expression, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.IsEmpty(mixed.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        foreach (string key in new[] { "Ankus.NativeSource", "Ankus.Sql", "Ankus.Exports", "Ankus.Relocatable" })
        {
            Assert.AreEqual(ManifestValue(valid, key), ManifestValue(mixed, key));
        }
    }
}
