using System.Buffers.Binary;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies exact packed storage in the published Native AOT extension and real PostgreSQL.
/// </summary>
/// <param name="context">The per-test cancellation context.</param>
[TestClass]
public sealed class NativeLayoutTypeTests(TestContext context)
{
    /// <summary>
    /// Preserves every field bit, SQL identity, zero and NULL across native and SPI ownership paths.
    /// </summary>
    /// <param name="mode">The direct or SPI path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task NativeLayoutValuesAndNullsCrossOwnershipPaths(int mode)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        Assert.AreEqual("AB:-2147483648:255:-32768:4660:32767:80000000:7FF8000000000042", await Scalar<string>(connection,
            $"SELECT native_layout.native_describe(native_layout.native_echo(native_layout.native_make(-2147483648),{mode}))"));
        Assert.AreEqual("AB:2147483647:255:-32768:4660:32767:80000000:7FF8000000000042", await Scalar<string>(connection,
            $"SELECT native_layout.native_describe(native_layout.native_echo(native_layout.native_make(2147483647),{mode}))"));
        Assert.AreEqual("00:0:0:0:0:0:00000000:0000000000000000", await Scalar<string>(connection,
            $"SELECT native_layout.native_describe(native_layout.native_echo(native_layout.native_zero(),{mode}))"));
        Assert.IsTrue(await Scalar<bool>(connection, $$"""
            SELECT native_layout.native_echo(NULL,{{mode}}) IS NULL
                AND pg_typeof(native_layout.native_echo(native_layout.native_make(7),{{mode}})) = 'native_layout.packet'::regtype
                AND encode(native_layout.packet_send(native_layout.native_echo(native_layout.native_zero(),{{mode}})),'hex') = repeat('00',24)
            """));
        Assert.AreEqual("0:0:0:0", await Scalar<string>(connection, "SELECT native_layout.native_counters()"));
    }

    /// <summary>
    /// Preserves shaped arrays, NULL cells, empty arrays, set ordering and typed tuple cells.
    /// </summary>
    /// <param name="mode">The direct or SPI path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(4)]
    [DataRow(7)]
    public async Task NativeLayoutArraysSetsAndTuplesPreserveValues(int mode)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        Assert.IsTrue(await Scalar<bool>(connection, $$$"""
            WITH output AS (SELECT native_layout.native_array('[2:3][-1:0]={{1,NULL},{0,9}}',{{{mode}}}) AS value)
            SELECT value::text = '[2:3][-1:0]={{1,NULL},{0,9}}' AND array_dims(value) = '[2:3][-1:0]'
                AND native_layout.native_describe(value[2][-1]) = 'AB:1:255:-32768:4660:32767:80000000:7FF8000000000042'
                AND value[2][0] IS NULL AND pg_typeof(value) = 'native_layout.packet[]'::regtype
                AND cardinality(native_layout.native_array('{}',{{{mode}}})) = 0
                AND native_layout.native_array(NULL,{{{mode}}}) IS NULL FROM output
            """));
        Assert.AreEqual("7,NULL,9", await Scalar<string>(connection, """
            SELECT string_agg(coalesce(value::text,'NULL'),',' ORDER BY ordinal)
            FROM native_layout.native_rows('7') WITH ORDINALITY row(value,ordinal)
            """));
        Assert.AreEqual("NULL,NULL,9", await Scalar<string>(connection, """
            SELECT string_agg(coalesce(value::text,'NULL'),',' ORDER BY ordinal)
            FROM native_layout.native_rows(NULL) WITH ORDINALITY row(value,ordinal)
            """));
        foreach (string row in new[]
        {
            "ROW('7'::native_layout.packet,'[3:4]={9,NULL}'::native_layout.packet[])",
            "ROW(NULL::native_layout.packet,'{}'::native_layout.packet[])",
            "ROW(NULL::native_layout.packet,NULL::native_layout.packet[])",
        })
        {
            Assert.IsTrue(await Scalar<bool>(connection,
                $"WITH input AS (SELECT {row} AS value) SELECT record_send(value)=record_send(native_layout.native_tuple(value,{mode})) FROM input"));
        }
    }

    /// <summary>
    /// Preserves copied raw values while rejecting wrong SQL identities and disposed owners.
    /// </summary>
    [TestMethod]
    public async Task NativeLayoutRawValuesPreserveTypeAndLifetime()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int process = connection.ProcessID;
        Assert.AreEqual("AB:42:255:-32768:4660:32767:80000000:7FF8000000000042", await Scalar<string>(connection,
            "SELECT native_layout.native_describe(native_layout.native_raw(native_layout.native_make(42)))"));
        Assert.AreEqual(42, await Scalar<int>(connection,
            "SELECT native_layout.native_raw_read(native_layout.native_raw(native_layout.native_make(42)))"));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT native_layout.native_raw(NULL) IS NULL"));
        Assert.AreEqual("AB:42:255:-32768:4660:32767:80000000:7FF8000000000042|2", await Scalar<string>(connection,
            "SELECT native_layout.native_raw_lifetime()"));
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<object>(connection,
            "SELECT native_layout.native_raw_read(native_layout.native_fault(42))"));
        Assert.AreEqual("38000", error.SqlState);
        Assert.AreEqual(7, await Scalar<int>(connection, "SELECT native_layout.native_raw_read(native_layout.native_make(7))"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// A naturally aligned layout stores zero padding whether a value is returned, built as a varlena or changed in place,
    /// and its text and binary forms round-trip.
    /// </summary>
    [TestMethod]
    public async Task AlignedNativeLayoutsStoreZeroPadding()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        byte[] expected = BitConverter.IsLittleEndian ? [5, 0, 0, 0, 7, 0, 0, 0] : [5, 0, 0, 0, 0, 0, 0, 7];
        Assert.AreSequenceEqual(expected, await Scalar<byte[]>(connection, "SELECT native_layout.aligned_send(native_layout.native_aligned_dirty(5, 7))"));
        Assert.AreSequenceEqual(expected, await Scalar<byte[]>(connection, "SELECT native_layout.aligned_send(native_layout.native_aligned_varlena(5, 7))"));
        Assert.AreSequenceEqual(expected, await Scalar<byte[]>(connection,
            "SELECT native_layout.aligned_send(native_layout.native_aligned_set_number(native_layout.native_aligned_varlena(5, 1), 7))"));
        Assert.AreEqual("5:7", await Scalar<string>(connection, "SELECT '5:7'::native_layout.aligned::text"));
        Assert.IsTrue(await Scalar<bool>(connection,
            "SELECT native_layout.aligned_send('5:7'::native_layout.aligned) = native_layout.aligned_send(native_layout.native_aligned_dirty(5, 7))"));
    }

    /// <summary>
    /// Constructs the custom text adapter once and keeps all binary operations independent of it.
    /// </summary>
    [TestMethod]
    public async Task NativeLayoutTextFactoryIsDeferredAndBinaryIndependent()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        Assert.AreEqual("0:0:0:0", await Scalar<string>(connection, "SELECT native_layout.native_counters()"));
        Assert.AreSequenceEqual(PacketBytes(), await Scalar<byte[]>(connection,
            "SELECT native_layout.packet_send(native_layout.native_echo(native_layout.native_make(16909060),7))"));
        Assert.AreEqual("0:0:0:0", await Scalar<string>(connection, "SELECT native_layout.native_counters()"));
        Assert.AreEqual("7", await Scalar<string>(connection, "SELECT native_layout.packet_in('7'::cstring)::text"));
        Assert.AreEqual("1:1:1:0", await Scalar<string>(connection, "SELECT native_layout.native_counters()"));
        Assert.AreEqual("9", await Scalar<string>(connection, "SELECT native_layout.native_echo(native_layout.packet_in('9'::cstring),7)::text"));
        Assert.AreEqual("1:2:2:0", await Scalar<string>(connection, "SELECT native_layout.native_counters()"));
    }

    /// <summary>
    /// Caches ordinary factory failures without disabling native reads or writes in the same backend.
    /// </summary>
    [TestMethod]
    public async Task NativeLayoutFactoryFailureLeavesBinaryUsable()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int process = connection.ProcessID;
        Assert.AreEqual(42, await Scalar<int>(connection, "SELECT native_layout.native_fault_number(native_layout.native_fault(42))"));
        byte[] expected = BitConverter.IsLittleEndian ? [42, 0, 0, 0] : [0, 0, 0, 42];
        Assert.AreSequenceEqual(expected, await Scalar<byte[]>(connection, "SELECT native_layout.fault_send(native_layout.native_fault(42))"));
        Assert.AreEqual("0:0:0:0", await Scalar<string>(connection, "SELECT native_layout.native_counters()"));
        foreach (string sql in new[] { "SELECT native_layout.native_fault(7)::text", "SELECT '7'::native_layout.fault" })
        {
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<object>(connection, sql));
            Assert.AreEqual("38000", error.SqlState);
            Assert.AreEqual("native text factory failed", error.MessageText);
            Assert.AreEqual("0:0:0:1", await Scalar<string>(connection, "SELECT native_layout.native_counters()"));
        }

        Assert.AreEqual(-7, await Scalar<int>(connection, "SELECT native_layout.native_fault_number(native_layout.native_fault(-7))"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Preserves custom and ordinary text failures after managed frames unwind.
    /// </summary>
    /// <param name="sql">The invalid conversion.</param>
    /// <param name="state">The expected SQLSTATE.</param>
    /// <param name="message">The exact diagnostic.</param>
    [TestMethod]
    [DataRow("SELECT '!pg'::native_layout.packet", "P7921", "native parser failed")]
    [DataRow("SELECT '!managed'::native_layout.packet", "38000", "native managed parser failed")]
    [DataRow("SELECT 'bad'::native_layout.packet", "22P02", "Expected a native packet integer.")]
    [DataRow("SELECT native_layout.native_make(-998)::text", "P7922", "native formatter failed")]
    [DataRow("SELECT native_layout.native_make(-999)::text", "38000", "A custom text codec returned null text for a present value.")]
    public async Task NativeLayoutTextErrorsPreserveBackend(string sql, string state, string message)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int process = connection.ProcessID;
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<object>(connection, sql));
        Assert.AreEqual(state, error.SqlState);
        Assert.AreEqual(message, error.MessageText);
        Assert.AreEqual("42", await Scalar<string>(connection, "SELECT native_layout.native_echo('42',1)::text"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Distinguishes configured input NULL errors from typed NULL that bypasses conversion.
    /// </summary>
    [TestMethod]
    public async Task NativeLayoutNullInputPolicyPreservesTypedNull()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int process = connection.ProcessID;
        Assert.IsTrue(await Scalar<bool>(connection, """
            SELECT native_layout.native_fault_echo(native_layout.native_fault_null()) IS NULL
                AND native_layout.packet_in(NULL) IS NULL
                AND native_layout.native_array('{NULL}',1)::text = '{NULL}'
                AND (SELECT input.proisstrict FROM pg_type t JOIN pg_proc input ON input.oid=t.typinput WHERE t.oid='native_layout.packet'::regtype)
                AND (SELECT NOT input.proisstrict AND output.proisstrict AND receive.proisstrict AND send.proisstrict
                    FROM pg_type t JOIN pg_proc input ON input.oid=t.typinput JOIN pg_proc output ON output.oid=t.typoutput
                        JOIN pg_proc receive ON receive.oid=t.typreceive JOIN pg_proc send ON send.oid=t.typsend WHERE t.oid='native_layout.fault'::regtype)
            """));
        foreach (string sql in new[]
        {
            "SELECT native_layout.fault_in(NULL)", "SELECT NULL::native_layout.fault",
            "SELECT native_layout.native_fault_echo(NULL)", "SELECT '{NULL}'::native_layout.fault[]",
        })
        {
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<object>(connection, sql));
            Assert.AreEqual("22004", error.SqlState);
            Assert.AreEqual("native fault needs input", error.MessageText);
        }

        await Scalar<object>(connection, "CREATE TEMP TABLE native_null(value native_layout.fault); SELECT 1");
        PostgresException copyError = await Assert.ThrowsExactlyAsync<PostgresException>(() => ImportNullText(connection));
        Assert.AreEqual("22004", copyError.SqlState);
        Assert.AreEqual("native fault needs input", copyError.MessageText);
        Assert.AreEqual(0L, await Scalar<long>(connection, "SELECT count(*) FROM native_null"));
        await Import(connection, Convert.FromHexString("5047434F50590AFF0D0A0000000000000000000001FFFFFFFFFFFF"), "native_null");
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT value IS NULL FROM native_null"));
        Assert.AreEqual("0:0:0:0", await Scalar<string>(connection, "SELECT native_layout.native_counters()"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Reads and writes a complete independently specified native binary COPY row.
    /// </summary>
    [TestMethod]
    public async Task NativeLayoutBinaryCopyUsesIndependentFixture()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await Scalar<object>(connection, "CREATE TEMP TABLE native_binary(value native_layout.packet); SELECT 1");
        byte[] input = CopyPayload(PacketBytes());
        await Import(connection, input);
        Assert.AreEqual(25, await Scalar<int>(connection, "SELECT pg_column_size(value) FROM native_binary"));
        Assert.AreEqual("AB:16909060:255:-32768:4660:32767:80000000:7FF8000000000042", await Scalar<string>(connection,
            "SELECT native_layout.native_describe(value) FROM native_binary"));
        await using var output = new MemoryStream();
        await using (Stream copy = await connection.BeginRawBinaryCopyAsync("COPY native_binary TO STDOUT (FORMAT BINARY)", context.CancellationToken))
        {
            await copy.CopyToAsync(output, context.CancellationToken);
        }

        Assert.AreSequenceEqual(input, output.ToArray());
        Assert.AreEqual("0:0:0:0", await Scalar<string>(connection, "SELECT native_layout.native_counters()"));
    }

    /// <summary>
    /// Rejects every nonexact binary length without inserting rows or replacing the backend.
    /// </summary>
    /// <param name="length">The invalid payload length.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(23)]
    [DataRow(25)]
    [DataRow(48)]
    public async Task NativeLayoutBinaryErrorsPreserveRowsAndBackend(int length)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int process = connection.ProcessID;
        await Scalar<object>(connection, "CREATE TEMP TABLE native_binary(value native_layout.packet); SELECT 1");
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Import(connection, CopyPayload(new byte[length])));
        Assert.AreEqual("22P03", error.SqlState);
        Assert.AreEqual("Invalid native-layout custom-type value.", error.MessageText);
        Assert.AreEqual(0L, await Scalar<long>(connection, "SELECT count(*) FROM native_binary"));
        await Import(connection, CopyPayload(PacketBytes()));
        Assert.AreEqual("AB:16909060:255:-32768:4660:32767:80000000:7FF8000000000042", await Scalar<string>(connection,
            "SELECT native_layout.native_describe(value) FROM native_binary"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Preserves every fixed-buffer byte through compressed and external TOAST followed by typed SPI.
    /// </summary>
    [TestMethod]
    public async Task NativeLayoutFixedBuffersSurviveToast()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await Scalar<object>(connection, """
            CREATE TEMP TABLE native_external(value native_layout.block);
            ALTER TABLE native_external ALTER COLUMN value SET STORAGE EXTERNAL;
            INSERT INTO native_external VALUES (native_layout.native_block(1));
            CREATE TEMP TABLE native_compressed(value native_layout.block);
            INSERT INTO native_compressed VALUES (native_layout.native_block(0));
            SELECT 1
            """);
        Assert.AreEqual(8192, await Scalar<int>(connection, "SELECT pg_column_size(value) FROM native_external"));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT pg_column_size(value)<1000 FROM native_compressed"));
        byte[] expected = new byte[8192];
        for (int i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)((i * 17 + (i >> 4)) & 255);
        }

        Assert.AreSequenceEqual(expected, await Scalar<byte[]>(connection,
            "SELECT native_layout.block_send(native_layout.native_block_echo(value,4)) FROM native_external"));
        Array.Fill(expected, (byte)7);
        Assert.AreSequenceEqual(expected, await Scalar<byte[]>(connection,
            "SELECT native_layout.block_send(native_layout.native_block_echo(value,1)) FROM native_compressed"));
    }

    /// <summary>
    /// Specifies the packed field bytes independently of managed layout APIs.
    /// </summary>
    private static byte[] PacketBytes() => Convert.FromHexString(BitConverter.IsLittleEndian
        ? "AB04030201FF00803412FF7F00000080420000000000F87F"
        : "AB01020304FF800012347FFF800000007FF8000000000042");

    /// <summary>
    /// Constructs a complete binary COPY row without relying on generated storage code.
    /// </summary>
    private static byte[] CopyPayload(byte[] payload)
    {
        byte[] copy = new byte[27 + payload.Length];
        new byte[] { 80, 71, 67, 79, 80, 89, 10, 255, 13, 10, 0 }.CopyTo(copy, 0);
        BinaryPrimitives.WriteInt16BigEndian(copy.AsSpan(19), 1);
        BinaryPrimitives.WriteInt32BigEndian(copy.AsSpan(21), payload.Length);
        payload.CopyTo(copy, 25);
        BinaryPrimitives.WriteInt16BigEndian(copy.AsSpan(25 + payload.Length), -1);
        return copy;
    }

    /// <summary>
    /// Completes binary COPY and observes native receive errors before returning.
    /// </summary>
    private async Task Import(NpgsqlConnection connection, byte[] data, string table = "native_binary")
    {
        await using Stream copy = await connection.BeginRawBinaryCopyAsync($"COPY {table} FROM STDIN (FORMAT BINARY)", context.CancellationToken);
        await copy.WriteAsync(data, context.CancellationToken);
    }

    /// <summary>
    /// Completes a text COPY containing one native NULL input field.
    /// </summary>
    private async Task ImportNullText(NpgsqlConnection connection)
    {
        await using TextWriter copy = await connection.BeginTextImportAsync("COPY native_null FROM STDIN", context.CancellationToken);
        await copy.WriteLineAsync("\\N");
    }

    /// <summary>
    /// Reads one exact typed scalar with fixture cancellation.
    /// </summary>
    private async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        object? result = await command.ExecuteScalarAsync(context.CancellationToken);
        return Assert.IsInstanceOfType<T>(result);
    }
}
