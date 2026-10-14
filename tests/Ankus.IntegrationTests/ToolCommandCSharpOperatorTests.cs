using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Published C# operators preserve exact calls, conversion overloads, SQL ownership and same-session recovery.
    /// </summary>
    [TestMethod]
    public async Task CSharpOperatorPackageExecutesExactSignatures()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "CSharpOperators.csproj");
        File.Copy(s_project, project);
        await File.WriteAllTextAsync(Path.Join(directory, "Operators.cs"), CSharpOperatorPackageSource, token);
        string output = Path.Join(directory, "published");
        await PublishSqlControlsAsync(project, output, token);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await ExecuteSqlPackageAsync(connection, "CREATE SCHEMA op_first; CREATE SCHEMA op_second; CREATE EXTENSION ankus_tool_probe WITH SCHEMA op_first");

        foreach (string schema in new[] { "op_first", "op_second" })
        {
            await using var values = new NpgsqlCommand($$"""
                SELECT
                    (('{"Value":20}'::{{schema}}.metric OPERATOR({{schema}}.@+) '{"Value":22}'::{{schema}}.metric)::text::jsonb->>'Value')::integer,
                    (('{"Value":20}'::{{schema}}.metric OPERATOR({{schema}}.@^) '{"Value":22}'::{{schema}}.metric)::text::jsonb->>'Value')::integer,
                    ({{schema}}.negate(NULL::{{schema}}.metric)::text::jsonb->>'Value')::integer,
                    {{schema}}.is_positive('{"Value":20}'), {{schema}}.is_nonpositive('{"Value":20}'),
                    '{"Value":20}'::{{schema}}.metric::integer,
                    '{"Value":20}'::{{schema}}.metric::bigint,
                    {{schema}}.checked_integer('{"Value":20}'),
                    {{schema}}.checked_bigint('{"Value":20}'),
                    (42::{{schema}}.metric::text::jsonb->>'Value')::integer,
                    ({{schema}}.class_sum('{"Value":20}','{"Value":22}')::text::jsonb->>'Value')::integer,
                    {{schema}}.sum(NULL::{{schema}}.metric,'{"Value":22}') IS NULL,
                    (SELECT NOT proisstrict FROM pg_proc WHERE oid='{{schema}}.negate({{schema}}.metric)'::regprocedure),
                    (SELECT proisstrict FROM pg_proc WHERE oid='{{schema}}.sum({{schema}}.metric,{{schema}}.metric)'::regprocedure),
                    (SELECT count(*) FROM pg_depend d JOIN pg_extension e ON e.oid=d.refobjid
                        WHERE d.refclassid='pg_extension'::regclass AND e.extname='ankus_tool_probe'
                        AND d.classid='pg_cast'::regclass AND d.deptype='e'),
                    ARRAY(SELECT {{schema}}.sequence('{"Value":20}')),
                    {{schema}}.reference_identity(NULL::{{schema}}.ref_metric) IS NULL,
                    ({{schema}}.reference_identity('{"Value":20}')::text::jsonb->>'Value')::integer,
                    (SELECT NOT proisstrict FROM pg_proc WHERE oid='{{schema}}.reference_identity({{schema}}.ref_metric)'::regprocedure)
                """, connection);
            await using (NpgsqlDataReader reader = await values.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(42, reader.GetInt32(0));
                Assert.AreEqual(142, reader.GetInt32(1));
                Assert.AreEqual(100, reader.GetInt32(2));
                Assert.IsTrue(reader.GetBoolean(3));
                Assert.IsFalse(reader.GetBoolean(4));
                Assert.AreEqual(20, reader.GetInt32(5));
                Assert.AreEqual(120L, reader.GetInt64(6));
                Assert.AreEqual(220, reader.GetInt32(7));
                Assert.AreEqual(320L, reader.GetInt64(8));
                Assert.AreEqual(42, reader.GetInt32(9));
                Assert.AreEqual(42, reader.GetInt32(10));
                Assert.IsTrue(reader.GetBoolean(11));
                Assert.IsTrue(reader.GetBoolean(12));
                Assert.IsTrue(reader.GetBoolean(13));
                Assert.AreEqual(3L, reader.GetInt64(14));
                Assert.AreSequenceEqual([20, 42], reader.GetFieldValue<int[]>(15));
                Assert.IsTrue(reader.GetBoolean(16));
                Assert.AreEqual(20, reader.GetInt32(17));
                Assert.IsTrue(reader.GetBoolean(18));
                Assert.IsFalse(await reader.ReadAsync(token));
            }

            PostgresException overflow = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteSqlPackageAsync(connection,
                $$"""SELECT {{schema}}.checked_sum('{"Value":2147483647}','{"Value":1}')"""));
            Assert.AreEqual("38000", overflow.SqlState);
            Assert.AreEqual(backend, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
            Assert.AreEqual(20, await SqlPackageScalarAsync<int>(connection, $$"""SELECT {{schema}}.integer_value('{"Value":20}')"""));
            if (schema == "op_first")
            {
                await ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_tool_probe SET SCHEMA op_second");
                Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, "SELECT to_regtype('op_first.metric') IS NULL"));
            }
        }

        await ExecuteSqlPackageAsync(connection, "DROP EXTENSION ankus_tool_probe");
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, "SELECT to_regtype('op_second.metric') IS NULL AND to_regtype('op_second.ref_metric') IS NULL"));
        Assert.AreEqual(backend, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Supplies actual attributed special methods, including overloaded conversion result signatures.
    /// </summary>
    private const string CSharpOperatorPackageSource = """
        using Ankus;

        [PgType]
        public readonly record struct Metric(int Value)
        {
            [PgOperator("@+")]
            [PgFunction(Name = "sum", Volatility = PgVolatility.Immutable)]
            public static Metric operator +(Metric left, Metric right) => new(unchecked(left.Value + right.Value));

            [PgOperator("@^")]
            [PgFunction(Name = "checked_sum", Volatility = PgVolatility.Immutable)]
            public static Metric operator checked +(Metric left, Metric right) => new(checked(left.Value + right.Value + 100));

            [PgOperator("@-")]
            [PgFunction(Name = "negate")]
            public static Metric operator -(Metric? value) => new(value is null ? 100 : -value.Value.Value);

            [PgFunction(Name = "is_positive")]
            public static bool operator true(Metric value) => value.Value > 0;

            [PgFunction(Name = "is_nonpositive")]
            public static bool operator false(Metric value) => value.Value <= 0;

            [PgCast]
            [PgFunction(Name = "integer_value")]
            public static explicit operator int(Metric value) => value.Value;

            [PgCast]
            [PgFunction(Name = "bigint_value")]
            public static explicit operator long(Metric value) => value.Value + 100L;

            [PgFunction(Name = "checked_integer")]
            public static explicit operator checked int(Metric value) => checked(value.Value + 200);

            [PgFunction(Name = "checked_bigint")]
            public static explicit operator checked long(Metric value) => checked(value.Value + 300L);

            [PgCast(PgCastContext.Implicit)]
            [PgFunction(Name = "from_integer")]
            public static implicit operator Metric(int value) => new(value);

            [PgFunction(Name = "sequence")]
            public static System.Collections.Generic.IEnumerable<int> operator ~(Metric value) => new[] { value.Value, 42 };
        }

        [PgType]
        public sealed record RefMetric(int Value)
        {
            [PgFunction(Name = "class_sum")]
            public static RefMetric operator +(RefMetric left, RefMetric right) => new(left.Value + right.Value);

            [PgFunction(Name = "reference_identity")]
            public static RefMetric? operator !(RefMetric? value) => value;
        }
        """;
}
