using System.Globalization;
using Ankus.Examples.Arrays;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Checks the upstream array example against independent native values, ownership and recovery contracts.
/// </summary>
/// <param name="context">The current test cancellation context.</param>
[TestClass]
public sealed class ArrayExampleTests(TestContext context)
{
    /// <summary>
    /// Distinguishes borrowed NULL contributions from mutable copied values without changing native input storage.
    /// </summary>
    /// <param name="input">The independent native input, including flattened shape and nonstandard bounds.</param>
    /// <param name="borrowed">The expected sum with minus one for each NULL.</param>
    /// <param name="copied">The expected copied sum after appending six.</param>
    /// <param name="stripped">The independently specified present cells in their original order.</param>
    [TestMethod]
    [DataRow("ARRAY[]::integer[]", 0L, 6L, "{}")]
    [DataRow("ARRAY[1,NULL,2,NULL,3]", 4L, 12L, "{1,2,3}")]
    [DataRow("ARRAY[NULL,NULL]::integer[]", -2L, 6L, "{}")]
    [DataRow("ARRAY[-2147483648,2147483647]", -1L, 5L, "{-2147483648,2147483647}")]
    [DataRow("ARRAY[2147483647,2147483647]", 4294967294L, 4294967300L, "{2147483647,2147483647}")]
    [DataRow("'[0:1][-1:0]={{1,NULL},{3,4}}'::integer[]", 7L, 14L, "{1,3,4}")]
    public Task ArraySamplePreservesNullableValuesAndInputOwnership(string input, long borrowed, long copied, string stripped)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ArraySamplePreservesNullableValuesAndInputOwnership), async (connection, transaction, token) =>
        {
            await InstallAsync(connection, transaction, token);
            await ExecuteAsync(connection, transaction, $"CREATE TEMP TABLE array_sample_input AS SELECT {input} AS payload", token);
            byte[] original = await ScalarAsync<byte[]>(connection, transaction, "SELECT array_send(payload) FROM array_sample_input", token);
            Assert.AreEqual(borrowed, await ScalarAsync<long>(connection, transaction, "SELECT arrays.sum_array(payload) FROM array_sample_input", token));
            Assert.AreEqual(copied, await ScalarAsync<long>(connection, transaction, "SELECT arrays.sum_vec(payload) FROM array_sample_input", token));
            Assert.AreEqual(stripped, await ScalarAsync<string>(connection, transaction, "SELECT arrays.strip_nulls(payload)::text FROM array_sample_input", token));
            Assert.AreSequenceEqual(original, await ScalarAsync<byte[]>(connection, transaction, "SELECT array_send(payload) FROM array_sample_input", token));
        }, context.CancellationToken);

    /// <summary>
    /// Preserves the exact upstream names, NULL positions, default dependency and installed custom-type identity.
    /// </summary>
    [TestMethod]
    public Task ArraySamplePreservesDefaultsNamesSetsAndCustomTypes()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ArraySamplePreservesDefaultsNamesSetsAndCustomTypes), async (connection, transaction, token) =>
        {
            await InstallAsync(connection, transaction, token);
            Assert.AreEqual(0L, await ScalarAsync<long>(connection, transaction, "SELECT arrays.sum_array()", token));
            Assert.AreEqual("{}", await ScalarAsync<string>(connection, transaction, "SELECT arrays.default_array()::text", token));
            Assert.AreEqual("{Brandy,Sally,NULL,Anchovy}", await ScalarAsync<string>(connection, transaction, "SELECT arrays.static_names()::text", token));
            Assert.AreSequenceEqual(["{Brandy,Sally,NULL,Anchovy}", "{Eric,David}", "{ZomboDB,PostgreSQL,Elasticsearch}"],
                await ScalarAsync<string[]>(connection, transaction, "SELECT array_agg(names::text ORDER BY ordinal) FROM arrays.static_names_set() WITH ORDINALITY AS input(names,ordinal)", token));
            Assert.AreEqual("{1,2,3,4,5}", await ScalarAsync<string>(connection, transaction, "SELECT arrays.i32_array_no_nulls()::text", token));
            Assert.AreEqual("{1,NULL,2,3,NULL,4,5}", await ScalarAsync<string>(connection, transaction, "SELECT arrays.i32_array_with_nulls()::text", token));
            Assert.IsTrue(await ScalarAsync<bool>(connection, transaction, """
                SELECT pg_typeof(value) = 'arrays.some_struct[]'::regtype
                    AND cardinality(value) = 1 AND value[1]::text = '{}'
                FROM (SELECT arrays.return_vec_of_customtype() AS value) AS input
                """, token));
            Assert.IsFalse(await ScalarAsync<bool>(connection, transaction, "SELECT extrelocatable FROM pg_extension WHERE extname='ankus_arrays'", token));
        }, context.CancellationToken);

    /// <summary>
    /// Exercises every side of the sixteen-lane boundary with independently known finite sums.
    /// </summary>
    /// <param name="length">The independently selected native array size.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(15)]
    [DataRow(16)]
    [DataRow(17)]
    [DataRow(31)]
    [DataRow(32)]
    [DataRow(33)]
    public Task VectorSamplePreservesChunkBoundaries(int length)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(VectorSamplePreservesChunkBoundaries), async (connection, transaction, token) =>
        {
            await InstallAsync(connection, transaction, token);
            string input = $"ARRAY(SELECT value::real FROM generate_series(1,{length}) AS value)";
            float expected = length * (length + 1f) / 2;
            string[] functions = ["sum_vector_array", "sum_vector_vec", "sum_vector_slice", "sum_vector_simd"];
            foreach (string function in functions)
            {
                string bits = length == 0 && function != "sum_vector_simd" ? "80000000"
                    : BitConverter.SingleToUInt32Bits(expected).ToString("x8", CultureInfo.InvariantCulture);
                Assert.AreEqual(bits, await ScalarAsync<string>(connection, transaction,
                    $"SELECT encode(float4send(vectors.{function}({input})), 'hex')", token), function);
            }
        }, context.CancellationToken);

    /// <summary>
    /// Pins Rust's negative-zero sum identity and distinct sequential versus sixteen-lane accumulation order.
    /// </summary>
    /// <param name="input">The independent signed-zero or non-associative input.</param>
    /// <param name="sequential">The expected native sequential IEEE-754 bits.</param>
    /// <param name="lanes">The independently derived sixteen-lane IEEE-754 bits.</param>
    [TestMethod]
    [DataRow("ARRAY[]::real[]", "80000000", "00000000")]
    [DataRow("ARRAY['-0']::real[]", "80000000", "00000000")]
    [DataRow("ARRAY['-0','-0']::real[]", "80000000", "00000000")]
    [DataRow("ARRAY['0']::real[]", "00000000", "00000000")]
    [DataRow("ARRAY['-0','0']::real[]", "00000000", "00000000")]
    [DataRow("ARRAY[100000000,-100000000]::real[] || array_fill(1::real,ARRAY[30])", "41f00000", "41e00000")]
    public Task VectorSamplePreservesFloatingPointIdentityAndOrder(string input, string sequential, string lanes)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(VectorSamplePreservesFloatingPointIdentityAndOrder), async (connection, transaction, token) =>
        {
            await InstallAsync(connection, transaction, token);
            string[] functions = ["sum_vector_array", "sum_vector_vec", "sum_vector_slice", "sum_vector_simd"];
            foreach (string function in functions)
            {
                Assert.AreEqual(function == "sum_vector_simd" ? lanes : sequential, await ScalarAsync<string>(connection, transaction,
                    $"SELECT encode(float4send(vectors.{function}({input})), 'hex')", token), function);
            }
        }, context.CancellationToken);

    /// <summary>
    /// Retains empty distance identities, shorter-input pairing and selected zero-based distances with unselected NULL cells.
    /// </summary>
    [TestMethod]
    public Task ArraySamplePreservesDistanceSelectionAndEmptyIdentity()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ArraySamplePreservesDistanceSelectionAndEmptyIdentity), async (connection, transaction, token) =>
        {
            await InstallAsync(connection, transaction, token);
            var notices = new List<PostgresNotice>();
            connection.Notice += (_, args) => notices.Add(args.Notice);
            Assert.AreEqual("80000000", await ScalarAsync<string>(connection, transaction,
                "SELECT encode(float4send(arrays.sq_euclid(ARRAY[]::real[],ARRAY[42]::real[])),'hex')", token));
            Assert.AreEqual(25f, await ScalarAsync<float>(connection, transaction,
                "SELECT arrays.sq_euclid(ARRAY[2,5,99]::real[],ARRAY[5,9]::real[])", token));
            Assert.AreEqual("8000000000000000", await ScalarAsync<string>(connection, transaction,
                "SELECT encode(float8send(arrays.approx_distance(ARRAY[]::bigint[],ARRAY[NULL]::double precision[])),'hex')", token));
            Assert.AreEqual(7.25, await ScalarAsync<double>(connection, transaction,
                "SELECT arrays.approx_distance(ARRAY[0,2,0]::bigint[],'[-2:0]={1.5,NULL,4.25}'::double precision[])", token));
            PostgresNotice[] observations = [.. notices.Where(static notice => notice.InvariantSeverity == "INFO")];
            Assert.AreSequenceEqual(["cc=0, d=1.5", "cc=2, d=4.25", "cc=0, d=1.5"],
                observations.Select(static notice => notice.MessageText));
            Assert.AreEqual(1L, await ScalarAsync<long>(connection, transaction, """
                SELECT count(*) FROM pg_proc JOIN pg_namespace ON pg_namespace.oid=pronamespace
                WHERE nspname='arrays' AND proname='approx_distance'
                    AND provolatile='i' AND proparallel='s' AND proisstrict
                """, token));
        }, context.CancellationToken);

    /// <summary>
    /// Each vector representation accepts the upstream flattened shape while preserving signed zero and IEEE special values.
    /// </summary>
    /// <param name="input">The independently specified native real cells.</param>
    /// <param name="expected">The independently specified PostgreSQL floating result.</param>
    [TestMethod]
    [DataRow("'[-2:-1][4:5]={{1,2},{3,4}}'::real[]", "10")]
    [DataRow("ARRAY['Infinity',1]::real[]", "Infinity")]
    [DataRow("ARRAY['-Infinity',1]::real[]", "-Infinity")]
    [DataRow("ARRAY['Infinity','-Infinity']::real[]", "NaN")]
    [DataRow("ARRAY['NaN',1]::real[]", "NaN")]
    public Task VectorSamplePreservesShapesAndSpecialValues(string input, string expected)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(VectorSamplePreservesShapesAndSpecialValues), async (connection, transaction, token) =>
        {
            await InstallAsync(connection, transaction, token);
            string[] functions = ["sum_vector_array", "sum_vector_vec", "sum_vector_slice", "sum_vector_simd"];
            foreach (string function in functions)
            {
                Assert.AreEqual(expected, await ScalarAsync<string>(connection, transaction,
                    $"SELECT vectors.{function}({input})::text", token), function);
            }
        }, context.CancellationToken);

    /// <summary>
    /// No vector conversion loses SQL NULL or prevents correct work in the same backend after explicit rollback.
    /// </summary>
    /// <param name="input">The native cells with NULL in the first, interior, last or every position.</param>
    [TestMethod]
    [DataRow("ARRAY[NULL,1]::real[]")]
    [DataRow("ARRAY[1,NULL,2]::real[]")]
    [DataRow("ARRAY[1,NULL]::real[]")]
    [DataRow("ARRAY[NULL,NULL]::real[]")]
    public Task VectorSampleRejectsNullCellsAndRecovers(string input)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(VectorSampleRejectsNullCellsAndRecovers), async (connection, transaction, token) =>
        {
            await InstallAsync(connection, transaction, token);
            string[] functions = ["sum_vector_array", "sum_vector_vec", "sum_vector_slice", "sum_vector_simd"];
            foreach (string function in functions)
            {
                await transaction.SaveAsync("vector_null", token);
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connection, transaction,
                    $"SELECT vectors.{function}({input})", token));
                Assert.AreEqual("38000", error.SqlState, function);
                Assert.Contains("SQL NULL", error.MessageText);
                await transaction.RollbackAsync("vector_null", token);
                Assert.AreEqual(42f, await ScalarAsync<float>(connection, transaction,
                    $"SELECT vectors.{function}(ARRAY[42]::real[])", token));
                Assert.AreEqual(connection.ProcessID, await ScalarAsync<int>(connection, transaction, "SELECT pg_backend_pid()", token));
            }
        }, context.CancellationToken);

    /// <summary>
    /// The exact custom-type backend test is discovered from its generated catalog and runs through the ordinary author fixture.
    /// </summary>
    [TestMethod]
    public async Task ArraySampleGeneratedCustomTypeTestRunsInPostgres()
    {
        string project = Path.Join(IntegrationEnvironment.RepositoryRoot, "samples", "Ankus.Examples.Arrays", "Ankus.Examples.Arrays.csproj");
        PgTestCase test = Assert.ContainsSingle(ArrayFunctions.PostgresTests.Cases);
        Assert.EndsWith(".CustomTypeArrayRoundTrip()", test.Name);
        Assert.IsNull(test.ExpectedError);
        Assert.IsNull(test.IgnoreReason);
        await using PostgresExtensionTest fixture = await PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions
        {
            ProjectPath = project,
            Installation = await IntegrationEnvironment.GetInstallationAsync(context.CancellationToken),
            IncludeTests = true,
            DataDirectoryBase = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-pgdata"),
        }, context.CancellationToken);
        await fixture.RunTestAsync(test, context.CancellationToken);
        await using NpgsqlConnection connection = await fixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT pg_typeof(arrays.return_vec_of_customtype())::text", connection);
        Assert.AreEqual("arrays.some_struct[]", await command.ExecuteScalarAsync(context.CancellationToken));
    }

    /// <summary>
    /// Rejects NULLs even beyond a shorter zipped input and invalid selected positions without poisoning the backend after rollback.
    /// </summary>
    /// <param name="sql">The independently invalid distance operation.</param>
    [TestMethod]
    [DataRow("SELECT arrays.sq_euclid(ARRAY[1]::real[],ARRAY[2,NULL]::real[])")]
    [DataRow("SELECT arrays.sq_euclid(ARRAY[1,NULL]::real[],ARRAY[2]::real[])")]
    [DataRow("SELECT arrays.approx_distance(ARRAY[-1]::bigint[],ARRAY[42]::double precision[])")]
    [DataRow("SELECT arrays.approx_distance(ARRAY[1]::bigint[],ARRAY[42]::double precision[])")]
    [DataRow("SELECT arrays.approx_distance(ARRAY[9223372036854775807]::bigint[],ARRAY[42]::double precision[])")]
    [DataRow("SELECT arrays.approx_distance(ARRAY[NULL]::bigint[],ARRAY[42]::double precision[])")]
    [DataRow("SELECT arrays.approx_distance(ARRAY[0]::bigint[],ARRAY[NULL]::double precision[])")]
    public Task ArraySampleRejectsInvalidDistancesAndRecovers(string sql)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ArraySampleRejectsInvalidDistancesAndRecovers), async (connection, transaction, token) =>
        {
            await InstallAsync(connection, transaction, token);
            await transaction.SaveAsync("array_sample_failure", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connection, transaction, sql, token));
            Assert.AreEqual("38000", error.SqlState);
            await transaction.RollbackAsync("array_sample_failure", token);
            Assert.AreEqual(42, await ScalarAsync<int>(connection, transaction, "SELECT 42", token));
            Assert.AreEqual(connection.ProcessID, await ScalarAsync<int>(connection, transaction, "SELECT pg_backend_pid()", token));
            Assert.AreSequenceEqual([0, 0], await ScalarAsync<int[]>(connection, transaction, "SELECT set_values.set_interrupt_state()", token));
        }, context.CancellationToken);

    /// <summary>
    /// Checks seeded data and random output sizes without depending on a particular random stream.
    /// </summary>
    [TestMethod]
    public Task VectorSamplePreservesSeededShapeAndHalfOpenRange()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(VectorSamplePreservesSeededShapeAndHalfOpenRange), async (connection, transaction, token) =>
        {
            await InstallAsync(connection, transaction, token);
            Assert.AreEqual(1000L, await ScalarAsync<long>(connection, transaction, "SELECT count(*) FROM vectors.data", token));
            Assert.IsTrue(await ScalarAsync<bool>(connection, transaction, "SELECT bool_and(cardinality(v)=768 AND array_lower(v,1)=1) FROM vectors.data", token));
            Assert.AreEqual(0L, await ScalarAsync<long>(connection, transaction, "SELECT count(*) FROM vectors.data CROSS JOIN LATERAL unnest(v) AS input(value) WHERE value IS NULL OR NOT (value>=0 AND value<1)", token));
            int[] lengths = [-1, 0, 1, 17];
            foreach (int length in lengths)
            {
                float[] values = await ScalarAsync<float[]>(connection, transaction, $"SELECT vectors.random_vector({length})", token);
                Assert.HasCount(Math.Max(0, length), values);
                foreach (float value in values)
                {
                    Assert.IsGreaterThanOrEqualTo(0f, value);
                    Assert.IsLessThan(1f, value);
                }
            }
        }, context.CancellationToken);

    /// <summary>
    /// Installs the independently published sample with its fixed upstream schema.
    /// </summary>
    /// <param name="connection">The isolated backend connection.</param>
    /// <param name="transaction">The rollback-isolated sample test.</param>
    /// <param name="token">Cancels native work.</param>
    /// <returns>The installation completion.</returns>
    private static Task InstallAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        => ExecuteAsync(connection, transaction, "CREATE EXTENSION ankus_arrays", token);

    /// <summary>
    /// Executes an independently specified native fixture statement.
    /// </summary>
    /// <param name="connection">The isolated backend connection.</param>
    /// <param name="transaction">The rollback-isolated sample test.</param>
    /// <param name="sql">The fixture SQL.</param>
    /// <param name="token">Cancels native work.</param>
    /// <returns>The statement completion.</returns>
    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Reads a required detached scalar with an explicit type assertion.
    /// </summary>
    /// <typeparam name="T">The expected client value type.</typeparam>
    /// <param name="connection">The isolated backend connection.</param>
    /// <param name="transaction">The rollback-isolated sample test.</param>
    /// <param name="sql">The independent scalar query.</param>
    /// <param name="token">Cancels native work.</param>
    /// <returns>The required detached scalar.</returns>
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }
}
