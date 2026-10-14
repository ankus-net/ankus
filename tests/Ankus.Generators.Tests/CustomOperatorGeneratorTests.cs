using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies generated value-semantic operators, diagnostics and index-family dependency contracts.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Executes exact interfaces and preserves extreme comparison signs independently of stored metadata.
    /// </summary>
    [TestMethod]
    public void CustomOperatorHelpersExecuteExactValueContracts()
    {
        string[] result = RunCustomOperatorProbe(CustomOperatorValueSource, """
            var a=new Value(1,10); var same=new Value(1,99); var b=new Value(2,10);
            return new[]{Call("eq",a,same),Call("ne",a,same),Call("eq",a,b),Call("ne",a,b),
                Call("lt",a,b),Call("le",a,b),Call("gt",a,b),Call("ge",a,b),
                Call("lt",b,a),Call("le",b,a),Call("gt",b,a),Call("ge",b,a),
                Call("lt",a,same),Call("le",a,same),Call("gt",a,same),Call("ge",a,same),
                Call("cmp",a,b),Call("cmp",a,same),Call("cmp",b,a),Call("hash",a),Call("hash",same),
                a.Metadata.ToString(),same.Metadata.ToString()};
            """);
        Assert.AreSequenceEqual(["True", "False", "False", "True", "True", "True", "False", "False",
            "False", "False", "True", "True", "False", "True", "False", "True",
            "-2147483648", "0", "2147483647", "-991", "-991", "10", "99"], result);
    }

    /// <summary>
    /// Inherited exact interfaces remain callable from generated closed helpers.
    /// </summary>
    [TestMethod]
    public void CustomOperatorInheritedContractsExecute()
    {
        string[] result = RunCustomOperatorProbe("""
            public abstract class Parent : System.IEquatable<Value>, System.IComparable<Value>, Ankus.IPgHashable
            {
                public bool Equals(Value? other)=>other?.Number==7;
                public int CompareTo(Value? other)=>other?.Number==7?0:-19;
                public int GetPostgresHashCode()=>123456789;
            }
            [Ankus.PgType][Ankus.PgEquality][Ankus.PgOrdering][Ankus.PgHashing]
            public sealed class Value(int number):Parent { public int Number{get;}=number; }
            """, "return new[]{Call(\"eq\",new Value(7),new Value(7)),Call(\"cmp\",new Value(7),new Value(8)),Call(\"hash\",new Value(7))};");
        Assert.AreSequenceEqual(["True", "-19", "123456789"], result);
    }

    /// <summary>
    /// Native and custom-base enums use numeric order and independently known little-endian hash inputs.
    /// </summary>
    /// <param name="attribute">The SQL enum or base-type declaration.</param>
    /// <param name="underlying">The integral representation.</param>
    /// <param name="first">A numerically high value declared first.</param>
    /// <param name="second">A numerically low value declared second.</param>
    /// <param name="firstHash">An independent SeaHash oracle result for the high value.</param>
    /// <param name="secondHash">An independent SeaHash oracle result for the low value.</param>
    [TestMethod]
    [DataRow("PgEnum", "ulong", "ulong.MaxValue", "0", "851917799", "-1536329377")]
    [DataRow("PgType", "ulong", "ulong.MaxValue", "0", "851917799", "-1536329377")]
    [DataRow("PgEnum", "long", "-1", "long.MinValue", "851917799", "721385558")]
    [DataRow("PgType", "long", "-1", "long.MinValue", "851917799", "721385558")]
    public void CustomOperatorEnumsPreserveNumericOrderAndHashBits(string attribute, string underlying, string first, string second,
        string firstHash, string secondHash)
    {
        string[] result = RunCustomOperatorProbe($$"""
            [Ankus.{{attribute}}][Ankus.PgEquality][Ankus.PgOrdering][Ankus.PgHashing]
            public enum Value:{{underlying}} { First={{first}}, Second={{second}} }
            """, "return new[]{Call(\"gt\",Value.First,Value.Second),Call(\"lt\",Value.First,Value.Second),Call(\"eq\",Value.Second,Value.Second),Call(\"hash\",Value.First),Call(\"hash\",Value.Second)};");
        Assert.AreSequenceEqual(["True", "False", "True", firstHash, secondHash], result);
    }

    /// <summary>
    /// Emits complete, strictly typed families after all support functions and operators.
    /// </summary>
    [TestMethod]
    public void CustomOperatorFamiliesRetainExactSqlAndDependencies()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(CustomOperatorValueSource.Replace(
            "[Ankus.PgType]", "[Ankus.PgType(Name=\"renamed\",Schema=\"Operators Here\",Id=\"type\")]", StringComparison.Ordinal));
        AssertAggregateCompilation(compilation, diagnostics);
        string[] sql = OperatorCastStatements(compilation);
        string equality = Assert.ContainsSingle(sql.Where(static statement => statement.StartsWith("CREATE OPERATOR \"Operators Here\".= ", StringComparison.Ordinal)));
        Assert.AreEqual("CREATE OPERATOR \"Operators Here\".= (FUNCTION = \"Operators Here\".\"renamed_eq\", LEFTARG = \"Operators Here\".\"renamed\", RIGHTARG = \"Operators Here\".\"renamed\", COMMUTATOR = OPERATOR(\"Operators Here\".=), NEGATOR = OPERATOR(\"Operators Here\".<>), RESTRICT = pg_catalog.eqsel, JOIN = pg_catalog.eqjoinsel, HASHES, MERGES);", equality);
        string btree = Assert.ContainsSingle(sql.Where(static statement => statement.StartsWith("CREATE OPERATOR CLASS", StringComparison.Ordinal) && statement.Contains("USING btree", StringComparison.Ordinal)));
        string hashing = Assert.ContainsSingle(sql.Where(static statement => statement.StartsWith("CREATE OPERATOR CLASS", StringComparison.Ordinal) && statement.Contains("USING hash", StringComparison.Ordinal)));
        Assert.Contains("DEFAULT FOR TYPE \"Operators Here\".\"renamed\" USING btree", btree);
        string[] tokens = ["<", "<=", "=", ">=", ">"];
        for (int index = 0; index < tokens.Length; index++)
        {
            Assert.Contains($"OPERATOR {index + 1} \"Operators Here\".{tokens[index]} (\"Operators Here\".\"renamed\",\"Operators Here\".\"renamed\")", btree);
        }

        Assert.Contains("FUNCTION 1 \"Operators Here\".\"renamed_cmp\"(", btree);
        Assert.Contains("DEFAULT FOR TYPE \"Operators Here\".\"renamed\" USING hash", hashing);
        Assert.Contains("OPERATOR 1 \"Operators Here\".= (", hashing);
        Assert.Contains("FUNCTION 1 \"Operators Here\".\"renamed_hash\"(", hashing);
        foreach (string role in new[] { "eq", "ne", "lt", "gt", "le", "ge", "cmp", "hash" })
        {
            string helper = Assert.ContainsSingle(sql.Where(statement => statement.StartsWith("CREATE FUNCTION \"Operators Here\".\"renamed_" + role + "\"(", StringComparison.Ordinal)));
            Assert.Contains("LANGUAGE c IMMUTABLE PARALLEL SAFE STRICT;", helper);
            Assert.IsLessThan(Array.IndexOf(sql, role == "hash" ? hashing : btree), Array.IndexOf(sql, helper));
        }

        Assert.IsLessThan(Array.IndexOf(sql, btree), Array.IndexOf(sql, equality));
        Assert.IsLessThan(Array.IndexOf(sql, hashing), Array.IndexOf(sql, equality));
    }

    /// <summary>
    /// Each opt-in adds only its own callbacks and uses a declared equality operator when needed.
    /// </summary>
    /// <param name="attribute">The independent opt-in.</param>
    /// <param name="family">The only expected access method, or empty for equality alone.</param>
    [TestMethod]
    [DataRow("PgEquality", "")]
    [DataRow("PgOrdering", "btree")]
    [DataRow("PgHashing", "hash")]
    public void CustomOperatorOptionsAcceptManualEquality(string attribute, string family)
    {
        string source = CustomOperatorValueSource.Replace("[Ankus.PgEquality][Ankus.PgOrdering][Ankus.PgHashing]", $"[Ankus.{attribute}]", StringComparison.Ordinal);
        if (family.Length != 0)
        {
            source += "public static class Functions { [Ankus.PgOperator(\"=\")] public static bool Equal(Value left,Value right)=>((System.IEquatable<Value>)left).Equals(right); }";
        }

        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        AssertAggregateCompilation(compilation, diagnostics);
        string[] classes = [.. OperatorCastStatements(compilation).Where(static statement => statement.StartsWith("CREATE OPERATOR CLASS", StringComparison.Ordinal))];
        if (family.Length == 0)
        {
            Assert.IsEmpty(classes);
            Assert.DoesNotContain("_cmp\"", ManifestValue(compilation, "Ankus.Sql"));
            Assert.DoesNotContain("_hash\"", ManifestValue(compilation, "Ankus.Sql"));
        }
        else
        {
            Assert.Contains(" USING " + family + " ", Assert.ContainsSingle(classes));
            Assert.DoesNotContain("\"value_eq\"", ManifestValue(compilation, "Ankus.Sql"));
        }
    }

    /// <summary>
    /// Rejects missing or wrong exact contracts instead of selecting object or lookalike methods.
    /// </summary>
    /// <param name="source">A complete invalid declaration.</param>
    /// <param name="expected">The precise generated-operator diagnostic.</param>
    [TestMethod]
    [DataRow("[Ankus.PgEquality] public readonly record struct Value(int Number);", "ANKUS413")]
    [DataRow("[Ankus.PgType][Ankus.PgEquality] public sealed class Value { public int Number{get;set;} }", "ANKUS415")]
    [DataRow("[Ankus.PgType][Ankus.PgEquality] public sealed class Value:System.IEquatable<int> { public int Number{get;set;} public bool Equals(int other)=>true; }", "ANKUS415")]
    [DataRow("[Ankus.PgType][Ankus.PgEquality][Ankus.PgOrdering] public readonly record struct Value(int Number);", "ANKUS416")]
    [DataRow("[Ankus.PgType][Ankus.PgEquality][Ankus.PgOrdering] public readonly record struct Value(int Number):System.IComparable<int> { public int CompareTo(int other)=>0; }", "ANKUS416")]
    [DataRow("[Ankus.PgType][Ankus.PgEquality][Ankus.PgHashing] public readonly record struct Value(int Number) { public int GetPostgresHashCode()=>7; }", "ANKUS417")]
    [DataRow("[Ankus.PgType][Ankus.PgOrdering] public readonly record struct Value(int Number):System.IComparable<Value> { public int CompareTo(Value other)=>0; }", "ANKUS418")]
    [DataRow("[Ankus.PgType][Ankus.PgHashing] public readonly record struct Value(int Number):Ankus.IPgHashable { public int GetPostgresHashCode()=>7; }", "ANKUS418")]
    [DataRow("[Ankus.PgEnum][Ankus.PgOrdering] public enum Value { One,Two }", "ANKUS418")]
    [DataRow("[Ankus.PgEnum][Ankus.PgHashing] public enum Value { One,Two }", "ANKUS418")]
    public void InvalidCustomOperatorContractsAreDiagnosed(string source, string expected)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Diagnostic error = Assert.ContainsSingle(diagnostics.Where(diagnostic => diagnostic.Id == expected));
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.IsFalse(compilation.Assembly.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            (string?)attribute.ConstructorArguments[0].Value == "Ankus.Sql" && !string.IsNullOrEmpty((string?)attribute.ConstructorArguments[1].Value)));
    }

    /// <summary>
    /// Resolves each attribute's graph identity and diagnoses missing requirements, cycles and SQL collisions.
    /// </summary>
    /// <param name="change">The additional conflicting declaration or changed attribute.</param>
    [TestMethod]
    [DataRow("missing")]
    [DataRow("cycle")]
    [DataRow("operator")]
    [DataRow("function")]
    public void CustomOperatorGraphsDiagnoseInvalidDependencies(string change)
    {
        string source = change switch
        {
            "missing" => CustomOperatorValueSource.Replace("[Ankus.PgOrdering]", "[Ankus.PgOrdering(Requires=new[]{\"absent\"})]", StringComparison.Ordinal),
            "cycle" => "[assembly:Ankus.PgSql(\"after\",\"SELECT 1;\",Requires=new[]{\"order\"})]" +
                CustomOperatorValueSource.Replace("[Ankus.PgOrdering]", "[Ankus.PgOrdering(Id=\"order\",Requires=new[]{\"after\"})]", StringComparison.Ordinal),
            "operator" => CustomOperatorValueSource + "public static class Functions { [Ankus.PgOperator(\"=\")] public static bool Equal(Value a,Value b)=>true; }",
            _ => CustomOperatorValueSource + "public static class Functions { [Ankus.PgFunction(Name=\"value_cmp\")] public static int Compare(Value a,Value b)=>0; }",
        };
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        (string Id, string Span, int Occurrence, string[] Arguments) expected = change switch
        {
            "missing" => ("ANKUS498", "\"absent\"", 0, ["absent"]),
            "cycle" => ("ANKUS499", "\"after\"", 1, ["order -> after -> order"]),
            "operator" => ("ANKUS511", "Ankus.PgEquality", 0, ["=(\"value\",\"value\")", "Value"]),
            _ => ("ANKUS510", "Ankus.PgOrdering", 0, ["\"value_cmp\"(\"value\",\"value\")", "Value"]),
        };
        AssertSqlControlGraphError(compilation, diagnostics, expected.Id, expected.Span, expected.Arguments);
        AssertDiagnosticOccurrence(diagnostics[0], expected.Span, expected.Occurrence);
    }

    /// <summary>
    /// Each declared group ID includes its complete support graph and honors its prerequisites.
    /// </summary>
    [TestMethod]
    public void CustomOperatorGroupIdsOrderCompleteFamilies()
    {
        string source = """
            [assembly:Ankus.PgSql("before","SELECT 'before';")]
            [assembly:Ankus.PgSql("after","SELECT 'after';",Requires=new[]{"equality","ordering","hashing"})]
            """ + CustomOperatorValueSource
            .Replace("[Ankus.PgEquality]", "[Ankus.PgEquality(Id=\"equality\",Requires=new[]{\"before\"})]", StringComparison.Ordinal)
            .Replace("[Ankus.PgOrdering]", "[Ankus.PgOrdering(Id=\"ordering\",Requires=new[]{\"before\"})]", StringComparison.Ordinal)
            .Replace("[Ankus.PgHashing]", "[Ankus.PgHashing(Id=\"hashing\",Requires=new[]{\"before\"})]", StringComparison.Ordinal);
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        AssertAggregateCompilation(compilation, diagnostics);
        string[] statements = OperatorCastStatements(compilation);
        int before = Array.IndexOf(statements, "SELECT 'before';");
        int after = Array.IndexOf(statements, "SELECT 'after';");
        Assert.IsGreaterThanOrEqualTo(0, before);
        Assert.IsGreaterThan(before, after);
        foreach (int position in statements.Where(static sql => sql.StartsWith("CREATE OPERATOR", StringComparison.Ordinal) ||
            sql.StartsWith("CREATE FUNCTION \"value_", StringComparison.Ordinal) && !sql.StartsWith("CREATE FUNCTION \"value_in\"", StringComparison.Ordinal) &&
            !sql.StartsWith("CREATE FUNCTION \"value_out\"", StringComparison.Ordinal)).Select(statement => Array.IndexOf(statements, statement)))
        {
            Assert.IsGreaterThan(before, position);
            Assert.IsLessThan(after, position);
        }
    }

    /// <summary>
    /// An equality operator in another schema does not satisfy the exact same-schema family contract.
    /// </summary>
    [TestMethod]
    public void CustomOperatorManualEqualityRequiresMatchingSchema()
    {
        string source = CustomOperatorValueSource.Replace("[Ankus.PgEquality]", string.Empty, StringComparison.Ordinal) +
            "public static class Functions { [Ankus.PgOperator(\"=\")][Ankus.PgFunction(Schema=\"elsewhere\")] public static bool Equal(Value a,Value b)=>true; }";
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Assert.ContainsSingle(diagnostics.Where(static diagnostic => diagnostic.Id == "ANKUS418"));
    }

    /// <summary>
    /// A same-named nonboolean operator cannot supply an index family's equality strategy.
    /// </summary>
    /// <param name="attribute">The opt-in requiring manual equality.</param>
    [TestMethod]
    [DataRow("PgOrdering")]
    [DataRow("PgHashing")]
    public void CustomOperatorManualEqualityMustReturnBoolean(string attribute)
    {
        string source = CustomOperatorValueSource.Replace("[Ankus.PgEquality][Ankus.PgOrdering][Ankus.PgHashing]", $"[Ankus.{attribute}]", StringComparison.Ordinal) +
            "public static class Functions { [Ankus.PgOperator(\"=\")] public static int Equal(Value a,Value b)=>0; }";
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Assert.ContainsSingle(diagnostics.Where(static diagnostic => diagnostic.Id == "ANKUS418"));
        Assert.IsFalse(compilation.Assembly.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            (string?)attribute.ConstructorArguments[0].Value == "Ankus.Sql" && !string.IsNullOrEmpty((string?)attribute.ConstructorArguments[1].Value)));
    }

    /// <summary>
    /// Long derived identifiers are bounded and deterministic without collapsing separate roles or types.
    /// </summary>
    /// <param name="unicode">Whether the type name reaches the limit through multibyte UTF-8.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CustomOperatorLongNamesRemainDistinctAndDeterministic(bool unicode)
    {
        string name = unicode ? new string('é', 31) : new string('n', 63);
        string source = CustomOperatorValueSource.Replace("[Ankus.PgType]", "[Ankus.PgType(Name=\"" + name + "\",Schema=\"one\")]", StringComparison.Ordinal);
        string one = "namespace One {" + source + "}";
        string two = "namespace Two {" + source.Replace("Schema=\"one\"", "Schema=\"two\"", StringComparison.Ordinal) + "}";
        (Compilation first, ImmutableArray<Diagnostic> firstDiagnostics) = Generate(one + two);
        (Compilation second, ImmutableArray<Diagnostic> secondDiagnostics) = Generate(two + one);
        AssertAggregateCompilation(first, firstDiagnostics);
        AssertAggregateCompilation(second, secondDiagnostics);
        Assert.AreEqual(ManifestValue(first, "Ankus.Sql"), ManifestValue(second, "Ankus.Sql"));
        string[] helpers = [.. OperatorCastStatements(first).Where(static sql => sql.StartsWith("CREATE FUNCTION", StringComparison.Ordinal) && sql.Contains(".\"ankus_", StringComparison.Ordinal))];
        Assert.IsNotEmpty(helpers);
        var names = new List<string>();
        foreach (string identifier in helpers.Select(static helper => helper.Split('"')[3]))
        {
            names.Add(identifier);
            Assert.IsLessThanOrEqualTo(63, System.Text.Encoding.UTF8.GetByteCount(identifier));
        }

        Assert.HasCount(helpers.Length, names.Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// Compiles and executes ordinary generated helpers without invoking unmanaged entry points through reflection.
    /// </summary>
    private string[] RunCustomOperatorProbe(string declarations, string body)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(declarations + $$"""
            public static class OperatorProbe
            {
                public static string[] Run()
                {
                    string Call(string role,params object[] values)
                    {
                        var methods=typeof(Value).Assembly.GetType("Ankus.Generated.ExtensionDispatchers")!.GetMethods(System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
                        var method=System.Linq.Enumerable.Single(methods,m=>m.Name.StartsWith("ankus_operator_",System.StringComparison.Ordinal)&&m.Name.EndsWith("_"+role,System.StringComparison.Ordinal));
                        return method.Invoke(null,values)!.ToString()!;
                    }
                    {{body}}
                }
            }
            """);
        AssertAggregateCompilation(compilation, diagnostics);
        using var bytes = new MemoryStream();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(bytes, cancellationToken: context.CancellationToken);
        Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        bytes.Position = 0;
        var load = new AssemblyLoadContext("CustomOperatorProbe", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(bytes);
            return Assert.IsInstanceOfType<string[]>(assembly.GetType("OperatorProbe", throwOnError: true)!.GetMethod("Run")!.Invoke(null, null));
        }
        finally
        {
            load.Unload();
        }
    }

    /// <summary>
    /// Supplies explicit exact interfaces whose results cannot come from reference or structural equality.
    /// </summary>
    private const string CustomOperatorValueSource = """
        [Ankus.PgType][Ankus.PgEquality][Ankus.PgOrdering][Ankus.PgHashing]
        public sealed class Value(int number,int metadata):System.IEquatable<Value>,System.IComparable<Value>,Ankus.IPgHashable
        {
            public int Number{get;}=number;
            public int Metadata{get;}=metadata;
            bool System.IEquatable<Value>.Equals(Value? other)=>other?.Number==Number;
            int System.IComparable<Value>.CompareTo(Value? other)=>Number<other!.Number?int.MinValue:Number>other.Number?int.MaxValue:0;
            int Ankus.IPgHashable.GetPostgresHashCode()=>Number-992;
        }
        """;
}
