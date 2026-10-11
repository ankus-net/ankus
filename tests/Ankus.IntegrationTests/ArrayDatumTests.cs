using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Checks arrays against PostgreSQL's independent binary representation and subscripting rules.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed class ArrayDatumTests(TestContext context)
{
    /// <summary>
    /// Checks each scalar family, typed NULL, empty arrays, and dimensions through every ownership path.
    /// </summary>
    /// <param name="mode">The direct, SPI, plan, session, cursor, session-plan cursor, retained-plan, or edited-row path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public Task ArraysPreserveBinaryValuesAcrossOwners(int mode)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ArraysPreserveBinaryValuesAcrossOwners),
            async (connection, transaction, token) =>
            {
                (string name, string type, string values)[] cases =
                [
                    ("bool", "boolean", "true, false, NULL"),
                    ("char", "\"char\"", "'-128', '127', NULL"),
                    ("short", "smallint", "-32768, 32767, NULL"),
                    ("int", "integer", "-2147483648, 2147483647, NULL"),
                    ("long", "bigint", "'-9223372036854775808', '9223372036854775807', NULL"),
                    ("oid", "oid", "'0', '4294967295', NULL"),
                    ("xid", "xid", "'3', '4294967295', NULL"),
                    ("float", "real", "'-0', 'NaN', 'Infinity', '-Infinity', '1e-45', NULL"),
                    ("double", "double precision", "'-0', 'NaN', 'Infinity', '-Infinity', '5e-324', NULL"),
                    ("text", "text", "'héllo 😀', '', 'NULL', NULL, 'a,b{c}\\d'"),
                    ("bytes", "bytea", "decode('00ff1020','hex'), ''::bytea, NULL"),
                    ("uuid", "uuid", "'00112233-4455-6677-8899-aabbccddeeff', NULL"),
                    ("json", "json", "'{ \"n\": 1.2300 }', 'null', NULL"),
                    ("jsonb", "jsonb", "'{\"z\":1,\"a\":2}', 'null', NULL"),
                    ("numeric", "numeric", "'12345678901234567890.123456789012345678900', 'NaN', 'Infinity', '-Infinity', '0.000', NULL"),
                    ("date", "date", "'4714-11-24 BC', '5874897-12-31', 'infinity', '-infinity', NULL"),
                    ("time", "time", "'00:00:00.000001', '24:00:00', NULL"),
                    ("timetz", "timetz", "'24:00:00+15:59:59', '01:02:03.000001-00:00:01', NULL"),
                    ("timestamp", "timestamp", "'4714-11-24 BC', '294276-12-31 23:59:59.999999', 'infinity', NULL"),
                    ("timestamptz", "timestamptz", "'4714-11-24+00 BC', '294276-12-31 23:59:59.999999+00', '-infinity', NULL"),
                    ("interval", "interval", "'1 mon -1 day -1 microsecond', 'infinity', '-infinity', NULL"),
                ];
                foreach ((string name, string type, string values) in cases)
                {
                    int major = PostgresFixture.Cluster.Installation.Version.Major;
                    bool rejectsInfinity = (name == "numeric" && major < 14) || (name == "interval" && major < 17);
                    string inputs = rejectsInfinity
                        ? values.Replace(name == "numeric" ? "'Infinity', '-Infinity', " : "'infinity', '-infinity', ", string.Empty, StringComparison.Ordinal)
                        : values;
                    await using var command = new NpgsqlCommand($"""
                        WITH inputs(value) AS (VALUES
                            (ARRAY[{inputs}]::{type}[]),
                            (ARRAY[ARRAY[{inputs}]::{type}[],ARRAY[{inputs}]::{type}[]]),
                            (array_fill(NULL::{type}, ARRAY[2,3], ARRAY[-2,4])),
                            (ARRAY[]::{type}[]), (NULL::{type}[]))
                        SELECT array_send(value), array_send(datatype.array_{name}(value, $1)) FROM inputs
                        """, connection, transaction);
                    command.Parameters.AddWithValue(mode);
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                    int rows = 0;
                    while (await reader.ReadAsync(token))
                    {
                        if (reader.IsDBNull(0))
                        {
                            Assert.IsTrue(reader.IsDBNull(1), name);
                        }
                        else
                        {
                            Assert.AreSequenceEqual(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), $"{name}, path {mode}, row {rows}");
                        }

                        rows++;
                    }

                    Assert.AreEqual(5, rows, name);
                    await reader.DisposeAsync();
                    if (rejectsInfinity)
                    {
                        string[] unsupported = name == "numeric" ? ["Infinity", "-Infinity"] : ["infinity", "-infinity"];
                        foreach (string input in unsupported)
                        {
                            await AssertRejectedElementAsync(command, transaction, name, type, input, mode, token);
                        }
                    }
                }
            }, context.CancellationToken);

    /// <summary>
    /// A million-element array whose checked sum overflows fails with the managed overflow and the backend continues,
    /// as pgrx's <c>sum_array</c> overflow tests require of copied and borrowed arrays.
    /// </summary>
    /// <param name="function">The copied or borrowed summing function.</param>
    [TestMethod]
    [DataRow("sum_array_checked")]
    [DataRow("sum_array_view_checked")]
    public async Task MillionElementOverflowFailsAndRecovers(string function)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await using var command = new NpgsqlCommand(
            $"SELECT datatype.{function}(array_agg(s)) FROM generate_series(1, 1000000) s", connection);
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(PostgresErrorCodes.ExternalRoutineException, error.SqlState);
        Assert.AreEqual("Arithmetic operation resulted in an overflow.", error.MessageText);
        command.CommandText = $"SELECT datatype.{function}(array_agg(s)) FROM generate_series(1, 65535) s";
        Assert.AreEqual(2147450880, await command.ExecuteScalarAsync(token));
        Assert.AreEqual(backend, connection.ProcessID);
    }

    /// <summary>
    /// Checks vector mappings and exact ordinary .NET element adapters inside Native AOT.
    /// </summary>
    /// <param name="mode">The ownership path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public Task VectorsAndDotnetElementsUseExactConversions(int mode)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(VectorsAndDotnetElementsUseExactConversions),
            async (connection, transaction, token) =>
            {
                (string name, string type, string values)[] cases =
                [
                    ("vector", "int", "1,NULL,-3"),
                    ("byte_vectors", "bytea", "decode('000102ff','hex'),NULL,''::bytea"),
                    ("decimal", "numeric", "'79228162514264337593543950335',NULL,'0.0000000000000000000000000001'"),
                    ("date_only", "date", "'0001-01-01',NULL,'9999-12-31'"),
                    ("time_only", "time", "'00:00:00',NULL,'23:59:59.999999'"),
                    ("date_time", "timestamp", "'1999-12-31 23:59:59.999999',NULL,'0001-01-01'"),
                    ("date_time_offset", "timestamptz", "'2024-11-03 01:30:00-04',NULL,'2024-11-03 01:30:00-05'"),
                    ("time_span", "interval", "'-123 microseconds',NULL,'49 hours'"),
                ];
                foreach ((string name, string type, string values) in cases)
                {
                    await using var command = new NpgsqlCommand($"""
                        WITH inputs(value) AS (VALUES (ARRAY[{values}]::{type}[]), (ARRAY[]::{type}[]), (NULL::{type}[]))
                        SELECT array_send(value), array_send(datatype.array_{name}(value, $1)) FROM inputs
                        """, connection, transaction);
                    command.Parameters.AddWithValue(mode);
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                    int rows = 0;
                    while (await reader.ReadAsync(token))
                    {
                        if (reader.IsDBNull(0))
                        {
                            Assert.IsTrue(reader.IsDBNull(1), name);
                        }
                        else
                        {
                            Assert.AreSequenceEqual(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), name);
                        }

                        rows++;
                    }

                    Assert.AreEqual(3, rows, name);
                }
            }, context.CancellationToken);

    /// <summary>
    /// Verifies managed construction and native input independently, including six dimensions and extreme lower bounds.
    /// </summary>
    [TestMethod]
    public Task ShapeAndIndexingMatchPostgresSubscripts()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ShapeAndIndexingMatchPostgresSubscripts),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT datatype.array_construct()::text,
                           datatype.array_inspect('[-2:-1][4:6]={{11,NULL,-7},{0,2147483647,-2147483648}}'::int[]),
                           datatype.array_int('[1:1][2:2][3:3][4:4][5:5][6:7]={{{{{{8,9}}}}}}'::int[], 1)::text,
                           datatype.array_int('[-2147483648:-2147483648]={7}'::int[], 1)::text,
                           datatype.array_int('[2147483646:2147483646]={8}'::int[], 1)::text
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("[-2:-1][4:6]={{11,NULL,-7},{0,2147483647,-2147483648}}", reader.GetString(0));
                Assert.AreEqual("2:6:2,3:-2,4:-7:-2147483648", reader.GetString(1));
                Assert.AreEqual("[1:1][2:2][3:3][4:4][5:5][6:7]={{{{{{8,9}}}}}}", reader.GetString(2));
                Assert.AreEqual("[-2147483648:-2147483648]={7}", reader.GetString(3));
                Assert.AreEqual("[2147483646:2147483646]={8}", reader.GetString(4));
            }, context.CancellationToken);

    /// <summary>
    /// Checks PostgreSQL variadic dispatch, explicit empty arrays, NULL semantics, and strict catalog declarations.
    /// </summary>
    [TestMethod]
    public Task ParamsArraysDeclareSqlVariadicFunctions()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ParamsArraysDeclareSqlVariadicFunctions),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT datatype.array_variadic(8, 2, NULL, -5)::text,
                           datatype.array_variadic(8, VARIADIC ARRAY[]::int[])::text,
                           datatype.array_variadic(8, VARIADIC NULL::int[]) IS NULL,
                           datatype.array_variadic_nullable('a', NULL, 'b'),
                           datatype.array_variadic_nullable(VARIADIC NULL::text[]),
                           datatype.array_variadic_nullable(VARIADIC ARRAY[]::text[]),
                           (SELECT provariadic FROM pg_proc WHERE oid = 'datatype.array_variadic(int,int[])'::regprocedure),
                           (SELECT proisstrict FROM pg_proc WHERE oid = 'datatype.array_variadic(int,int[])'::regprocedure),
                           (SELECT proisstrict FROM pg_proc WHERE oid = 'datatype.array_variadic_nullable(text[])'::regprocedure)
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("{8,3,1,-3}", reader.GetString(0));
                Assert.AreEqual("{8,0,0,0}", reader.GetString(1));
                Assert.IsTrue(reader.GetBoolean(2));
                Assert.AreEqual("a|null element|b", reader.GetString(3));
                Assert.AreEqual("null array", reader.GetString(4));
                Assert.AreEqual("", reader.GetString(5));
                Assert.AreEqual(23U, reader.GetFieldValue<uint>(6));
                Assert.IsTrue(reader.GetBoolean(7));
                Assert.IsFalse(reader.GetBoolean(8));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies binary array output against known bytes without passing through the array reader.
    /// </summary>
    [TestMethod]
    public Task BinaryArrayOutputPreservesEmbeddedZeroBytes()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(BinaryArrayOutputPreservesEmbeddedZeroBytes),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT encode(value[1], 'hex'), encode(value[2], 'hex'), octet_length(value[3]),
                           value[4] IS NULL, cardinality(value) FROM (SELECT datatype.array_binary_construct() AS value) x
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("000102ff", reader.GetString(0));
                Assert.AreEqual("030004", reader.GetString(1));
                Assert.AreEqual(0, reader.GetInt32(2));
                Assert.IsTrue(reader.GetBoolean(3));
                Assert.AreEqual(4, reader.GetInt32(4));
            }, context.CancellationToken);

    /// <summary>
    /// Rejects implicit shape loss, NULL narrowing, and scalar element precision loss while the backend remains usable.
    /// </summary>
    /// <param name="sql">The invalid managed conversion.</param>
    [TestMethod]
    [DataRow("SELECT datatype.array_vector(ARRAY[[1,2],[3,4]], 0)")]
    [DataRow("SELECT datatype.array_vector('[0:1]={1,2}'::int[], 0)")]
    [DataRow("SELECT datatype.array_required(ARRAY[1,NULL,3])")]
    [DataRow("SELECT datatype.array_decimal(ARRAY[1e-29]::numeric[], 0)")]
    [DataRow("SELECT datatype.array_date_only(ARRAY['infinity']::date[], 0)")]
    [DataRow("SELECT datatype.array_time_only(ARRAY['24:00']::time[], 0)")]
    [DataRow("SELECT datatype.array_time_span(ARRAY['1 mon']::interval[], 0)")]
    public Task LossyArrayConversionsRaiseManagedErrors(string sql)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(LossyArrayConversionsRaiseManagedErrors),
            async (connection, transaction, token) =>
            {
                await transaction.SaveAsync("array_failure", token);
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
                Assert.AreEqual("38000", error.SqlState);
                await transaction.RollbackAsync("array_failure", token);
                command.CommandText = "SELECT datatype.array_required(ARRAY[1,2,3])::text";
                Assert.AreEqual("{1,2,3}", await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Reads domains and text aliases without borrowing native memory or discarding array shape.
    /// </summary>
    [TestMethod]
    public Task DomainAndTextAliasArraysRemainOwned()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(DomainAndTextAliasArraysRemainOwned),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    CREATE DOMAIN array_text_element AS text CHECK (VALUE <> 'forbidden');
                    CREATE DOMAIN array_text_domain AS text[];
                    SELECT datatype.array_text_query('SELECT ''[0:2]={a,NULL,c}''::array_text_element[]')::text,
                           datatype.array_text_query('SELECT ''[0:2]={a,NULL,c}''::array_text_domain')::text,
                           datatype.array_text_query('SELECT ARRAY[''hi'',NULL]::varchar[]')::text,
                           datatype.array_text_query('SELECT ARRAY[''hi'',NULL]::char(4)[]')::text
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("[0:2]={a,NULL,c}", reader.GetString(0));
                Assert.AreEqual("[0:2]={a,NULL,c}", reader.GetString(1));
                Assert.AreEqual("{hi,NULL}", reader.GetString(2));
                Assert.AreEqual("{\"hi  \",NULL}", reader.GetString(3));
            }, context.CancellationToken);

    /// <summary>
    /// A text array result keeps NULLs past its first bitmap byte, empty and multibyte elements, its element type and
    /// its element hashes, and an empty result is PostgreSQL's empty array.
    /// </summary>
    [TestMethod]
    public Task TextArrayResultsKeepEveryElement()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TextArrayResultsKeepEveryElement),
            async (connection, transaction, token) =>
            {
                const string Values = "ARRAY['', 'a', NULL, 'ｱ😀', 'longer text', NULL, NULL, NULL, NULL, 'tail']::text[]";
                Assert.AreEqual("{\"\",a,NULL,ｱ😀,\"longer text\",NULL,NULL,NULL,NULL,tail};t;t;[1:10];text", await Scalar<string>(connection, transaction,
                    $"""
                    SELECT concat_ws(';', r::text, r = {Values}, hash_array(r) = hash_array({Values}), array_dims(r), pg_typeof(r[1]))
                    FROM (SELECT datatype.array_text_echo({Values}) AS r) s
                    """, token));
                Assert.AreEqual("{};t;0", await Scalar<string>(connection, transaction, """
                    SELECT concat_ws(';', r::text, r = '{}'::text[], cardinality(r))
                    FROM (SELECT datatype.array_text_echo(ARRAY[]::text[]) AS r) s
                    """, token));
            }, context.CancellationToken);

    /// <summary>
    /// Reads compressed and external arrays and verifies binary payloads survive detoasting and later SPI operations.
    /// </summary>
    [TestMethod]
    public Task ToastedArrayPayloadsSurviveNativeCleanup()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ToastedArrayPayloadsSurviveNativeCleanup),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    CREATE TEMP TABLE array_toast(value text[]);
                    INSERT INTO array_toast SELECT array_agg(repeat(i::text, 10000)) FROM generate_series(1,30) i;
                    SELECT pg_column_size(value) < octet_length(array_send(value)),
                           md5(array_send(value)), md5(array_send(datatype.array_text(value, 4))) FROM array_toast;
                    """, connection, transaction);
                await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
                {
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.IsTrue(reader.GetBoolean(0));
                    Assert.AreEqual(reader.GetString(1), reader.GetString(2));
                }

                command.CommandText = """
                    ALTER TABLE array_toast ALTER COLUMN value SET STORAGE EXTERNAL;
                    TRUNCATE array_toast;
                    INSERT INTO array_toast SELECT array_agg(repeat(md5(i::text), 1000)) FROM generate_series(1,30) i;
                    SELECT md5(array_send(value)), md5(array_send(datatype.array_text(value, 2))) FROM array_toast
                    """;
                await using NpgsqlDataReader external = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await external.ReadAsync(token));
                Assert.AreEqual(external.GetString(0), external.GetString(1));
            }, context.CancellationToken);

    /// <summary>
    /// Repeated failures unwind managed frames, preserve earlier writes and plans, and release native operation contexts.
    /// </summary>
    [TestMethod]
    public Task ArrayFailuresPreserveWritesPlansAndCleanup()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ArrayFailuresPreserveWritesPlansAndCleanup),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT datatype.array_recover()", connection, transaction);
                Assert.AreEqual("60:30:1:4:0", await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Applies database encoding per element and recovers after a failure partway through native output construction.
    /// </summary>
    [TestMethod]
    public async Task Latin1ArraysConvertElementsAndRecoverFromOutputFailure()
    {
        CancellationToken token = context.CancellationToken;
        string database = "array_latin1_" + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection administrator = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await using (var create = new NpgsqlCommand(
            $"CREATE DATABASE {database} TEMPLATE template0 ENCODING 'LATIN1' LC_COLLATE 'C' LC_CTYPE 'C'", administrator))
        {
            await create.ExecuteNonQueryAsync(token);
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Database = database };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token);
            int backend = connection.ProcessID;
            await using var command = new NpgsqlCommand("CREATE EXTENSION ankus_test", connection);
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = """
                SELECT array_text(ARRAY['café', NULL, 'élève'], 4)::text,
                       array_json(ARRAY['"café"', NULL, 'null']::json[], 2)::text,
                       array_jsonb(ARRAY['"élève"', NULL, 'null']::jsonb[], 3)::text
                """;
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("{café,NULL,élève}", reader.GetString(0));
                Assert.AreEqual("{\"\\\"café\\\"\",NULL,\"null\"}", reader.GetString(1));
                Assert.AreEqual("{\"\\\"élève\\\"\",NULL,\"null\"}", reader.GetString(2));
            }

            command.CommandText = "SELECT array_untranslatable()";
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual(PostgresErrorCodes.UntranslatableCharacter, error.SqlState);
            command.CommandText = "SELECT array_text(ARRAY['récupéré'], 1)::text";
            Assert.AreEqual("{récupéré}", await command.ExecuteScalarAsync(token));
            Assert.AreEqual(backend, connection.ProcessID);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", administrator);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Checks unavailable element admission and exact finite array recovery through the same owner and backend.
    /// </summary>
    private static async Task AssertRejectedElementAsync(NpgsqlCommand command, NpgsqlTransaction transaction,
        string name, string type, string input, int mode, CancellationToken token)
    {
        NpgsqlConnection? connection = command.Connection;
        Assert.IsNotNull(connection);
        int backend = connection.ProcessID;
        command.Parameters.Clear();
        command.Parameters.AddWithValue(input);
        command.Parameters.AddWithValue(mode);
        await transaction.SaveAsync("array_native_error", token);
        command.CommandText = $"SELECT ARRAY[$1::{type},NULL]";
        PostgresException native = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(type == "numeric" ? "22P02" : "22007", native.SqlState);
        Assert.AreEqual($"invalid input syntax for type {type}: \"{input}\"", native.MessageText);
        await transaction.RollbackAsync("array_native_error", token);
        await transaction.ReleaseAsync("array_native_error", token);

        await transaction.SaveAsync("array_managed_error", token);
        command.CommandText = $"SELECT datatype.array_{name}(ARRAY[$1::{type},NULL],$2)";
        PostgresException managed = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(native.SqlState, managed.SqlState);
        Assert.AreEqual(native.MessageText, managed.MessageText);
        Assert.AreEqual(native.Detail, managed.Detail);
        Assert.AreEqual(native.Hint, managed.Hint);
        await transaction.RollbackAsync("array_managed_error", token);
        await transaction.ReleaseAsync("array_managed_error", token);

        command.Parameters.Clear();
        command.Parameters.AddWithValue(mode);
        command.CommandText = $"""
            SELECT array_send(datatype.array_{name}(ARRAY[NULL,'1']::{type}[],$1)) =
                array_send(ARRAY[NULL,'1']::{type}[]), pg_backend_pid()
            """;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
        Assert.IsTrue(await reader.ReadAsync(token));
        Assert.IsTrue(reader.GetBoolean(0));
        Assert.AreEqual(backend, reader.GetInt32(1));
        Assert.IsFalse(await reader.ReadAsync(token));
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }
}
