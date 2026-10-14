using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Explicit default controls preserve SQL and both sides of every supported callback boundary.
    /// </summary>
    /// <param name="kind">The callback declaration kind.</param>
    /// <param name="declaration">An independently expected SQL signature.</param>
    [TestMethod]
    [DataRow("scalar", "CREATE FUNCTION \"echo\"(\"value\" integer)\nRETURNS integer")]
    [DataRow("set", "CREATE FUNCTION \"echo\"(\"value\" integer)\nRETURNS SETOF integer")]
    [DataRow("table", "CREATE FUNCTION \"echo\"(\"value\" integer)\nRETURNS TABLE (\"number\" integer, \"text\" text)")]
    [DataRow("trigger", "CREATE FUNCTION \"echo\"()\nRETURNS trigger")]
    [DataRow("event", "CREATE FUNCTION \"echo\"()\nRETURNS event_trigger")]
    [DataRow("helper", "CREATE FUNCTION \"values_transition\"(\"state\" bigint, \"value\" integer)\nRETURNS bigint")]
    public void SqlGenerationDefaultPreservesAllWrapperKinds(string kind, string declaration)
    {
        Compilation original = GenerateSqlControl(SqlControlSource(kind, string.Empty));
        Compilation explicitDefaults = GenerateSqlControl(SqlControlSource(kind, "GenerateSql = true, Sql = null, SqlRelocatable = true"));
        Assert.Contains(declaration, InstallationBody(original));
        Assert.AreEqual(InstallationBody(original), InstallationBody(explicitDefaults));
        Assert.AreEqual("true", ManifestValue(explicitDefaults, "Ankus.Relocatable"));
        AssertSqlControlBoundary(original, explicitDefaults);
    }

    /// <summary>
    /// Suppression and replacement retain native and managed callbacks, including aggregate support functions.
    /// </summary>
    /// <param name="kind">The callback declaration kind.</param>
    [TestMethod]
    [DataRow("scalar")]
    [DataRow("set")]
    [DataRow("table")]
    [DataRow("trigger")]
    [DataRow("event")]
    [DataRow("helper")]
    public void SqlGenerationControlsPreserveEveryWrapperKind(string kind)
    {
        Compilation original = GenerateSqlControl(SqlControlSource(kind, string.Empty));
        Compilation disabled = GenerateSqlControl(SqlControlSource(kind, "GenerateSql = false"));
        Compilation replacement = GenerateSqlControl(SqlControlSource(kind,
            "Sql = \"SELECT '@FUNCTION_NAME@', '@MODULE_PATHNAME@';\", SqlRelocatable = true"));
        AssertSqlControlBoundary(original, disabled);
        AssertSqlControlBoundary(original, replacement);
        string export = Assert.ContainsSingle(SqlControlExports(original));
        string replacedSql = InstallationBody(replacement);
        string disabledSql = InstallationBody(disabled);
        Assert.DoesNotContain("CREATE FUNCTION", replacedSql);
        Assert.DoesNotContain("CREATE FUNCTION", disabledSql);
        Assert.StartsWith($"SELECT '{export}', 'MODULE_PATHNAME';\n", replacedSql);
        Assert.AreEqual("true", ManifestValue(disabled, "Ankus.Relocatable"));
        Assert.AreEqual("true", ManifestValue(replacement, "Ankus.Relocatable"));
        if (kind == "helper")
        {
            const string aggregate = "CREATE AGGREGATE \"values\"(\"value\" integer) (\n    SFUNC = \"values_transition\",\n    STYPE = bigint,\n" +
                "    FINALFUNC_MODIFY = READ_ONLY,\n    INITCOND = E'0',\n    PARALLEL = UNSAFE\n);\n";
            Assert.AreEqual(aggregate, disabledSql);
            Assert.AreEqual($"SELECT '{export}', 'MODULE_PATHNAME';\n" + aggregate, replacedSql);
        }
        else
        {
            Assert.AreEqual("-- No installable objects declared.\n", disabledSql);
            Assert.AreEqual($"SELECT '{export}', 'MODULE_PATHNAME';\n", replacedSql);
        }
    }

    /// <summary>
    /// Empty and whitespace literals remain replacements, and comment text is preserved without restoring generated SQL.
    /// </summary>
    /// <param name="literal">The exact replacement text.</param>
    /// <param name="expected">The complete independently expected installation script.</param>
    [TestMethod]
    [DataRow("", "-- No installable objects declared.\n")]
    [DataRow(" \t", " \t\n")]
    [DataRow("-- comment only", "-- comment only\n")]
    [DataRow("/* block */\n", "/* block */\n")]
    [DataRow("SELECT 'unrelated';", "SELECT 'unrelated';\n")]
    public void SqlGenerationEmptyAndLiteralReplacementsRemainExact(string literal, string expected)
    {
        Compilation original = GenerateSqlControl(SqlControlSource("scalar", string.Empty));
        Compilation replacement = GenerateSqlControl(SqlControlSource("scalar", "Sql = " + SymbolDisplay.FormatLiteral(literal, quote: true)));
        Assert.AreEqual(expected, InstallationBody(replacement));
        Assert.AreEqual("false", ManifestValue(replacement, "Ankus.Relocatable"));
        AssertSqlControlBoundary(original, replacement);
    }

    /// <summary>
    /// Only the documented literal tokens change, including repeated occurrences inside comments and quoted text.
    /// </summary>
    [TestMethod]
    public void SqlGenerationLiteralUsesExactWrapperAndModulePlaceholders()
    {
        const string literal = "SELECT '@MODULE_PATHNAME@', '@FUNCTION_NAME@', $$quotes' ; \\ café 😀 @FUNCTION_NAME@$$, '@UNKNOWN@', '{value}';\r\n" +
            "-- @MODULE_PATHNAME@ @FUNCTION_NAME@ @function_name@";
        Compilation compilation = GenerateSqlControl(SqlControlSource("scalar", "Sql = " + SymbolDisplay.FormatLiteral(literal, quote: true)));
        string export = Assert.ContainsSingle(SqlControlExports(compilation));
        Assert.AreEqual($"SELECT 'MODULE_PATHNAME', '{export}', $$quotes' ; \\ café 😀 {export}$$, '@UNKNOWN@', '{{value}}';\r\n" +
            $"-- MODULE_PATHNAME {export} @function_name@\n", InstallationBody(compilation));
        Assert.Contains($"PG_FUNCTION_INFO_V1({export});", ManifestValue(compilation, "Ankus.NativeSource"));
        Assert.Contains("pg_finfo_" + export, ManifestValue(compilation, "Ankus.Exports").Split('\n'));
    }

    /// <summary>
    /// Overloads use their own native entry points and retain deterministic identities when source order changes.
    /// </summary>
    [TestMethod]
    public void SqlGenerationOverloadsRemainDistinctAndDeterministic()
    {
        const string integer = "[Ankus.PgFunction(Sql = \"SELECT 'integer:@FUNCTION_NAME@';\")] public static int Echo(int value) => value;";
        const string bigint = "[Ankus.PgFunction(Sql = \"SELECT 'bigint:@FUNCTION_NAME@';\")] public static long Echo(long value) => value;";
        Compilation first = GenerateSqlControl("public static class Functions {" + integer + bigint + "}");
        Compilation reversed = GenerateSqlControl("public static class Functions {" + bigint + integer + "}");
        Assert.AreEqual(InstallationBody(first), InstallationBody(reversed));
        AssertSqlControlBoundary(first, reversed);
        string[] exports = SqlControlExports(first);
        Assert.HasCount(2, exports);
        Assert.HasCount(2, exports.Distinct(StringComparer.Ordinal));
        INamedTypeSymbol dispatchers = first.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!;
        (string Type, string Argument)[] contracts = [("integer", "(int)arguments[0].Integral"), ("bigint", "(arguments[0].Integral)")];
        foreach ((string type, string argument) in contracts)
        {
            IMethodSymbol callback = Assert.ContainsSingle(dispatchers.GetMembers().OfType<IMethodSymbol>().Where(method =>
                method.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax(context.CancellationToken).ToString().Contains(argument, StringComparison.Ordinal))));
            string export = callback.Name.Replace("ankus_managed_", "ankus_fn_", StringComparison.Ordinal);
            Assert.Contains(export, exports);
            Assert.Contains($"SELECT '{type}:{export}';\n", InstallationBody(first));
        }
    }

    /// <summary>
    /// Attribute-only edits invalidate script and relocation metadata without changing callback identities.
    /// </summary>
    [TestMethod]
    public void SqlGenerationAttributeChangesInvalidateIncrementalOutput()
    {
        CSharpCompilation input = CSharpCompilation.Create("GeneratorTest", references: s_references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new PgFunctionGenerator().AsSourceGenerator());
        string? originalNative = null;
        string? originalExports = null;
        (string Options, string? Expected, string Relocatable)[] stages =
        [
            (string.Empty, null, "true"),
            ("Sql = \"SELECT 'first';\"", "SELECT 'first';\n", "false"),
            ("Sql = \"SELECT 'second';\", SqlRelocatable = true", "SELECT 'second';\n", "true"),
            ("GenerateSql = false", "-- No installable objects declared.\n", "true"),
            ("Sql = null", null, "true"),
        ];
        foreach ((string options, string? expected, string relocatable) in stages)
        {
            CSharpCompilation current = input.AddSyntaxTrees(CSharpSyntaxTree.ParseText(SqlControlSource("scalar", options), cancellationToken: context.CancellationToken));
            driver = driver.RunGeneratorsAndUpdateCompilation(current, out Compilation output, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
            AssertSqlControlCompilation(output, diagnostics);
            string native = ManifestValue(output, "Ankus.NativeSource");
            string exports = ManifestValue(output, "Ankus.Exports");
            originalNative ??= native;
            originalExports ??= exports;
            Assert.AreEqual(originalNative, native);
            Assert.AreEqual(originalExports, exports);
            Assert.AreEqual(relocatable, ManifestValue(output, "Ankus.Relocatable"));
            string sql = InstallationBody(output);
            if (expected is null)
            {
                Assert.StartsWith("CREATE FUNCTION \"echo\"(\"value\" integer)\nRETURNS integer", sql);
                Assert.DoesNotContain("SELECT 'first'", sql);
                Assert.DoesNotContain("SELECT 'second'", sql);
            }
            else
            {
                Assert.AreEqual(expected, sql);
            }
        }
    }

    /// <summary>
    /// A replacement owns attached operator and cast SQL while all related IDs and prerequisites still resolve.
    /// </summary>
    [TestMethod]
    public void SqlGenerationBundlePreservesRelatedAliasesAndPrerequisites()
    {
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("first", "SELECT 'first';", Order = Ankus.PgSqlOrder.Bootstrap)]
            [assembly: Ankus.PgSql("prepare", "SELECT 'prepare';")]
            [assembly: Ankus.PgSql("prepare-operator", "SELECT 'before operator';", Before = new[] { "operator" })]
            [assembly: Ankus.PgSql("prepare-cast", "SELECT 'before cast';", Before = new[] { "cast" })]
            [assembly: Ankus.PgSql("after-function", "SELECT 'after function';", Requires = new[] { "function" })]
            [assembly: Ankus.PgSql("after-operator", "SELECT 'after operator';", Requires = new[] { "operator" })]
            [assembly: Ankus.PgSql("after-cast", "SELECT 'after cast';", Requires = new[] { "cast" })]
            [assembly: Ankus.PgSql("last", "SELECT 'last';", Order = Ankus.PgSqlOrder.Finalize)]
            public static class Functions
            {
                [Ankus.PgFunction(Id = "function", Sql = "SELECT 'whole replacement';")]
                [Ankus.PgOperator("@", Id = "operator", Requires = new[] { "function", "prepare" })]
                [Ankus.PgCast(Id = "cast", Requires = new[] { "operator" })]
                public static string Render(int value, int modifier) => value.ToString();
            }
            """);
        string sql = InstallationBody(compilation);
        Assert.StartsWith("SELECT 'first';\n", sql);
        AssertSqlControlBefore(sql, "SELECT 'prepare';", "SELECT 'whole replacement';");
        AssertSqlControlBefore(sql, "SELECT 'before operator';", "SELECT 'whole replacement';");
        AssertSqlControlBefore(sql, "SELECT 'before cast';", "SELECT 'whole replacement';");
        Assert.EndsWith("SELECT 'last';\n", sql);
        Assert.DoesNotContain("CREATE FUNCTION", sql);
        Assert.DoesNotContain("CREATE OPERATOR", sql);
        Assert.DoesNotContain("CREATE CAST", sql);
        string[] dependents = ["SELECT 'after function';", "SELECT 'after operator';", "SELECT 'after cast';"];
        foreach (string dependent in dependents)
        {
            AssertSqlControlBefore(sql, "SELECT 'whole replacement';", dependent);
        }

        Assert.HasCount(1, SqlControlExports(compilation));
    }

    /// <summary>
    /// Disabled declarations retain legal original interleaving while replacement diagnoses an indivisible bundle cycle.
    /// </summary>
    /// <param name="incomingBefore">Whether an incoming Before edge supplies the middle prerequisite.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SqlGenerationDisabledPreservesAnchorsWithoutLiftingRequirements(bool incomingBefore)
    {
        const string source = """
            [assembly: Ankus.PgSql("middle", "SELECT 'middle';", Requires = new[] { "function" } BEFORE)]
            [assembly: Ankus.PgSql("after", "SELECT 'after';", Requires = new[] { "cast" })]
            public static class Functions
            {
                [Ankus.PgFunction(Id = "function", CONTROL)]
                [Ankus.PgOperator("@", Id = "operator" REQUIRES)]
                [Ankus.PgCast(Id = "cast", Requires = new[] { "operator" })]
                public static string Render(int value, int modifier) => value.ToString();
            }
            """;
        string graph = source.Replace("BEFORE", incomingBefore ? ", Before = new[] { \"operator\" }" : string.Empty, StringComparison.Ordinal)
            .Replace("REQUIRES", incomingBefore ? string.Empty : ", Requires = new[] { \"middle\" }", StringComparison.Ordinal);
        Compilation disabled = GenerateSqlControl(graph.Replace("CONTROL", "GenerateSql = false", StringComparison.Ordinal));
        Assert.AreEqual("SELECT 'middle';\nSELECT 'after';\n", InstallationBody(disabled));
        (Compilation replacement, ImmutableArray<Diagnostic> diagnostics) = Generate(graph.Replace("CONTROL", "Sql = \"SELECT 'replacement';\"", StringComparison.Ordinal));
        AssertSqlControlGraphError(replacement, diagnostics, "ANKUS499", "\"function\"", "function -> middle -> function");
        Assert.StartsWith("Ankus.PgSql(\"middle\"", DiagnosticAttribute(Assert.ContainsSingle(diagnostics)));
    }

    /// <summary>
    /// Empty anchors preserve original graph errors rather than silently dropping identifiers or internal edges.
    /// </summary>
    /// <param name="options">The function SQL policy.</param>
    /// <param name="operatorOptions">The attached operator graph options.</param>
    /// <param name="castOptions">The attached cast graph options.</param>
    /// <param name="expected">The fixed graph diagnostic.</param>
    /// <param name="span">The exact authored value to correct.</param>
    /// <param name="argument">The authored identity in the fixed message.</param>
    [TestMethod]
    [DataRow("GenerateSql = false", "Id = \"operator\", Requires = new[] { \"missing\" }", "Id = \"cast\"", "ANKUS498", "\"missing\"", "missing")]
    [DataRow("Sql = \"SELECT 1;\"", "Id = \"operator\", Requires = new[] { \"missing\" }", "Id = \"cast\"", "ANKUS498", "\"missing\"", "missing")]
    [DataRow("GenerateSql = false", "Id = \"same\"", "Id = \"same\"", "ANKUS497", "\"same\"", "same")]
    [DataRow("Sql = \"SELECT 1;\"", "Id = \"same\"", "Id = \"same\"", "ANKUS497", "\"same\"", "same")]
    [DataRow("GenerateSql = false", "Id = \"operator\", Requires = new[] { \"cast\" }", "Id = \"cast\", Requires = new[] { \"operator\" }",
        "ANKUS499", "\"operator\"", "cast -> operator -> cast")]
    [DataRow("Sql = \"SELECT 1;\"", "Id = \"operator\", Requires = new[] { \"cast\" }", "Id = \"cast\", Requires = new[] { \"operator\" }",
        "ANKUS499", "\"operator\"", "cast -> operator -> cast")]
    public void SqlGenerationRetainsInvalidRelatedGraphs(string options, string operatorOptions, string castOptions, string expected, string span, string argument)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            public static class Functions
            {
                [Ankus.PgFunction(Id = "function", {{options}})]
                [Ankus.PgOperator("@", {{operatorOptions}})]
                [Ankus.PgCast({{castOptions}})]
                public static string Render(int value, int modifier) => value.ToString();
            }
            """);
        AssertSqlControlGraphError(compilation, diagnostics, expected, span, argument);
    }

    /// <summary>
    /// Custom type, enum array and schema prerequisites still precede replacement functions with unrelated SQL text.
    /// </summary>
    [TestMethod]
    public void SqlGenerationReplacementRetainsAutomaticTypeAndSchemaEdges()
    {
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("after", "SELECT 'after';", Requires = new[] { "function" })]
            [Ankus.PgSchema("ordered", Id = "schema")]
            public static class Types
            {
                [Ankus.PgEnum(Id = "enum")] public enum Mood { First, Last }
                [Ankus.PgType(Id = "type")] public readonly record struct Value(int Number);
                [Ankus.PgFunction(Id = "function", Sql = "SELECT 'replacement';")]
                public static Value Convert(Ankus.PgArray<Mood?> input) => new(input.Count);
            }
            """);
        string sql = InstallationBody(compilation);
        AssertSqlControlBefore(sql, "CREATE SCHEMA IF NOT EXISTS \"ordered\";", "CREATE TYPE \"ordered\".\"mood\" AS ENUM");
        AssertSqlControlBefore(sql, "CREATE TYPE \"ordered\".\"mood\" AS ENUM", "SELECT 'replacement';");
        AssertSqlControlBefore(sql, "CREATE TYPE \"ordered\".\"value\" (", "SELECT 'replacement';");
        AssertSqlControlBefore(sql, "SELECT 'replacement';", "SELECT 'after';");
        Assert.DoesNotContain("CREATE FUNCTION \"ordered\".\"convert\"", sql);
        Assert.AreEqual("false", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// A helper shared by ordinary and moving aggregate roles receives one replacement and retains aggregate SQL.
    /// </summary>
    [TestMethod]
    public void SqlGenerationAggregateHelperReplacementPreservesIndependentMovingRoles()
    {
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("before", "SELECT 'before';")]
            [assembly: Ankus.PgSql("another", "SELECT 'another parent prerequisite';")]
            [assembly: Ankus.PgSql("helper-before", "SELECT 'helper prerequisite';")]
            [assembly: Ankus.PgSql("after", "SELECT 'after';", Requires = new[] { "aggregate" })]
            [Ankus.PgAggregate(Id = "aggregate", InitialCondition = "0", MovingInitialCondition = "0", Requires = new[] { "before", "another" })]
            public sealed class Shared : Ankus.IPgAggregate<int,int>,Ankus.IPgMovingAggregate<int?,int>
            {
                [Ankus.PgFunction(Name = "add_value", Sql = "SELECT '@FUNCTION_NAME@';", Id = "helper", Requires = new[] { "helper-before" })]
                public static int Transition(Ankus.PgAggregateContext context,int state, int value) => Add(state,value);
                [Ankus.PgFunction(Name = "moving_add_value",NullInput=Ankus.PgNullInput.Strict)]
                public static int? MovingTransition(Ankus.PgAggregateContext context,int? state, int value) => Add(state!.Value,value);
                [Ankus.PgFunction(NullInput=Ankus.PgNullInput.Strict)]
                public static int? MovingInverse(Ankus.PgAggregateContext context,int? state, int value) => state - value;
                private static int Add(int state,int value) => state+value;
            }
            """);
        string sql = InstallationBody(compilation);
        Assert.HasCount(3, SqlControlExports(compilation));
        string[] replacements = [.. sql.Split('\n').Where(static line => line.StartsWith("SELECT 'ankus_fn_", StringComparison.Ordinal))];
        Assert.ContainsSingle(replacements);
        IMethodSymbol transition = AggregateCallback(compilation, "transition");
        string transitionExport = transition.Name.Replace("ankus_managed_", "ankus_fn_", StringComparison.Ordinal);
        Assert.AreEqual($"SELECT '{transitionExport}';", replacements[0]);
        Assert.Contains(transitionExport, SqlControlExports(compilation));
        Assert.DoesNotContain("CREATE FUNCTION \"add_value\"", sql);
        Assert.Contains("CREATE FUNCTION \"shared_moving_inverse\"", sql);
        Assert.Contains("CREATE FUNCTION \"moving_add_value\"", sql);
        Assert.Contains("SFUNC = \"add_value\",", sql);
        Assert.Contains("MSFUNC = \"moving_add_value\",", sql);
        AssertSqlControlBefore(sql, "SELECT 'before';", replacements[0]);
        AssertSqlControlBefore(sql, "SELECT 'another parent prerequisite';", replacements[0]);
        AssertSqlControlBefore(sql, "SELECT 'helper prerequisite';", replacements[0]);
        AssertSqlControlBefore(sql, replacements[0], "CREATE AGGREGATE \"shared\"");
        AssertSqlControlBefore(sql, "CREATE AGGREGATE \"shared\"", "SELECT 'after';");
    }

    /// <summary>
    /// Relocation considers only active replacements and combines all fixed schema and custom SQL policies.
    /// </summary>
    /// <param name="options">The primary function policy.</param>
    /// <param name="other">An additional declaration or assembly block.</param>
    /// <param name="expected">The exact resulting relocation metadata.</param>
    [TestMethod]
    [DataRow("", "", "true")]
    [DataRow("GenerateSql = false", "", "true")]
    [DataRow("GenerateSql = false, SqlRelocatable = true", "", "true")]
    [DataRow("Sql = null, SqlRelocatable = false", "", "true")]
    [DataRow("Sql = \"\"", "", "false")]
    [DataRow("Sql = \"SELECT 1;\"", "", "false")]
    [DataRow("Sql = \"SELECT 1;\", SqlRelocatable = true", "", "true")]
    [DataRow("Sql = \"SELECT 1;\", SqlRelocatable = true", "[assembly: Ankus.PgSql(\"other\", \"SELECT 2;\")]", "false")]
    [DataRow("Sql = \"SELECT 1;\", SqlRelocatable = true", "[assembly: Ankus.PgSql(\"other\", \"SELECT 2;\", Relocatable = true)]", "true")]
    [DataRow("Sql = \"SELECT 1;\", SqlRelocatable = true", "[Ankus.PgSchema(\"fixed\")] public static class Fixed;", "false")]
    [DataRow("Sql = \"SELECT 1;\", SqlRelocatable = true", "public static class Other { [Ankus.PgFunction(Sql = \"SELECT 2;\")] public static int Second() => 2; }", "false")]
    public void SqlGenerationRelocationRequiresEveryCustomDeclaration(string options, string other, string expected)
    {
        Compilation compilation = GenerateSqlControl(other + SqlControlSource("scalar", options));
        Assert.AreEqual(expected, ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Contradictory controls and malformed text reject the whole installation, even when another valid function exists.
    /// </summary>
    /// <param name="options">The invalid function attribute options.</param>
    /// <param name="expected">The fixed replacement diagnostic.</param>
    /// <param name="span">The exact authored option value to correct.</param>
    [TestMethod]
    [DataRow("GenerateSql = false, Sql = \"SELECT 1;\"", "ANKUS502", "false")]
    [DataRow("Sql = \"\", GenerateSql = false", "ANKUS502", "false")]
    [DataRow("Sql = \"SELECT '\\0';\"", "ANKUS503", "\"SELECT '\\0';\"")]
    [DataRow("Sql = \"SELECT '\\ud800';\"", "ANKUS504", "\"SELECT '\\ud800';\"")]
    [DataRow("Sql = \"SELECT '\\udfff';\"", "ANKUS504", "\"SELECT '\\udfff';\"")]
    [DataRow("Sql = \"SELECT '\\0\\ud800';\"", "ANKUS503", "\"SELECT '\\0\\ud800';\"")]
    public void InvalidSqlGenerationOptionsAreDiagnosed(string options, string expected, string span)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(SqlControlSource("scalar", options) +
            "public static class Other { [Ankus.PgFunction] public static int Good() => 7; }");
        AssertSqlControlGraphError(compilation, diagnostics, expected, span);
    }

    /// <summary>
    /// Specialized paths reject contradictory options through the same policy as ordinary scalar functions.
    /// </summary>
    /// <param name="kind">The callback declaration kind.</param>
    [TestMethod]
    [DataRow("set")]
    [DataRow("table")]
    [DataRow("trigger")]
    [DataRow("event")]
    [DataRow("helper")]
    public void SqlGenerationSpecializedCallbacksRejectContradictoryOptions(string kind)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(SqlControlSource(kind, "GenerateSql = false, Sql = \"\""));
        AssertSqlControlGraphError(compilation, diagnostics, "ANKUS502", "false");
    }

    /// <summary>
    /// SQL controls preserve managed ABI, nullability and attached operator validation instead of enabling unsafe wrappers.
    /// </summary>
    /// <param name="declaration">The invalid managed declaration.</param>
    /// <param name="id">The existing diagnostic identity.</param>
    [TestMethod]
    [DataRow("[Ankus.PgFunction(GenerateSql = false)] public static System.Uri Bad() => new(\"https://example.com\");", "ANKUS039")]
    [DataRow("[Ankus.PgFunction(Sql = \"\")] public static int Bad(ref int value) => value;", "ANKUS038")]
    [DataRow("[Ankus.PgFunction(Sql = \"SELECT 1;\", NullInput = Ankus.PgNullInput.CalledOnNull)] public static int Bad(int value) => value;", "ANKUS049")]
    [DataRow("[Ankus.PgFunction(GenerateSql = false), Ankus.PgOperator(\"@\")] public static int Bad() => 1;", "ANKUS066")]
    [DataRow("[Ankus.PgFunction(Sql = \"SELECT 1;\"), Ankus.PgCast] public static int Bad(int value) => value;", "ANKUS079")]
    [DataRow("[Ankus.PgFunction(GenerateSql = false), Ankus.PgTrigger] public static int Bad(Ankus.PgTriggerContext context) => 1;", "ANKUS214")]
    [DataRow("[Ankus.PgFunction(Sql = \"\"), Ankus.PgEventTrigger] public static int Bad(Ankus.PgEventTriggerContext context) => 1;", "ANKUS214")]
    public void SqlGenerationDoesNotBypassExistingContractValidation(string declaration, string id)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions {" + declaration + "}");
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(id, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.IsTrue(error.Location.IsInSource);
    }

    /// <summary>
    /// Builds a small independently valid declaration for each callback family.
    /// </summary>
    private static string SqlControlSource(string kind, string options)
    {
        string attribute = "[Ankus.PgFunction(" + options + ")]";
        string declaration = kind switch
        {
            "scalar" => attribute + " public static int Echo(int value) => value + 1;",
            "set" => attribute + " public static System.Collections.Generic.IEnumerable<int> Echo(int value) { yield return value; }",
            "table" => attribute + " public static System.Collections.Generic.IEnumerable<(int Number, string? Text)> Echo(int value) { yield return (value, null); }",
            "trigger" => "[Ankus.PgTrigger] " + attribute + " public static Ankus.PgHeapTuple? Echo(Ankus.PgTriggerContext context) => null;",
            "event" => "[Ankus.PgEventTrigger] " + attribute + " public static void Echo(Ankus.PgEventTriggerContext context) { }",
            "helper" => attribute + " public static long Transition(Ankus.PgAggregateContext context,long state, int value) => state + value;",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return kind == "helper"
            ? "[Ankus.PgAggregate(InitialCondition = \"0\")] public sealed class Values : Ankus.IPgAggregate<long,int> {" + declaration + "}"
            : "public static class Functions {" + declaration + "}";
    }

    /// <summary>
    /// Generates and checks a fixture before its contract assertions consume metadata.
    /// </summary>
    private Compilation GenerateSqlControl(string source)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        AssertSqlControlCompilation(compilation, diagnostics);
        return compilation;
    }

    /// <summary>
    /// Verifies both source generator and generated C# semantic diagnostics.
    /// </summary>
    private void AssertSqlControlCompilation(Compilation compilation, ImmutableArray<Diagnostic> diagnostics)
    {
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Returns PostgreSQL-callable function exports rather than managed dispatch or finfo symbols.
    /// </summary>
    private static string[] SqlControlExports(Compilation compilation)
        => [.. ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static symbol => symbol.StartsWith("ankus_fn_", StringComparison.Ordinal))];

    /// <summary>
    /// Requires unchanged native source, linker exports and complete managed dispatch bodies after SQL-only changes.
    /// </summary>
    private static void AssertSqlControlBoundary(Compilation expected, Compilation actual)
    {
        Assert.AreEqual(ManifestValue(expected, "Ankus.NativeSource"), ManifestValue(actual, "Ankus.NativeSource"));
        Assert.AreEqual(ManifestValue(expected, "Ankus.Exports"), ManifestValue(actual, "Ankus.Exports"));
        Assert.AreEqual(expected.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.DeclaringSyntaxReferences.Single().GetSyntax().ToString(),
            actual.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.DeclaringSyntaxReferences.Single().GetSyntax().ToString());
    }

    /// <summary>
    /// Asserts an actual prerequisite and dependent occur in the required order.
    /// </summary>
    private static void AssertSqlControlBefore(string sql, string before, string after)
    {
        int first = sql.IndexOf(before, StringComparison.Ordinal);
        int second = sql.IndexOf(after, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, first, before);
        Assert.IsGreaterThan(first, second, after);
    }

    /// <summary>
    /// Requires one fixed graph error at the exact authored value and the absence of any partial installation manifest.
    /// </summary>
    /// <param name="compilation">The generated compilation.</param>
    /// <param name="diagnostics">The generator diagnostics.</param>
    /// <param name="diagnosticId">The fixed diagnostic ID.</param>
    /// <param name="span">The exact authored source text to correct.</param>
    /// <param name="arguments">The authored identities substituted into the fixed message.</param>
    private void AssertSqlControlGraphError(Compilation compilation, ImmutableArray<Diagnostic> diagnostics, string diagnosticId, string span,
        params string[] arguments)
    {
        AssertGraphDiagnostic(Assert.ContainsSingle(diagnostics, string.Join(Environment.NewLine, diagnostics)), diagnosticId, span, arguments);
        AssertNoSqlManifest(compilation);
    }

    /// <summary>
    /// Verifies one graph diagnostic's identity, severity, fixed message, guide section and exact authored span.
    /// </summary>
    /// <param name="diagnostic">The reported diagnostic.</param>
    /// <param name="diagnosticId">The fixed diagnostic ID.</param>
    /// <param name="span">The exact authored source text to correct.</param>
    /// <param name="arguments">The authored identities substituted into the fixed message.</param>
    private void AssertGraphDiagnostic(Diagnostic diagnostic, string diagnosticId, string span, params string[] arguments)
    {
        Assert.AreEqual(diagnosticId, diagnostic.Id, diagnostic.ToString());
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(GraphMessage(diagnosticId, arguments), diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual(GraphHelp(diagnosticId), diagnostic.Descriptor.HelpLinkUri);
        Assert.AreEqual(span, DiagnosticText(diagnostic));
    }

    /// <summary>
    /// Requires a diagnostic to select one exact occurrence of repeated authored text.
    /// </summary>
    /// <param name="diagnostic">The reported diagnostic.</param>
    /// <param name="span">The exact authored text.</param>
    /// <param name="occurrence">The zero-based occurrence of that text in the diagnostic's source.</param>
    private void AssertDiagnosticOccurrence(Diagnostic diagnostic, string span, int occurrence)
    {
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        string text = diagnostic.Location.SourceTree.GetText(context.CancellationToken).ToString();
        int index = -1;
        for (int count = 0; count <= occurrence; count++)
        {
            index = text.IndexOf(span, index + 1, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, index, span);
        }

        Assert.AreEqual(index, diagnostic.Location.SourceSpan.Start);
    }

    /// <summary>
    /// Reads the complete attribute application enclosing a diagnostic's authored value.
    /// </summary>
    /// <param name="diagnostic">The reported diagnostic.</param>
    /// <returns>The enclosing attribute text, distinguishing equal values in different declarations.</returns>
    private string DiagnosticAttribute(Diagnostic diagnostic)
    {
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        return diagnostic.Location.SourceTree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan)
            .AncestorsAndSelf().OfType<AttributeSyntax>().First().ToString();
    }

    /// <summary>
    /// Requires that a rejected graph publishes no installation manifest.
    /// </summary>
    /// <param name="compilation">The generated compilation.</param>
    private static void AssertNoSqlManifest(Compilation compilation)
        => Assert.IsEmpty(compilation.Assembly.GetAttributes().Where(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            (string?)attribute.ConstructorArguments[0].Value == "Ankus.Sql"));

    /// <summary>
    /// Formats the public fixed message contract of one installation graph, replacement or generated identity diagnostic.
    /// </summary>
    /// <param name="diagnosticId">The fixed diagnostic ID.</param>
    /// <param name="arguments">The authored identities substituted into the message.</param>
    /// <returns>The complete expected message.</returns>
    private static string GraphMessage(string diagnosticId, params string[] arguments) => string.Format(CultureInfo.InvariantCulture, diagnosticId switch
    {
        "ANKUS490" => "Give Id at least one non-whitespace character, or omit Id when no explicit dependency identifier is needed",
        "ANKUS491" => "Remove embedded zero characters from the dependency identifier",
        "ANKUS492" => "Replace unpaired UTF-16 surrogate characters in the dependency identifier",
        "ANKUS493" => "Omit Requires or Before, or supply an array of dependency identifiers instead of null",
        "ANKUS494" => "Give each Requires and Before entry a nonnull dependency identifier containing at least one non-whitespace character",
        "ANKUS495" => "Remove embedded zero characters from the Requires or Before entry",
        "ANKUS496" => "Replace unpaired UTF-16 surrogate characters in the Requires or Before entry",
        "ANKUS497" => "Dependency identifier '{0}' is already declared in this extension; give each SQL declaration a distinct Id or PgSql name",
        "ANKUS498" => "No SQL declaration in this extension has dependency identifier '{0}'; declare it with Id, PgSql or PgSqlFile, or correct this entry",
        "ANKUS499" => "Installation SQL dependencies form the cycle {0}; remove or reverse one explicit dependency in it",
        "ANKUS500" => "Another PgSql or PgSqlFile block already uses PgSqlOrder.Bootstrap; give this block a different Order",
        "ANKUS501" => "Another PgSql or PgSqlFile block already uses PgSqlOrder.Finalize; give this block a different Order",
        "ANKUS502" => "GenerateSql cannot be false when Sql supplies replacement text, including empty text; remove GenerateSql = false or Sql",
        "ANKUS503" => "Remove embedded zero characters from the Sql replacement",
        "ANKUS504" => "Replace unpaired UTF-16 surrogate characters in the Sql replacement",
        "ANKUS505" => "The Sql replacement token {0} requires BinaryProtocol = true; enable the binary protocol or remove the token",
        "ANKUS506" => "PostgreSQL type {0} is already generated in this extension; give this enum a distinct PgEnum Name or Schema",
        "ANKUS507" => "PostgreSQL type {0} is already generated in this extension; give this type a distinct PgType Name or Schema",
        "ANKUS508" => "PostgreSQL operator {0} is already declared in this extension; change this operator's name, schema or operand types",
        "ANKUS509" => "PostgreSQL cast {0} is already declared in this extension; keep one cast for each source and target type",
        "ANKUS510" => "Function {0} generated for '{1}' is already declared in this extension; remove the conflicting declaration or this generated-operator attribute",
        "ANKUS511" => "Operator {0} generated for '{1}' is already declared in this extension; remove the conflicting operator or this generated-operator attribute",
        "ANKUS512" => "The embedded installation graph cannot exceed 100,000 SQL declarations; split the extension",
        "ANKUS513" => "An embedded installation declaration cannot have more than 100,000 names, dependencies or attachments; split the declaration",
        "ANKUS514" => "The embedded installation graph cannot exceed 32 MiB; reduce or split its installation SQL",
        _ => throw new ArgumentOutOfRangeException(nameof(diagnosticId)),
    }, arguments);

    /// <summary>
    /// Selects the public guide section documenting one installation graph, replacement or generated identity diagnostic.
    /// </summary>
    /// <param name="diagnosticId">The fixed diagnostic ID.</param>
    /// <returns>The help link carried by the diagnostic.</returns>
    private static string GraphHelp(string diagnosticId) => "https://willibrandon.github.io/ankus/" + diagnosticId switch
    {
        "ANKUS502" or "ANKUS503" or "ANKUS504" or "ANKUS505" => "custom-sql/#sql-replacement-diagnostics",
        "ANKUS506" => "enums/#declaration-diagnostics",
        "ANKUS507" => "custom-types/#declaration-diagnostics",
        "ANKUS508" or "ANKUS509" => "operators-and-casts/#declaration-diagnostics",
        "ANKUS510" or "ANKUS511" => "operators-and-casts/#generated-type-operators",
        _ => "custom-sql/#dependency-graph-diagnostics",
    };
}
