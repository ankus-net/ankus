using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Prefix-only libraries preserve literal native input and expose only native loader entry points.
    /// </summary>
    /// <param name="prefix">The literal prefix accepted by PostgreSQL.</param>
    /// <param name="literal">The exact UTF8 native literal.</param>
    [TestMethod]
    [DataRow("", "\"\"")]
    [DataRow("demo", "\"\\144\\145\\155\\157\"")]
    [DataRow("DeMo", "\"\\104\\145\\115\\157\"")]
    [DataRow("demo.scope", "\"\\144\\145\\155\\157\\056\\163\\143\\157\\160\\145\"")]
    [DataRow("1$", "\"\\061\\044\"")]
    [DataRow(" ", "\"\\040\"")]
    [DataRow("\n", "\"\\012\"")]
    [DataRow("é", "\"\\303\\251\"")]
    [DataRow("🐘", "\"\\360\\237\\220\\230\"")]
    [DataRow("\"\\", "\"\\042\\134\"")]
    public void GucPrefixOnlyLiteralValuesCompileWithoutManagedEntry(string prefix, string literal)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "[assembly: Ankus.PgGucPrefix(" + SymbolDisplay.FormatLiteral(prefix, true) + ")]");
        AssertGucCompilation(compilation, diagnostics);
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(compilation).ReplaceLineEndings("\n"));
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual("true", ManifestValue(compilation, "Ankus.Relocatable"));
        Assert.IsEmpty(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers());
        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        Assert.Contains("ankus_reserve_guc_prefix(" + literal + ");", native);
        Assert.Contains("""
            #if PG_VERSION_NUM >= 150000
                    MarkGUCPrefixReserved(prefix);
            #else
                    EmitWarningsOnPlaceholders(prefix);
            #endif
            """.ReplaceLineEndings("\n"), native);
        Assert.DoesNotContain("ankus_spi_execute", native);
        Assert.DoesNotContain("ankus_guc_register(", native);
        Assert.DoesNotContain("AnkusError", native);
        Assert.DoesNotContain("ankus_managed_", native);
        Assert.DoesNotContain("cannot run through shared_preload_libraries", native);
    }

    /// <summary>
    /// Native prefixes are not constrained by SQL identifier lengths.
    /// </summary>
    [TestMethod]
    public void GucPrefixLengthIsNotTruncatedToAnIdentifier()
    {
        string prefix = new('a', 128);
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "[assembly: Ankus.PgGucPrefix(\"" + prefix + "\")]");
        AssertGucCompilation(compilation, diagnostics);
        Assert.Contains("ankus_reserve_guc_prefix(\"" + string.Concat(Enumerable.Repeat("\\141", 128)) + "\");",
            ManifestValue(compilation, "Ankus.NativeSource"));
    }

    /// <summary>
    /// Invalid C-string transport inputs produce an argument-located generator error instead of truncated native values.
    /// </summary>
    /// <param name="expression">The invalid attribute constant.</param>
    /// <param name="expected">The independently required transport error.</param>
    [TestMethod]
    [DataRow("null!", "ANKUS347")]
    [DataRow("\"\\0\"", "ANKUS348")]
    [DataRow("\"a\\0b\"", "ANKUS348")]
    [DataRow("\"\\uD800\"", "ANKUS349")]
    [DataRow("\"\\uDC00\"", "ANKUS349")]
    [DataRow("\"\\uD800x\"", "ANKUS349")]
    public void InvalidGucPrefixTransportIsDiagnosed(string expression, string expected)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("[assembly: Ankus.PgGucPrefix(" + expression + ")]");
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(expected, diagnostic.Id);
        Assert.AreEqual("https://ankus.net/configuration/#prefix-declaration-diagnostics", diagnostic.Descriptor.HelpLinkUri);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.IsTrue(diagnostic.Location.IsInSource);
        Assert.AreEqual(expression, diagnostic.Location.SourceTree!.GetText(context.CancellationToken)
            .ToString(diagnostic.Location.SourceSpan));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity >= DiagnosticSeverity.Warning));
        Assert.DoesNotContain("ankus_reserve_guc_prefix(", ManifestValue(compilation, "Ankus.NativeSource"));
        Assert.AreSequenceEqual(["Pg_magic_func"], ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Literal duplicate declarations coalesce without merging differently cased prefixes or depending on source order.
    /// </summary>
    [TestMethod]
    public void GucPrefixOrderingPreservesCaseAndDeduplicatesExactText()
    {
        const string first = "[assembly: Ankus.PgGucPrefix(\"a\")][assembly: Ankus.PgGucPrefix(\"A\")][assembly: Ankus.PgGucPrefix(\"a\")]";
        const string second = "[assembly: Ankus.PgGucPrefix(\"A\")][assembly: Ankus.PgGucPrefix(\"a\")]";
        (Compilation left, ImmutableArray<Diagnostic> leftDiagnostics) = Generate(first);
        (Compilation right, ImmutableArray<Diagnostic> rightDiagnostics) = Generate(second);
        AssertGucCompilation(left, leftDiagnostics);
        AssertGucCompilation(right, rightDiagnostics);
        foreach (string key in new[] { "Ankus.NativeSource", "Ankus.Sql", "Ankus.Exports", "Ankus.Relocatable" })
        {
            Assert.AreEqual(ManifestValue(left, key), ManifestValue(right, key));
        }

        Assert.AreSequenceEqual(["ankus_reserve_guc_prefix(\"\\101\");", "ankus_reserve_guc_prefix(\"\\141\");"],
            ManifestValue(left, "Ankus.NativeSource").Split('\n').Select(static line => line.Trim())
                .Where(static line => line.StartsWith("ankus_reserve_guc_prefix(\"", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Settings adopt placeholders before prefix cleanup, and managed initialization observes the reserved native state.
    /// </summary>
    [TestMethod]
    public void GucPrefixRegistrationRunsAfterAllSettingsBeforeInitialization()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgGucPrefix("a")]
            [assembly: Ankus.PgGucPrefix("b")]
            public static partial class Settings
            {
                [Ankus.PgGucInt("a.value", 42, "Value")] public static partial int First { get; }
                [Ankus.PgGucBool("b.value", true, "Value")] public static partial bool Second { get; }
                [Ankus.PgInitialize] public static void Initialize()
                {
                    if (First != 42)
                    {
                        throw new System.InvalidOperationException("Unexpected initialized setting.");
                    }
                }

                [Ankus.PgFunction] public static int Read() => First;
            }
            """);
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        string loader = native[native.IndexOf("PGDLLEXPORT void _PG_init(void)\n", StringComparison.Ordinal)..];
        string[] registration = [.. loader.Split('\n').Where(static line => line.Contains("ankus_guc_register(&", StringComparison.Ordinal))];
        Assert.HasCount(2, registration);
        int firstPrefix = loader.IndexOf("ankus_reserve_guc_prefix(\"\\141\");", StringComparison.Ordinal);
        int lastPrefix = loader.IndexOf("ankus_reserve_guc_prefix(\"\\142\");", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, firstPrefix);
        Assert.IsGreaterThan(firstPrefix, lastPrefix);
        foreach (string line in registration)
        {
            Assert.IsLessThan(firstPrefix, loader.IndexOf(line, StringComparison.Ordinal));
        }

        Assert.IsGreaterThan(lastPrefix, loader.IndexOf("ANKUS_MANAGED_INVOKE(status, error, ankus_managed_", StringComparison.Ordinal));
        Assert.ContainsSingle(ManifestValue(compilation, "Ankus.Exports").Split('\n').Where(static item => item == "_PG_init"));
        Assert.Contains("CREATE FUNCTION \"read\"()\nRETURNS integer", InstallationBody(compilation).ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// Prefix text conversion owns its allocation through native errors and rejects ambiguous shared-preload encoding.
    /// </summary>
    [TestMethod]
    public void GucPrefixNativeEncodingHasOwnedCleanupAndPostmasterGuard()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("[assembly: Ankus.PgGucPrefix(\"é\")]");
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        int guard = native.IndexOf("if (IsPostmasterEnvironment && !IsUnderPostmaster)", StringComparison.Ordinal);
        int conversion = native.IndexOf("char *prefix = pg_any_to_server(utf8, strlen(utf8), PG_UTF8);", StringComparison.Ordinal);
        int call = native.IndexOf("MarkGUCPrefixReserved(prefix);", StringComparison.Ordinal);
        Assert.IsGreaterThan(guard, conversion);
        Assert.IsGreaterThan(conversion, call);
        Assert.Contains("Ankus shared-preload configuration prefixes must be ASCII", native);
        Assert.Contains("""
                PG_FINALLY();
                {
                    if (prefix != utf8)
                        pfree(prefix);
                }
                PG_END_TRY();
            """.ReplaceLineEndings("\n"), native);
    }

    /// <summary>
    /// Libraries without prefix attributes do not emit an unused native helper or reserve their setting prefixes implicitly.
    /// </summary>
    [TestMethod]
    public void GucPrefixReservationRequiresAnExplicitAttribute()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static partial class Settings
            {
                [Ankus.PgGucInt("a.value", 42, "Value")] public static partial int Value { get; }
            }
            """);
        AssertGucCompilation(compilation, diagnostics);
        Assert.DoesNotContain("ankus_reserve_guc_prefix", ManifestValue(compilation, "Ankus.NativeSource"));
        Assert.DoesNotContain("MarkGUCPrefixReserved", ManifestValue(compilation, "Ankus.NativeSource"));
    }
}
