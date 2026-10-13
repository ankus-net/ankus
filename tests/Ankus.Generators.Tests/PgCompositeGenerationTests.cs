using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Composite scalars, vectors and shaped arrays preserve nullability and named or anonymous SQL contracts in compilable dispatchers.
    /// </summary>
    /// <param name="managed">The managed parameter and result type.</param>
    /// <param name="array">Whether the SQL type is an array.</param>
    /// <param name="nullable">Whether the parameter accepts SQL NULL.</param>
    [TestMethod]
    [DataRow("Ankus.PgHeapTuple", false, false)]
    [DataRow("Ankus.PgHeapTuple?", false, true)]
    [DataRow("Ankus.PgHeapTuple[]", true, false)]
    [DataRow("Ankus.PgHeapTuple?[]", true, false)]
    [DataRow("Ankus.PgHeapTuple[]?", true, true)]
    [DataRow("Ankus.PgHeapTuple?[]?", true, true)]
    [DataRow("Ankus.PgArray<Ankus.PgHeapTuple>", true, false)]
    [DataRow("Ankus.PgArray<Ankus.PgHeapTuple?>", true, false)]
    [DataRow("Ankus.PgArray<Ankus.PgHeapTuple>?", true, true)]
    [DataRow("Ankus.PgArray<Ankus.PgHeapTuple?>?", true, true)]
    public void CompositeSignaturesCompileWithNamedAndAnonymousTypes(string managed, bool array, bool nullable)
    {
        foreach (bool named in new[] { false, true })
        {
            string binding = named ? "Ankus.PgCompositeType(\"Dog\\\"Type\", Schema = \"Types Space\")" : string.Empty;
            (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
                public static class Functions
                {
                    [Ankus.PgFunction]
                    {{(named ? "[return: " + binding + "]" : "")}}
                    public static {{managed}} Echo({{(named ? "[" + binding + "]" : "")}}{{managed}} value) => value;
                }
                """);
            AssertCompositeCompilationSucceeds(compilation, diagnostics);
            string sqlType = (named ? "\"Types Space\".\"Dog\"\"Type\"" : "record") + (array ? "[]" : string.Empty);
            string sql = CompositeSql(compilation);
            Assert.StartsWith("CREATE FUNCTION \"echo\"(\"value\" " + sqlType + ") RETURNS " + sqlType + " AS ", sql);
            Assert.Contains(nullable ? " CALLED ON NULL INPUT " : " STRICT ", sql);
            Assert.AreEqual(named ? "false" : "true", ManifestValue(compilation, "Ankus.Relocatable"));
            Assert.DoesNotContain("CREATE TYPE", sql);
            string native = ManifestValue(compilation, "Ankus.NativeSource");
            Assert.Contains(array ? "get_element_type(get_func_rettype(fcinfo->flinfo->fn_oid))" :
                "ankus_write_tuple(&result, result_type, descriptor)", native);
            if (!array)
            {
                // A record result alone asks for the caller's columns.
                Assert.Contains("if (result_type == RECORDOID)\n            (void) get_call_result_type(fcinfo, NULL, &descriptor);",
                    native.ReplaceLineEndings("\n"));
            }

            if (!array)
            {
                Assert.Contains("ankus_read_tuple(PG_GETARG_DATUM(0), &arguments[0], &owned[0])", native);
            }
        }
    }

    /// <summary>
    /// SQL NULL results still validate a composite domain before the generated wrapper returns the null datum.
    /// </summary>
    [TestMethod]
    public void CompositeNullResultValidatesItsDeclaredDomain()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgFunction]
                [return: Ankus.PgCompositeType("required_dog", Schema = "records")]
                public static Ankus.PgHeapTuple? Missing() => null;
            }
            """);
        AssertCompositeCompilationSucceeds(compilation, diagnostics);
        Assert.StartsWith("CREATE FUNCTION \"missing\"() RETURNS \"records\".\"required_dog\" AS ", CompositeSql(compilation));
        Assert.Contains("""
                if (result.is_null)
                {
                    AnkusParameter parameter = {0};
                    parameter.type_oid = get_func_rettype(fcinfo->flinfo->fn_oid);
                    parameter.value.is_null = true;
                    (void) ankus_parameter_datum(&parameter);
                    PG_RETURN_NULL();
                }
            """.ReplaceLineEndings("\n"), ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// Named composite array declarations retain PostgreSQL variadic semantics and optional NULL defaults.
    /// </summary>
    [TestMethod]
    public void CompositeVariadicsAndDefaultsKeepBindings()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgFunction]
                [return: Ankus.PgCompositeType("dog")]
                public static Ankus.PgHeapTuple?[] Echo([Ankus.PgCompositeType("dog")] params Ankus.PgHeapTuple?[] values) => values;
                [Ankus.PgFunction]
                [return: Ankus.PgCompositeType("dog")]
                public static Ankus.PgHeapTuple? Optional([Ankus.PgCompositeType("dog")] Ankus.PgHeapTuple? value = null) => value;
            }
            """);
        AssertCompositeCompilationSucceeds(compilation, diagnostics);
        string sql = CompositeSql(compilation);
        Assert.Contains("CREATE FUNCTION \"echo\"(VARIADIC \"values\" \"dog\"[]) RETURNS \"dog\"[] AS ", sql);
        Assert.Contains("CREATE FUNCTION \"optional\"(\"value\" \"dog\" DEFAULT (NULL)) RETURNS \"dog\" AS ", sql);
        Assert.AreEqual("true", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// A function's schema does not silently qualify an explicitly unqualified composite reference.
    /// </summary>
    [TestMethod]
    public void CompositeSchemaBindingIsIndependentOfFunctionPlacement()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgSchema("functions")]
            public static class Functions
            {
                [Ankus.PgFunction]
                [return: Ankus.PgCompositeType("dog", Schema = "records")]
                public static Ankus.PgHeapTuple Echo([Ankus.PgCompositeType("dog")] Ankus.PgHeapTuple value) => value;
            }
            """);
        AssertCompositeCompilationSucceeds(compilation, diagnostics);
        Assert.Contains("CREATE FUNCTION \"functions\".\"echo\"(\"value\" \"dog\") RETURNS \"records\".\"dog\" AS ", CompositeSql(compilation));
    }

    /// <summary>
    /// Enumerable composite values and arrays retain their bound type while anonymous values remain SETOF record.
    /// </summary>
    /// <param name="managed">The enumerable element type.</param>
    /// <param name="array">Whether the element itself is an array.</param>
    [TestMethod]
    [DataRow("Ankus.PgHeapTuple", false)]
    [DataRow("Ankus.PgHeapTuple?", false)]
    [DataRow("Ankus.PgHeapTuple?[]?", true)]
    [DataRow("Ankus.PgArray<Ankus.PgHeapTuple?>?", true)]
    public void CompositeSetElementsRetainTheirBindings(string managed, bool array)
    {
        foreach (bool named in new[] { false, true })
        {
            (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
                public static class Functions
                {
                    [Ankus.PgFunction]
                    {{(named ? "[return: Ankus.PgCompositeType(\"dog\", Schema = \"records\")]" : "")}}
                    public static System.Collections.Generic.IEnumerable<{{managed}}> Rows(
                        [Ankus.PgCompositeType("cat", Schema = "inputs")] Ankus.PgHeapTuple? value)
                        => System.Array.Empty<{{managed}}>();
                }
                """);
            AssertCompositeCompilationSucceeds(compilation, diagnostics);
            string sqlType = (named ? "\"records\".\"dog\"" : "record") + (array ? "[]" : string.Empty);
            Assert.StartsWith("CREATE FUNCTION \"rows\"(\"value\" \"inputs\".\"cat\") RETURNS SETOF " + sqlType + " AS ", CompositeSql(compilation));
        }
    }

    /// <summary>
    /// TABLE output bindings select exact final SQL column names and apply independently to scalar and array composites.
    /// </summary>
    [TestMethod]
    public void CompositeTableBindingsSelectFinalColumnNames()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgFunction]
                [return: Ankus.PgColumnNames("Dog Name", "cats", "count")]
                [return: Ankus.PgCompositeType("dog", Schema = "first", Column = "Dog Name")]
                [return: Ankus.PgCompositeType("cat", Schema = "second", Column = "cats")]
                public static System.Collections.Generic.IEnumerable<(Ankus.PgHeapTuple? Dog, Ankus.PgArray<Ankus.PgHeapTuple?>? Cats, int Count)> Rows()
                    => System.Array.Empty<(Ankus.PgHeapTuple?, Ankus.PgArray<Ankus.PgHeapTuple?>?, int)>();
            }
            """);
        AssertCompositeCompilationSucceeds(compilation, diagnostics);
        Assert.StartsWith("CREATE FUNCTION \"rows\"() RETURNS TABLE (\"Dog Name\" \"first\".\"dog\", \"cats\" \"second\".\"cat\"[], \"count\" integer) AS ", CompositeSql(compilation));
    }

    /// <summary>
    /// A single composite output is unambiguous, including when it is surrounded by ordinary TABLE columns.
    /// </summary>
    /// <param name="declaration">The complete enumerable declaration.</param>
    /// <param name="clause">The expected TABLE return clause.</param>
    [TestMethod]
    [DataRow("[return: Ankus.PgColumnNames(\"dog\")] public static System.Collections.Generic.IEnumerable<Ankus.PgHeapTuple> Rows() => System.Array.Empty<Ankus.PgHeapTuple>();", "TABLE (\"dog\" \"pets\".\"dog\")")]
    [DataRow("public static System.Collections.Generic.IEnumerable<(int Id, Ankus.PgHeapTuple Pet)> Rows() => System.Array.Empty<(int, Ankus.PgHeapTuple)>();", "TABLE (\"id\" integer, \"pet\" \"pets\".\"dog\")")]
    public void CompositeSingleTableOutputCanOmitColumn(string declaration, string clause)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { [Ankus.PgFunction] [return: Ankus.PgCompositeType(\"dog\", Schema = \"pets\")] " + declaration + " }");
        AssertCompositeCompilationSucceeds(compilation, diagnostics);
        Assert.StartsWith("CREATE FUNCTION \"rows\"() RETURNS " + clause + " AS ", CompositeSql(compilation));
    }

    /// <summary>
    /// Unbound TABLE composites remain anonymous and may coexist with individually bound named outputs.
    /// </summary>
    [TestMethod]
    public void CompositeTableCanMixNamedAndAnonymousOutputs()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgFunction]
                [return: Ankus.PgCompositeType("dog", Column = "named")]
                public static System.Collections.Generic.IEnumerable<(Ankus.PgHeapTuple Named, Ankus.PgHeapTuple Anonymous)> Rows()
                    => System.Array.Empty<(Ankus.PgHeapTuple, Ankus.PgHeapTuple)>();
            }
            """);
        AssertCompositeCompilationSucceeds(compilation, diagnostics);
        Assert.StartsWith("CREATE FUNCTION \"rows\"() RETURNS TABLE (\"named\" \"dog\", \"anonymous\" record) AS ", CompositeSql(compilation));
    }

    /// <summary>
    /// Context bindings distinguish identical managed signatures by their named PostgreSQL types and schemas.
    /// </summary>
    /// <param name="secondType">The second PostgreSQL type binding.</param>
    /// <param name="duplicate">Whether the generated SQL signatures must collide.</param>
    [TestMethod]
    [DataRow("\"cat\", Schema = \"one\"", false)]
    [DataRow("\"dog\", Schema = \"two\"", false)]
    [DataRow("\"dog\", Schema = \"one\"", true)]
    public void CompositeFunctionOverloadsUseBoundSqlIdentity(string secondType, bool duplicate)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            public static class Functions
            {
                [Ankus.PgFunction(Name = "echo")] public static int First([Ankus.PgCompositeType("dog", Schema = "one")] Ankus.PgHeapTuple value) => 1;
                [Ankus.PgFunction(Name = "echo")] public static int Second([Ankus.PgCompositeType({{secondType}})] Ankus.PgHeapTuple value) => 2;
            }
            """);
        if (duplicate)
        {
            Assert.AreEqual("ANKUS207", Assert.ContainsSingle(diagnostics).Id);
            return;
        }

        AssertCompositeCompilationSucceeds(compilation, diagnostics);
        Assert.HasCount(2, CompositeSql(compilation).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("CREATE FUNCTION \"echo\"(\"value\" \"one\".\"dog\") RETURNS integer", CompositeSql(compilation));
    }

    /// <summary>
    /// Operators and casts preserve individually bound composite source, target and operand types.
    /// </summary>
    [TestMethod]
    public void CompositeOperatorsAndCastsUseContextBindings()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgOperator("@=")]
                public static bool Same([Ankus.PgCompositeType("dog", Schema = "pets")] Ankus.PgHeapTuple left,
                    [Ankus.PgCompositeType("cat", Schema = "pets")] Ankus.PgHeapTuple right) => false;
                [Ankus.PgCast(Ankus.PgCastContext.Assignment)]
                [return: Ankus.PgCompositeType("cat", Schema = "pets")]
                public static Ankus.PgHeapTuple Convert([Ankus.PgCompositeType("dog", Schema = "pets")] Ankus.PgHeapTuple value) => value;
            }
            """);
        AssertCompositeCompilationSucceeds(compilation, diagnostics);
        string sql = CompositeSql(compilation);
        Assert.Contains("CREATE OPERATOR @= (FUNCTION = \"same\", LEFTARG = \"pets\".\"dog\", RIGHTARG = \"pets\".\"cat\");", sql);
        Assert.Contains("CREATE CAST (\"pets\".\"dog\" AS \"pets\".\"cat\") WITH FUNCTION \"convert\"(\"pets\".\"dog\") AS ASSIGNMENT;", sql);
        Assert.Contains("CREATE FUNCTION \"convert\"(\"value\" \"pets\".\"dog\") RETURNS \"pets\".\"cat\" AS ", sql);
    }

    /// <summary>
    /// Casts require concrete composite identities because PostgreSQL rejects record pseudo-types as cast endpoints.
    /// </summary>
    /// <param name="method">The cast with an anonymous record endpoint.</param>
    [TestMethod]
    [DataRow("public static int Convert(Ankus.PgHeapTuple value) => 1;")]
    [DataRow("public static Ankus.PgHeapTuple Convert(int value) => null!;")]
    public void CompositeCastsRejectAnonymousRecordEndpoints(string method)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { [Ankus.PgCast] " + method + " }");
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS078", diagnostic.Id);
        Assert.Contains("record pseudo-type", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Explicit custom SQL dependencies supply composite creation without generator-created replacement types.
    /// </summary>
    [TestMethod]
    public void CompositeCustomSqlDependenciesOrderExistingTypeCreation()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSql("dog", "CREATE TYPE pets.dog AS (name text);", Requires = new[] { "schema" })]
            [Ankus.PgSchema("pets", Id = "schema")] public static class Types;
            public static class Functions
            {
                [Ankus.PgFunction(Requires = new[] { "dog" })]
                [return: Ankus.PgCompositeType("dog", Schema = "pets")]
                public static Ankus.PgHeapTuple Echo([Ankus.PgCompositeType("dog", Schema = "pets")] Ankus.PgHeapTuple value) => value;
            }
            """);
        AssertCompositeCompilationSucceeds(compilation, diagnostics);
        string[] statements = CompositeSql(compilation).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(3, statements);
        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"pets\";", statements[0]);
        Assert.AreEqual("CREATE TYPE pets.dog AS (name text);", statements[1]);
        Assert.StartsWith("CREATE FUNCTION \"echo\"(\"value\" \"pets\".\"dog\") RETURNS \"pets\".\"dog\" AS ", statements[2]);
    }

    /// <summary>
    /// Schema dependencies are real graph edges for parameter, scalar-return and set-column composite bindings.
    /// </summary>
    /// <param name="method">The function referencing the schema.</param>
    [TestMethod]
    [DataRow("public static int Echo([Ankus.PgCompositeType(\"dog\", Schema = \"pets\")] Ankus.PgHeapTuple value) => 1;")]
    [DataRow("[return: Ankus.PgCompositeType(\"dog\", Schema = \"pets\")] public static Ankus.PgHeapTuple Echo() => null!;")]
    [DataRow("[return: Ankus.PgCompositeType(\"dog\", Schema = \"pets\")] public static System.Collections.Generic.IEnumerable<Ankus.PgHeapTuple> Echo() => System.Array.Empty<Ankus.PgHeapTuple>();")]
    public void CompositeSchemaBindingsParticipateInGraphCycles(string method)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("[Ankus.PgSchema(\"pets\", Requires = new[] { \"function\" })] public static class Types; " +
            "public static class Functions { [Ankus.PgFunction(Id = \"function\")] " + method + " }");
        AssertSqlControlGraphError(compilation, diagnostics, "ANKUS499", "\"function\"", "Types -> function -> Types");
        AssertDiagnosticOccurrence(diagnostics[0], "\"function\"", 0);
    }

    /// <summary>
    /// Invalid identifiers and misplaced, ambiguous or repeated bindings report their exact contract.
    /// </summary>
    /// <param name="method">The invalid attributed method.</param>
    /// <param name="expected">The precise binding diagnostic.</param>
    [TestMethod]
    [DataRow("public static Ankus.PgHeapTuple Echo([Ankus.PgCompositeType(null!)] Ankus.PgHeapTuple value) => value;", "ANKUS401")]
    [DataRow("public static Ankus.PgHeapTuple Echo([Ankus.PgCompositeType(\"\")] Ankus.PgHeapTuple value) => value;", "ANKUS401")]
    [DataRow("public static Ankus.PgHeapTuple Echo([Ankus.PgCompositeType(\"a\\0b\")] Ankus.PgHeapTuple value) => value;", "ANKUS401")]
    [DataRow("public static Ankus.PgHeapTuple Echo([Ankus.PgCompositeType(\"\\ud800\")] Ankus.PgHeapTuple value) => value;", "ANKUS401")]
    [DataRow("public static Ankus.PgHeapTuple Echo([Ankus.PgCompositeType(\"dog\", Schema = \"\")] Ankus.PgHeapTuple value) => value;", "ANKUS401")]
    [DataRow("public static Ankus.PgHeapTuple Echo([Ankus.PgCompositeType(\"dog\", Schema = \"a\\0b\")] Ankus.PgHeapTuple value) => value;", "ANKUS401")]
    [DataRow("public static Ankus.PgHeapTuple Echo([Ankus.PgCompositeType(\"dog\", Schema = \"\\udc00\")] Ankus.PgHeapTuple value) => value;", "ANKUS401")]
    [DataRow("public static int Echo([Ankus.PgCompositeType(\"dog\")] int value) => value;", "ANKUS406")]
    [DataRow("[return: Ankus.PgCompositeType(\"dog\")] public static int Echo() => 1;", "ANKUS406")]
    [DataRow("public static Ankus.PgHeapTuple Echo([Ankus.PgCompositeType(\"dog\", Column = \"x\")] Ankus.PgHeapTuple value) => value;", "ANKUS411")]
    [DataRow("[return: Ankus.PgCompositeType(\"dog\", Column = \"x\")] public static Ankus.PgHeapTuple Echo() => null!;", "ANKUS411")]
    [DataRow("public static Ankus.PgHeapTuple Echo([Ankus.PgCompositeType(\"dog\"), Ankus.PgCompositeType(\"cat\")] Ankus.PgHeapTuple value) => value;", "ANKUS409")]
    [DataRow("[return: Ankus.PgCompositeType(\"dog\"), Ankus.PgCompositeType(\"cat\")] public static Ankus.PgHeapTuple Echo() => null!;", "ANKUS409")]
    [DataRow("[return: Ankus.PgCompositeType(\"dog\", Column = \"x\")] public static System.Collections.Generic.IEnumerable<Ankus.PgHeapTuple> Echo() => System.Array.Empty<Ankus.PgHeapTuple>();", "ANKUS403")]
    [DataRow("[return: Ankus.PgCompositeType(\"dog\")] public static System.Collections.Generic.IEnumerable<(Ankus.PgHeapTuple One, Ankus.PgHeapTuple Two)> Echo() => System.Array.Empty<(Ankus.PgHeapTuple, Ankus.PgHeapTuple)>();", "ANKUS404")]
    [DataRow("[return: Ankus.PgCompositeType(\"dog\", Column = \"One\")] public static System.Collections.Generic.IEnumerable<(Ankus.PgHeapTuple One, int Two)> Echo() => System.Array.Empty<(Ankus.PgHeapTuple, int)>();", "ANKUS403")]
    [DataRow("[return: Ankus.PgCompositeType(\"dog\", Column = \"two\")] public static System.Collections.Generic.IEnumerable<(Ankus.PgHeapTuple One, int Two)> Echo() => System.Array.Empty<(Ankus.PgHeapTuple, int)>();", "ANKUS406")]
    [DataRow("[return: Ankus.PgCompositeType(\"dog\", Column = \"one\"), Ankus.PgCompositeType(\"cat\", Column = \"one\")] public static System.Collections.Generic.IEnumerable<(Ankus.PgHeapTuple One, int Two)> Echo() => System.Array.Empty<(Ankus.PgHeapTuple, int)>();", "ANKUS407")]
    [DataRow("[return: Ankus.PgCompositeType(\"dog\")] public static System.Collections.Generic.IEnumerable<int> Echo() => System.Array.Empty<int>();", "ANKUS404")]
    public void InvalidCompositeBindingsAreDiagnosed(string method, string expected)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { [Ankus.PgFunction] " + method + " }");
        Assert.AreEqual(expected, Assert.ContainsSingle(diagnostics).Id);
    }

    /// <summary>
    /// Composite name and schema limits use UTF-8 byte length immediately below, at and above PostgreSQL's limit.
    /// </summary>
    /// <param name="schema">Whether to test the schema instead of the type identifier.</param>
    /// <param name="bytes">The identifier length in UTF-8 bytes.</param>
    [TestMethod]
    [DataRow(false, 62)]
    [DataRow(false, 63)]
    [DataRow(false, 64)]
    [DataRow(true, 62)]
    [DataRow(true, 63)]
    [DataRow(true, 64)]
    public void CompositeIdentifiersUseUtf8ByteBoundaries(bool schema, int bytes)
    {
        string name = new('é', bytes / 2);
        if (bytes % 2 != 0)
        {
            name += 'x';
        }

        string literal = SymbolDisplay.FormatLiteral(name, quote: true);
        string binding = schema ? "\"dog\", Schema = " + literal : literal;
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { [Ankus.PgFunction] [return: Ankus.PgCompositeType(" + binding + ")] public static Ankus.PgHeapTuple Echo() => null!; }");
        if (bytes > 63)
        {
            Assert.AreEqual("ANKUS401", Assert.ContainsSingle(diagnostics).Id);
            return;
        }

        AssertCompositeCompilationSucceeds(compilation, diagnostics);
        string sqlType = schema ? "\"" + name + "\".\"dog\"" : "\"" + name + "\"";
        Assert.StartsWith("CREATE FUNCTION \"echo\"() RETURNS " + sqlType + " AS ", CompositeSql(compilation));
    }

    private void AssertCompositeCompilationSucceeds(Compilation compilation, ImmutableArray<Diagnostic> diagnostics)
    {
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    private static string CompositeSql(Compilation compilation)
        => InstallationBody(compilation).Replace("\nRETURNS", " RETURNS", StringComparison.Ordinal);
}
