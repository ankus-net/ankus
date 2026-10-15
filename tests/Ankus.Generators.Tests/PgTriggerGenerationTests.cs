using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// The marker alone discovers a trigger, and combining PgFunction does not duplicate the callback or SQL declaration.
    /// </summary>
    /// <param name="attribute">The optional common function metadata.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("[Ankus.PgFunction]")]
    public void TriggerMarkerEmitsOneCompilableZeroArgumentFunction(string attribute)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            public static class Functions
            {
                [Ankus.PgTrigger] {{attribute}}
                public static Ankus.PgHeapTuple? AuditRow(Ankus.PgTriggerContext context) => null;
            }
            """);
        AssertTriggerCompilationSucceeds(compilation, diagnostics);
        IMethodSymbol callback = TriggerCallback(compilation);
        string nativeName = callback.Name.Replace("ankus_managed_", "ankus_fn_", StringComparison.Ordinal);
        Assert.AreEqual($"CREATE FUNCTION \"audit_row\"()\nRETURNS trigger AS 'MODULE_PATHNAME', '{nativeName}' LANGUAGE c " +
            "VOLATILE PARALLEL UNSAFE CALLED ON NULL INPUT SECURITY INVOKER NOT LEAKPROOF COST 1;\n",
            InstallationBody(compilation).ReplaceLineEndings("\n"));
        Assert.AreSequenceEqual(["Pg_magic_func", nativeName, "pg_finfo_" + nativeName],
            ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual("true", ManifestValue(compilation, "Ankus.Relocatable"));
        AttributeData entry = Assert.ContainsSingle(callback.GetAttributes());
        Assert.AreEqual(callback.Name, entry.NamedArguments.Single(static argument => argument.Key == "EntryPoint").Value.Value);
        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        Assert.Contains("ankus_trigger_call(FunctionCallInfo fcinfo, AnkusTriggerCallback callback)", native);
        Assert.Contains($"PG_FUNCTION_INFO_V1({nativeName});\nPGDLLEXPORT Datum {nativeName}(PG_FUNCTION_ARGS)\n{{\n" +
            $"    return ankus_trigger_call(fcinfo, {callback.Name});\n}}", native);
    }

    /// <summary>
    /// Trigger-only static helpers are omitted from extensions without triggers so native warning-as-error builds remain valid.
    /// </summary>
    /// <param name="source">An ordinary function or enum-only extension.</param>
    [TestMethod]
    [DataRow("public static class Functions { [Ankus.PgFunction] public static int Answer() => 42; }")]
    [DataRow("[Ankus.PgEnum] public enum Mood { Happy, Sad }")]
    public void NonTriggerExtensionsDoNotEmitUnusedTriggerEntryPoints(string source)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        AssertTriggerCompilationSucceeds(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.DoesNotContain("ankus_trigger_call", native);
        Assert.DoesNotContain("ankus_close_trigger_cursors", native);
        Assert.DoesNotContain("ankus_next_trigger_id", native);
    }

    /// <summary>
    /// Trigger dispatch handles ignored, skipped and unchanged returns before serialization while preserving the managed error boundary.
    /// </summary>
    [TestMethod]
    public void TriggerManagedDispatchOrdersEventSemanticsBeforeSerialization()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgTrigger]
                public static Ankus.PgHeapTuple? @return(Ankus.PgTriggerContext context) => null;
            }
            """);
        AssertTriggerCompilationSucceeds(compilation, diagnostics);
        MethodDeclarationSyntax callback = Assert.IsInstanceOfType<MethodDeclarationSyntax>(TriggerCallback(compilation)
            .DeclaringSyntaxReferences.Single().GetSyntax(context.CancellationToken));
        BlockSyntax body = Assert.IsInstanceOfType<BlockSyntax>(callback.Body);
        Assert.AreEqual("nint previous = global::Ankus.CompilerServices.NativeBackend.Enter(execute);", body.Statements[0].ToString());
        TryStatementSyntax guarded = AssertMemoryCallbackScope(callback, "global::Ankus.CompilerServices.NativeBackend.Exit(previous);");
        CatchClauseSyntax error = Assert.ContainsSingle(guarded.Catches);
        Assert.AreEqual("global::System.Exception", error.Declaration!.Type.ToString());
        Assert.AreSequenceEqual(["global::Ankus.CompilerServices.NativeError.Write(exception, error);", "return 1;"],
            error.Block.Statements.Select(static statement => statement.ToString()));
        Assert.Contains("new global::System.ReadOnlySpan<global::Ankus.CompilerServices.NativeValue>(arguments, 16)", guarded.Block.Statements[2].ToString());
        Assert.AreEqual("global::Ankus.PgHeapTuple? value = global::Functions.@return(context);", guarded.Block.Statements[3].ToString());
        IfStatementSyntax after = Assert.IsInstanceOfType<IfStatementSyntax>(guarded.Block.Statements[4]);
        IfStatementSyntax statement = Assert.IsInstanceOfType<IfStatementSyntax>(guarded.Block.Statements[5]);
        IfStatementSyntax skipped = Assert.IsInstanceOfType<IfStatementSyntax>(guarded.Block.Statements[6]);
        IfStatementSyntax deleted = Assert.IsInstanceOfType<IfStatementSyntax>(guarded.Block.Statements[7]);
        Assert.AreEqual("context.Timing == global::Ankus.PgTriggerTiming.After", after.Condition.ToString());
        Assert.AreEqual("context.Level == global::Ankus.PgTriggerLevel.Statement", statement.Condition.ToString());
        Assert.AreEqual("value is null", skipped.Condition.ToString());
        Assert.AreEqual("context.Operation == global::Ankus.PgTriggerOperation.Delete", deleted.Condition.ToString());
        foreach (IfStatementSyntax ignored in new[] { after, skipped })
        {
            Assert.AreSequenceEqual(["result->IsNull = 1;", "return 0;"],
                Assert.IsInstanceOfType<BlockSyntax>(ignored.Statement).Statements.Select(static item => item.ToString()));
        }

        BlockSyntax statementBody = Assert.IsInstanceOfType<BlockSyntax>(statement.Statement);
        IfStatementSyntax nonnullStatement = Assert.IsInstanceOfType<IfStatementSyntax>(statementBody.Statements[0]);
        Assert.AreEqual("value is not null", nonnullStatement.Condition.ToString());
        Assert.AreEqual("throw new global::Ankus.PgException(\"39P01\", \"A BEFORE STATEMENT trigger must return null.\");",
            Assert.ContainsSingle(Assert.IsInstanceOfType<BlockSyntax>(nonnullStatement.Statement).Statements).ToString());
        Assert.AreSequenceEqual(["result->IsNull = 1;", "return 0;"], statementBody.Statements.Skip(1).Select(static item => item.ToString()));
        Assert.AreSequenceEqual(["result->IsNull = 0;", "return 0;"],
            Assert.IsInstanceOfType<BlockSyntax>(deleted.Statement).Statements.Select(static item => item.ToString()));
        IfStatementSyntax unchanged = Assert.IsInstanceOfType<IfStatementSyntax>(guarded.Block.Statements[8]);
        Assert.AreEqual("global::Ankus.CompilerServices.NativeValue.IsUnchangedTriggerRow(context, value)", unchanged.Condition.ToString());
        Assert.AreSequenceEqual(["result->IsNull = 0;", "return 0;"],
            Assert.IsInstanceOfType<BlockSyntax>(unchanged.Statement).Statements.Select(static item => item.ToString()));
        Assert.AreSequenceEqual(["*result = global::Ankus.CompilerServices.NativeValue.FromTriggerRow(context, value, " +
            "new global::System.ReadOnlySpan<global::Ankus.CompilerServices.NativeValue>(arguments, 16));", "return 0;"],
            guarded.Block.Statements.Skip(9).Select(static item => item.ToString()));
    }

    /// <summary>
    /// Trigger callbacks remain accessible through internal nested containers and aliased CLR type spellings.
    /// </summary>
    /// <param name="source">The valid callback container.</param>
    [TestMethod]
    [DataRow("internal class Outer { internal class Inner { [Ankus.PgTrigger] internal static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; } }")]
    [DataRow("using Tuple = Ankus.PgHeapTuple; using Context = Ankus.PgTriggerContext; public class Functions { [Ankus.PgTrigger] public static Tuple? Audit(Context context) => null; }")]
    [DataRow("public interface Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }")]
    [DataRow("public static class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple Audit(Ankus.PgTriggerContext context) => context.New!; }")]
    public void TriggerAccessibleContainersAndAliasesCompile(string source)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        AssertTriggerCompilationSucceeds(compilation, diagnostics);
        Assert.Contains("CREATE FUNCTION \"audit\"()\nRETURNS trigger", InstallationBody(compilation).ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// Common planner, privilege, name, search-path and schema options remain available to trigger functions.
    /// </summary>
    [TestMethod]
    public void TriggerCommonOptionsPreserveExactSqlMetadata()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgSchema("inherited", Create = false)]
            public static class Functions
            {
                [Ankus.PgTrigger]
                [Ankus.PgFunction(Name = "audit", Schema = "Exact \"Schema", Volatility = Ankus.PgVolatility.Immutable,
                    ParallelSafety = Ankus.PgParallelSafety.Safe, NullInput = Ankus.PgNullInput.CalledOnNull,
                    SecurityDefiner = true, Leakproof = true, CreateOrReplace = true, Cost = 2.5,
                    SearchPath = new[] { "pg_temp", "$user", "Mixed Schema" }, SupportFunction = "planner.support")]
                public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null;
            }
            """);
        AssertTriggerCompilationSucceeds(compilation, diagnostics);
        string nativeName = TriggerCallback(compilation).Name.Replace("ankus_managed_", "ankus_fn_", StringComparison.Ordinal);
        Assert.AreEqual($"CREATE OR REPLACE FUNCTION \"Exact \"\"Schema\".\"audit\"()\nRETURNS trigger AS 'MODULE_PATHNAME', '{nativeName}' LANGUAGE c " +
            "IMMUTABLE PARALLEL SAFE CALLED ON NULL INPUT SECURITY DEFINER LEAKPROOF COST 2.5 SUPPORT \"planner\".\"support\" " +
            "SET search_path TO \"pg_temp\", \"$user\", \"Mixed Schema\";\n", InstallationBody(compilation).ReplaceLineEndings("\n"));
        Assert.AreEqual("false", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Explicit null-input options do not interpret the required CLR trigger context as a SQL input argument.
    /// </summary>
    /// <param name="option">The explicit null-input policy.</param>
    /// <param name="sql">The expected SQL clause.</param>
    [TestMethod]
    [DataRow("Inferred", "CALLED ON NULL INPUT")]
    [DataRow("Strict", "STRICT")]
    [DataRow("CalledOnNull", "CALLED ON NULL INPUT")]
    public void TriggerNullInputOptionsDoNotInferStrictnessFromContext(string option, string sql)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { " +
            "[Ankus.PgTrigger, Ankus.PgFunction(NullInput = Ankus.PgNullInput." + option + ")] " +
            "public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }");
        AssertTriggerCompilationSucceeds(compilation, diagnostics);
        Assert.Contains("PARALLEL UNSAFE " + sql + " SECURITY INVOKER", InstallationBody(compilation));
    }

    /// <summary>
    /// Trigger signature rejection produces one actionable diagnostic while the consumer source remains valid C#.
    /// </summary>
    /// <param name="method">The invalid trigger signature.</param>
    /// <param name="diagnostic">The independently correctable callback contract.</param>
    [TestMethod]
    [DataRow("public Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null;", "ANKUS208")]
    [DataRow("private static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null;", "ANKUS213")]
    [DataRow("protected static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null;", "ANKUS213")]
    [DataRow("public static Ankus.PgHeapTuple? Audit<T>(Ankus.PgTriggerContext context) => null;", "ANKUS210")]
    [DataRow("public static int Audit(Ankus.PgTriggerContext context) => 1;", "ANKUS214")]
    [DataRow("public static void Audit(Ankus.PgTriggerContext context) { }", "ANKUS214")]
    [DataRow("public static System.Threading.Tasks.Task<Ankus.PgHeapTuple?> Audit(Ankus.PgTriggerContext context) => null!;", "ANKUS228")]
    [DataRow("public static async System.Threading.Tasks.Task<Ankus.PgHeapTuple?> Audit(Ankus.PgTriggerContext context) { await System.Threading.Tasks.Task.Yield(); return null; }", "ANKUS209")]
    [DataRow("public static System.Collections.Generic.IEnumerable<Ankus.PgHeapTuple?> Audit(Ankus.PgTriggerContext context) => System.Array.Empty<Ankus.PgHeapTuple?>();", "ANKUS214")]
    [DataRow("public static Ankus.PgHeapTuple? Audit() => null;", "ANKUS215")]
    [DataRow("public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context, int extra) => null;", "ANKUS215")]
    [DataRow("public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext? context) => null;", "ANKUS219")]
    [DataRow("public static Ankus.PgHeapTuple? Audit(object context) => null;", "ANKUS220")]
    [DataRow("public static Ankus.PgHeapTuple? Audit(ref Ankus.PgTriggerContext context) => null;", "ANKUS216")]
    [DataRow("public static Ankus.PgHeapTuple? Audit(in Ankus.PgTriggerContext context) => null;", "ANKUS216")]
    [DataRow("public static Ankus.PgHeapTuple? Audit(out Ankus.PgTriggerContext context) { context = null!; return null; }", "ANKUS216")]
    [DataRow("public static Ankus.PgHeapTuple? Audit(params Ankus.PgTriggerContext[] context) => null;", "ANKUS217")]
    [DataRow("public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context = null!) => null;", "ANKUS218")]
    [DataRow("public static Ankus.PgHeapTuple? Audit([System.Runtime.InteropServices.Optional] Ankus.PgTriggerContext context) => null;", "ANKUS218")]
    [DataRow("public static ref Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => throw new System.InvalidOperationException();", "ANKUS212")]
    [DataRow("public static ref readonly Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => throw new System.InvalidOperationException();", "ANKUS212")]
    public void InvalidTriggerSignaturesAreDiagnosed(string method, string diagnostic)
        => AssertInvalidTrigger("public class Functions { [Ankus.PgTrigger] " + method + " }", diagnostic);

    /// <summary>
    /// Generic, inaccessible and abstract containers cannot supply a concrete trigger callback.
    /// </summary>
    /// <param name="source">The complete invalid container.</param>
    /// <param name="diagnostic">The independently correctable callback contract.</param>
    [TestMethod]
    [DataRow("public class Functions<T> { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }", "ANKUS221")]
    [DataRow("public class Outer<T> { public class Inner { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; } }", "ANKUS221")]
    [DataRow("file class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }", "ANKUS222")]
    [DataRow("public class Outer { private class Inner { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; } }", "ANKUS223")]
    [DataRow("public interface Functions { [Ankus.PgTrigger] static abstract Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context); }", "ANKUS211")]
    public void InvalidTriggerContainersAreDiagnosed(string source, string diagnostic) => AssertInvalidTrigger(source, diagnostic);

    /// <summary>
    /// Metadata for SQL operands, columns, parameter defaults and set execution is rejected on trigger callbacks.
    /// </summary>
    /// <param name="attribute">The conflicting method or return attribute.</param>
    /// <param name="parameterAttribute">The conflicting context parameter attribute.</param>
    /// <param name="diagnostic">The independently correctable callback contract.</param>
    [TestMethod]
    [DataRow("[Ankus.PgOperator(\"+\")]", "", "ANKUS224")]
    [DataRow("[Ankus.PgCast]", "", "ANKUS224")]
    [DataRow("[return: Ankus.PgCompositeType(\"dog\")]", "", "ANKUS225")]
    [DataRow("[return: Ankus.PgNumericPrecision(5, 2)]", "", "ANKUS225")]
    [DataRow("[return: Ankus.PgColumnNames(\"row\")]", "", "ANKUS225")]
    [DataRow("", "[Ankus.PgCompositeType(\"dog\")]", "ANKUS225")]
    [DataRow("", "[Ankus.PgNumericPrecision(5, 2)]", "ANKUS225")]
    [DataRow("", "[Ankus.PgParameter(Name = \"arg\")]", "ANKUS225")]
    [DataRow("", "[Ankus.PgParameter(Default = \"NULL\")]", "ANKUS225")]
    [DataRow("[Ankus.PgFunction(Rows = 1000)]", "", "ANKUS226")]
    [DataRow("[Ankus.PgFunction(Rows = 0)]", "", "ANKUS226")]
    [DataRow("[Ankus.PgFunction(SetMode = Ankus.PgSetMode.Auto)]", "", "ANKUS227")]
    [DataRow("[Ankus.PgFunction(SetMode = Ankus.PgSetMode.Materialize)]", "", "ANKUS227")]
    public void ConflictingTriggerMetadataIsDiagnosed(string attribute, string parameterAttribute, string diagnostic)
        => AssertInvalidTrigger("public static class Functions { [Ankus.PgTrigger] " + attribute +
            " public static Ankus.PgHeapTuple? Audit(" + parameterAttribute + " Ankus.PgTriggerContext context) => null; }", diagnostic);

    /// <summary>
    /// Shared function option validation still applies without attempting to convert the trigger context into a SQL type.
    /// </summary>
    /// <param name="option">The invalid common option.</param>
    /// <param name="diagnostic">The expected shared diagnostic.</param>
    [TestMethod]
    [DataRow("Name = \"bad-name\"", "ANKUS002")]
    [DataRow("Schema = \"\"", "ANKUS050")]
    [DataRow("Cost = 0", "ANKUS046")]
    [DataRow("NullInput = (Ankus.PgNullInput)3", "ANKUS045")]
    [DataRow("ParallelSafety = (Ankus.PgParallelSafety)3", "ANKUS045")]
    [DataRow("Volatility = (Ankus.PgVolatility)3", "ANKUS045")]
    [DataRow("SearchPath = new[] { \"\" }", "ANKUS055")]
    [DataRow("SupportFunction = \"a.b.c\"", "ANKUS054")]
    public void InvalidTriggerCommonOptionsUseSharedDiagnostics(string option, string diagnostic)
        => AssertInvalidTrigger("public static class Functions { [Ankus.PgTrigger, Ankus.PgFunction(" + option + ")] " +
            "public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }", diagnostic);

    /// <summary>
    /// Trigger callbacks share PostgreSQL's zero-argument signature namespace with ordinary functions and other callbacks.
    /// </summary>
    /// <param name="other">The second declaration.</param>
    /// <param name="duplicate">Whether the SQL signatures coincide.</param>
    [TestMethod]
    [DataRow("[Ankus.PgFunction(Name = \"audit\")] public static int Other() => 1;", true)]
    [DataRow("[Ankus.PgTrigger, Ankus.PgFunction(Name = \"audit\")] public static Ankus.PgHeapTuple? Other(Ankus.PgTriggerContext context) => null;", true)]
    [DataRow("[Ankus.PgFunction(Name = \"audit\")] public static int Other(int value) => value;", false)]
    [DataRow("[Ankus.PgTrigger, Ankus.PgFunction(Name = \"audit\", Schema = \"other\")] public static Ankus.PgHeapTuple? Other(Ankus.PgTriggerContext context) => null;", false)]
    public void TriggerSqlSignaturesCollideOnlyOnSchemaNameAndZeroArguments(string other, bool duplicate)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { " +
            "[Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; " + other + " }");
        if (duplicate)
        {
            Assert.AreEqual("ANKUS207", Assert.ContainsSingle(diagnostics).Id);
        }
        else
        {
            AssertTriggerCompilationSucceeds(compilation, diagnostics);
            string sql = InstallationBody(compilation);
            Assert.Contains("CREATE FUNCTION \"audit\"()", sql);
            Assert.Contains(other.Contains("Schema", StringComparison.Ordinal)
                ? "CREATE FUNCTION \"other\".\"audit\"()" : "CREATE FUNCTION \"audit\"(\"value\" integer)", sql);
        }
    }

    /// <summary>
    /// Explicit custom SQL dependencies create the table and callback before attaching a trigger, with inherited schema ordering.
    /// </summary>
    [TestMethod]
    public void TriggerSqlDependenciesOrderSchemaTableFunctionAndAttachment()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSql("table", "CREATE TABLE audit.rows(value integer);", Requires = new[] { "schema" })]
            [assembly: Ankus.PgSql("attach", "CREATE TRIGGER audit BEFORE INSERT ON audit.rows FOR EACH ROW EXECUTE FUNCTION audit.callback();", Requires = new[] { "callback" })]
            [Ankus.PgSchema("audit", Id = "schema")]
            public static class Functions
            {
                [Ankus.PgTrigger, Ankus.PgFunction(Id = "callback", Requires = new[] { "table" })]
                public static Ankus.PgHeapTuple? Callback(Ankus.PgTriggerContext context) => null;
            }
            """);
        AssertTriggerCompilationSucceeds(compilation, diagnostics);
        string[] statements = InstallationBody(compilation).ReplaceLineEndings("\n").Replace("\nRETURNS", " RETURNS", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(4, statements);
        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"audit\";", statements[0]);
        Assert.AreEqual("CREATE TABLE audit.rows(value integer);", statements[1]);
        Assert.StartsWith("CREATE FUNCTION \"audit\".\"callback\"() RETURNS trigger AS ", statements[2]);
        Assert.AreEqual("CREATE TRIGGER audit BEFORE INSERT ON audit.rows FOR EACH ROW EXECUTE FUNCTION audit.callback();", statements[3]);
    }

    /// <summary>
    /// Trigger dependencies participate in missing-identifier, duplicate-identifier and cycle diagnostics.
    /// </summary>
    /// <param name="declarations">The additional schema or SQL declaration.</param>
    /// <param name="options">The callback's dependency metadata.</param>
    /// <param name="expected">The fixed graph diagnostic.</param>
    /// <param name="span">The exact authored value to correct.</param>
    /// <param name="occurrence">The zero-based occurrence of that text in the source.</param>
    /// <param name="argument">The authored identity in the fixed message.</param>
    [TestMethod]
    [DataRow("", "Requires = new[] { \"missing\" }", "ANKUS498", "\"missing\"", 0, "missing")]
    [DataRow("[assembly: Ankus.PgSql(\"other\", \"SELECT 1;\", Requires = new[] { \"callback\" })]", "Id = \"callback\", Requires = new[] { \"other\" }",
        "ANKUS499", "\"other\"", 1, "callback -> other -> callback")]
    [DataRow("[assembly: Ankus.PgSql(\"callback\", \"SELECT 1;\")]", "Id = \"callback\"", "ANKUS497", "\"callback\"", 1, "callback")]
    [DataRow("[Ankus.PgSchema(\"audit\", Requires = new[] { \"callback\" })] public static class Schema;", "Id = \"callback\", Schema = \"audit\"",
        "ANKUS499", "\"callback\"", 0, "Schema -> callback -> Schema")]
    public void InvalidTriggerDependencyGraphsAreDiagnosed(string declarations, string options, string expected, string span, int occurrence, string argument)
    {
        AssertInvalidTrigger(declarations + " public static class Functions { [Ankus.PgTrigger, Ankus.PgFunction(" + options + ")] " +
            "public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }", expected, out Diagnostic diagnostic);
        AssertGraphDiagnostic(diagnostic, expected, span, argument);
        AssertDiagnosticOccurrence(diagnostic, span, occurrence);
    }

    /// <summary>
    /// A trigger context has no ordinary SQL datum conversion when the trigger marker is absent.
    /// </summary>
    [TestMethod]
    public void TriggerContextRequiresTheTriggerMarker()
        => AssertInvalidTrigger("public static class Functions { [Ankus.PgFunction] " +
            "public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }", "ANKUS040");

    /// <summary>
    /// Checks generator diagnostics and compiler diagnostics before emitting the generated assembly.
    /// </summary>
    private void AssertTriggerCompilationSucceeds(Compilation compilation, ImmutableArray<Diagnostic> diagnostics)
    {
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        using var assembly = new MemoryStream();
        Assert.IsTrue(compilation.Emit(assembly, cancellationToken: context.CancellationToken).Success);
    }

    /// <summary>
    /// Checks one expected generator error without mistaking malformed consumer C# for a generator validation witness.
    /// </summary>
    private void AssertInvalidTrigger(string source, string expected) => AssertInvalidTrigger(source, expected, out _);

    /// <summary>
    /// Checks one expected generator error and returns it for exact message and location assertions.
    /// </summary>
    private void AssertInvalidTrigger(string source, string expected, out Diagnostic diagnostic)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(expected, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Resolves the single generated callback independently of its deterministic assembly-specific symbol hash.
    /// </summary>
    private static IMethodSymbol TriggerCallback(Compilation compilation)
    {
        INamedTypeSymbol dispatchers = compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!;
        return Assert.IsInstanceOfType<IMethodSymbol>(Assert.ContainsSingle(dispatchers.GetMembers()));
    }
}
