using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies unsafe raw interpolation is rejected against real static and scoped SPI APIs.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed partial class SpiInterpolationAnalyzerTests(TestContext context)
{
    /// <summary>
    /// Every raw SQL entry point rejects a runtime value formatted directly into its command text.
    /// </summary>
    /// <param name="expression">The consumer invocation.</param>
    [TestMethod]
    [DataRow("Spi.Execute($\"SELECT {value}\")")]
    [DataRow("Spi.ExecuteScalar<string>($\"SELECT {value}\")")]
    [DataRow("Spi.ExecuteScalars<string, string>($\"SELECT {value}\")")]
    [DataRow("Spi.Query($\"SELECT {value}\")")]
    [DataRow("Spi.Select($\"SELECT {value}\")")]
    [DataRow("Spi.SelectRaw($\"SELECT {value}\")")]
    [DataRow("Spi.QueryRaw($\"SELECT {value}\")")]
    [DataRow("Spi.Explain($\"SELECT {value}\")")]
    [DataRow("Spi.OpenCursor($\"SELECT {value}\")")]
    [DataRow("Spi.Prepare($\"SELECT {value}\")")]
    [DataRow("Spi.PrepareWithTypeOids($\"SELECT {value}\")")]
    [DataRow("session.Execute($\"SELECT {value}\")")]
    [DataRow("session.ExecuteScalar<string>($\"SELECT {value}\")")]
    [DataRow("session.ExecuteScalars<string, string, string>($\"SELECT {value}\")")]
    [DataRow("session.Query($\"SELECT {value}\")")]
    [DataRow("session.Select($\"SELECT {value}\")")]
    [DataRow("session.SelectRaw($\"SELECT {value}\")")]
    [DataRow("session.QueryRaw($\"SELECT {value}\")")]
    [DataRow("session.Explain($\"SELECT {value}\")")]
    [DataRow("session.OpenCursor($\"SELECT {value}\")")]
    [DataRow("session.Prepare($\"SELECT {value}\")")]
    [DataRow("session.PrepareWithTypeOids($\"SELECT {value}\")")]
    [DataRow("Spi.Query(limit: 1, commandText: $\"SELECT {value}\", readOnly: false)")]
    [DataRow("Spi.Connect(active => active.Execute($\"SELECT {value}\"))")]
    public async Task RawInterpolationIsAnError(string expression)
        => await AssertRejected(expression, "$\"SELECT {value}\"");

    /// <summary>
    /// Casts, concatenation, formatting and raw literals cannot hide direct interpolation.
    /// </summary>
    /// <param name="expression">The command expression.</param>
    [TestMethod]
    [DataRow("(string)$\"SELECT {value}\"")]
    [DataRow("\"SELECT \" + $\"{value}\"")]
    [DataRow("$\"SELECT {value}\" + \";\"")]
    [DataRow("$\"SELECT {number:000}\"")]
    [DataRow("$\"SELECT {number,10}\"")]
    [DataRow("$\"\"\"SELECT {value}\"\"\"")]
    [DataRow("number == 0 ? $\"SELECT {value}\" : \"SELECT 42\"")]
    [DataRow("number == 0 ? \"SELECT 42\" : $\"SELECT {value}\"")]
    [DataRow("value ?? $\"SELECT {value}\"")]
    [DataRow("number switch { 0 => $\"SELECT {value}\", _ => \"SELECT 42\" }")]
    [DataRow("$\"SELECT {value}\" ?? \"SELECT 42\"")]
    [DataRow("string.Format(\"SELECT {0}\", value)")]
    [DataRow("string.Format(System.Globalization.CultureInfo.InvariantCulture, \"SELECT {0}\", value)")]
    [DataRow("string.Format(\"SELECT {0} {1}\", new object[] { Spi.QuoteIdentifier(value), value })")]
    [DataRow("string.Format(System.Globalization.CultureInfo.CurrentCulture, \"SELECT {0}\", Spi.QuoteLiteral(value))")]
    [DataRow("$\"SELECT {Spi.QuoteIdentifier(value)}, {value}\"")]
    [DataRow("$\"SELECT {Spi.QuoteIdentifier(value),10}\"")]
    [DataRow("$\"SELECT {Spi.QuoteIdentifier(value):X}\"")]
    [DataRow("$\"SELECT {(number == 0 ? Spi.QuoteLiteral(value) : value)}\"")]
    [DataRow("$\"SELECT {(value ?? Spi.QuoteLiteral(\"fallback\"))}\"")]
    public async Task ExpressionWrappersRetainTheError(string expression)
        => await AssertRejected("Spi.Execute(" + expression + ")", expression);

    /// <summary>
    /// Literal SQL, explicit parameters and Spi.Sql retain their supported overloads without false positives.
    /// </summary>
    /// <param name="expression">The valid consumer expression.</param>
    [TestMethod]
    [DataRow("Spi.Execute(\"SELECT 42\")")]
    [DataRow("Spi.Execute($\"SELECT 42\")")]
    [DataRow("Spi.Execute($\"SELECT '{\"safe\"}'\")")]
    [DataRow("Spi.Execute(value)")]
    [DataRow("Spi.Execute(\"SELECT $1\", SpiParameter.Create(value))")]
    [DataRow("Spi.Execute(Spi.Sql($\"SELECT {value}\"))")]
    [DataRow("session.Execute(Spi.Sql($\"SELECT {value}\"))")]
    [DataRow("Spi.OpenCursor(Spi.Sql($\"SELECT {value}\"), readOnly: false)")]
    [DataRow("session.Query(Spi.Sql($\"SELECT {value}\"), readOnly: true)")]
    [DataRow("Spi.Prepare(\"SELECT $1\", typeof(string))")]
    [DataRow("Spi.ExecuteScalar<int>(\"SELECT 42::\" + Spi.QuoteQualifiedIdentifier(\"pg_catalog\", \"int4\"))")]
    [DataRow("Spi.Execute($\"SELECT {Spi.QuoteLiteral(value)}\")")]
    [DataRow("Spi.Execute($\"SELECT count(*) FROM {Spi.QuoteIdentifier(value)}\")")]
    [DataRow("Spi.Execute($\"SELECT count(*) FROM {Spi.QuoteQualifiedIdentifier(\"public\", value)}\")")]
    [DataRow("Spi.Execute($\"SELECT {Spi.QuoteLiteral($\"prefix {value}\")}\")")]
    [DataRow("Spi.Execute(number == 0 ? $\"SELECT {Spi.QuoteLiteral(value)}\" : \"SELECT 42\")")]
    [DataRow("Spi.Execute(number == 0 ? $\"SELECT '{\"safe\"}'\" : \"SELECT 42\")")]
    [DataRow("Spi.Execute(string.Format(\"SELECT {0}\", Spi.QuoteLiteral(value)))")]
    [DataRow("Spi.Execute(string.Format(System.Globalization.CultureInfo.InvariantCulture, \"SELECT {0}\", Spi.QuoteLiteral(value)))")]
    [DataRow("Spi.Execute(string.Format(null, \"SELECT {0}\", Spi.QuoteLiteral(value)))")]
    [DataRow("Spi.Execute(string.Format(\"SELECT {0} {1}\", new object[] { Spi.QuoteLiteral(value), \"AS label\" }))")]
    [DataRow("Spi.Execute($\"SELECT {(number == 0 ? Spi.QuoteLiteral(value) : Spi.QuoteLiteral(\"fallback\"))}\")")]
    [DataRow("Spi.Execute($\"SELECT {(Spi.QuoteLiteral(value) ?? Spi.QuoteLiteral(\"fallback\"))}\")")]
    [DataRow("Spi.Execute($\"SELECT {(number switch { 0 => Spi.QuoteLiteral(value), _ => throw new System.InvalidOperationException() })}\")")]
    public async Task SafeCommandsRemainValid(string expression)
        => Assert.IsEmpty(await Analyze(expression));

    /// <summary>
    /// The documented quoting pattern keeps unchanged identifier and literal locals valid.
    /// </summary>
    /// <param name="initializer">The actual runtime quoting call.</param>
    [TestMethod]
    [DataRow("Spi.QuoteIdentifier(value)")]
    [DataRow("(string)Spi.QuoteQualifiedIdentifier(null, value)")]
    [DataRow("(Spi.QuoteLiteral(value))")]
    [DataRow("number == 0 ? Spi.QuoteLiteral(value) : Spi.QuoteLiteral(\"fallback\")")]
    public async Task UnchangedQuotedLocalsRemainValid(string initializer)
        => Assert.IsEmpty(await Analyze("Spi.Execute($\"SELECT {fragment}\")", before: "string fragment = " + initializer + ";"));

    /// <summary>
    /// Replacing or aliasing a quoted local cannot exempt unescaped values from the raw interpolation error.
    /// </summary>
    /// <param name="change">The local replacement or reference escape.</param>
    [TestMethod]
    [DataRow("fragment = value;")]
    [DataRow("fragment += value;")]
    [DataRow("fragment ??= value;")]
    [DataRow("(fragment, number) = (value, 1);")]
    [DataRow("ref string alias = ref fragment; alias = value;")]
    [DataRow("void Replace() { fragment = value; } Replace();")]
    [DataRow("System.Action replace = () => fragment = value; replace();")]
    public async Task ModifiedQuotedLocalsAreRejected(string change)
    {
        Diagnostic error = Assert.ContainsSingle(await Analyze("Spi.Execute($\"SELECT {fragment}\")",
            before: "string fragment = Spi.QuoteIdentifier(value); " + change));
        Assert.AreEqual("ANKUS044", error.Id);
        Assert.AreEqual("$\"SELECT {fragment}\"", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// A helper with a familiar name has no authority to mark raw values as PostgreSQL fragments.
    /// </summary>
    [TestMethod]
    public async Task UnrelatedQuoteHelperDoesNotExemptRawInterpolation()
    {
        Diagnostic error = Assert.ContainsSingle(await Analyze("Spi.Execute($\"SELECT {Other.QuoteIdentifier(value)}\")",
            "internal static class Other { internal static string QuoteIdentifier(string value) => value; }"));
        Assert.AreEqual("ANKUS044", error.Id);
        Assert.AreEqual("$\"SELECT {Other.QuoteIdentifier(value)}\"", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// Similar names outside the actual SPI type must not become PostgreSQL compiler errors.
    /// </summary>
    [TestMethod]
    public async Task UnrelatedCommandTextOverloadsRemainValid()
        => Assert.IsEmpty(await Analyze("Other.Execute($\"SELECT {value}\")",
            "internal static class Other { internal static void Execute(string commandText) { } }"));

    /// <summary>
    /// Asserts the exact diagnostic and current expression location rather than merely counting errors.
    /// </summary>
    /// <param name="invocation">The raw SQL invocation.</param>
    /// <param name="expression">The unsafe argument's exact source text.</param>
    private async Task AssertRejected(string invocation, string expression)
    {
        Diagnostic error = Assert.ContainsSingle(await Analyze(invocation));
        Assert.AreEqual("ANKUS044", error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(expression, error.Location.SourceTree!.GetText(context.CancellationToken)
            .ToString(error.Location.SourceSpan));
        Assert.Contains("Spi.Sql", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("https://ankus.net/spi/#parameterized-interpolation", error.Descriptor.HelpLinkUri);
    }

    /// <summary>
    /// Compiles an actual Runtime consumer and invokes the shipped compiler analyzer.
    /// </summary>
    /// <param name="expression">The body expression.</param>
    /// <param name="extra">Optional unrelated declarations.</param>
    /// <param name="before">Declarations and writes preceding the SQL call in the same method.</param>
    /// <returns>The analyzer's diagnostics after requiring a valid C# compilation.</returns>
    private async Task<ImmutableArray<Diagnostic>> Analyze(string expression, string extra = "", string before = "")
    {
        string source = "using Ankus; internal static class Consumer { internal static void Invoke(string value, int number, SpiSession session) { "
            + before + " " + expression + "; } } " + extra;
        CSharpCompilation compilation = CSharpCompilation.Create("SpiAnalyzerConsumer",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14), path: "Consumer.cs",
                cancellationToken: context.CancellationToken)], CompilationReferences.Platform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken));
        var analysis = new CompilationWithAnalyzers(compilation, [new SpiInterpolationAnalyzer()],
            new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
                concurrentAnalysis: true, logAnalyzerExecutionTime: false));
        return await analysis.GetAnalyzerDiagnosticsAsync(context.CancellationToken);
    }
}
