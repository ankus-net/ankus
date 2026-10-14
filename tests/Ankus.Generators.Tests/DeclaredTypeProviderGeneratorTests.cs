using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    private const string ProviderDuplicateError = "PostgreSQL type \"item\" has more than one provider, including generated type or enum declarations; retain one custom block or the generated declaration";

    /// <summary>
    /// Inferred providers preserve the complete contract produced by explicit prerequisites for every signature position.
    /// </summary>
    /// <param name="kind">The raw or composite consumer shape.</param>
    /// <param name="signature">An independently specified SQL contract.</param>
    [TestMethod]
    [DataRow("raw-parameter", "\"value\" \"item\")\nRETURNS integer")]
    [DataRow("raw-result", "RETURNS \"item\" AS")]
    [DataRow("raw-array", "\"value\" \"item\"[])\nRETURNS \"item\"[]")]
    [DataRow("raw-set", "RETURNS SETOF \"item\"")]
    [DataRow("raw-table", "RETURNS TABLE (\"first\" \"item\", \"second\" \"item\"[])")]
    [DataRow("composite-parameter", "\"value\" \"item\")\nRETURNS integer")]
    [DataRow("composite-result", "RETURNS \"item\" AS")]
    [DataRow("composite-vector", "\"value\" \"item\"[])\nRETURNS \"item\"[]")]
    [DataRow("composite-shaped", "\"value\" \"item\"[])\nRETURNS \"item\"[]")]
    [DataRow("composite-set", "RETURNS SETOF \"item\"")]
    [DataRow("composite-table", "RETURNS TABLE (\"first\" \"item\", \"second\" \"item\"[])")]
    [DataRow("aggregate", "STYPE = \"item\"")]
    [DataRow("operator", "LEFTARG = \"item\", RIGHTARG = \"item\"")]
    [DataRow("cast", "CREATE CAST (\"item\" AS integer)")]
    public void DeclaredTypeProvidersOrderEveryBoundSignature(string kind, string signature)
    {
        const string block = "[assembly: Ankus.PgSql(\"types\", \"CREATE TYPE item AS (number integer);\", Relocatable = true)]";
        Compilation explicitOrder = GenerateSqlControl(block + DeclaredProviderConsumer(kind, "Requires = [\"types\"]"));
        Compilation inferred = GenerateSqlControl(block +
            "[assembly: Ankus.PgSqlTypeProvider(\"types\", \"item\")]" + DeclaredProviderConsumer(kind, string.Empty));
        string sql = InstallationBody(inferred);
        Assert.StartsWith("CREATE TYPE item AS (number integer);\nCREATE FUNCTION ", sql);
        Assert.Contains(signature, sql);
        Assert.AreEqual(InstallationBody(explicitOrder), sql);
        Assert.AreEqual("true", ManifestValue(inferred, "Ankus.Relocatable"));
        AssertSqlControlBoundary(explicitOrder, inferred);
    }

    /// <summary>
    /// Catalog identity is exact and does not fold names, schemas, aliases, qualification syntax or array names.
    /// </summary>
    /// <param name="name">The provided catalog leaf name.</param>
    /// <param name="schema">The optional provider schema.</param>
    /// <param name="bindingName">The requested catalog leaf name.</param>
    /// <param name="bindingSchema">The requested schema.</param>
    /// <param name="array">Whether the binding represents the whole array.</param>
    /// <param name="matches">Whether the provider must precede the consumer.</param>
    [TestMethod]
    [DataRow("item", null, "item", null, false, true)]
    [DataRow("item", null, "item", null, true, true)]
    [DataRow("item", "one", "item", "one", false, true)]
    [DataRow("item", "one", "item", "two", false, false)]
    [DataRow("item", "one", "item", null, false, false)]
    [DataRow("item", null, "item", "one", false, false)]
    [DataRow("Item", null, "item", null, false, false)]
    [DataRow("item", "One", "item", "one", false, false)]
    [DataRow("integer", "pg_catalog", "int4", "pg_catalog", false, false)]
    [DataRow("one.item", null, "item", "one", false, false)]
    [DataRow("_item", null, "item", null, true, false)]
    [DataRow("Mixed \" café", "Schema \" Name", "Mixed \" café", "Schema \" Name", true, true)]
    public void DeclaredTypeProvidersMatchExactLeafIdentity(string name, string? schema, string bindingName,
        string? bindingSchema, bool array, bool matches)
    {
        string provider = "[assembly: Ankus.PgSqlTypeProvider(\"types\", " + SymbolDisplay.FormatLiteral(name, true) +
            DeclaredProviderSchema(schema) + ")]";
        string binding = "[Ankus.PgSqlType(" + SymbolDisplay.FormatLiteral(bindingName, true) +
            DeclaredProviderSchema(bindingSchema) + ", IsArray = " + (array ? "true" : "false") + ")]";
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("types", "SELECT 'provider';", Relocatable = true)]
            """ + provider + "public static class Functions { [Ankus.PgFunction(Sql = \"SELECT 'consumer';\", SqlRelocatable = true)] " +
            "public static int Read(" + binding + " Ankus.PgDatum? value) => 7; }");
        Assert.AreEqual(matches ? "SELECT 'provider';\nSELECT 'consumer';\n" : "SELECT 'consumer';\nSELECT 'provider';\n",
            InstallationBody(compilation));
    }

    /// <summary>
    /// Multiple providers in one block remain distinct from same-named types in another schema and function placement.
    /// </summary>
    [TestMethod]
    public void DeclaredTypeProvidersKeepSchemaPlacementAndMultipleClaimsIndependent()
    {
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("first", "SELECT 'first';", Relocatable = true)]
            [assembly: Ankus.PgSql("second", "SELECT 'second';", Relocatable = true)]
            [assembly: Ankus.PgSqlTypeProvider("first", "item")]
            [assembly: Ankus.PgSqlTypeProvider("first", "other")]
            [assembly: Ankus.PgSqlTypeProvider("second", "item", Schema = "placed")]
            [Ankus.PgSchema("placed", Create = false)]
            public static class Functions
            {
                [Ankus.PgFunction(Sql = "SELECT 'unqualified';", SqlRelocatable = true)]
                public static int A([Ankus.PgSqlType("item")] Ankus.PgDatum value) => 1;
                [Ankus.PgFunction(Sql = "SELECT 'other';", SqlRelocatable = true)]
                public static int B([Ankus.PgCompositeType("other")] Ankus.PgHeapTuple value) => 2;
                [Ankus.PgFunction(Sql = "SELECT 'qualified';", SqlRelocatable = true)]
                public static int C([Ankus.PgSqlType("item", Schema = "placed")] Ankus.PgDatum value) => 3;
            }
            """);
        Assert.AreEqual("SELECT 'first';\nSELECT 'unqualified';\nSELECT 'other';\nSELECT 'second';\nSELECT 'qualified';\n",
            InstallationBody(compilation));
        Assert.AreEqual("false", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Ordinary external catalog bindings need no provider, and an unused provider does not add runtime registrations.
    /// </summary>
    [TestMethod]
    public void DeclaredTypeProvidersLeaveExistingExternalBindingsAndNativeContractsUnchanged()
    {
        const string source = """
            [assembly: Ankus.PgSql("unused", "SELECT 'unchanged';", Relocatable = true)]
            public static class Functions
            {
                [Ankus.PgFunction]
                [return: Ankus.PgSqlType("pg_lsn", Schema = "pg_catalog")]
                public static Ankus.PgDatum? Raw([Ankus.PgSqlType("pg_lsn", Schema = "pg_catalog")] Ankus.PgDatum? value) => value;
                [Ankus.PgFunction]
                [return: Ankus.PgCompositeType("external")]
                public static Ankus.PgHeapTuple? Composite([Ankus.PgCompositeType("external")] Ankus.PgHeapTuple? value) => value;
            }
            """;
        Compilation baseline = GenerateSqlControl(source);
        Compilation provider = GenerateSqlControl("[assembly: Ankus.PgSqlTypeProvider(\"unused\", \"unreferenced\")]" + source);
        Assert.AreEqual(InstallationBody(baseline), InstallationBody(provider));
        AssertSqlControlBoundary(baseline, provider);
        Assert.AreEqual("true", ManifestValue(provider, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Each TABLE column independently contributes its provider, including a later composite array column.
    /// </summary>
    [TestMethod]
    public void DeclaredTypeProvidersOrderEveryIndependentTableColumn()
    {
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("a-first", "SELECT 'first type';")]
            [assembly: Ankus.PgSql("z-second", "SELECT 'second type';")]
            [assembly: Ankus.PgSqlTypeProvider("a-first", "first")]
            [assembly: Ankus.PgSqlTypeProvider("z-second", "second")]
            public static class Functions
            {
                [Ankus.PgFunction]
                [return: Ankus.PgSqlType("first", Column = "first")]
                [return: Ankus.PgCompositeType("second", Column = "second")]
                public static System.Collections.Generic.IEnumerable<(Ankus.PgDatum? First, Ankus.PgArray<Ankus.PgHeapTuple?>? Second)> Read()
                    => [(null, null)];
            }
            """);
        string sql = InstallationBody(compilation);
        Assert.StartsWith("SELECT 'first type';\nSELECT 'second type';\nCREATE FUNCTION \"read\"", sql);
        Assert.Contains("RETURNS TABLE (\"first\" \"first\", \"second\" \"second\"[])", sql);
    }

    /// <summary>
    /// Aggregate input-only and final-result-only providers cannot be satisfied by traversing the state type alone.
    /// </summary>
    [TestMethod]
    public void DeclaredTypeProvidersOrderIndependentAggregateInputAndFinalResult()
    {
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("a-input", "SELECT 'input type';")]
            [assembly: Ankus.PgSql("z-result", "SELECT 'result type';")]
            [assembly: Ankus.PgSqlTypeProvider("a-input", "input")]
            [assembly: Ankus.PgSqlTypeProvider("z-result", "result")]
            [Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class Values : Ankus.IPgAggregate<int,Ankus.PgDatum?>,
                Ankus.IPgFinalizingAggregate<int,System.ValueTuple,Ankus.PgDatum?>
            {
                public static int Transition(Ankus.PgAggregateContext context,int state, [Ankus.PgSqlType("input")] Ankus.PgDatum? value) => state + 1;
                [return: Ankus.PgSqlType("result")]
                public static Ankus.PgDatum? Final(Ankus.PgAggregateContext context,int state,System.ValueTuple direct) => null;
            }
            """);
        string sql = InstallationBody(compilation);
        AssertSqlControlBefore(sql, "SELECT 'input type';", "CREATE FUNCTION \"values_transition\"");
        AssertSqlControlBefore(sql, "SELECT 'result type';", "CREATE FUNCTION \"values_final\"");
        AssertSqlControlBefore(sql, "CREATE FUNCTION \"values_final\"", "CREATE AGGREGATE \"values\"");
        Assert.Contains("\"value\" \"input\")\nRETURNS integer", sql);
        Assert.Contains("\"values_final\"(\"state\" integer)\nRETURNS \"result\"", sql);
        Assert.Contains("CREATE AGGREGATE \"values\"(\"value\" \"input\")", sql);
        Assert.Contains("STYPE = integer", sql);
    }

    /// <summary>
    /// SQL block IDs retain graph text semantics rather than inheriting catalog identifier length limits.
    /// </summary>
    [TestMethod]
    public void DeclaredTypeProviderSqlIdsRemainGraphIdentifiers()
    {
        string id = SymbolDisplay.FormatLiteral("provider " + new string('é', 64) + " \" block", true);
        Compilation compilation = GenerateSqlControl("[assembly: Ankus.PgSql(" + id + ", \"SELECT 'provider';\")]" +
            "[assembly: Ankus.PgSqlTypeProvider(" + id + ", \"item\")]" + DeclaredProviderConsumer("raw-result", string.Empty));
        Assert.StartsWith("SELECT 'provider';\nCREATE FUNCTION \"read\"()\nRETURNS \"item\"", InstallationBody(compilation));
    }

    /// <summary>
    /// File content and provider metadata invalidate reused generator output independently.
    /// </summary>
    [TestMethod]
    public void DeclaredTypeProviderFilesAndMetadataInvalidateIncrementalOutput()
    {
        string project = Path.Join(AppContext.BaseDirectory, "provider-project");
        var original = new SqlInput(Path.Join(project, "types.sql"), "SELECT 'original';");
        var replacement = new SqlInput(original.Path, "SELECT 'changed';");
        const string template = """
            [assembly: Ankus.PgSqlFile("types", "types.sql", Relocatable = true)]
            [assembly: Ankus.PgSqlTypeProvider("types", "NAME"SCHEMA)]
            public static class Functions
            {
                [Ankus.PgFunction(Sql = "SELECT 'consumer';", SqlRelocatable = true)]
                public static int Read([Ankus.PgSqlType("item")] Ankus.PgDatum value) => 1;
            }
            """;
        string[] sources =
        [
            template.Replace("NAME", "other", StringComparison.Ordinal).Replace("SCHEMA", string.Empty, StringComparison.Ordinal),
            template.Replace("NAME", "item", StringComparison.Ordinal).Replace("SCHEMA", string.Empty, StringComparison.Ordinal),
            template.Replace("NAME", "item", StringComparison.Ordinal).Replace("SCHEMA", ", Schema = \"fixed\"", StringComparison.Ordinal),
        ];
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new PgFunctionGenerator().AsSourceGenerator()],
            [original], optionsProvider: new SqlOptions(project));
        CSharpCompilation input = CSharpCompilation.Create("ProviderIncremental",
            [CSharpSyntaxTree.ParseText(sources[0], cancellationToken: context.CancellationToken)], s_references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        Compilation? previous = null;
        for (int index = 0; index < sources.Length; index++)
        {
            input = input.ReplaceSyntaxTree(input.SyntaxTrees.Single(),
                CSharpSyntaxTree.ParseText(sources[index], cancellationToken: context.CancellationToken));
            driver = driver.RunGeneratorsAndUpdateCompilation(input, out Compilation output, out ImmutableArray<Diagnostic> diagnostics,
                context.CancellationToken);
            AssertSqlControlCompilation(output, diagnostics);
            Assert.AreEqual(index == 1 ? "SELECT 'original';\nSELECT 'consumer';\n" : "SELECT 'consumer';\nSELECT 'original';\n",
                InstallationBody(output));
            Assert.AreEqual(index == 2 ? "false" : "true", ManifestValue(output, "Ankus.Relocatable"));
            if (previous is not null)
            {
                AssertSqlControlBoundary(previous, output);
            }

            previous = output;
        }

        input = input.ReplaceSyntaxTree(input.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(sources[1], cancellationToken: context.CancellationToken));
        driver.ReplaceAdditionalText(original, replacement).RunGeneratorsAndUpdateCompilation(input, out Compilation changed,
            out ImmutableArray<Diagnostic> changedErrors, context.CancellationToken);
        AssertSqlControlCompilation(changed, changedErrors);
        Assert.AreEqual("SELECT 'changed';\nSELECT 'consumer';\n", InstallationBody(changed));
        AssertSqlControlBoundary(previous!, changed);
    }

    /// <summary>
    /// Inline and tracked-file providers produce the same ordered script independently of attribute declaration order.
    /// </summary>
    [TestMethod]
    public void DeclaredTypeProviderOrderingIsDeterministicAcrossInlineAndFileInputs()
    {
        string project = Path.Join(AppContext.BaseDirectory, "provider-order-project");
        var file = new SqlInput(Path.Join(project, "types.sql"), "SELECT 'provider';");
        const string claim = "[assembly: Ankus.PgSqlTypeProvider(\"types\", \"item\")]";
        const string after = "[assembly: Ankus.PgSql(\"after\", \"SELECT 'after';\", Requires = [\"consumer\"], Relocatable = true)]";
        const string consumer = """
            public static class Functions
            {
                [Ankus.PgFunction(Id = "consumer", Sql = "SELECT 'consumer';", SqlRelocatable = true)]
                public static int Read([Ankus.PgSqlType("item")] Ankus.PgDatum value) => 1;
            }
            """;
        string? native = null;
        bool[] modes = [false, true];
        foreach (bool useFile in modes)
        {
            string block = useFile
                ? "[assembly: Ankus.PgSqlFile(\"types\", \"types.sql\", Relocatable = true)]"
                : "[assembly: Ankus.PgSql(\"types\", \"SELECT 'provider';\", Relocatable = true)]";
            string[] orders = [claim + block + after, after + block + claim];
            foreach (string attributes in orders)
            {
                (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(attributes + consumer, [file], new SqlOptions(project));
                AssertSqlControlCompilation(compilation, diagnostics);
                Assert.AreEqual("SELECT 'provider';\nSELECT 'consumer';\nSELECT 'after';\n", InstallationBody(compilation));
                native ??= ManifestValue(compilation, "Ankus.NativeSource");
                Assert.AreEqual(native, ManifestValue(compilation, "Ankus.NativeSource"));
            }
        }
    }

    /// <summary>
    /// Generated SQL identities remain hard prerequisites even when their own SQL is disabled or replaced.
    /// </summary>
    /// <param name="kind">The generated type declaration.</param>
    /// <param name="policy">Its SQL generation policy.</param>
    [TestMethod]
    [DataRow("type", "default")]
    [DataRow("type", "disabled")]
    [DataRow("type", "replacement")]
    [DataRow("enum", "default")]
    [DataRow("enum", "disabled")]
    [DataRow("enum", "replacement")]
    public void DeclaredTypeProvidersRecognizeReservedGeneratedIdentities(string kind, string policy)
    {
        string generated = DeclaredProviderGeneratedType(kind, policy, "Requires = [\"supplier\"]");
        const string supplier = "[assembly: Ankus.PgSql(\"supplier\", \"SELECT 'supplier';\", Relocatable = true)]";
        string consumer = "public static class Functions { [Ankus.PgFunction(OPTIONS)] " +
            "[return: Ankus.PgSqlType(\"item\", IsArray = true)] " +
            "public static Ankus.PgDatum? Read([Ankus.PgSqlType(\"item\")] Ankus.PgDatum? value) => value; }";
        Compilation inferred = GenerateSqlControl(supplier + generated + consumer.Replace("OPTIONS", string.Empty, StringComparison.Ordinal));
        Compilation explicitOrder = GenerateSqlControl(supplier + generated +
            consumer.Replace("OPTIONS", "Requires = [\"declared\"]", StringComparison.Ordinal));
        string sql = InstallationBody(inferred);
        Assert.AreEqual(InstallationBody(explicitOrder), sql);
        AssertSqlControlBefore(sql, "SELECT 'supplier';", "CREATE FUNCTION \"read\"");
        Assert.Contains("RETURNS \"item\"[]", sql);
        AssertSqlControlBoundary(explicitOrder, inferred);
        if (policy == "replacement")
        {
            AssertSqlControlBefore(sql, "SELECT 'generated';", "CREATE FUNCTION \"read\"");
        }
    }

    /// <summary>
    /// Composite bindings also order against an existing generated anchor without inventing another mapping or SQL declaration.
    /// </summary>
    [TestMethod]
    public void DeclaredTypeProvidersRecognizeCompositeBindingsToGeneratedAnchors()
    {
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("supplier", "SELECT 'supplier';")]
            [Ankus.PgType(Name = "item", GenerateSql = false, Requires = ["supplier"])]
            public readonly record struct Zulu(int Number);
            public static class Functions
            {
                [Ankus.PgFunction]
                [return: Ankus.PgCompositeType("item")]
                public static Ankus.PgHeapTuple?[]? Read([Ankus.PgCompositeType("item")] Ankus.PgHeapTuple?[]? value) => value;
            }
            """);
        string sql = InstallationBody(compilation);
        Assert.StartsWith("SELECT 'supplier';\nCREATE FUNCTION \"read\"(\"value\" \"item\"[])\nRETURNS \"item\"[]", sql);
        Assert.DoesNotContain("CREATE TYPE", sql);
    }

    /// <summary>
    /// A custom provider cannot take over a generated mapping under any declaration SQL policy.
    /// </summary>
    /// <param name="kind">The generated type kind.</param>
    /// <param name="policy">The generated type's SQL policy.</param>
    [TestMethod]
    [DataRow("type", "default")]
    [DataRow("type", "disabled")]
    [DataRow("type", "replacement")]
    [DataRow("enum", "default")]
    [DataRow("enum", "disabled")]
    [DataRow("enum", "replacement")]
    public void DeclaredTypeProvidersRejectGeneratedIdentityCollisions(string kind, string policy)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSql("types", "SELECT 'provider';")]
            [assembly: Ankus.PgSqlTypeProvider("types", "item")]
            """ + DeclaredProviderGeneratedType(kind, policy, string.Empty));
        AssertDeclaredProviderError(compilation, diagnostics);
        Assert.AreEqual(ProviderDuplicateError, Assert.ContainsSingle(diagnostics).GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Malformed, ambiguous and non-SQL provider targets fail before any partial native or SQL artifacts escape.
    /// </summary>
    /// <param name="inventory">The invalid declaration inventory.</param>
    /// <param name="diagnosticId">The independently correctable rejected boundary.</param>
    [TestMethod]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(null!, \"item\")]", "ANKUS378")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"\", \"item\")]", "ANKUS378")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\" \", \"item\")]", "ANKUS378")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"types\\0\", \"item\")]", "ANKUS379")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"types\\ud800\", \"item\")]", "ANKUS380")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"missing\", \"item\")]", "ANKUS381")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"Types\", \"item\")]", "ANKUS381")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"types\", (string)null!)]", "ANKUS385")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"types\", \"\")]", "ANKUS385")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"types\", \"x\\0y\")]", "ANKUS386")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"types\", \"\\ud800\")]", "ANKUS387")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"types\", \"item\", Schema = \"\")]", "ANKUS389")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"types\", \"item\", Schema = \"x\\0y\")]", "ANKUS390")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"types\", \"item\", Schema = \"\\udc00\")]", "ANKUS391")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"function\", \"item\")]", "ANKUS381")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"schema\", \"item\")]", "ANKUS381")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"types\", \"item\")][assembly: Ankus.PgSqlTypeProvider(\"types\", \"item\")]", "ANKUS394")]
    [DataRow("[assembly: Ankus.PgSqlTypeProvider(\"types\", \"item\")][assembly: Ankus.PgSqlTypeProvider(\"other\", \"item\")]", "ANKUS394")]
    public void InvalidDeclaredTypeProvidersSuppressAllArtifacts(string inventory, string diagnosticId)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(inventory + """
            [assembly: Ankus.PgSql("types", "SELECT 1;")]
            [assembly: Ankus.PgSql("other", "SELECT 2;")]
            [Ankus.PgSchema("present", Id = "schema")]
            public static class Functions
            {
                [Ankus.PgFunction(Id = "function")] public static int Read() => 7;
            }
            """);
        AssertDeclaredProviderError(compilation, diagnostics, diagnosticId: diagnosticId);
        Assert.AreEqual(diagnosticId, Assert.ContainsSingle(diagnostics).Id);
    }

    /// <summary>
    /// Inline and file inventories participate in the same unique-provider namespace.
    /// </summary>
    [TestMethod]
    public void DeclaredTypeProvidersRejectDuplicateInlineAndFileClaims()
    {
        string project = Path.Join(AppContext.BaseDirectory, "provider-duplicate-project");
        var file = new SqlInput(Path.Join(project, "types.sql"), "SELECT 'file';");
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSql("inline", "SELECT 'inline';")]
            [assembly: Ankus.PgSqlFile("file", "types.sql")]
            [assembly: Ankus.PgSqlTypeProvider("inline", "item")]
            [assembly: Ankus.PgSqlTypeProvider("file", "item")]
            """, [file], new SqlOptions(project));
        AssertDeclaredProviderError(compilation, diagnostics);
        Assert.AreEqual(ProviderDuplicateError, Assert.ContainsSingle(diagnostics).GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Catalog identifier limits count UTF-8 bytes for both name and schema rather than UTF-16 code units.
    /// </summary>
    /// <param name="schema">Whether the boundary identifier is a schema.</param>
    /// <param name="bytes">Its exact UTF-8 byte length.</param>
    [TestMethod]
    [DataRow(false, 63)]
    [DataRow(false, 64)]
    [DataRow(true, 63)]
    [DataRow(true, 64)]
    public void DeclaredTypeProviderIdentifiersUseUtf8Boundaries(bool schema, int bytes)
    {
        string identifier = new string('é', 31) + (bytes == 63 ? "a" : "é");
        string argument = schema ? "\"item\", Schema = " + SymbolDisplay.FormatLiteral(identifier, true)
            : SymbolDisplay.FormatLiteral(identifier, true);
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "[assembly: Ankus.PgSql(\"types\", \"SELECT 1;\", Relocatable = true)]" +
            "[assembly: Ankus.PgSqlTypeProvider(\"types\", " + argument + ")]");
        if (bytes == 64)
        {
            AssertDeclaredProviderError(compilation, diagnostics, diagnosticId: schema ? "ANKUS392" : "ANKUS388");
            Assert.AreEqual(schema ? "ANKUS392" : "ANKUS388", Assert.ContainsSingle(diagnostics).Id);
        }
        else
        {
            AssertSqlControlCompilation(compilation, diagnostics);
            Assert.AreEqual("SELECT 1;\n", InstallationBody(compilation));
            Assert.AreEqual(schema ? "false" : "true", ManifestValue(compilation, "Ankus.Relocatable"));
        }
    }

    /// <summary>
    /// A complete-type provider can follow its own I/O function only when explicit edges establish that reverse order.
    /// </summary>
    /// <param name="path">The explicit path form.</param>
    [TestMethod]
    [DataRow("requires")]
    [DataRow("before")]
    [DataRow("mixed")]
    public void DeclaredTypeProvidersDeferOnlyExplicitReversePaths(string path)
    {
        string completion = path == "requires" ? ", Requires = [\"middle\"]" : string.Empty;
        string middle = path switch
        {
            "requires" => "Requires = [\"io\"]",
            "before" => "Before = [\"types\"]",
            _ => "Requires = [\"io\"], Before = [\"types\"]",
        };
        string shell = path == "before" ? ", Before = [\"io\"]" : string.Empty;
        string io = path == "before" ? string.Empty : "Requires = [\"shell\"], ";
        string before = path == "before" ? "[assembly: Ankus.PgSql(\"io-order\", \"SELECT 'bridge';\", Requires = [\"io\"], Before = [\"middle\"])]" : string.Empty;
        Compilation compilation = GenerateSqlControl($$"""
            [assembly: Ankus.PgSql("shell", "CREATE TYPE item;"{{shell}})]
            [assembly: Ankus.PgSql("types", "SELECT 'complete';"{{completion}})]
            [assembly: Ankus.PgSql("middle", "SELECT 'middle';", {{middle}})]
            [assembly: Ankus.PgSqlTypeProvider("types", "item")]
            {{before}}
            public static class Functions
            {
                [Ankus.PgFunction(Id = "io", {{io}}Sql = "SELECT 'io';")]
                [return: Ankus.PgSqlType("item")]
                public static Ankus.PgDatum? Input() => null;
                [Ankus.PgFunction(Sql = "SELECT 'consumer';")]
                public static int Read([Ankus.PgSqlType("item")] Ankus.PgDatum value) => 7;
            }
            """);
        Assert.AreEqual("CREATE TYPE item;\nSELECT 'io';\n" + (path == "before" ? "SELECT 'bridge';\n" : string.Empty) +
            "SELECT 'middle';\nSELECT 'complete';\nSELECT 'consumer';\n", InstallationBody(compilation));
    }

    /// <summary>
    /// A shell can itself provide availability while ordinary consumers explicitly wait for completion.
    /// </summary>
    [TestMethod]
    public void DeclaredTypeProvidersAllowShellProvidersWithoutInferringCompletion()
    {
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("shell", "CREATE TYPE item;")]
            [assembly: Ankus.PgSqlTypeProvider("shell", "item")]
            [assembly: Ankus.PgSql("complete", "SELECT 'complete';", Requires = ["io"])]
            public static class Functions
            {
                [Ankus.PgFunction(Id = "io", Sql = "SELECT 'io';")]
                [return: Ankus.PgSqlType("item")]
                public static Ankus.PgDatum? Input() => null;
                [Ankus.PgFunction(Requires = ["complete"], Sql = "SELECT 'consumer';")]
                public static int Read([Ankus.PgSqlType("item")] Ankus.PgDatum value) => 1;
            }
            """);
        Assert.AreEqual("CREATE TYPE item;\nSELECT 'io';\nSELECT 'complete';\nSELECT 'consumer';\n",
            InstallationBody(compilation));
    }

    /// <summary>
    /// Structural, schema and boundary paths do not authorize dropping inferred edges, and explicit cycles remain errors.
    /// </summary>
    /// <param name="kind">The otherwise cyclic path.</param>
    [TestMethod]
    [DataRow("hard")]
    [DataRow("unrelated-hard")]
    [DataRow("operator")]
    [DataRow("replacement")]
    [DataRow("schema")]
    [DataRow("final")]
    [DataRow("generated")]
    public void DeclaredTypeProvidersPreserveHardAndUnjustifiedCycles(string kind)
    {
        string prefix = "[assembly: Ankus.PgSqlTypeProvider(\"types\", \"item\")]";
        string block = "[assembly: Ankus.PgSql(\"types\", \"SELECT 'provider';\", Requires = [\"consumer\"])]";
        string options = "Id = \"consumer\"";
        string attached = string.Empty;
        string extra = string.Empty;
        switch (kind)
        {
            case "hard":
                options += ", Requires = [\"types\"]";
                break;
            case "unrelated-hard":
                extra = "[assembly: Ankus.PgSql(\"a\", \"SELECT 'a';\", Requires = [\"b\"])]" +
                    "[assembly: Ankus.PgSql(\"b\", \"SELECT 'b';\", Requires = [\"a\"])]";
                break;
            case "operator":
            case "replacement":
                block = "[assembly: Ankus.PgSql(\"types\", \"SELECT 'provider';\", Requires = [\"operator\"])]";
                attached = "[Ankus.PgOperator(\"~\", Id = \"operator\")]";
                if (kind == "replacement")
                {
                    options += ", Sql = \"SELECT 'bundle';\"";
                }

                break;
            case "schema":
                block = "[assembly: Ankus.PgSql(\"types\", \"SELECT 'provider';\", Requires = [\"placed\"])]";
                extra = "[Ankus.PgSchema(\"s\", Requires = [\"consumer\"])] public static class Placed { " +
                    "[Ankus.PgFunction(Id = \"placed\")] public static int Other() => 2; }";
                break;
            case "final":
                block = "[assembly: Ankus.PgSql(\"types\", \"SELECT 'provider';\", Order = Ankus.PgSqlOrder.Finalize)]";
                break;
            case "generated":
                prefix = string.Empty;
                block = DeclaredProviderGeneratedType("enum", "disabled", "Requires = [\"consumer\"]");
                break;
        }

        string declarations = "public static class Functions { [Ankus.PgFunction(" + options + ")]" + attached +
            "public static int Read([Ankus.PgSqlType(\"item\")] Ankus.PgDatum value) => 1; }";
        string source = kind == "generated" ? block + declarations : prefix + block +
            (kind == "schema" ? string.Empty : extra) + declarations + (kind == "schema" ? extra : string.Empty);
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        (string span, string cycle) = kind switch
        {
            "hard" => ("\"types\"", "consumer -> types -> consumer"),
            "unrelated-hard" => ("\"b\"", "a -> b -> a"),
            "operator" or "replacement" => ("\"operator\"", "consumer -> types -> operator -> consumer"),
            "schema" => ("\"consumer\"", "Placed -> consumer -> types -> placed -> Placed"),
            "final" => ("Read", "consumer -> types -> consumer"),
            _ => ("\"consumer\"", "consumer -> declared -> consumer"),
        };
        AssertSqlControlGraphError(compilation, diagnostics, "ANKUS499", span, cycle);
        AssertDeclaredProviderError(compilation, diagnostics, "ANKUS499");
    }

    /// <summary>
    /// Provider prerequisites coexist with replacement lifting, disabled anchors and global installation boundaries.
    /// </summary>
    /// <param name="disabled">Whether the entire function/operator/cast bundle emits no SQL.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DeclaredTypeProvidersComposeWithBundlesAndBoundaries(bool disabled)
    {
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("bootstrap", "SELECT 'first';", Order = Ankus.PgSqlOrder.Bootstrap)]
            [assembly: Ankus.PgSql("types", "SELECT 'provider';")]
            [assembly: Ankus.PgSqlTypeProvider("types", "item")]
            [assembly: Ankus.PgSql("z-before", "SELECT 'before';", Before = ["operator", "cast"])]
            [assembly: Ankus.PgSql("after", "SELECT 'after';", Requires = ["operator", "cast"])]
            [assembly: Ankus.PgSql("final", "SELECT 'last';", Order = Ankus.PgSqlOrder.Finalize)]
            public static class Functions
            {
                [Ankus.PgFunction(POLICY), Ankus.PgOperator("~", Id = "operator"), Ankus.PgCast(Id = "cast")]
                public static int Read([Ankus.PgSqlType("item")] Ankus.PgDatum value) => 1;
            }
            """.Replace("POLICY", disabled ? "GenerateSql = false" : "Sql = \"SELECT 'bundle';\"", StringComparison.Ordinal));
        Assert.AreEqual("SELECT 'first';\nSELECT 'provider';\nSELECT 'before';\n" +
            (disabled ? string.Empty : "SELECT 'bundle';\n") + "SELECT 'after';\nSELECT 'last';\n",
            InstallationBody(compilation));
        Assert.HasCount(1, SqlControlExports(compilation));
    }

    /// <summary>
    /// Provider schema and custom SQL relocation assertions compose even when no signature references the type.
    /// </summary>
    /// <param name="schema">The provider schema.</param>
    /// <param name="sqlRelocatable">Whether its SQL claims relocation support.</param>
    /// <param name="expected">The resulting extension policy.</param>
    [TestMethod]
    [DataRow(null, false, "false")]
    [DataRow(null, true, "true")]
    [DataRow("fixed", false, "false")]
    [DataRow("fixed", true, "false")]
    public void DeclaredTypeProviderSchemasOrderCreationAndConstrainRelocation(string? schema, bool sqlRelocatable, string expected)
    {
        string attributes = "[assembly: Ankus.PgSql(\"types\", \"SELECT 'provider';\", Relocatable = " +
            (sqlRelocatable ? "true" : "false") + ")]" +
            "[assembly: Ankus.PgSqlTypeProvider(\"types\", \"item\"" + DeclaredProviderSchema(schema) + ")]";
        Compilation compilation = GenerateSqlControl(attributes +
            (schema is null ? string.Empty :
                "[assembly: Ankus.PgSql(\"z-prerequisite\", \"SELECT 'schema prerequisite';\", Relocatable = true)]" +
                "[Ankus.PgSchema(\"fixed\", Requires = [\"z-prerequisite\"])] public static class Schema;"));
        Assert.AreEqual(expected, ManifestValue(compilation, "Ankus.Relocatable"));
        Assert.AreEqual((schema is null ? string.Empty : "SELECT 'schema prerequisite';\nCREATE SCHEMA IF NOT EXISTS \"fixed\";\n") + "SELECT 'provider';\n",
            InstallationBody(compilation));
    }

    /// <summary>
    /// Builds one independently checked consumer contract with optional explicit prerequisites.
    /// </summary>
    private static string DeclaredProviderConsumer(string kind, string options)
    {
        string function = "[Ankus.PgFunction(" + options + ")]";
        string declaration = kind switch
        {
            "raw-parameter" => "public static int Read([Ankus.PgSqlType(\"item\")] Ankus.PgDatum? value) => 7;",
            "raw-result" => "[return: Ankus.PgSqlType(\"item\")] public static Ankus.PgDatum? Read() => null;",
            "raw-array" => "[return: Ankus.PgSqlType(\"item\", IsArray = true)] public static Ankus.PgDatum? Read(" +
                "[Ankus.PgSqlType(\"item\", IsArray = true)] Ankus.PgDatum? value) => value;",
            "raw-set" => "[return: Ankus.PgSqlType(\"item\")] public static System.Collections.Generic.IEnumerable<Ankus.PgDatum?> Read() => [null];",
            "raw-table" => "[return: Ankus.PgSqlType(\"item\", Column = \"first\"), Ankus.PgSqlType(\"item\", IsArray = true, Column = \"second\")] " +
                "public static System.Collections.Generic.IEnumerable<(Ankus.PgDatum? First, Ankus.PgDatum? Second)> Read() => [(null, null)];",
            "composite-parameter" => "public static int Read([Ankus.PgCompositeType(\"item\")] Ankus.PgHeapTuple? value) => 7;",
            "composite-result" => "[return: Ankus.PgCompositeType(\"item\")] public static Ankus.PgHeapTuple? Read() => null;",
            "composite-vector" => "[return: Ankus.PgCompositeType(\"item\")] public static Ankus.PgHeapTuple?[]? Read(" +
                "[Ankus.PgCompositeType(\"item\")] Ankus.PgHeapTuple?[]? value) => value;",
            "composite-shaped" => "[return: Ankus.PgCompositeType(\"item\")] public static Ankus.PgArray<Ankus.PgHeapTuple?>? Read(" +
                "[Ankus.PgCompositeType(\"item\")] Ankus.PgArray<Ankus.PgHeapTuple?>? value) => value;",
            "composite-set" => "[return: Ankus.PgCompositeType(\"item\")] public static System.Collections.Generic.IEnumerable<Ankus.PgHeapTuple?> Read() => [null];",
            "composite-table" => "[return: Ankus.PgCompositeType(\"item\", Column = \"first\"), Ankus.PgCompositeType(\"item\", Column = \"second\")] " +
                "public static System.Collections.Generic.IEnumerable<(Ankus.PgHeapTuple? First, Ankus.PgArray<Ankus.PgHeapTuple?>? Second)> Read() => [(null, null)];",
            "aggregate" => "[return: Ankus.PgSqlType(\"item\")] public static Ankus.PgDatum? Transition(Ankus.PgAggregateContext context," +
                "[Ankus.PgSqlType(\"item\")] Ankus.PgDatum? state, [Ankus.PgSqlType(\"item\")] Ankus.PgDatum? value) => state ?? value;",
            "operator" => "[Ankus.PgOperator(\"===\")] public static bool Read([Ankus.PgSqlType(\"item\")] Ankus.PgDatum left," +
                "[Ankus.PgSqlType(\"item\")] Ankus.PgDatum right) => left.TypeOid == right.TypeOid;",
            "cast" => "[Ankus.PgCast] public static int Read([Ankus.PgSqlType(\"item\")] Ankus.PgDatum value) => 7;",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return kind == "aggregate"
            ? "[Ankus.PgAggregate(" + options + ")] public sealed class First : Ankus.IPgAggregate<Ankus.PgDatum?,Ankus.PgDatum?> { " + declaration + " }"
            : "public static class Functions { " + function + declaration + " }";
    }

    /// <summary>
    /// Builds a generated identity without coupling its managed name to the consumer's SQL name.
    /// </summary>
    private static string DeclaredProviderGeneratedType(string kind, string policy, string options)
    {
        string setting = policy switch
        {
            "disabled" => "GenerateSql = false",
            "replacement" => "Sql = \"SELECT 'generated';\", SqlRelocatable = true",
            _ => "Sql = null",
        };
        string attribute = "[Ankus." + (kind == "enum" ? "PgEnum" : "PgType") +
            "(Name = \"item\", Id = \"declared\", " + setting + (options.Length == 0 ? string.Empty : ", " + options) + ")]";
        return attribute + (kind == "enum" ? "public enum Zulu { First, Second }" : "public readonly record struct Zulu(int Number);");
    }

    /// <summary>
    /// Emits a C# optional schema argument without interpreting its identifier text.
    /// </summary>
    private static string DeclaredProviderSchema(string? schema)
        => schema is null ? string.Empty : ", Schema = " + SymbolDisplay.FormatLiteral(schema, true);

    /// <summary>
    /// Requires graph diagnostics and complete suppression of both managed dispatch and every manifest artifact.
    /// </summary>
    private void AssertDeclaredProviderError(Compilation compilation, ImmutableArray<Diagnostic> diagnostics, string diagnosticId = "ANKUS394")
    {
        Assert.IsNotEmpty(diagnostics);
        foreach (Diagnostic diagnostic in diagnostics)
        {
            Assert.AreEqual(diagnosticId, diagnostic.Id);
            Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.IsTrue(diagnostic.Location.IsInSource);
        }

        AssertNoSqlManifest(compilation);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.IsNull(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers"));
        Assert.IsEmpty(compilation.Assembly.GetAttributes().Where(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            ((string?)attribute.ConstructorArguments[0].Value)?.StartsWith("Ankus.", StringComparison.Ordinal) == true));
    }
}
