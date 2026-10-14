using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Related attributes expose one backing function and one unmanaged callback regardless of attribute order.
    /// </summary>
    /// <param name="attributes">The function, operator, and cast attributes on one method.</param>
    /// <param name="operators">The expected number of operator declarations.</param>
    /// <param name="casts">The expected number of cast declarations.</param>
    [TestMethod]
    [DataRow("[Ankus.PgOperator(\"@+\")]", 1, 0)]
    [DataRow("[Ankus.PgFunction][Ankus.PgOperator(\"@+\")]", 1, 0)]
    [DataRow("[Ankus.PgOperator(\"@+\")][Ankus.PgFunction]", 1, 0)]
    [DataRow("[Ankus.PgCast]", 0, 1)]
    [DataRow("[Ankus.PgFunction][Ankus.PgCast]", 0, 1)]
    [DataRow("[Ankus.PgCast][Ankus.PgFunction]", 0, 1)]
    [DataRow("[Ankus.PgOperator(\"@+\")][Ankus.PgCast]", 1, 1)]
    [DataRow("[Ankus.PgCast][Ankus.PgOperator(\"@+\")]", 1, 1)]
    [DataRow("[Ankus.PgFunction][Ankus.PgOperator(\"@+\")][Ankus.PgCast]", 1, 1)]
    [DataRow("[Ankus.PgOperator(\"@+\")][Ankus.PgCast][Ankus.PgFunction]", 1, 1)]
    [DataRow("[Ankus.PgCast][Ankus.PgFunction][Ankus.PgOperator(\"@+\")]", 1, 1)]
    public void OperatorCastAttributesShareOneBackingFunction(string attributes, int operators, int casts)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static class Functions { " + attributes + " public static string Render(int value, int modifier) => value.ToString(); }");
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        string[] statements = OperatorCastStatements(compilation);
        Assert.HasCount(1 + operators + casts, statements);
        Assert.HasCount(1, statements.Where(static sql => sql.StartsWith("CREATE FUNCTION", StringComparison.Ordinal)));
        Assert.HasCount(operators, statements.Where(static sql => sql.StartsWith("CREATE OPERATOR", StringComparison.Ordinal)));
        Assert.HasCount(casts, statements.Where(static sql => sql.StartsWith("CREATE CAST", StringComparison.Ordinal)));
        Assert.StartsWith("CREATE FUNCTION \"render\"(\"value\" integer, \"modifier\" integer) RETURNS text AS ", statements[0]);
        INamedTypeSymbol? dispatchers = compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers");
        Assert.IsNotNull(dispatchers);
        Assert.HasCount(1, dispatchers.GetMembers().OfType<IMethodSymbol>().Where(static method => method.GetAttributes().Any(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute")));
        using var assembly = new MemoryStream();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(assembly, cancellationToken: context.CancellationToken);
        Assert.IsEmpty(emitted.Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.IsTrue(emitted.Success);
    }

    /// <summary>
    /// Operators preserve argument position, result contracts, and SQL NULL policy for unary and mixed binary declarations.
    /// </summary>
    /// <param name="method">The complete attributed operator method.</param>
    /// <param name="declaration">The exact normalized operator DDL.</param>
    /// <param name="nullInput">The expected backing function null-input policy.</param>
    [TestMethod]
    [DataRow("[Ankus.PgOperator(\"!\")] public static int Negate(int value) => -value;", "CREATE OPERATOR ! (FUNCTION = \"negate\", RIGHTARG = integer);", "STRICT")]
    [DataRow("[Ankus.PgOperator(\"@+\")] public static string Append(string left, int right) => left + right;", "CREATE OPERATOR @+ (FUNCTION = \"append\", LEFTARG = text, RIGHTARG = integer);", "STRICT")]
    [DataRow("[Ankus.PgOperator(\"@+\")] public static string Append(int left, string right) => left + right;", "CREATE OPERATOR @+ (FUNCTION = \"append\", LEFTARG = integer, RIGHTARG = text);", "STRICT")]
    [DataRow("[Ankus.PgOperator(\"@=\")] public static bool? Equal(int? left, long? right) => left == right;", "CREATE OPERATOR @= (FUNCTION = \"equal\", LEFTARG = integer, RIGHTARG = bigint);", "CALLED ON NULL INPUT")]
    [DataRow("[Ankus.PgOperator(\"@+\")] public static int? Add(int left, int? right) => left + right;", "CREATE OPERATOR @+ (FUNCTION = \"add\", LEFTARG = integer, RIGHTARG = integer);", "CALLED ON NULL INPUT")]
    public void OperatorSignaturesPreserveOperandOrderAndNullPolicy(string method, string declaration, string nullInput)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { " + method + " }");
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        string[] statements = OperatorCastStatements(compilation);
        Assert.HasCount(2, statements);
        Assert.Contains(" PARALLEL UNSAFE " + nullInput + " SECURITY INVOKER ", statements[0]);
        Assert.AreEqual(declaration, statements[1]);
        Assert.AreEqual("true", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Operator planning options preserve quoted schema references and augment an explicitly configured backing function.
    /// </summary>
    [TestMethod]
    public void OperatorOptionsPreserveQualifiedReferencesAndFunctionOptions()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgSchema("Operators \" Here")]
            public static class Functions
            {
                [Ankus.PgFunction(Name = "equal_value", Volatility = Ankus.PgVolatility.Immutable,
                    ParallelSafety = Ankus.PgParallelSafety.Safe, Cost = 7)]
                [Ankus.PgOperator("@=", Commutator = "Operators \" Here.@=", Negator = "Other Schema.@<>",
                    RestrictionEstimator = "pg_catalog.eqsel", JoinEstimator = "pg_catalog.eqjoinsel", Hashes = true, Merges = true)]
                public static bool Same(int left, int right) => left == right;
            }
            """);
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        string[] statements = OperatorCastStatements(compilation);
        Assert.HasCount(3, statements);
        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"Operators \"\" Here\";", statements[0]);
        Assert.StartsWith("CREATE FUNCTION \"Operators \"\" Here\".\"equal_value\"(", statements[1]);
        Assert.Contains(" IMMUTABLE PARALLEL SAFE STRICT SECURITY INVOKER NOT LEAKPROOF COST 7;", statements[1]);
        Assert.AreEqual("CREATE OPERATOR \"Operators \"\" Here\".@= (FUNCTION = \"Operators \"\" Here\".\"equal_value\", LEFTARG = integer, RIGHTARG = integer, " +
            "COMMUTATOR = OPERATOR(\"Operators \"\" Here\".@=), NEGATOR = OPERATOR(\"Other Schema\".@<>), " +
            "RESTRICT = \"pg_catalog\".\"eqsel\", JOIN = \"pg_catalog\".\"eqjoinsel\", HASHES, MERGES);", statements[2]);
        Assert.AreEqual("false", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Unary boolean operators support negation and restriction estimation without inventing a left operand.
    /// </summary>
    [TestMethod]
    public void UnaryBooleanOperatorsAllowNegatorAndRestrictionEstimator()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgOperator("?", Negator = "!", RestrictionEstimator = "booltestsel")]
                public static bool? Test(int? value) => value.HasValue;
            }
            """);
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        Assert.AreEqual("CREATE OPERATOR ? (FUNCTION = \"test\", RIGHTARG = integer, NEGATOR = OPERATOR(!), RESTRICT = \"booltestsel\");",
            OperatorCastStatements(compilation)[1]);
    }

    /// <summary>
    /// Arithmetic commutators do not require a boolean result, and unqualified references share a fixed operator schema.
    /// </summary>
    [TestMethod]
    public void OperatorUnqualifiedCommutatorUsesFixedSchemaForNonBooleanResult()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgFunction(Schema = "fixed")]
                [Ankus.PgOperator("@+", Commutator = "@+")]
                public static int Add(int left, int right) => left + right;
            }
            """);
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        Assert.AreEqual("CREATE OPERATOR \"fixed\".@+ (FUNCTION = \"fixed\".\"add\", LEFTARG = integer, RIGHTARG = integer, COMMUTATOR = OPERATOR(\"fixed\".@+));",
            OperatorCastStatements(compilation)[1]);
    }

    /// <summary>
    /// The nearest schema and explicit function override determine operator placement without conflating otherwise identical signatures.
    /// </summary>
    [TestMethod]
    public void OperatorSchemasFollowBackingFunctionPlacement()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgSchema("outer")]
            public static class Outer
            {
                public static class Nested { [Ankus.PgOperator("@+")] public static int Add(int a, int b) => a + b; }
                [Ankus.PgSchema("inner")]
                public static class Inner { [Ankus.PgOperator("@+")] public static int Add(int a, int b) => a + b; }
                [Ankus.PgFunction(Schema = "existing")][Ankus.PgOperator("@+")]
                public static int Add(int a, int b) => a + b;
            }
            """);
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        string[] operators = [.. OperatorCastStatements(compilation).Where(static sql => sql.StartsWith("CREATE OPERATOR", StringComparison.Ordinal))];
        Assert.HasCount(3, operators);
        Assert.Contains("CREATE OPERATOR \"outer\".@+ (FUNCTION = \"outer\".\"add\", LEFTARG = integer, RIGHTARG = integer);", operators);
        Assert.Contains("CREATE OPERATOR \"inner\".@+ (FUNCTION = \"inner\".\"add\", LEFTARG = integer, RIGHTARG = integer);", operators);
        Assert.Contains("CREATE OPERATOR \"existing\".@+ (FUNCTION = \"existing\".\"add\", LEFTARG = integer, RIGHTARG = integer);", operators);
        Assert.DoesNotContain("CREATE SCHEMA IF NOT EXISTS \"existing\";", OperatorCastStatements(compilation));
    }

    /// <summary>
    /// PostgreSQL's complete punctuation alphabet and nonstandard suffix rule remain accepted operator names.
    /// </summary>
    /// <param name="name">The operator token.</param>
    [TestMethod]
    [DataRow("~")]
    [DataRow("!")]
    [DataRow("@")]
    [DataRow("#")]
    [DataRow("^")]
    [DataRow("&")]
    [DataRow("|")]
    [DataRow("`")]
    [DataRow("?")]
    [DataRow("+")]
    [DataRow("-")]
    [DataRow("*")]
    [DataRow("/")]
    [DataRow("%")]
    [DataRow("<")]
    [DataRow(">")]
    [DataRow("=")]
    [DataRow("==>")]
    [DataRow("?-")]
    [DataRow("%+")]
    [DataRow("~!@#^&|`?+-*/%<>=")]
    public void ValidOperatorNamesPreserveEveryToken(string name)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static class Functions { [Ankus.PgOperator(" + SymbolDisplay.FormatLiteral(name, true) + ")] public static int Apply(int value) => value; }");
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        Assert.AreEqual("CREATE OPERATOR " + name + " (FUNCTION = \"apply\", RIGHTARG = integer);", OperatorCastStatements(compilation)[1]);
    }

    /// <summary>
    /// Operator identifiers are accepted immediately below and at PostgreSQL's length limit and rejected immediately above it.
    /// </summary>
    /// <param name="length">The number of ASCII punctuation bytes.</param>
    [TestMethod]
    [DataRow(62)]
    [DataRow(63)]
    [DataRow(64)]
    public void OperatorNameLengthUsesPostgresBoundary(int length)
    {
        string name = new('?', length);
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static class Functions { [Ankus.PgOperator(\"" + name + "\")] public static int Apply(int value) => value; }");
        if (length == 64)
        {
            Assert.AreEqual("ANKUS065", Assert.ContainsSingle(diagnostics).Id);
        }
        else
        {
            AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
            Assert.AreEqual("CREATE OPERATOR " + name + " (FUNCTION = \"apply\", RIGHTARG = integer);", OperatorCastStatements(compilation)[1]);
        }
    }

    /// <summary>
    /// Invalid punctuation, SQL-comment starts, and standard-operator suffixes cannot enter installation SQL.
    /// </summary>
    /// <param name="name">An invalid PostgreSQL operator token.</param>
    [TestMethod]
    [DataRow(default(string))]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("a")]
    [DataRow("1")]
    [DataRow("é")]
    [DataRow("🐘")]
    [DataRow("a\0b")]
    [DataRow("\ud800")]
    [DataRow("@;SELECT")]
    [DataRow("\"+\"")]
    [DataRow("schema.+")]
    [DataRow("--")]
    [DataRow("@--?")]
    [DataRow("/*")]
    [DataRow("@/*?")]
    [DataRow("++")]
    [DataRow("+-")]
    [DataRow("=-")]
    [DataRow("/+")]
    [DataRow("=>")]
    public void InvalidOperatorNamesAreDiagnosed(string? name)
    {
        string literal = name is null ? "null!" : SymbolDisplay.FormatLiteral(name, true);
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static class Functions { [Ankus.PgOperator(" + literal + ")] public static int Apply(int value) => value; }");
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS065", diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.IsTrue(diagnostic.Location.IsInSource);
    }

    /// <summary>
    /// The parser's not-equal alias is normalized consistently in definitions and related operator references.
    /// </summary>
    [TestMethod]
    public void OperatorNotEqualAliasesNormalizeInDeclarationsAndReferences()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgOperator("!=", Commutator = "!=", Negator = "other.!=")]
                public static bool Different(int left, int right) => left != right;
            }
            """);
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        Assert.AreEqual("CREATE OPERATOR <> (FUNCTION = \"different\", LEFTARG = integer, RIGHTARG = integer, COMMUTATOR = OPERATOR(<>), NEGATOR = OPERATOR(\"other\".<>));",
            OperatorCastStatements(compilation)[1]);
    }

    /// <summary>
    /// Operator declarations reject impossible operand counts, void results, and planner options incompatible with the signature.
    /// </summary>
    /// <param name="method">The invalid attributed operator method.</param>
    /// <param name="id">The specific contract diagnostic.</param>
    [TestMethod]
    [DataRow("[Ankus.PgOperator(\"+\")] public static int Apply() => 0;", "ANKUS066")]
    [DataRow("[Ankus.PgOperator(\"+\")] public static int Apply(int a, int b, int c) => 0;", "ANKUS066")]
    [DataRow("[Ankus.PgOperator(\"+\")] public static void Apply(int a) { }", "ANKUS068")]
    [DataRow("[Ankus.PgOperator(\"+\")] public static int Apply(params int[] a) => 0;", "ANKUS067")]
    [DataRow("[Ankus.PgOperator(\"?\", Commutator = \"?\")] public static bool Apply(int a) => true;", "ANKUS070")]
    [DataRow("[Ankus.PgOperator(\"?\", JoinEstimator = \"eqjoinsel\")] public static bool Apply(int a) => true;", "ANKUS070")]
    [DataRow("[Ankus.PgOperator(\"?\", Hashes = true)] public static bool Apply(int a) => true;", "ANKUS070")]
    [DataRow("[Ankus.PgOperator(\"?\", Merges = true)] public static bool Apply(int a) => true;", "ANKUS070")]
    [DataRow("[Ankus.PgOperator(\"+\", Negator = \"-\")] public static int Apply(int a, int b) => a;", "ANKUS071")]
    [DataRow("[Ankus.PgOperator(\"+\", RestrictionEstimator = \"eqsel\")] public static int Apply(int a, int b) => a;", "ANKUS071")]
    [DataRow("[Ankus.PgOperator(\"+\", JoinEstimator = \"eqjoinsel\")] public static int Apply(int a, int b) => a;", "ANKUS071")]
    [DataRow("[Ankus.PgOperator(\"+\", Hashes = true)] public static int Apply(int a, int b) => a;", "ANKUS071")]
    [DataRow("[Ankus.PgOperator(\"+\", Merges = true)] public static int Apply(int a, int b) => a;", "ANKUS071")]
    public void InvalidOperatorSignaturesAndPlannerOptionsAreDiagnosed(string method, string id)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { " + method + " }");
        Assert.AreEqual(id, Assert.ContainsSingle(diagnostics).Id);
    }

    /// <summary>
    /// Related operators and estimators accept only one optional schema and a valid terminal name.
    /// </summary>
    /// <param name="property">The reference property being validated.</param>
    /// <param name="reference">The invalid reference.</param>
    [TestMethod]
    [DataRow("Commutator", "")]
    [DataRow("Commutator", "schema.")]
    [DataRow("Commutator", ".+")]
    [DataRow("Commutator", "one.two.+")]
    [DataRow("Commutator", "schema.++")]
    [DataRow("Commutator", "schema.operand")]
    [DataRow("Commutator", "=>")]
    [DataRow("Negator", "")]
    [DataRow("Negator", "one.two.?")]
    [DataRow("Negator", "schema./*")]
    [DataRow("Negator", "schema.=>")]
    [DataRow("RestrictionEstimator", "")]
    [DataRow("RestrictionEstimator", "schema.")]
    [DataRow("RestrictionEstimator", ".eqsel")]
    [DataRow("RestrictionEstimator", "one.two.eqsel")]
    [DataRow("RestrictionEstimator", "bad\0name")]
    [DataRow("JoinEstimator", "")]
    [DataRow("JoinEstimator", "one.two.eqjoinsel")]
    [DataRow("JoinEstimator", "\ud800")]
    public void InvalidOperatorReferencesAreDiagnosed(string property, string reference)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static class Functions { [Ankus.PgOperator(\"@=\", " + property + " = " + SymbolDisplay.FormatLiteral(reference, true) +
            ")] public static bool Apply(int a, int b) => a == b; }");
        Assert.AreEqual(property is "Commutator" or "Negator" ? "ANKUS072" : "ANKUS073", Assert.ContainsSingle(diagnostics).Id);
    }

    /// <summary>
    /// An operator cannot name itself as its negator, including normalized inequality names and fixed schemas.
    /// </summary>
    /// <param name="attributes">The invalid operator and optional function schema.</param>
    [TestMethod]
    [DataRow("[Ankus.PgOperator(\"@=\", Negator = \"@=\")]")]
    [DataRow("[Ankus.PgOperator(\"!=\", Negator = \"<>\")]")]
    [DataRow("[Ankus.PgFunction(Schema = \"fixed\"), Ankus.PgOperator(\"@=\", Negator = \"fixed.@=\")]")]
    [DataRow("[Ankus.PgFunction(Schema = \"fixed\"), Ankus.PgOperator(\"@=\", Negator = \"@=\")]")]
    public void OperatorSelfNegatorsAreDiagnosed(string attributes)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { " + attributes +
            " public static bool Same(int a, int b) => a == b; }");
        Assert.AreEqual("ANKUS069", Assert.ContainsSingle(diagnostics).Id);
    }

    /// <summary>
    /// Operator references retain the operator byte limit and estimator/schema references use PostgreSQL's UTF-8 identifier limit.
    /// </summary>
    /// <param name="kind">The referenced terminal name or schema being constrained.</param>
    /// <param name="bytes">The UTF-8 byte length at or just beyond the identifier boundary.</param>
    [TestMethod]
    [DataRow("operator", 63)]
    [DataRow("operator", 64)]
    [DataRow("operator schema", 63)]
    [DataRow("operator schema", 64)]
    [DataRow("estimator", 63)]
    [DataRow("estimator", 64)]
    [DataRow("estimator schema", 63)]
    [DataRow("estimator schema", 64)]
    public void OperatorReferenceNamesRespectEncodedLengthLimits(string kind, int bytes)
    {
        string identifier = new('é', bytes / 2);
        if (bytes % 2 != 0)
        {
            identifier += 'x';
        }

        bool isOperator = kind.StartsWith("operator", StringComparison.Ordinal);
        string reference = kind switch
        {
            "operator" => new string('?', bytes),
            "operator schema" => identifier + ".@=",
            "estimator schema" => identifier + ".eqsel",
            _ => identifier,
        };
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static class Functions { [Ankus.PgOperator(\"@=\", " + (isOperator ? "Commutator" : "RestrictionEstimator") + " = " +
            SymbolDisplay.FormatLiteral(reference, true) + ")] public static bool Equal(int left, int right) => left == right; }");
        if (bytes == 64)
        {
            Assert.AreEqual(isOperator ? "ANKUS072" : "ANKUS073", Assert.ContainsSingle(diagnostics).Id);
        }
        else
        {
            AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
            string expected = kind switch
            {
                "operator" => "COMMUTATOR = OPERATOR(" + reference + ")",
                "operator schema" => "COMMUTATOR = OPERATOR(\"" + identifier + "\".@=)",
                "estimator schema" => "RESTRICT = \"" + identifier + "\".\"eqsel\"",
                _ => "RESTRICT = \"" + identifier + "\"",
            };
            Assert.Contains(expected, OperatorCastStatements(compilation)[1]);
        }
    }

    /// <summary>
    /// Cast context controls only the cast declaration while the backing function retains its complete SQL signature.
    /// </summary>
    /// <param name="attribute">The cast declaration attribute.</param>
    /// <param name="suffix">The expected context clause.</param>
    [TestMethod]
    [DataRow("[Ankus.PgCast]", "")]
    [DataRow("[Ankus.PgCast(Ankus.PgCastContext.Explicit)]", "")]
    [DataRow("[Ankus.PgCast(Ankus.PgCastContext.Assignment)]", " AS ASSIGNMENT")]
    [DataRow("[Ankus.PgCast(Ankus.PgCastContext.Implicit)]", " AS IMPLICIT")]
    public void CastContextsGenerateExactSql(string attribute, string suffix)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static class Functions { " + attribute + " public static string Render(int value) => value.ToString(); }");
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        string[] statements = OperatorCastStatements(compilation);
        Assert.HasCount(2, statements);
        Assert.StartsWith("CREATE FUNCTION \"render\"(\"value\" integer) RETURNS text AS ", statements[0]);
        Assert.AreEqual("CREATE CAST (integer AS text) WITH FUNCTION \"render\"(integer)" + suffix + ";", statements[1]);
        Assert.AreEqual("true", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Casts preserve nullable source/result types and PostgreSQL's optional type-modifier and explicitness arguments.
    /// </summary>
    /// <param name="method">The supported cast method.</param>
    /// <param name="declaration">The exact normalized cast DDL.</param>
    /// <param name="nullInput">The backing function null-input policy.</param>
    [TestMethod]
    [DataRow("public static string? Render(int? value) => value?.ToString();", "CREATE CAST (integer AS text) WITH FUNCTION \"render\"(integer);", "CALLED ON NULL INPUT")]
    [DataRow("public static string Render(int value, int modifier) => value.ToString();", "CREATE CAST (integer AS text) WITH FUNCTION \"render\"(integer, integer);", "STRICT")]
    [DataRow("public static string Render(int value, int modifier, bool explicitly) => value.ToString();", "CREATE CAST (integer AS text) WITH FUNCTION \"render\"(integer, integer, boolean);", "STRICT")]
    [DataRow("public static int Render(int value, int modifier) => value;", "CREATE CAST (integer AS integer) WITH FUNCTION \"render\"(integer, integer);", "STRICT")]
    [DataRow("public static int? Render(int? value, int modifier, bool explicitly) => value;", "CREATE CAST (integer AS integer) WITH FUNCTION \"render\"(integer, integer, boolean);", "CALLED ON NULL INPUT")]
    [DataRow("public static Ankus.PgNumeric Render(decimal value, int modifier) => default;", "CREATE CAST (numeric AS numeric) WITH FUNCTION \"render\"(numeric, integer);", "STRICT")]
    public void CastSignaturesPreserveTypmodExplicitnessAndNullPolicy(string method, string declaration, string nullInput)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { [Ankus.PgCast] " + method + " }");
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        string[] statements = OperatorCastStatements(compilation);
        Assert.HasCount(2, statements);
        Assert.Contains(" PARALLEL UNSAFE " + nullInput + " SECURITY INVOKER ", statements[0]);
        Assert.AreEqual(declaration, statements[1]);
    }

    /// <summary>
    /// A cast uses the configured function name and schema and retains its execution options without acquiring a schema of its own.
    /// </summary>
    [TestMethod]
    public void CastUsesQualifiedBackingFunctionAndExecutionOptions()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgSchema("outer")]
            public static class Functions
            {
                [Ankus.PgCast(Ankus.PgCastContext.Implicit)]
                [Ankus.PgFunction(Name = "render_value", Schema = "Other \" Schema", Volatility = Ankus.PgVolatility.Stable,
                    NullInput = Ankus.PgNullInput.Strict, ParallelSafety = Ankus.PgParallelSafety.Restricted)]
                public static string? Render(int? value) => value?.ToString();
            }
            """);
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        string[] statements = OperatorCastStatements(compilation);
        Assert.HasCount(3, statements);
        Assert.Contains(" STABLE PARALLEL RESTRICTED STRICT SECURITY INVOKER ", statements[1]);
        Assert.AreEqual("CREATE CAST (integer AS text) WITH FUNCTION \"Other \"\" Schema\".\"render_value\"(integer) AS IMPLICIT;", statements[2]);
    }

    /// <summary>
    /// Invalid cast contexts, extra argument contracts, void results, and one-argument identity casts fail during generation.
    /// </summary>
    /// <param name="method">The invalid attributed cast method.</param>
    /// <param name="id">The specific contract diagnostic.</param>
    [TestMethod]
    [DataRow("[Ankus.PgCast((Ankus.PgCastContext)(-1))] public static string Render(int value) => \"\";", "ANKUS074")]
    [DataRow("[Ankus.PgCast((Ankus.PgCastContext)3)] public static string Render(int value) => \"\";", "ANKUS074")]
    [DataRow("[Ankus.PgCast] public static string Render() => \"\";", "ANKUS075")]
    [DataRow("[Ankus.PgCast] public static string Render(int a, int b, bool c, int d) => \"\";", "ANKUS075")]
    [DataRow("[Ankus.PgCast] public static void Render(int value) { }", "ANKUS068")]
    [DataRow("[Ankus.PgCast] public static string Render(int value, long modifier) => \"\";", "ANKUS076")]
    [DataRow("[Ankus.PgCast] public static string Render(int value, int? modifier) => \"\";", "ANKUS076")]
    [DataRow("[Ankus.PgCast] public static string Render(int value, bool modifier) => \"\";", "ANKUS076")]
    [DataRow("[Ankus.PgCast] public static string Render(int value, int modifier, int explicitly) => \"\";", "ANKUS077")]
    [DataRow("[Ankus.PgCast] public static string Render(int value, int modifier, bool? explicitly) => \"\";", "ANKUS077")]
    [DataRow("[Ankus.PgCast] public static string Render(params int[] values) => \"\";", "ANKUS067")]
    [DataRow("[Ankus.PgCast] public static string Render(int value, params int[] modifiers) => \"\";", "ANKUS067")]
    [DataRow("[Ankus.PgCast] public static int Render(int value) => value;", "ANKUS079")]
    [DataRow("[Ankus.PgCast] public static int? Render(int value) => value;", "ANKUS079")]
    [DataRow("[Ankus.PgCast] public static Ankus.PgNumeric Render(decimal value) => default;", "ANKUS079")]
    [DataRow("[Ankus.PgCast] public static Ankus.PgDate Render(System.DateOnly value) => default;", "ANKUS079")]
    [DataRow("[Ankus.PgCast] public static Ankus.PgArray<int> Render(int[] value) => new(value);", "ANKUS079")]
    public void InvalidCastSignaturesAreDiagnosed(string method, string id)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { " + method + " }");
        Assert.AreEqual(id, Assert.ContainsSingle(diagnostics).Id);
    }

    /// <summary>
    /// Related declarations inherit ordinary exported-method restrictions instead of bypassing the Native AOT callable contract.
    /// </summary>
    /// <param name="attribute">The attribute triggering function discovery.</param>
    /// <param name="method">The unsupported managed declaration.</param>
    /// <param name="expected">The precise reason the calling contract is invalid.</param>
    [TestMethod]
    [DataRow("[Ankus.PgOperator(\"?\")]", "private static string Render(int value) => \"\";", "ANKUS034")]
    [DataRow("[Ankus.PgCast]", "private static string Render(int value) => \"\";", "ANKUS034")]
    [DataRow("[Ankus.PgOperator(\"?\")]", "public string Render(int value) => \"\";", "ANKUS033")]
    [DataRow("[Ankus.PgCast]", "public string Render(int value) => \"\";", "ANKUS033")]
    [DataRow("[Ankus.PgOperator(\"?\")]", "public static string Render<T>(int value) => \"\";", "ANKUS035")]
    [DataRow("[Ankus.PgCast]", "public static string Render<T>(int value) => \"\";", "ANKUS035")]
    [DataRow("[Ankus.PgOperator(\"?\")]", "public static System.Uri Render(int value) => new(\"https://example.com\");", "ANKUS039")]
    [DataRow("[Ankus.PgCast]", "public static System.Uri Render(int value) => new(\"https://example.com\");", "ANKUS039")]
    public void OperatorCastDiscoveryRetainsNativeFunctionRestrictions(string attribute, string method, string expected)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("public class Functions { " + attribute + method + " }");
        Assert.AreEqual(expected, Assert.ContainsSingle(diagnostics).Id);
    }

    /// <summary>
    /// Nullable enum arrays and casts retain their quoted SQL type identities and depend on both enum declarations.
    /// </summary>
    [TestMethod]
    public void OperatorCastEnumArraysPreserveIdentityAndTypeDependencies()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgOperator("@=", Hashes = true)]
                public static bool? Same(Ankus.PgArray<First?>? left, First?[]? right) => left is null ? null : true;
                [Ankus.PgCast(Ankus.PgCastContext.Assignment)]
                public static Second?[]? Convert(First?[]? value) => null;
            }

            [Ankus.PgEnum(Name = "First", Schema = "Types")] public enum First { Value }
            [Ankus.PgEnum(Name = "Second", Schema = "Types")] public enum Second { Value }
            """);
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        string[] statements = OperatorCastStatements(compilation);
        Assert.HasCount(6, statements);
        const string firstType = "CREATE TYPE \"Types\".\"First\" AS ENUM (E'Value');";
        const string secondType = "CREATE TYPE \"Types\".\"Second\" AS ENUM (E'Value');";
        Assert.Contains(firstType, statements);
        Assert.Contains(secondType, statements);
        int firstTypeIndex = Array.IndexOf(statements, firstType);
        int secondTypeIndex = Array.IndexOf(statements, secondType);
        int sameFunctionIndex = Array.FindIndex(statements, static sql => sql.StartsWith("CREATE FUNCTION \"same\"", StringComparison.Ordinal));
        int convertFunctionIndex = Array.FindIndex(statements, static sql => sql.StartsWith("CREATE FUNCTION \"convert\"", StringComparison.Ordinal));
        Assert.IsGreaterThan(firstTypeIndex, sameFunctionIndex);
        Assert.IsGreaterThan(firstTypeIndex, convertFunctionIndex);
        Assert.IsGreaterThan(secondTypeIndex, convertFunctionIndex);
        Assert.Contains("CREATE OPERATOR @= (FUNCTION = \"same\", LEFTARG = \"Types\".\"First\"[], RIGHTARG = \"Types\".\"First\"[], HASHES);", statements);
        Assert.Contains("CREATE CAST (\"Types\".\"First\"[] AS \"Types\".\"Second\"[]) WITH FUNCTION \"convert\"(\"Types\".\"First\"[]) AS ASSIGNMENT;", statements);
        Assert.HasCount(2, statements.Where(static sql => sql.StartsWith("CREATE FUNCTION", StringComparison.Ordinal) &&
            sql.Contains(" CALLED ON NULL INPUT ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Equivalent PostgreSQL signatures collide even when CLR aliases, nullable annotations, or operator spelling differ.
    /// </summary>
    /// <param name="first">The first attributed method.</param>
    /// <param name="second">The second attributed method with the same PostgreSQL entity identity.</param>
    /// <param name="expected">The fixed operator or cast duplicate diagnostic.</param>
    /// <param name="span">The second declaration's attribute.</param>
    /// <param name="occurrence">The zero-based occurrence of that attribute text in the source.</param>
    /// <param name="signature">The duplicated catalog signature.</param>
    [TestMethod]
    [DataRow("[Ankus.PgOperator(\"+\")] public static int First(int value) => value;", "[Ankus.PgOperator(\"+\")] public static int? Second(int? value) => value;",
        "ANKUS508", "Ankus.PgOperator(\"+\")", 1, "+(NONE,integer)")]
    [DataRow("[Ankus.PgOperator(\"+\")] public static decimal First(decimal a, decimal b) => a;", "[Ankus.PgOperator(\"+\")] public static Ankus.PgNumeric Second(Ankus.PgNumeric a, Ankus.PgNumeric b) => a;",
        "ANKUS508", "Ankus.PgOperator(\"+\")", 1, "+(numeric,numeric)")]
    [DataRow("[Ankus.PgOperator(\"+\")] public static int[] First(int[] a) => a;", "[Ankus.PgOperator(\"+\")] public static Ankus.PgArray<int?> Second(Ankus.PgArray<int?> a) => a;",
        "ANKUS508", "Ankus.PgOperator(\"+\")", 1, "+(NONE,integer[])")]
    [DataRow("[Ankus.PgOperator(\"!=\")] public static bool First(int a, int b) => a != b;", "[Ankus.PgOperator(\"<>\")] public static bool Second(int a, int b) => a != b;",
        "ANKUS508", "Ankus.PgOperator(\"<>\")", 0, "<>(integer,integer)")]
    [DataRow("[Ankus.PgCast] public static string First(int value) => \"\";", "[Ankus.PgCast(Ankus.PgCastContext.Implicit)] public static string? Second(int? value) => null;",
        "ANKUS509", "Ankus.PgCast(Ankus.PgCastContext.Implicit)", 0, "integer AS text")]
    [DataRow("[Ankus.PgCast] public static string First(decimal value) => \"\";", "[Ankus.PgCast] public static string Second(Ankus.PgNumeric value) => \"\";",
        "ANKUS509", "Ankus.PgCast", 1, "numeric AS text")]
    [DataRow("[Ankus.PgCast] public static string First(int value) => \"\";", "[Ankus.PgCast] public static string Second(int value, int modifier, bool explicitly) => \"\";",
        "ANKUS509", "Ankus.PgCast", 1, "integer AS text")]
    [DataRow("[Ankus.PgCast][Ankus.PgFunction(Schema = \"one\")] public static string First(int value) => \"\";", "[Ankus.PgCast][Ankus.PgFunction(Schema = \"two\")] public static string Second(int value) => \"\";",
        "ANKUS509", "Ankus.PgCast", 1, "integer AS text")]
    public void DuplicateOperatorCastSqlIdentitiesAreDiagnosed(string first, string second, string expected, string span, int occurrence, string signature)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { " + first + second + " }");
        AssertSqlControlGraphError(compilation, diagnostics, expected, span, signature);
        AssertDiagnosticOccurrence(diagnostics[0], span, occurrence);
    }

    /// <summary>
    /// Operator overload identity distinguishes arity and ordered operand types, while casts distinguish conversion direction.
    /// </summary>
    [TestMethod]
    public void DistinctOperatorOverloadsAndCastDirectionsRemainIndependent()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgOperator("@")] public static int Unary(int value) => value;
                [Ankus.PgOperator("@")] public static int Binary(int a, int b) => a;
                [Ankus.PgOperator("@")] public static int MixedLeft(int a, string b) => a;
                [Ankus.PgOperator("@")] public static int MixedRight(string a, int b) => b;
                [Ankus.PgCast] public static string Forward(int value) => "";
                [Ankus.PgCast] public static int Reverse(string value) => 0;
            }
            """);
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        string[] statements = OperatorCastStatements(compilation);
        Assert.HasCount(12, statements);
        Assert.Contains("CREATE OPERATOR @ (FUNCTION = \"unary\", RIGHTARG = integer);", statements);
        Assert.Contains("CREATE OPERATOR @ (FUNCTION = \"binary\", LEFTARG = integer, RIGHTARG = integer);", statements);
        Assert.Contains("CREATE OPERATOR @ (FUNCTION = \"mixed_left\", LEFTARG = integer, RIGHTARG = text);", statements);
        Assert.Contains("CREATE OPERATOR @ (FUNCTION = \"mixed_right\", LEFTARG = text, RIGHTARG = integer);", statements);
        Assert.Contains("CREATE CAST (integer AS text) WITH FUNCTION \"forward\"(integer);", statements);
        Assert.Contains("CREATE CAST (text AS integer) WITH FUNCTION \"reverse\"(text);", statements);
    }

    /// <summary>
    /// Separate function, operator, and cast dependency identifiers order custom SQL without conflating the three graph nodes.
    /// </summary>
    [TestMethod]
    public void OperatorCastGraphOrdersSeparateEntityDependencies()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSql("after", "SELECT 'after';", Requires = new[] { "operator", "cast" })]
            [assembly: Ankus.PgSql("middle", "SELECT 'middle';", Requires = new[] { "function" })]
            [assembly: Ankus.PgSql("before", "SELECT 'before';", Requires = new[] { "schema" })]
            [Ankus.PgSchema("operations", Id = "schema")]
            public static class Functions
            {
                [Ankus.PgFunction(Id = "function", Requires = new[] { "before" })]
                [Ankus.PgOperator("@", Id = "operator", Requires = new[] { "middle" })]
                [Ankus.PgCast(Id = "cast", Requires = new[] { "operator" })]
                public static string Render(int value, int modifier) => "";
            }
            """);
        AssertOperatorCastCompilationSucceeds(compilation, diagnostics);
        string[] statements = OperatorCastStatements(compilation);
        Assert.HasCount(7, statements);
        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"operations\";", statements[0]);
        Assert.AreEqual("SELECT 'before';", statements[1]);
        Assert.StartsWith("CREATE FUNCTION \"operations\".\"render\"(", statements[2]);
        Assert.AreEqual("SELECT 'middle';", statements[3]);
        Assert.AreEqual("CREATE OPERATOR \"operations\".@ (FUNCTION = \"operations\".\"render\", LEFTARG = integer, RIGHTARG = integer);", statements[4]);
        Assert.AreEqual("CREATE CAST (integer AS text) WITH FUNCTION \"operations\".\"render\"(integer, integer);", statements[5]);
        Assert.AreEqual("SELECT 'after';", statements[6]);
    }

    /// <summary>
    /// Missing dependencies, invalid identifiers, duplicate aliases, and cycles through the implicit backing-function edge are rejected.
    /// </summary>
    /// <param name="source">The invalid dependency graph source.</param>
    /// <param name="expected">The fixed graph diagnostic.</param>
    /// <param name="span">The exact authored value to correct.</param>
    /// <param name="occurrence">The zero-based occurrence of that text in the source.</param>
    /// <param name="argument">The authored identity in the fixed message, or null when the message has none.</param>
    [TestMethod]
    [DataRow("public static class C { [Ankus.PgOperator(\"@\", Requires = new[] { \"missing\" })] public static int F(int a) => a; }", "ANKUS498", "\"missing\"", 0, "missing")]
    [DataRow("public static class C { [Ankus.PgCast(Requires = new[] { \"missing\" })] public static string F(int a) => \"\"; }", "ANKUS498", "\"missing\"", 0, "missing")]
    [DataRow("public static class C { [Ankus.PgOperator(\"@\", Id = \"\")] public static int F(int a) => a; }", "ANKUS490", "\"\"", 0, null)]
    [DataRow("public static class C { [Ankus.PgCast(Id = \"\")] public static string F(int a) => \"\"; }", "ANKUS490", "\"\"", 0, null)]
    [DataRow("public static class C { [Ankus.PgOperator(\"@\", Requires = null!)] public static int F(int a) => a; }", "ANKUS493", "null!", 0, null)]
    [DataRow("public static class C { [Ankus.PgCast(Requires = new[] { \"\" })] public static string F(int a) => \"\"; }", "ANKUS494", "\"\"", 0, null)]
    [DataRow("public static class C { [Ankus.PgFunction(Id = \"same\")][Ankus.PgOperator(\"@\", Id = \"same\")] public static int F(int a) => a; }",
        "ANKUS497", "\"same\"", 1, "same")]
    [DataRow("public static class C { [Ankus.PgOperator(\"@\", Id = \"same\")][Ankus.PgCast(Id = \"same\")] public static string F(int a) => \"\"; }",
        "ANKUS497", "\"same\"", 1, "same")]
    [DataRow("public static class C { [Ankus.PgFunction(Requires = new[] { \"operator\" })][Ankus.PgOperator(\"@\", Id = \"operator\")] public static int F(int a) => a; }",
        "ANKUS499", "\"operator\"", 0, "C.F(int) -> operator -> C.F(int)")]
    [DataRow("public static class C { [Ankus.PgFunction(Requires = new[] { \"cast\" })][Ankus.PgCast(Id = \"cast\")] public static string F(int a) => \"\"; }",
        "ANKUS499", "\"cast\"", 0, "C.F(int) -> cast -> C.F(int)")]
    [DataRow("[assembly: Ankus.PgSql(\"after\", \"SELECT 1;\", Requires = new[] { \"op\" })] public static class C { [Ankus.PgOperator(\"@\", Id = \"op\", Requires = new[] { \"after\" })] public static int F(int a) => a; }",
        "ANKUS499", "\"after\"", 1, "op -> after -> op")]
    [DataRow("public static class C { [Ankus.PgOperator(\"@\", Id = \"op\", Requires = new[] { \"cast\" })][Ankus.PgCast(Id = \"cast\", Requires = new[] { \"op\" })] public static string F(int a) => \"\"; }",
        "ANKUS499", "\"op\"", 1, "cast -> op -> cast")]
    public void InvalidOperatorCastGraphsAreDiagnosed(string source, string expected, string span, int occurrence, string? argument)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        AssertSqlControlGraphError(compilation, diagnostics, expected, span, argument is null ? [] : [argument]);
        AssertDiagnosticOccurrence(diagnostics[0], span, occurrence);
    }

    /// <summary>
    /// Reordering independent operators and casts cannot alter installation SQL, native exports, or generated managed callbacks.
    /// </summary>
    [TestMethod]
    public void OperatorCastOutputIsDeterministicAcrossDeclarationOrder()
    {
        const string first = "public static class First { [Ankus.PgOperator(\"@\")] public static int Apply(int a) => a; }";
        const string second = "public static class Second { [Ankus.PgCast] public static string Render(int a) => \"\"; }";
        (Compilation left, ImmutableArray<Diagnostic> leftDiagnostics) = Generate(first + second);
        (Compilation right, ImmutableArray<Diagnostic> rightDiagnostics) = Generate(second + first);
        AssertOperatorCastCompilationSucceeds(left, leftDiagnostics);
        AssertOperatorCastCompilationSucceeds(right, rightDiagnostics);
        Assert.HasCount(4, OperatorCastStatements(left));
        Assert.AreEqual(ManifestValue(left, "Ankus.Sql"), ManifestValue(right, "Ankus.Sql"));
        Assert.AreEqual(ManifestValue(left, "Ankus.Exports"), ManifestValue(right, "Ankus.Exports"));
        Assert.AreSequenceEqual(left.SyntaxTrees.Skip(1).Select(static tree => tree.ToString()),
            right.SyntaxTrees.Skip(1).Select(static tree => tree.ToString()));
    }

    /// <summary>
    /// Validates the generated managed compilation as well as generator-reported diagnostics.
    /// </summary>
    /// <param name="compilation">The generated output compilation.</param>
    /// <param name="diagnostics">The diagnostics reported by the generator.</param>
    private void AssertOperatorCastCompilationSucceeds(Compilation compilation, ImmutableArray<Diagnostic> diagnostics)
    {
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Extracts complete SQL declarations while ignoring insignificant layout between tokens.
    /// </summary>
    /// <param name="compilation">The generated output compilation containing SQL metadata.</param>
    /// <returns>SQL statements with normalized layout and their terminating semicolons.</returns>
    private static string[] OperatorCastStatements(Compilation compilation)
        => [.. DeclarationBodies(compilation).SelectMany(static body => body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(static statement => string.Join(" ", statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Replace("( ", "(", StringComparison.Ordinal).Replace(" )", ")", StringComparison.Ordinal) + ";")];

    /// <summary>
    /// Reads exact SQL bodies for semantic assertions while independently validating annotation framing and graph agreement.
    /// </summary>
    private static IEnumerable<string> DeclarationBodies(Compilation compilation)
    {
        Ankus.PgConfig.ExtensionSchemaGraph graph = Ankus.PgConfig.ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        Assert.AreEqual(ManifestValue(compilation, "Ankus.Sql"), graph.Sql);
        Assert.StartsWith("-- Generated by Ankus. Do not edit.\n\n", graph.Sql);
        foreach (Ankus.PgConfig.ExtensionSchemaItem item in graph.Items.Where(static item => item.Sql.Length != 0))
        {
            const string Begin = "/* <begin connected objects> */\n";
            const string End = "/* </end connected objects> */\n\n";
            Assert.StartsWith(Begin, item.Sql);
            Assert.EndsWith(End, item.Sql);
            int start = item.Sql.IndexOf("\n\n", Begin.Length, StringComparison.Ordinal) + 2;
            Assert.IsGreaterThan(Begin.Length, start);

            yield return item.Sql[start..^End.Length];
        }
    }

    /// <summary>
    /// Compares declaration semantics independently of separately tested source comments, retaining authored SQL verbatim.
    /// </summary>
    private static string InstallationBody(Compilation compilation)
    {
        string body = string.Concat(DeclarationBodies(compilation));
        return body.Length == 0 ? "-- No installable objects declared.\n" : body;
    }
}
