using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Disabled nullable annotations cannot silently select a required SQL input contract.
    /// </summary>
    /// <param name="type">The SQL reference type whose declaration is ambiguous.</param>
    /// <param name="attribute">The ordinary function, operator or cast declaration.</param>
    [TestMethod]
    [DataRow("string", "Ankus.PgFunction")]
    [DataRow("byte[]", "Ankus.PgFunction")]
    [DataRow("string[]", "Ankus.PgFunction")]
    [DataRow("Ankus.PgArray<string>", "Ankus.PgFunction")]
    [DataRow("Ankus.PgArrayView<string>", "Ankus.PgFunction")]
    [DataRow("string", "Ankus.PgFunction(NullInput = Ankus.PgNullInput.Strict)")]
    [DataRow("string", "Ankus.PgCast")]
    [DataRow("string", "Ankus.PgOperator(\"#@\")")]
    public void ObliviousSqlParametersReportTheirType(string type, string attribute)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            #nullable disable
            public static class Functions
            {
                [{{attribute}}]
                public static int Length({{type}} value) => 1;
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS024", diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(type, diagnostic.Location.SourceTree!.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan));
        Assert.Contains("'value'", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("https://ankus.net/function-declarations/#sql-nullability", diagnostic.Descriptor.HelpLinkUri);
        Assert.DoesNotContain("CREATE FUNCTION", ManifestValue(compilation, "Ankus.Sql"));
    }

    /// <summary>
    /// Scalar results, set elements and table columns must declare their own SQL nullability.
    /// </summary>
    /// <param name="type">The return type containing an ambiguous SQL reference.</param>
    /// <param name="count">The number of ambiguous SQL result columns.</param>
    [TestMethod]
    [DataRow("string", 1)]
    [DataRow("byte[]", 1)]
    [DataRow("System.Collections.Generic.IEnumerable<string>", 1)]
    [DataRow("System.Collections.Generic.IEnumerable<(int Id, string Name)>", 1)]
    [DataRow("System.Collections.Generic.IEnumerable<(string First, string Last)>", 2)]
    public void ObliviousSqlResultsCannotGenerateRequiredConversions(string type, int count)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            #nullable disable
            public static class Functions
            {
                [Ankus.PgFunction]
                public static {{type}} Read() => throw new System.NotSupportedException();
            }
            """);
        Assert.HasCount(count, diagnostics);
        foreach (Diagnostic diagnostic in diagnostics)
        {
            Assert.AreEqual("ANKUS024", diagnostic.Id);
            Assert.AreEqual(type, diagnostic.Location.SourceTree!.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan));
            Assert.Contains("'Read result'", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        }

        Assert.DoesNotContain("CREATE FUNCTION", ManifestValue(compilation, "Ankus.Sql"));
    }

    /// <summary>
    /// An aggregate callback cannot infer STRICT from an oblivious state or input reference.
    /// </summary>
    /// <param name="state">The ambiguous state type.</param>
    [TestMethod]
    [DataRow("string")]
    [DataRow("Ankus.PgAggregateState<int>")]
    public void ObliviousAggregateStatesRequireAnnotations(string state)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            #nullable disable
            [Ankus.PgAggregate]
            public sealed class Values : Ankus.IPgAggregate<{{state}},int>
            {
                public static {{state}} Transition(Ankus.PgAggregateContext context,{{state}} state, int value) => state;
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS024", diagnostic.Id);
        Assert.Contains("Transition result", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.DoesNotContain("CREATE AGGREGATE", ManifestValue(compilation, "Ankus.Sql"));
        Assert.DoesNotContain("CREATE FUNCTION", ManifestValue(compilation, "Ankus.Sql"));
    }

    /// <summary>
    /// A nullable-aware use site cannot conceal an oblivious SQL element captured by a type alias.
    /// </summary>
    /// <param name="container">The SQL array wrapper.</param>
    [TestMethod]
    [DataRow("Ankus.PgArray<string>")]
    [DataRow("Ankus.PgArrayView<string>")]
    public void ObliviousArrayAliasElementsRemainAmbiguous(string container)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            #nullable disable
            using Values = {{container}};
            #nullable enable
            public static class Functions
            {
                [Ankus.PgFunction]
                public static int Count(Values values) => 1;
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS024", diagnostic.Id);
        Assert.Contains("reference type 'string'", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.DoesNotContain("CREATE FUNCTION", ManifestValue(compilation, "Ankus.Sql"));
    }

    /// <summary>
    /// SQL state nullability does not impose a contract on unrelated managed payload members.
    /// </summary>
    [TestMethod]
    public void ManagedAggregatePayloadMembersRemainOutsideTheSqlNullContract()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            #nullable disable
            public sealed class Payload
            {
                public string Name
                {
                    get;
                    set;
                } = "value";
            }
            #nullable enable
            [Ankus.PgAggregate]
            public sealed class Values : Ankus.IPgAggregate<Ankus.PgAggregateState<Payload>?,int>,
                Ankus.IPgFinalizingAggregate<Ankus.PgAggregateState<Payload>?,System.ValueTuple,int>
            {
                public static Ankus.PgAggregateState<Payload> Transition(Ankus.PgAggregateContext context,Ankus.PgAggregateState<Payload>? state, int value)
                    => state ?? new(new Payload());

                public static int Final(Ankus.PgAggregateContext context,Ankus.PgAggregateState<Payload>? state,System.ValueTuple direct) => state?.Value.Name.Length ?? 0;
            }
            """);
        AssertAggregateCompilation(compilation, diagnostics);
        Assert.Contains("STYPE = internal", ManifestValue(compilation, "Ankus.Sql"));
    }

    /// <summary>
    /// Value-only SQL and injected contexts remain valid with annotations disabled.
    /// </summary>
    /// <param name="declaration">A declaration whose SQL null contract is unambiguous.</param>
    [TestMethod]
    [DataRow("[Ankus.PgFunction] public static int Echo(int value, Ankus.PgMemoryContext context) => value;")]
    [DataRow("[Ankus.PgFunction] public static int? Echo(int? value, Ankus.PgFunctionContext context) => value;")]
    [DataRow("""
        [Ankus.PgFunction]
        public static System.Collections.Generic.IEnumerable<int?> Echo(int value)
        {
            yield return value;
        }
        """)]
    public void ValueContractsDoNotRequireReferenceAnnotations(string declaration)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("#nullable disable\npublic static class Functions { " + declaration + " }");
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        Assert.Contains("CREATE FUNCTION", ManifestValue(compilation, "Ankus.Sql"));
    }

    /// <summary>
    /// Enabling annotations locally restores exact nullable and required SQL inference without requiring nullable warnings.
    /// </summary>
    /// <param name="type">The explicit reference contract.</param>
    /// <param name="strict">Whether the SQL function must skip null inputs.</param>
    [TestMethod]
    [DataRow("string", true)]
    [DataRow("string?", false)]
    [DataRow("byte[]", true)]
    [DataRow("byte[]?", false)]
    public void ExplicitSqlAnnotationsDetermineStrictness(string type, bool strict)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            #nullable disable
            #nullable enable annotations
            public static class Functions
            {
                [Ankus.PgFunction]
                public static {{type}} Echo({{type}} value) => value;
            }
            """);
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        string sql = ManifestValue(compilation, "Ankus.Sql");
        Assert.Contains(strict ? " STRICT " : " CALLED ON NULL INPUT ", sql);
    }
}
