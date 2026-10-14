using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Read-only mappings retain exact explicit value contracts without constructing the converter during registration or helper calls.
    /// </summary>
    [TestMethod]
    public void MappedOperatorHelpersExecuteExactContracts()
    {
        string[] result = RunCustomOperatorProbe(MappedOperatorSource(), """
            var a = new Value(1, 10); var same = new Value(1, 99); var b = new Value(2, 10);
            return new[] { Call("eq", a, same), Call("ne", a, same), Call("eq", a, b), Call("ne", a, b),
                Call("lt", a, b), Call("le", a, b), Call("gt", a, b), Call("ge", a, b),
                Call("lt", b, a), Call("le", b, a), Call("gt", b, a), Call("ge", b, a),
                Call("lt", a, same), Call("le", a, same), Call("gt", a, same), Call("ge", a, same),
                Call("cmp", a, b), Call("cmp", a, same), Call("cmp", b, a), Call("hash", a), Call("hash", same),
                a.Metadata.ToString(), same.Metadata.ToString(), Converter.Created.ToString() };
            """);
        Assert.AreSequenceEqual(["True", "False", "False", "True", "True", "True", "False", "False",
            "False", "False", "True", "True", "False", "True", "False", "True",
            "-2147483648", "0", "2147483647", "-991", "-991", "10", "99", "0"], result);
    }

    /// <summary>
    /// Inherited interfaces on an exact reference root and explicit interfaces on a value root remain statically callable.
    /// </summary>
    /// <param name="valueType">Whether the mapped root is a struct instead of inheriting its interfaces.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MappedOperatorInheritedAndValueContractsExecute(bool valueType)
    {
        string declaration = valueType ? """
            public readonly struct Value(int number) : System.IEquatable<Value>, System.IComparable<Value>, Ankus.IPgHashable
            {
                public int Number { get; } = number;
                bool System.IEquatable<Value>.Equals(Value other) => Number == other.Number;
                int System.IComparable<Value>.CompareTo(Value other) => Number < other.Number ? -19 : Number > other.Number ? 23 : 0;
                int Ankus.IPgHashable.GetPostgresHashCode() => Number + 123456782;
            }
            """ : "public sealed class Value(int number) : Parent { public override int Number => number; }";
        string source = """
            public abstract class Parent : System.IEquatable<Value>, System.IComparable<Value>, Ankus.IPgHashable
            {
                public abstract int Number { get; }
                public bool Equals(Value? other) => Number == other?.Number;
                public int CompareTo(Value? other) => Number < other!.Number ? -19 : Number > other.Number ? 23 : 0;
                public int GetPostgresHashCode() => Number + 123456782;
            }
            """;
        source = (valueType ? string.Empty : source) + DatumMappingSource(
            "[Ankus.PgEquality][Ankus.PgOrdering][Ankus.PgHashing]" + declaration, writer: false);
        string[] result = RunCustomOperatorProbe(source,
            "return new[] { Call(\"eq\", new Value(7), new Value(7)), Call(\"cmp\", new Value(7), new Value(8)), Call(\"cmp\", new Value(8), new Value(7)), Call(\"hash\", new Value(7)) }; ");
        Assert.AreSequenceEqual(["True", "-19", "23", "123456789"], result);
    }

    /// <summary>
    /// Mapped enums compare their underlying numeric values and hash unnamed bits through the frozen byte algorithm.
    /// </summary>
    /// <param name="underlying">The signed or unsigned integral representation.</param>
    /// <param name="high">The larger unnamed value.</param>
    /// <param name="low">The smaller unnamed value.</param>
    /// <param name="highHash">An independent SeaHash fixture for the larger value.</param>
    /// <param name="lowHash">An independent SeaHash fixture for the smaller value.</param>
    [TestMethod]
    [DataRow("ulong", "ulong.MaxValue", "0", "851917799", "-1536329377")]
    [DataRow("long", "-1", "long.MinValue", "851917799", "721385558")]
    public void MappedOperatorEnumsPreserveUnnamedBits(string underlying, string high, string low, string highHash, string lowHash)
    {
        string source = DatumMappingSource("[Ankus.PgEquality][Ankus.PgOrdering][Ankus.PgHashing] public enum Value : " + underlying + " { Named = 7 }", writer: false);
        string[] result = RunCustomOperatorProbe(source, "var high = (Value)(" + high + "); var low = (Value)(" + low + "); " +
            "return new[] { Call(\"gt\", high, low), Call(\"lt\", high, low), Call(\"eq\", low, low), Call(\"hash\", high), Call(\"hash\", low) }; ");
        Assert.AreSequenceEqual(["True", "False", "True", highHash, lowHash], result);
    }

    /// <summary>
    /// A module with only derived mapped helpers has exact SQL contracts, one lazy registration and the required native raw-input implementation.
    /// </summary>
    [TestMethod]
    public void MappedExternalOperatorContractsRetainExactSqlAndNativeInputs()
    {
        Compilation compilation = GenerateSqlControl(MappedOperatorSource());
        string[] statements = OperatorCastStatements(compilation);
        Assert.HasCount(18, statements);
        Assert.AreEqual("false", ManifestValue(compilation, "Ankus.Relocatable"));
        Assert.Contains("CREATE OPERATOR \"mapped\".= (FUNCTION = \"mapped\".\"key_eq\", LEFTARG = \"mapped\".\"key\", RIGHTARG = \"mapped\".\"key\", COMMUTATOR = OPERATOR(\"mapped\".=), NEGATOR = OPERATOR(\"mapped\".<>), RESTRICT = pg_catalog.eqsel, JOIN = pg_catalog.eqjoinsel, HASHES, MERGES);", statements);
        const string btree = "CREATE OPERATOR CLASS \"mapped\".\"key_btree_ops\" DEFAULT FOR TYPE \"mapped\".\"key\" USING btree FAMILY \"mapped\".\"key_btree_ops\" AS " +
            "OPERATOR 1 \"mapped\".< (\"mapped\".\"key\",\"mapped\".\"key\"), OPERATOR 2 \"mapped\".<= (\"mapped\".\"key\",\"mapped\".\"key\"), " +
            "OPERATOR 3 \"mapped\".= (\"mapped\".\"key\",\"mapped\".\"key\"), OPERATOR 4 \"mapped\".>= (\"mapped\".\"key\",\"mapped\".\"key\"), " +
            "OPERATOR 5 \"mapped\".> (\"mapped\".\"key\",\"mapped\".\"key\"), FUNCTION 1 \"mapped\".\"key_cmp\"(\"mapped\".\"key\",\"mapped\".\"key\");";
        Assert.Contains(btree, statements);
        Assert.Contains("CREATE OPERATOR CLASS \"mapped\".\"key_hash_ops\" DEFAULT FOR TYPE \"mapped\".\"key\" USING hash FAMILY \"mapped\".\"key_hash_ops\" AS OPERATOR 1 \"mapped\".= (\"mapped\".\"key\",\"mapped\".\"key\"), FUNCTION 1 \"mapped\".\"key_hash\"(\"mapped\".\"key\");", statements);
        string[] roles = ["eq", "ne", "cmp", "lt", "le", "gt", "ge", "hash"];
        foreach (string role in roles)
        {
            string helper = Assert.ContainsSingle(statements.Where(statement => statement.StartsWith("CREATE FUNCTION \"mapped\".\"key_" + role + "\"(", StringComparison.Ordinal)));
            Assert.Contains(role is "cmp" or "hash" ? "RETURNS integer" : "RETURNS boolean", helper);
            Assert.EndsWith("LANGUAGE c IMMUTABLE PARALLEL SAFE STRICT;", helper);
            Assert.Contains(role == "hash" ? "(\"mapped\".\"key\")" : "(\"mapped\".\"key\",\"mapped\".\"key\")", helper);
        }

        string managed = DatumMappingManaged(compilation);
        Assert.AreEqual(1, managed.Split("RegisterReference<global::Value>", StringSplitOptions.None).Length - 1);
        Assert.Contains("Converter(), true, false);", managed);
        Assert.AreEqual(15, managed.Split(".ReadMapped<global::Value>()", StringSplitOptions.None).Length - 1);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.AreEqual(1, native.Split("ankus_read_polymorphic(FunctionCallInfo call, int index, AnkusValue *value)", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("CREATE TYPE", InstallationBody(compilation));
        Assert.HasCount(8, SqlControlExports(compilation));
    }

    /// <summary>
    /// Completed providers and schema prerequisites precede every helper while the existing shell-to-I/O deferral remains legal.
    /// </summary>
    /// <param name="fixedSchema">Whether the owned mapping has a fixed schema.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MappedOperatorFamiliesFollowCompletedProviders(bool fixedSchema)
    {
        string source = """
            [assembly: Ankus.PgSql("shell", "SELECT 'shell';", Relocatable=true)]
            [assembly: Ankus.PgSql("complete", "SELECT 'complete';", Requires=["input"], Relocatable=true)]
            [assembly: Ankus.PgSqlTypeProvider("complete", typeof(Value))]
            [assembly: Ankus.PgSql("before", "SELECT 'before';", Relocatable=true)]
            [assembly: Ankus.PgSql("after", "SELECT 'after';", Requires=["equality","ordering","hashing"], Relocatable=true)]
            """ + (fixedSchema ? "[Ankus.PgSchema(\"mapped\", Requires=[\"before\"])] public static class Placement { }" : string.Empty) +
            MappedOperatorSource(external: false, schema: fixedSchema ? "mapped" : null)
                .Replace("[Ankus.PgEquality]", "[Ankus.PgEquality(Id=\"equality\", Requires=[\"before\"])]", StringComparison.Ordinal)
                .Replace("[Ankus.PgOrdering]", "[Ankus.PgOrdering(Id=\"ordering\", Requires=[\"before\"])]", StringComparison.Ordinal)
                .Replace("[Ankus.PgHashing]", "[Ankus.PgHashing(Id=\"hashing\", Requires=[\"before\"])]", StringComparison.Ordinal) + """
            public static class Functions
            {
                [Ankus.PgFunction(Id="input", Requires=["shell"], Sql="SELECT 'input';", SqlRelocatable=true)]
                public static int Input(Value value) => value.Number;
            }
            """;
        Compilation compilation = GenerateSqlControl(source);
        string[] statements = OperatorCastStatements(compilation);
        Assert.IsLessThan(Array.IndexOf(statements, "SELECT 'input';"), Array.IndexOf(statements, "SELECT 'shell';"));
        Assert.IsLessThan(Array.IndexOf(statements, "SELECT 'complete';"), Array.IndexOf(statements, "SELECT 'input';"));
        foreach (int position in statements.Where(static statement => statement.StartsWith("CREATE FUNCTION", StringComparison.Ordinal) || statement.StartsWith("CREATE OPERATOR", StringComparison.Ordinal)).Select(statement => Array.IndexOf(statements, statement)))
        {
            Assert.IsGreaterThan(Array.IndexOf(statements, "SELECT 'complete';"), position);
            Assert.IsGreaterThan(Array.IndexOf(statements, "SELECT 'before';"), position);
            Assert.IsLessThan(Array.IndexOf(statements, "SELECT 'after';"), position);
            if (fixedSchema)
            {
                Assert.IsGreaterThan(Array.IndexOf(statements, "CREATE SCHEMA IF NOT EXISTS \"mapped\";"), position);
            }
        }

        Assert.AreEqual(fixedSchema ? "false" : "true", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Distinct managed views can share one completed owned type without duplicating its declaration or selecting the undecorated alias's converter.
    /// </summary>
    [TestMethod]
    public void MappedOperatorAliasesShareOneCompletedProvider()
    {
        const string provider = """
            [assembly: Ankus.PgSql("complete", "SELECT 'complete';", Relocatable=true)]
            [assembly: Ankus.PgSqlTypeProvider("complete", typeof(Value))]
            [assembly: Ankus.PgSqlTypeProvider("complete", typeof(Other))]
            """;
        string value = MappedOperatorSource(external: false, schema: null);
        string other = DatumMappingSource("public readonly record struct Other(int Number);", writer: false,
            external: false, name: "key", type: "Other");
        Compilation first = GenerateSqlControl(provider + value + other);
        Compilation reversed = GenerateSqlControl(provider + other + value);
        Assert.AreEqual(InstallationBody(first), InstallationBody(reversed));
        Assert.StartsWith("SELECT 'complete';\nCREATE FUNCTION ", InstallationBody(first));
        Assert.ContainsSingle(OperatorCastStatements(first).Where(static statement => statement == "SELECT 'complete';"));
        string managed = DatumMappingManaged(first);
        Assert.Contains("RegisterReference<global::Value>", managed);
        Assert.Contains("RegisterValue<global::Other>", managed);
        Assert.Contains(".ReadMapped<global::Value>()", managed);
        Assert.DoesNotContain(".ReadMapped<global::Other>()", managed);
        Assert.HasCount(8, SqlControlExports(first));
        Assert.AreEqual("true", ManifestValue(first, "Ankus.Relocatable"));
    }

    /// <summary>
    /// A completion block cannot depend on a family that itself requires the completed type, even when family SQL is suppressed.
    /// </summary>
    /// <param name="disabled">Whether the family declaration emits no SQL.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MappedOperatorProvidersRejectReverseCompletionCycles(bool disabled)
    {
        string source = """
            [assembly: Ankus.PgSql("complete", "SELECT 'complete';", Requires=["ordering"], Relocatable=true)]
            [assembly: Ankus.PgSqlTypeProvider("complete", typeof(Value))]
            """ + MappedOperatorSource(external: false, schema: null).Replace("[Ankus.PgOrdering]",
                "[Ankus.PgOrdering(Id=\"ordering\", GenerateSql=" + (disabled ? "false" : "true") + ")]", StringComparison.Ordinal);
        AssertDatumGraphError(source, "ANKUS499", "\"ordering\"", 0, "ordering -> complete -> ordering");
    }

    /// <summary>
    /// Mapping factories cannot hide missing reader capabilities or exact managed contracts behind synthetic helper inputs.
    /// </summary>
    /// <param name="change">The invalid declaration partition.</param>
    /// <param name="id">The exact diagnostic category.</param>
    /// <param name="reason">The specific rejected contract.</param>
    [TestMethod]
    [DataRow("writer-equality", "ANKUS414", "requires an IPgDatumReader<T>")]
    [DataRow("writer-ordering", "ANKUS414", "requires an IPgDatumReader<T>")]
    [DataRow("writer-hashing", "ANKUS414", "requires an IPgDatumReader<T>")]
    [DataRow("equatable", "ANKUS415", "must implement IEquatable<T>")]
    [DataRow("comparable", "ANKUS416", "must implement IComparable<T>")]
    [DataRow("hashable", "ANKUS417", "must implement IPgHashable")]
    [DataRow("mapping", "ANKUS145", "exact non-nullable managed type")]
    [DataRow("provider", "ANKUS395", "requires a PgSqlTypeProvider")]
    public void InvalidMappedOperatorContractsAreDiagnosed(string change, string id, string reason)
    {
        string source = MappedOperatorSource();
        source = change switch
        {
            "writer-equality" or "writer-ordering" or "writer-hashing" => DatumMappingSource(
                "[Ankus." + (change == "writer-equality" ? "PgEquality" : change == "writer-ordering" ? "PgOrdering" : "PgHashing") + "] public enum Value { Zero }", reader: false),
            "equatable" => DatumMappingSource("[Ankus.PgEquality] public sealed class Value : System.IEquatable<int> { public bool Equals(int other) => true; }", writer: false),
            "comparable" => DatumMappingSource("[Ankus.PgEquality][Ankus.PgOrdering] public readonly record struct Value(int Number) : System.IComparable<int> { public int CompareTo(int other) => 0; }", writer: false),
            "hashable" => DatumMappingSource("[Ankus.PgEquality][Ankus.PgHashing] public readonly record struct Value(int Number) { public int GetPostgresHashCode() => 7; }", writer: false),
            "mapping" => source.Replace("IPgDatumReader<Value>", "IPgDatumReader<int>", StringComparison.Ordinal).Replace("public Value Read", "public int Read", StringComparison.Ordinal),
            _ => MappedOperatorSource(external: false, schema: null),
        };
        AssertDatumMappingError(source, id, reason);
    }

    /// <summary>
    /// A manual equality operand contract must match the mapped type and schema and return boolean.
    /// </summary>
    /// <param name="equality">The manual equality variation.</param>
    [TestMethod]
    [DataRow("valid")]
    [DataRow("missing")]
    [DataRow("schema")]
    [DataRow("result")]
    public void MappedOperatorManualEqualityRetainsExactContract(string equality)
    {
        string source = MappedOperatorSource().Replace("[Ankus.PgEquality]", string.Empty, StringComparison.Ordinal);
        if (equality != "missing")
        {
            source += "public static class Functions { [Ankus.PgOperator(\"=\")][Ankus.PgFunction(Schema=\"" +
                (equality == "schema" ? "elsewhere" : "mapped") + "\")] public static " + (equality == "result" ? "int" : "bool") +
                " Equal(Value left, Value right) => " + (equality == "result" ? "0" : "true") + "; }";
        }

        if (equality == "valid")
        {
            Compilation compilation = GenerateSqlControl(source);
            string sql = InstallationBody(compilation);
            Assert.DoesNotContain("\"key_eq\"", sql);
            Assert.Contains("OPERATOR 3 \"mapped\".= (\"mapped\".\"key\",\"mapped\".\"key\")", sql);
            Assert.Contains("OPERATOR 1 \"mapped\".= (\"mapped\".\"key\",\"mapped\".\"key\")", sql);
            Assert.HasCount(7, SqlControlExports(compilation));
        }
        else
        {
            AssertDatumMappingError(source, "ANKUS418", "boolean same-schema");
        }
    }

    /// <summary>
    /// Several managed aliases remain legal until they claim the same generated SQL objects; explicit helper and operator collisions also reject all artifacts.
    /// </summary>
    /// <param name="collision">The independently colliding SQL declaration.</param>
    [TestMethod]
    [DataRow("alias")]
    [DataRow("function")]
    [DataRow("operator")]
    public void MappedOperatorSqlIdentitiesRejectCollisions(string collision)
    {
        string source = MappedOperatorSource();
        source += collision switch
        {
            "alias" => "namespace Alias {" + MappedOperatorSource() + "}",
            "function" => "public static class Functions { [Ankus.PgFunction(Name=\"key_cmp\", Schema=\"mapped\")] public static int Compare(Value left, Value right) => 0; }",
            _ => "public static class Functions { [Ankus.PgOperator(\"=\")][Ankus.PgFunction(Schema=\"mapped\")] public static bool Equal(Value left, Value right) => true; }",
        };
        string[] expected = collision switch
        {
            "alias" =>
            [
                "ANKUS510 Ankus.PgEquality " + Key("\"key_eq\"(", "key", "key"),
                "ANKUS510 Ankus.PgEquality " + Key("\"key_ne\"(", "key", "key"),
                "ANKUS511 Ankus.PgEquality " + Key("=(", "key", "key"),
                "ANKUS511 Ankus.PgEquality " + Key("<>(", "key", "key"),
                "ANKUS510 Ankus.PgOrdering " + Key("\"key_cmp\"(", "key", "key"),
                "ANKUS510 Ankus.PgOrdering " + Key("\"key_lt\"(", "key", "key"),
                "ANKUS511 Ankus.PgOrdering " + Key("<(", "key", "key"),
                "ANKUS510 Ankus.PgOrdering " + Key("\"key_gt\"(", "key", "key"),
                "ANKUS511 Ankus.PgOrdering " + Key(">(", "key", "key"),
                "ANKUS510 Ankus.PgOrdering " + Key("\"key_le\"(", "key", "key"),
                "ANKUS511 Ankus.PgOrdering " + Key("<=(", "key", "key"),
                "ANKUS510 Ankus.PgOrdering " + Key("\"key_ge\"(", "key", "key"),
                "ANKUS511 Ankus.PgOrdering " + Key(">=(", "key", "key"),
                "ANKUS510 Ankus.PgHashing " + Key("\"key_hash\"(", "key"),
            ],
            "function" => ["ANKUS510 Ankus.PgOrdering \"mapped\".\"key_cmp\"(\"mapped\".\"key\",\"mapped\".\"key\")"],
            _ => ["ANKUS511 Ankus.PgEquality \"mapped\".=(\"mapped\".\"key\",\"mapped\".\"key\")"],
        };
        AssertDerivedCollisions(source, "Value", expected);

        static string Key(string name, params string[] operands)
            => "\"mapped\"." + name + string.Join(",", operands.Select(static operand => "\"mapped\".\"" + operand + "\"")) + ")";
    }

    /// <summary>
    /// Requires each generated SQL identity collision at its generating attribute with the fixed message, and the absence of all artifacts.
    /// </summary>
    /// <param name="source">The colliding declarations.</param>
    /// <param name="type">The managed type whose generated operator SQL collides.</param>
    /// <param name="expected">Each diagnostic ID, reported attribute text and duplicated signature, in report order.</param>
    private void AssertDerivedCollisions(string source, string type, params string[] expected)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        var actual = new List<string>();
        foreach (Diagnostic diagnostic in diagnostics)
        {
            Assert.IsTrue(diagnostic.Id is "ANKUS510" or "ANKUS511", diagnostic.ToString());
            string message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
            string prefix = diagnostic.Id == "ANKUS510" ? "Function " : "Operator ";
            int end = message.IndexOf(" generated for '", StringComparison.Ordinal);
            Assert.StartsWith(prefix, message);
            Assert.IsGreaterThan(prefix.Length, end);
            string signature = message[prefix.Length..end];
            AssertGraphDiagnostic(diagnostic, diagnostic.Id, DiagnosticText(diagnostic), signature, type);
            actual.Add(diagnostic.Id + " " + DiagnosticText(diagnostic) + " " + signature);
        }

        Assert.AreSequenceEqual(expected, actual, string.Join(Environment.NewLine, actual));
        Assert.IsNull(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers"));
        Assert.IsEmpty(compilation.Assembly.GetAttributes().Where(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute"));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static error => error.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Fixed external schemas order their helpers after declared schema prerequisites and cannot be made relocatable by a family replacement.
    /// </summary>
    /// <param name="kind">The family with a changed SQL policy.</param>
    /// <param name="disabled">Whether to disable its SQL instead of replacing it.</param>
    [TestMethod]
    [DataRow("ordering", false)]
    [DataRow("ordering", true)]
    [DataRow("hashing", false)]
    [DataRow("hashing", true)]
    public void MappedExternalOperatorSqlControlsPreserveHelpers(string kind, bool disabled)
    {
        const string prerequisite = """
            [assembly: Ankus.PgSql("prerequisite", "SELECT 'before';", Relocatable=true)]
            [Ankus.PgSchema("mapped", Requires=["prerequisite"])] public static class Placement { }
            """;
        string source = prerequisite + MappedOperatorSource();
        Compilation baseline = GenerateSqlControl(source);
        string attribute = kind == "ordering" ? "PgOrdering" : "PgHashing";
        string token = kind == "ordering" ? "@COMPARISON_FUNCTION_SQL@" : "@HASH_FUNCTION_SQL@";
        string role = kind == "ordering" ? "cmp" : "hash";
        string options = disabled ? "GenerateSql=false" : "Sql=\"SELECT '" + token + "';\", SqlRelocatable=true";
        Compilation changed = GenerateSqlControl(source.Replace("[Ankus." + attribute + "]", "[Ankus." + attribute + "(" + options + ")]", StringComparison.Ordinal));
        string sql = InstallationBody(changed);
        string family = string.Join("\n", OperatorCastStatements(baseline).Where(statement => statement.StartsWith("CREATE OPERATOR FAMILY", StringComparison.Ordinal) ||
            statement.StartsWith("CREATE OPERATOR CLASS", StringComparison.Ordinal)).Where(statement => statement.Contains(" USING " + (kind == "ordering" ? "btree" : "hash"), StringComparison.Ordinal)));
        Assert.HasCount(2, family.Split('\n'));
        foreach (string statement in family.Split('\n'))
        {
            Assert.DoesNotContain(statement, OperatorCastStatements(changed));
        }

        AssertSqlControlBoundary(baseline, changed);
        Assert.HasCount(8, SqlControlExports(changed));
        Assert.AreEqual("false", ManifestValue(changed, "Ankus.Relocatable"));
        Assert.StartsWith("SELECT 'before';\nCREATE SCHEMA IF NOT EXISTS \"mapped\";\n", sql);
        Assert.Contains("CREATE FUNCTION \"mapped\".\"key_" + role + "\"(", sql);
        Assert.HasCount(1, OperatorCastStatements(changed).Where(static statement => statement.StartsWith("CREATE OPERATOR CLASS", StringComparison.Ordinal)));
        if (!disabled)
        {
            Assert.ContainsSingle(OperatorCastStatements(changed).Where(statement => statement == "SELECT '\"mapped\".\"key_" + role + "\"';"));
            AssertSqlControlBefore(sql, "CREATE FUNCTION \"mapped\".\"key_" + role + "\"", "SELECT '\"mapped\".\"key_" + role + "\"';");
        }
    }

    /// <summary>
    /// Long UTF-8 mapped names use deterministic distinct helper identifiers while retaining the exact external SQL identity.
    /// </summary>
    [TestMethod]
    public void MappedOperatorLongNamesRemainDistinctAndDeterministic()
    {
        string name = new('é', 31);
        string one = "namespace One {" + MappedOperatorSource().Replace("\"key\"", "\"" + name + "\"", StringComparison.Ordinal) + "}";
        string two = "namespace Two {" + MappedOperatorSource().Replace("\"mapped\"", "\"other\"", StringComparison.Ordinal)
            .Replace("\"key\"", "\"" + name + "\"", StringComparison.Ordinal) + "}";
        Compilation first = GenerateSqlControl(one + two);
        Compilation second = GenerateSqlControl(two + one);
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        string[] helpers = [.. OperatorCastStatements(first).Where(static statement => statement.StartsWith("CREATE FUNCTION", StringComparison.Ordinal))];
        Assert.HasCount(16, helpers);
        var identifiers = new List<string>();
        foreach (string helper in helpers)
        {
            string identifier = helper.Split('"')[3];
            identifiers.Add(identifier);
            Assert.IsLessThanOrEqualTo(63, Encoding.UTF8.GetByteCount(identifier));
            Assert.Contains("\"" + name + "\"", helper);
        }

        Assert.HasCount(16, identifiers.Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// Supplies an explicit reader-only mapping with independent logical equality, extreme ordering and lazy construction observations.
    /// </summary>
    private static string MappedOperatorSource(bool external = true, string? schema = "mapped")
        => "[Ankus.PgDatumType(\"key\", typeof(Converter)" + (external ? ", Origin=Ankus.PgTypeOrigin.External" : string.Empty) +
            (schema is null ? string.Empty : ", Schema=\"" + schema + "\"") + ")]" + """
            [Ankus.PgEquality][Ankus.PgOrdering][Ankus.PgHashing]
            public sealed class Value(int number, int metadata) : System.IEquatable<Value>, System.IComparable<Value>, Ankus.IPgHashable
            {
                public int Number { get; } = number;
                public int Metadata { get; } = metadata;
                bool System.IEquatable<Value>.Equals(Value? other) => other?.Number == Number;
                int System.IComparable<Value>.CompareTo(Value? other) => Number < other!.Number ? int.MinValue : Number > other.Number ? int.MaxValue : 0;
                int Ankus.IPgHashable.GetPostgresHashCode() => Number - 992;
            }
            public sealed class Converter : Ankus.IPgDatumReader<Value>
            {
                public static int Created;
                public Converter() { Created++; }
                public Value Read(Ankus.PgDatum value) => throw new System.InvalidOperationException("Direct helpers must not invoke readers.");
            }
            """;
}
