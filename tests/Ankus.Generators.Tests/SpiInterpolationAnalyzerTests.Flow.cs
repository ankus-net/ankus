using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class SpiInterpolationAnalyzerTests
{
    /// <summary>
    /// Requires the exact raw-command diagnostic for misplaced fragments and reaching unsafe construction.
    /// </summary>
    /// <param name="body">The valid consumer construction and command.</param>
    /// <param name="argument">The exact command argument receiving the diagnostic.</param>
    [TestMethod]
    [DataRow("Spi.Execute($\"SELECT '{Spi.QuoteIdentifier(value)}'\");", "$\"SELECT '{Spi.QuoteIdentifier(value)}'\"", DisplayName = "inside_string")]
    [DataRow("Spi.Execute($\"SELECT \\\"{Spi.QuoteIdentifier(value)}\\\"\");", "$\"SELECT \\\"{Spi.QuoteIdentifier(value)}\\\"\"", DisplayName = "inside_identifier")]
    [DataRow("Spi.Execute($\"SELECT 1 -- {Spi.QuoteIdentifier(value)}\");", "$\"SELECT 1 -- {Spi.QuoteIdentifier(value)}\"", DisplayName = "inside_line_comment")]
    [DataRow("Spi.Execute($\"SELECT 1 /* outer /* {Spi.QuoteIdentifier(value)} */ end */\");", "$\"SELECT 1 /* outer /* {Spi.QuoteIdentifier(value)} */ end */\"", DisplayName = "inside_nested_comment")]
    [DataRow("Spi.Execute($\"SELECT $tag${Spi.QuoteLiteral(value)}$tag$\");", "$\"SELECT $tag${Spi.QuoteLiteral(value)}$tag$\"", DisplayName = "inside_dollar_string")]
    [DataRow("Spi.Execute($\"SELECT E'{Spi.QuoteIdentifier(value)}'\");", "$\"SELECT E'{Spi.QuoteIdentifier(value)}'\"", DisplayName = "inside_escape_string")]
    [DataRow("Spi.Execute(\"SELECT '\" + Spi.QuoteIdentifier(value) + \"'\");", "\"SELECT '\" + Spi.QuoteIdentifier(value) + \"'\"", DisplayName = "quoted_concatenation")]
    [DataRow("Spi.Execute(string.Format(\"SELECT '{0}'\", Spi.QuoteIdentifier(value)));", "string.Format(\"SELECT '{0}'\", Spi.QuoteIdentifier(value))", DisplayName = "quoted_format")]
    [DataRow("string command = $\"SELECT {value}\"; Spi.Execute(command);", "command", DisplayName = "stored_interpolation")]
    [DataRow("Spi.Execute(\"SELECT \" + value);", "\"SELECT \" + value", DisplayName = "raw_concatenation")]
    [DataRow("Spi.Execute(new System.Text.StringBuilder(\"SELECT \").Append(value).ToString());", "new System.Text.StringBuilder(\"SELECT \").Append(value).ToString()", DisplayName = "builder_value")]
    [DataRow("Spi.Execute(new System.Text.StringBuilder(\"SELECT '\").Append(Spi.QuoteIdentifier(value)).Append(\"'\").ToString());", "new System.Text.StringBuilder(\"SELECT '\").Append(Spi.QuoteIdentifier(value)).Append(\"'\").ToString()", DisplayName = "builder_quoted_context")]
    [DataRow("Spi.Execute(new System.Text.StringBuilder().AppendFormat(\"SELECT {0}\", value).ToString());", "new System.Text.StringBuilder().AppendFormat(\"SELECT {0}\", value).ToString()", DisplayName = "builder_format_value")]
    [DataRow("var sql = new System.Text.StringBuilder(\"SELECT \"); sql.Append(value); Spi.Execute(sql.ToString());", "sql.ToString()", DisplayName = "builder_local_value")]
    [DataRow("string sql = \"SELECT 1\"; sql = $\"SELECT {value}\"; Spi.Execute(sql);", "sql", DisplayName = "command_local_written")]
    [DataRow("var sql = new System.Text.StringBuilder(\"SELECT '\"); sql.Append(Spi.QuoteIdentifier(value)); sql.Append(\"'\"); Spi.Execute(sql.ToString());", "sql.ToString()", DisplayName = "builder_local_quoted_context")]
    [DataRow("var sql = new System.Text.StringBuilder(\"SELECT \"); var alias = sql; alias.Append(value); Spi.Execute(sql.ToString());", "sql.ToString()", DisplayName = "builder_alias_value")]
    [DataRow("string sql = \"SELECT 1\"; if (flag) { sql = $\"SELECT {value}\"; } Spi.Execute(sql);", "sql", DisplayName = "conditional_write")]
    [DataRow("var sql = new System.Text.StringBuilder(); while (flag) { sql.Append(value); flag = false; } Spi.Execute(sql.ToString());", "sql.ToString()", DisplayName = "loop_write")]
    [DataRow("string sql = \"SELECT 1\"; try { } finally { sql = $\"SELECT {value}\"; } Spi.Execute(sql);", "sql", DisplayName = "finally_write")]
    [DataRow("string sql = \"SELECT 1\"; try { sql = $\"SELECT {value}\"; throw new System.Exception(); } catch { Spi.Execute(sql); }", "sql", DisplayName = "catch_write")]
    [DataRow("string sql = Spi.QuoteLiteral(value); System.Action change = () => sql = value; change(); Spi.Execute($\"SELECT {sql}\");", "$\"SELECT {sql}\"", DisplayName = "delegate_write")]
    [DataRow("string sql = Spi.QuoteLiteral(value); void Change() { sql = value; } Change(); Spi.Execute($\"SELECT {sql}\");", "$\"SELECT {sql}\"", DisplayName = "local_function_write")]
    [DataRow("Spi.Connect(session => session.Execute($\"SELECT {value}\"));", "$\"SELECT {value}\"", DisplayName = "nested_query")]
    [DataRow("Spi.Connect(session => session.Execute($\"SELECT '{Spi.QuoteIdentifier(value)}'\"));", "$\"SELECT '{Spi.QuoteIdentifier(value)}'\"", DisplayName = "nested_quoted_context")]
    [DataRow("string sql = Spi.QuoteLiteral(value); ref string alias = ref sql; alias = value; Spi.Execute($\"SELECT {sql}\");", "$\"SELECT {sql}\"", DisplayName = "ref_alias_write")]
    [DataRow("string sql = Spi.QuoteLiteral(value); (sql, _) = (value, 1); Spi.Execute($\"SELECT {sql}\");", "$\"SELECT {sql}\"", DisplayName = "deconstruction_write")]
    [DataRow("string sql = $\"SELECT {value}\"; Other.Maybe(() => sql = \"SELECT 1\"); Spi.Execute(sql);", "sql", DisplayName = "callback_may_not_run")]
    [DataRow("var sql = new System.Text.StringBuilder(\"SELECT \").Append(Spi.QuoteLiteral(value)); Other.Mutate(sql); Spi.Execute(sql.ToString());", "sql.ToString()", DisplayName = "external_builder_mutation")]
    [DataRow("Spi.Execute(new System.Text.StringBuilder(Spi.QuoteLiteral(value), 1, 3, 16).ToString());", "new System.Text.StringBuilder(Spi.QuoteLiteral(value), 1, 3, 16).ToString()", DisplayName = "builder_slice_quote")]
    [DataRow("Spi.Execute(new System.Text.StringBuilder(Spi.QuoteLiteral(value)).ToString(1, 3));", "new System.Text.StringBuilder(Spi.QuoteLiteral(value)).ToString(1, 3)", DisplayName = "builder_to_string_slice_quote")]
    [DataRow("var old = new System.Text.StringBuilder(); var current = old; while (flag) { old = current; current = new System.Text.StringBuilder().Append(value); flag = number-- > 0; } current.Clear(); current.Append(\"SELECT 1\"); Spi.Execute(old.ToString());", "old.ToString()", DisplayName = "separate_allocations_loop")]
    [DataRow("Spi.Execute($\"SELECT {value}\");", "$\"SELECT {value}\"", DisplayName = "direct_value_control")]
    [DataRow("object[] inputs = { value }; Spi.Execute(string.Format(\"SELECT {0}\", inputs));", "string.Format(\"SELECT {0}\", inputs)", DisplayName = "array_local_dynamic_format")]
    [DataRow("object[] inputs = { Spi.QuoteLiteral(value) }; inputs[0] = value; Spi.Execute(string.Format(\"SELECT {0}\", inputs));", "string.Format(\"SELECT {0}\", inputs)", DisplayName = "array_write_format")]
    [DataRow("object[] inputs = { Spi.QuoteLiteral(value) }; object[] alias = inputs; alias[0] = value; Spi.Execute(string.Format(\"SELECT {0}\", inputs));", "string.Format(\"SELECT {0}\", inputs)", DisplayName = "array_alias_format")]
    [DataRow("var old = new System.Text.StringBuilder(); var current = old; int iteration = 0; do { old = current; current = new System.Text.StringBuilder().Append(value); } while (++iteration < number); current.Clear(); current.Append(\"SELECT 1\"); Spi.Execute(old.ToString());", "old.ToString()", DisplayName = "do_loop_distinct_allocations")]
    [DataRow("string sql = Spi.QuoteLiteral(value); void Change(ref string text) { text = value; } Change(ref sql); Spi.Execute($\"SELECT {sql}\");", "$\"SELECT {sql}\"", DisplayName = "local_ref_write")]
    [DataRow("string sql = Spi.QuoteLiteral(value); void Change() { sql = value; } System.Action callback = Change; callback(); Spi.Execute($\"SELECT {sql}\");", "$\"SELECT {sql}\"", DisplayName = "local_method_group_write")]
    [DataRow("string sql = \"SELECT 1\"; System.Action callback = flag ? () => sql = $\"SELECT {value}\" : () => sql = \"SELECT 1\"; callback(); Spi.Execute(sql);", "sql", DisplayName = "delegate_choices")]
    public async Task ConstructedRawCommandsAreRejected(string body, string argument)
    {
        Diagnostic error = Assert.ContainsSingle(await Analyze(body, FlowHelpers, "bool flag = number != 0;"));
        Assert.AreEqual("ANKUS044", error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(argument, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.Contains("Spi.Sql", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("https://ankus.net/spi/#parameterized-interpolation", error.Descriptor.HelpLinkUri);
    }

    /// <summary>
    /// Preserves literal, quoted, overwritten and cleared command values through actual consumer control flow.
    /// </summary>
    /// <param name="body">The safe consumer construction and command.</param>
    [TestMethod]
    [DataRow("Spi.Execute($\"SELECT count(*) FROM {Spi.QuoteIdentifier(value)}\");")]
    [DataRow("Spi.Execute($\"SELECT {Spi.QuoteLiteral(value)}\");")]
    [DataRow("Spi.Execute(\"SELECT timestamp\" + Spi.QuoteLiteral(value));")]
    [DataRow("Spi.Execute(\"SELECT 'prefix'\\n\" + Spi.QuoteLiteral(value));")]
    [DataRow("Spi.Execute(Spi.QuoteLiteral(value) + \"\\n\" + Spi.QuoteLiteral(value));")]
    [DataRow("Spi.Execute(\"SELECT E'prefix'\\n\" + Spi.QuoteLiteral(value));")]
    [DataRow("Spi.Execute(string.Format(\"SELECT {0}\", Spi.QuoteLiteral(value)));")]
    [DataRow("Spi.Execute(new System.Text.StringBuilder(\"SELECT 1\").ToString());")]
    [DataRow("Spi.Execute(new System.Text.StringBuilder(\"SELECT 1 FROM \").Append(Spi.QuoteIdentifier(value)).ToString());")]
    [DataRow("Spi.Execute(new System.Text.StringBuilder().AppendFormat(\"SELECT {0}\", Spi.QuoteLiteral(value)).ToString());")]
    [DataRow("Spi.Execute(new System.Text.StringBuilder(\"SELECT \").Append(value).Clear().Append(\"SELECT 1\").ToString());")]
    [DataRow("string sql = $\"SELECT {value}\"; sql = \"SELECT 1\"; Spi.Execute(sql);")]
    [DataRow("var sql = new System.Text.StringBuilder(\"SELECT \"); sql.Append(value); sql.Clear(); sql.Append(\"SELECT 1\"); Spi.Execute(sql.ToString());")]
    [DataRow("var sql = new System.Text.StringBuilder(\"SELECT \"); sql.Append(Spi.QuoteLiteral(value)); Spi.Execute(sql.ToString());")]
    [DataRow("var sql = new System.Text.StringBuilder(\"SELECT \"); sql.Append(value); var alias = sql; alias.Clear(); alias.Append(\"SELECT 1\"); Spi.Execute(sql.ToString());")]
    [DataRow("var sql = new System.Text.StringBuilder(); if (flag) { sql.Append(value); } sql.Clear(); sql.Append(\"SELECT 1\"); Spi.Execute(sql.ToString());")]
    [DataRow("var sql = new System.Text.StringBuilder(); while (flag) { sql.Append(value); flag = false; } sql.Clear(); sql.Append(\"SELECT 1\"); Spi.Execute(sql.ToString());")]
    [DataRow("var sql = new System.Text.StringBuilder(); try { sql.Append(value); } finally { sql.Clear(); sql.Append(\"SELECT 1\"); } Spi.Execute(sql.ToString());")]
    [DataRow("var sql = new System.Text.StringBuilder(); try { sql.Append(value); throw new System.Exception(); } catch { sql.Clear(); sql.Append(\"SELECT 1\"); Spi.Execute(sql.ToString()); }")]
    [DataRow("Spi.Execute(string.Format(\"SELECT {0} {1}\", new object[] { Spi.QuoteLiteral(value), \"AS label\" }));")]
    [DataRow("string sql = Spi.QuoteIdentifier(value); sql = Spi.QuoteLiteral(value); Spi.Execute($\"SELECT {sql}\");")]
    [DataRow("Spi.Execute(new System.Text.StringBuilder(\"garbageSELECT 1\", 7, 8, 16).ToString());")]
    [DataRow("Spi.Execute(new System.Text.StringBuilder(\"garbageSELECT 1\").ToString(7, 8));")]
    [DataRow("Spi.Execute(new System.Text.StringBuilder().Append(value).ToString(0, 0));")]
    [DataRow("var sql = new System.Text.StringBuilder().Append(value); sql.Length = 0; sql.Append(\"SELECT 1\"); Spi.Execute(sql.ToString());")]
    [DataRow("object[] inputs = { value }; inputs[0] = 42; Spi.Execute(string.Format(\"SELECT {0}\", inputs));")]
    [DataRow("string Fragment() => Spi.QuoteLiteral(value); Spi.Execute($\"SELECT {Fragment()}\");")]
    [DataRow("Spi.Execute(string.Format(\"SELECT 1\", value));")]
    [DataRow("Spi.Execute(string.Format(\"SELECT {0}, {1}\", Spi.QuoteLiteral(value), \"'$9999999999999999990000000000'\"));")]
    [DataRow("Spi.Execute($\"SELECT {Spi.ParameterReference(number)}\");")]
    [DataRow("var sql = new System.Text.StringBuilder(\"INSERT INTO t VALUES (\"); for (int i = 1; i <= number; i++) { sql.Append(i > 1 ? \", \" : \"\").Append(Spi.ParameterReference(i)); } sql.Append(')'); Spi.Execute(sql.ToString());")]
    [DataRow("int[] numbers = [1, 2]; Spi.Execute(\"SELECT \" + string.Join(\", \", System.Linq.Enumerable.Select(numbers, Spi.ParameterReference)));")]
    public async Task ProvenCommandConstructionRemainsValid(string body)
        => Assert.IsEmpty(await Analyze(body, FlowHelpers, "bool flag = number != 0;"));

    /// <summary>
    /// A parameter number formatted from a runtime integer is a runtime value; only the reference helper writes one.
    /// </summary>
    [TestMethod]
    public async Task RuntimeParameterNumbersNeedTheReferenceHelper()
    {
        const string body = "var sql = new System.Text.StringBuilder(\"SELECT \"); for (int i = 1; i <= number; i++) " +
            "{ sql.Append('$').Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)); } Spi.Execute(sql.ToString());";
        Diagnostic error = Assert.ContainsSingle(await Analyze(body));
        Assert.AreEqual("ANKUS044", error.Id);
    }

    /// <summary>
    /// Preserves correctly quoted identifier and literal fragments across loop convergence.
    /// </summary>
    /// <param name="body">The variable-length quoted command construction.</param>
    [TestMethod]
    [DataRow("string sql = \"SELECT * FROM t WHERE true\"; string[] filters = [value, value]; foreach (string filter in filters) { sql += \" AND \" + Spi.QuoteIdentifier(filter) + \" = \" + Spi.QuoteLiteral(filter); } Spi.Execute(sql);")]
    [DataRow("var sql = new System.Text.StringBuilder(\"SELECT \"); string[] columns = [value, value]; foreach (string column in columns) { sql.Append(Spi.QuoteIdentifier(column)); sql.Append(\", \"); } Spi.Execute(sql.ToString());")]
    public async Task QuotedLoopConstructionRemainsValid(string body)
        => Assert.IsEmpty(await Analyze(body));

    /// <summary>
    /// Preserves a variable-length sequence whose framework selector is the actual SPI quoting method.
    /// </summary>
    /// <param name="selector">The runtime-owned quoting method.</param>
    [TestMethod]
    [DataRow("Spi.QuoteIdentifier")]
    [DataRow("Spi.QuoteLiteral")]
    public async Task QuotedSelectCanBuildAJoinedFragmentList(string selector)
        => Assert.IsEmpty(await Analyze("string[] columns = [value, value]; " +
            "Spi.Execute(\"SELECT \" + string.Join(\", \", System.Linq.Enumerable.Select(columns, " + selector + ")));"));

    /// <summary>
    /// Rejects the same joined sequence when its selector leaves runtime values unquoted.
    /// </summary>
    [TestMethod]
    public async Task UnquotedSelectCannotBuildAJoinedIdentifierList()
    {
        const string body = "string[] columns = [value, value]; " +
            "Spi.Execute(\"SELECT \" + string.Join(\", \", System.Linq.Enumerable.Select(columns, column => column)));";
        Diagnostic error = Assert.ContainsSingle(await Analyze(body));
        Assert.AreEqual("ANKUS044", error.Id);
        Assert.AreEqual("\"SELECT \" + string.Join(\", \", System.Linq.Enumerable.Select(columns, column => column))",
            error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// Preserves every safely quoted branch after the exact-layout representation reaches its bound.
    /// </summary>
    [TestMethod]
    public async Task QuotedOptionalClausesRemainValidBeyondLayoutBound()
    {
        string clauses = string.Join(' ', Enumerable.Range(0, 9).Select(index =>
            FormattableString.Invariant($"if ((number & {1 << index}) != 0) {{ sql += \" AND value{index} = \" + Spi.QuoteLiteral(value); }}")));
        Assert.IsEmpty(await Analyze("string sql = \"SELECT * FROM t WHERE true\"; " + clauses + " Spi.Execute(sql);"));
    }

    /// <summary>
    /// Keeps unquoted loop input rejected while accepting quoted loop summaries.
    /// </summary>
    [TestMethod]
    public async Task LoopSummaryRetainsUnquotedInputHazards()
    {
        const string body = "string sql = \"SELECT * FROM t WHERE true\"; string[] filters = [value, value]; " +
            "foreach (string filter in filters) { sql += \" AND name = \" + filter; } Spi.Execute(sql);";
        Diagnostic error = Assert.ContainsSingle(await Analyze(body));
        Assert.AreEqual("ANKUS044", error.Id);
        Assert.AreEqual("sql", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// Refuses to summarize a quote-helper result that becomes part of another PostgreSQL token.
    /// </summary>
    [TestMethod]
    public async Task LoopSummaryRejectsQuotedFragmentsAcrossTokenBoundaries()
    {
        const string body = "string sql = \"SELECT E\"; string[] values = [value, value]; " +
            "foreach (string item in values) { sql += Spi.QuoteLiteral(item); } Spi.Execute(sql);";
        Diagnostic error = Assert.ContainsSingle(await Analyze(body));
        Assert.AreEqual("ANKUS044", error.Id);
        Assert.AreEqual("sql", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// Keeps one invalid quoted branch visible after the exact-layout representation reaches its bound.
    /// </summary>
    [TestMethod]
    public async Task QuotedOptionalClausesRetainInvalidTokenContexts()
    {
        string clauses = string.Join(' ', Enumerable.Range(0, 8).Select(index =>
            FormattableString.Invariant($"if ((number & {1 << index}) != 0) {{ sql += \" AND value{index} = \" + Spi.QuoteLiteral(value); }}")));
        const string invalid = "if ((number & 256) != 0) { sql += \" AND '\" + Spi.QuoteLiteral(value) + \"'\"; }";
        Diagnostic error = Assert.ContainsSingle(await Analyze("string sql = \"SELECT * FROM t WHERE true\"; " + clauses + " " + invalid + " Spi.Execute(sql);"));
        Assert.AreEqual("ANKUS044", error.Id);
        Assert.AreEqual("sql", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// Resolves nonconstant array reads from every possible element instead of assuming safe raw text.
    /// </summary>
    [TestMethod]
    public async Task NonconstantArrayIndexRetainsUnsafeElements()
    {
        const string body = "object[] inputs = { Spi.QuoteLiteral(value), value }; " +
            "Spi.Execute(string.Format(\"SELECT {0}\", inputs[number & 1]));";
        Diagnostic error = Assert.ContainsSingle(await Analyze(body));
        Assert.AreEqual("ANKUS044", error.Id);
        Assert.AreEqual("string.Format(\"SELECT {0}\", inputs[number & 1])",
            error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// Applies increment operations before resolving a later indexed command value.
    /// </summary>
    [TestMethod]
    public async Task IncrementedArrayIndexSelectsTheCurrentElement()
    {
        const string body = "object[] inputs = { Spi.QuoteLiteral(value), value }; int index = 0; index++; " +
            "Spi.Execute(string.Format(\"SELECT {0}\", inputs[index]));";
        Diagnostic error = Assert.ContainsSingle(await Analyze(body));
        Assert.AreEqual("ANKUS044", error.Id);
        Assert.AreEqual("string.Format(\"SELECT {0}\", inputs[index])",
            error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// Merges conditional ref targets that include previously untracked parameters without crashing the analyzer.
    /// </summary>
    [TestMethod]
    public async Task ConditionalRefParameterTargetsProduceTheCommandDiagnostic()
    {
        const string body = "string other = Spi.QuoteLiteral(value); bool flag = number != 0; " +
            "(flag ? ref value : ref other) = number.ToString(); Spi.Execute($\"SELECT {value} {other}\");";
        Diagnostic error = Assert.ContainsSingle(await Analyze(body));
        Assert.AreEqual("ANKUS044", error.Id);
        Assert.AreEqual("$\"SELECT {value} {other}\"",
            error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// Bounds repeated local-helper interpretation and fails closed on the affected command.
    /// </summary>
    [TestMethod]
    public async Task RepeatedLocalHelperCallsRespectTheAnalysisBudget()
    {
        string helpers = string.Join(' ', Enumerable.Range(0, 16).Select(index =>
            index == 15 ? "void Step15() { sql = value; }" :
                FormattableString.Invariant($"void Step{index}() {{ Step{index + 1}(); Step{index + 1}(); }}")));
        Diagnostic error = Assert.ContainsSingle(await Analyze("string sql = \"SELECT 1\"; " + helpers + " Step0(); Spi.Execute(sql);"));
        Assert.AreEqual("ANKUS044", error.Id);
        Assert.AreEqual("sql", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// Supplies ordinary helpers outside the analyzed method without executing their consumer code.
    /// </summary>
    private const string FlowHelpers = """
        internal static class Other
        {
            internal static void Maybe(System.Action callback) { }
            internal static void Mutate(System.Text.StringBuilder builder) { }
        }
        """;
}
