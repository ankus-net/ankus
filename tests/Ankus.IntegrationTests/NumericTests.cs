using Npgsql;
using NpgsqlTypes;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies exact numeric storage, server arithmetic, decimal conversion, and guarded error recovery.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed class NumericTests(TestContext context)
{
    /// <summary>
    /// Verifies exact numeric payload and scale through every implemented ownership path.
    /// </summary>
    /// <param name="text">The numeric input or SQL NULL.</param>
    [TestMethod]
    [DataRow("0.0000")]
    [DataRow("-1234567890123456789012345678901234567890.123456789012345678901234567890")]
    [DataRow("0.00000000000000000000000000001")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("-Infinity")]
    [DataRow(new object?[] { null })]
    public Task NumericStorageSurvivesEveryPath(string? text)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(NumericStorageSurvivesEveryPath),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT mode, numeric_send($1::numeric), numeric_send(datatype.exchange_numeric($1::numeric, mode))
                    FROM generate_series(0, 7) AS mode
                    """, connection, transaction);
                command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = text });
                if (PostgresFixture.Cluster.Installation.Version.Major < 14 && text is "Infinity" or "-Infinity")
                {
                    for (int mode = 0; mode < 8; mode++)
                    {
                        command.CommandText = "SELECT datatype.exchange_numeric($1::numeric, $2)";
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue(text);
                        command.Parameters.AddWithValue(mode);
                        await AssertMatchingFailureAsync(command, transaction, "SELECT $1::numeric", "22P02", mode, token);
                    }

                    return;
                }

                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                int count = 0;
                while (await reader.ReadAsync(token))
                {
                    if (text is null)
                    {
                        Assert.IsTrue(reader.IsDBNull(2));
                    }
                    else
                    {
                        Assert.AreSequenceEqual(reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2), $"Path {reader.GetInt32(0)}");
                    }

                    count++;
                }

                Assert.AreEqual(8, count);
            }, context.CancellationToken);

    /// <summary>
    /// Verifies decimal values survive all SPI paths while retaining representable scale.
    /// </summary>
    /// <param name="text">The exact decimal value.</param>
    [TestMethod]
    [DataRow("79228162514264337593543950335")]
    [DataRow("-79228162514264337593543950335")]
    [DataRow("123.4500")]
    [DataRow("0.0000000000000000000000000001")]
    [DataRow(new object?[] { null })]
    public Task DecimalAdaptersRemainExactAcrossSpi(string? text)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(DecimalAdaptersRemainExactAcrossSpi),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT bool_and(numeric_send($1::numeric) IS NOT DISTINCT FROM
                        numeric_send(datatype.exchange_decimal($1::numeric, mode)))
                    FROM generate_series(0, 7) AS mode
                    """, connection, transaction);
                command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = text });
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
                command.Parameters.Clear();
                command.CommandText = "SELECT datatype.decimal_from_managed()::text";
                Assert.AreEqual("12345678901234567890.123456789", await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Checks native parsing at full numeric limits and exponent normalization without a decimal intermediary.
    /// </summary>
    [TestMethod]
    public Task FullRangeParsingAndScaleStayExact()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(FullRangeParsingAndScaleStayExact),
            async (connection, transaction, token) =>
            {
                string largest = new('9', 131072);
                string smallest = "0." + new string('0', 16382) + "1";
                await using var command = new NpgsqlCommand("""
                    SELECT datatype.numeric_from_text($1)::text, datatype.numeric_from_text($2)::text,
                        datatype.numeric_from_text(' +001.2300e2 ')::text, datatype.numeric_from_text('-0.000')::text
                    """, connection, transaction);
                command.Parameters.AddWithValue(largest);
                command.Parameters.AddWithValue(smallest);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(largest, reader.GetString(0));
                Assert.AreEqual(smallest, reader.GetString(1));
                Assert.AreEqual("123.00", reader.GetString(2));
                Assert.AreEqual("0.000", reader.GetString(3));
            }, context.CancellationToken);

    /// <summary>
    /// Checks native numeric operations against independently written SQL, including result scale.
    /// </summary>
    /// <param name="operation">The managed operation.</param>
    /// <param name="left">The primary input.</param>
    /// <param name="right">The secondary input.</param>
    /// <param name="precision">The precision or rounding scale.</param>
    /// <param name="scale">The declared scale.</param>
    /// <param name="expectedSql">The native expression.</param>
    [TestMethod]
    [DataRow("add", "123456789012345678901234567890.1234", "0.0006", 0, 0, "123456789012345678901234567890.1234 + 0.0006")]
    [DataRow("subtract", "1.2300", "2.34", 0, 0, "1.2300 - 2.34")]
    [DataRow("multiply", "1.2300", "2.340", 0, 0, "1.2300 * 2.340")]
    [DataRow("divide", "1", "3", 0, 0, "1::numeric / 3")]
    [DataRow("remainder", "-10.5", "3", 0, 0, "mod(-10.5::numeric, 3)")]
    [DataRow("negate", "1.2300", "0", 0, 0, "-1.2300::numeric")]
    [DataRow("abs", "-1.2300", "0", 0, 0, "abs(-1.2300::numeric)")]
    [DataRow("round", "2.5", "0", 0, 0, "round(2.5::numeric)")]
    [DataRow("round", "-2.5", "0", 0, 0, "round(-2.5::numeric)")]
    [DataRow("truncate", "-123.456", "0", -1, 0, "trunc(-123.456::numeric, -1)")]
    [DataRow("ceiling", "-1.5", "0", 0, 0, "ceil(-1.5::numeric)")]
    [DataRow("floor", "-1.5", "0", 0, 0, "floor(-1.5::numeric)")]
    [DataRow("sqrt", "2", "0", 0, 0, "sqrt(2::numeric)")]
    [DataRow("exp", "1", "0", 0, 0, "exp(1::numeric)")]
    [DataRow("log", "10", "0", 0, 0, "ln(10::numeric)")]
    [DataRow("logbase", "8", "2", 0, 0, "log(2::numeric, 8::numeric)")]
    [DataRow("power", "2", "100", 0, 0, "power(2::numeric, 100::numeric)")]
    [DataRow("gcd", "12.5", "7.5", 0, 0, "gcd(12.5::numeric, 7.5::numeric)")]
    [DataRow("lcm", "12.5", "7.5", 0, 0, "lcm(12.5::numeric, 7.5::numeric)")]
    [DataRow("rescale", "123.455", "0", 5, 2, "123.455::numeric(5,2)")]
    [DataRow("rescale", "12345", "0", 3, -2, "12345::numeric(3,-2)")]
    [DataRow("rescale", "0.001234", "0", 3, 5, "0.001234::numeric(3,5)")]
    [DataRow("add", "Infinity", "-Infinity", 0, 0, "'Infinity'::numeric + '-Infinity'::numeric")]
    [DataRow("multiply", "NaN", "0", 0, 0, "'NaN'::numeric * 0")]
    public Task ArithmeticMatchesPostgresNumericSemantics(string operation, string left, string right, int precision, int scale, string expectedSql)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ArithmeticMatchesPostgresNumericSemantics),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand($"""
                    SELECT numeric_send({expectedSql}), numeric_send(datatype.numeric_apply($1, $2::numeric, $3::numeric, $4, $5))
                    """, connection, transaction);
                command.Parameters.AddWithValue(operation);
                command.Parameters.AddWithValue(left);
                command.Parameters.AddWithValue(right);
                command.Parameters.AddWithValue(precision);
                command.Parameters.AddWithValue(scale);
                int major = PostgresFixture.Cluster.Installation.Version.Major;
                // PostgreSQL 15 allows a scale outside 0 through the precision, and PostgreSQL 14 added numeric infinity.
                bool rescaleRejected = major < 15 && operation == "rescale" && (scale < 0 || scale > precision);
                bool infinityRejected = major < 14 && left == "Infinity";
                if (rescaleRejected || infinityRejected)
                {
                    command.CommandText = "SELECT datatype.numeric_apply($1, $2::numeric, $3::numeric, $4, $5)";
                    await AssertMatchingFailureAsync(command, transaction, $"SELECT {expectedSql}", operation == "rescale" ? "22023" : "22P02", 3, token);
                    return;
                }

                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreSequenceEqual(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1));
            }, context.CancellationToken);

    /// <summary>
    /// Checks high-range temporal extraction retains exact fractional seconds.
    /// </summary>
    /// <param name="type">The temporal type.</param>
    /// <param name="input">The temporal input.</param>
    /// <param name="field">The field enum.</param>
    /// <param name="sqlField">The native field spelling.</param>
    [TestMethod]
    [DataRow("date", "5874897-12-31", "Epoch", "epoch")]
    [DataRow("time", "23:59:59.999999", "Second", "second")]
    [DataRow("timetz", "12:00+05:30:17", "TimeZone", "timezone")]
    [DataRow("timestamp", "294276-12-31 23:59:59.999999", "Epoch", "epoch")]
    [DataRow("timestamptz", "294276-12-31 23:59:59.999999+00", "Epoch", "epoch")]
    [DataRow("interval", "2147483647 months 2147483647 days 9223372036854.775807 seconds", "Epoch", "epoch")]
    [DataRow("timestamp", "infinity", "Month", "month")]
    [DataRow("interval", "-infinity", "Epoch", "epoch")]
    public Task TemporalExtractionPreservesNumericPrecision(string type, string input, string field, string sqlField)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TemporalExtractionPreservesNumericPrecision),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand($"""
                    SELECT (extract({sqlField} FROM $1::{type})::text)::numeric::text, datatype.numeric_extract($2, $1, $3)::text
                    """, connection, transaction);
                command.Parameters.AddWithValue(input);
                command.Parameters.AddWithValue(type);
                command.Parameters.AddWithValue(field);
                int major = PostgresFixture.Cluster.Installation.Version.Major;
                if ((major < 14 && type == "date") || (major < 17 && type == "interval" && input == "-infinity"))
                {
                    command.CommandText = "SELECT datatype.numeric_extract($2, $1, $3)";
                    await AssertMatchingFailureAsync(command, transaction, $"SELECT extract({sqlField} FROM $1::{type})", type == "date" ? "22008" : "22007", 3, token);
                    return;
                }

                bool rawInterval = type == "interval" && input.StartsWith("2147483647 months", StringComparison.Ordinal);
                if (rawInterval)
                {
                    command.CommandText = $"""
                        SELECT (extract({sqlField} FROM $1::interval)::text)::numeric::text,
                            datatype.numeric_interval_extract($1::interval, $2)::text,
                            encode(interval_send($1::interval), 'hex')
                        """;
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue(NpgsqlDbType.Interval, new NpgsqlInterval(int.MaxValue, int.MaxValue, long.MaxValue));
                    command.Parameters.AddWithValue(field);
                }

                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(reader.GetValue(0), reader.GetValue(1));
                if (rawInterval)
                {
                    Assert.AreEqual("7fffffffffffffff7fffffff7fffffff", reader.GetString(2));
                }

                Assert.IsFalse(await reader.ReadAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies native and managed failures preserve writes and run finally.
    /// </summary>
    /// <param name="operation">The failing operation.</param>
    /// <param name="left">The primary input.</param>
    /// <param name="right">The secondary input.</param>
    /// <param name="precision">The precision.</param>
    /// <param name="scale">The scale.</param>
    /// <param name="expected">The failure code.</param>
    [TestMethod]
    [DataRow("divide", "1", "0", 0, 0, "22012")]
    [DataRow("sqrt", "-1", "0", 0, 0, "2201F")]
    [DataRow("log", "-1", "0", 0, 0, "2201E")]
    [DataRow("add", "invalid", "1", 0, 0, "22P02")]
    [DataRow("add", "1e131072", "1", 0, 0, "22003")]
    [DataRow("add", "1e-16384", "1", 0, 0, "22003")]
    [DataRow("rescale", "999.995", "0", 5, 2, "22003")]
    [DataRow("rescale", "1", "0", 0, 0, "22023")]
    [DataRow("decimal", "0.00000000000000000000000000001", "0", 0, 0, "decimal overflow")]
    [DataRow("decimal", "NaN", "0", 0, 0, "decimal overflow")]
    public Task NumericErrorsPreserveSessionState(string operation, string left, string right, int precision, int scale, string expected)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(NumericErrorsPreserveSessionState),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT datatype.numeric_recovery($1, $2, $3, $4, $5)", connection, transaction);
                command.Parameters.AddWithValue(operation);
                command.Parameters.AddWithValue(left);
                command.Parameters.AddWithValue(right);
                command.Parameters.AddWithValue(precision);
                command.Parameters.AddWithValue(scale);
                Assert.AreEqual($"{expected}:1:2", await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Checks domains, nullable metadata, full native cleanup, and floating-point conversions.
    /// </summary>
    [TestMethod]
    public Task NumericDomainsConversionsAndCleanupRemainOwned()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(NumericDomainsConversionsAndCleanupRemainOwned),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    CREATE DOMAIN pg_temp.price AS numeric(5,2);
                    CREATE TEMP TABLE numeric_domain_values(value pg_temp.price);
                    INSERT INTO numeric_domain_values VALUES (12.30);
                    SELECT datatype.numeric_domain(), datatype.numeric_context_growth(),
                        datatype.numeric_try_parse('not a number'), datatype.numeric_try_parse('1e131072'),
                        datatype.numeric_try_parse(NULL), datatype.numeric_try_parse('NaN'),
                        numeric_send(0.1::float8::numeric) = numeric_send(datatype.numeric_from_double(0.1)),
                        float8send(1.234567890123456789::numeric::float8) = float8send(datatype.numeric_to_double(1.234567890123456789))
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("12.30:12.30", reader.GetString(0));
                Assert.AreEqual("0:42", reader.GetString(1));
                Assert.AreEqual("False:0", reader.GetString(2));
                Assert.AreEqual("False:0", reader.GetString(3));
                Assert.AreEqual("False:0", reader.GetString(4));
                Assert.AreEqual("True:NaN", reader.GetString(5));
                Assert.IsTrue(reader.GetBoolean(6));
                Assert.IsTrue(reader.GetBoolean(7));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies comparisons of values received from PostgreSQL match native ordering, including special values.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    [TestMethod]
    [DataRow("1.2300", "1.23")]
    [DataRow("-0.0000", "0")]
    [DataRow("NaN", "NaN")]
    [DataRow("-Infinity", "-Infinity")]
    [DataRow("Infinity", "Infinity")]
    [DataRow("1.230000000000000000000000000001", "1.23")]
    [DataRow("-10000000000000000000000000000", "-9999999999999999999999999999")]
    [DataRow("-Infinity", "0")]
    [DataRow("NaN", "Infinity")]
    [DataRow("0.001", "0.01")]
    public Task ManagedComparisonMatchesNativeValues(string left, string right)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ManagedComparisonMatchesNativeValues),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT numeric_cmp($1::numeric, $2::numeric), $1::numeric = $2::numeric,
                        datatype.numeric_comparison($1::numeric, $2::numeric)
                    """, connection, transaction);
                command.Parameters.AddWithValue(left);
                command.Parameters.AddWithValue(right);
                if (PostgresFixture.Cluster.Installation.Version.Major < 14 &&
                    (left is "Infinity" or "-Infinity" || right is "Infinity" or "-Infinity"))
                {
                    command.CommandText = "SELECT datatype.numeric_comparison($1::numeric, $2::numeric)";
                    await AssertMatchingFailureAsync(command, transaction, "SELECT numeric_cmp($1::numeric, $2::numeric)", "22P02", 3, token);
                    return;
                }

                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                bool equal = reader.GetBoolean(1);
                string actual = reader.GetString(2);
                Assert.StartsWith($"{Math.Sign(reader.GetInt32(0))}:{equal}:", actual);
                if (equal)
                {
                    Assert.AreEqual("0:True:True", actual);
                }
            }, context.CancellationToken);

    /// <summary>
    /// Checks packed, compressed and external numeric datums are detoasted and copied before native cleanup.
    /// </summary>
    /// <param name="storage">The requested storage mode.</param>
    [TestMethod]
    [DataRow("EXTENDED")]
    [DataRow("EXTERNAL")]
    public Task StoredNumericPayloadsSurviveDetoasting(string storage)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(StoredNumericPayloadsSurviveDetoasting),
            async (connection, transaction, token) =>
            {
                string text = string.Concat(Enumerable.Repeat("1234567890", 5000)) + ".1234500";
                string compression = PostgresFixture.Cluster.Installation.Version.Major >= 14
                    ? "ALTER TABLE numeric_toast ALTER COLUMN value SET COMPRESSION pglz;"
                    : "";
                await using var command = new NpgsqlCommand($"""
                    CREATE TEMP TABLE numeric_toast(value numeric);
                    ALTER TABLE numeric_toast ALTER COLUMN value SET STORAGE {storage};
                    {compression}
                    INSERT INTO numeric_toast VALUES (1.23);
                    SELECT pg_column_size(value), datatype.exchange_numeric(value, 4)::text FROM numeric_toast
                    """, connection, transaction);
                await using (NpgsqlDataReader packed = await command.ExecuteReaderAsync(token))
                {
                    Assert.IsTrue(await packed.ReadAsync(token));
                    Assert.AreEqual(7, packed.GetInt32(0));
                    Assert.AreEqual("1.23", packed.GetString(1));
                }

                command.CommandText = "UPDATE numeric_toast SET value = $1::numeric";
                command.Parameters.AddWithValue(text);
                await command.ExecuteNonQueryAsync(token);
                command.Parameters.Clear();
                string compressed = PostgresFixture.Cluster.Installation.Version.Major >= 14
                    ? "pg_column_compression(value) = 'pglz'"
                    : "pg_column_size(value) < pg_column_size(value + 0::numeric)";
                command.CommandText = storage == "EXTENDED"
                    ? $"SELECT {compressed} FROM numeric_toast"
                    : "SELECT pg_relation_size(reltoastrelid) > 0 FROM pg_class WHERE oid = 'pg_temp.numeric_toast'::regclass";
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
                command.CommandText = "SELECT datatype.exchange_numeric(value, 4)::text FROM numeric_toast";
                Assert.AreEqual(text, await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Checks generated decimal input adapters reject rounding, display-scale loss and nonfinite values while the backend survives.
    /// </summary>
    /// <param name="input">The unrepresentable value.</param>
    [TestMethod]
    [DataRow("8.0000000000000000000000000001")]
    [DataRow("0.00000000000000000000000000001")]
    [DataRow("79228162514264337593543950336")]
    [DataRow("1.00000000000000000000000000000")]
    [DataRow("7922816251426433759354395033.50")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    public Task GeneratedDecimalAdaptersRejectLossyInput(string input)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(GeneratedDecimalAdaptersRejectLossyInput),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT datatype.exchange_decimal($1::numeric, 0)", connection, transaction);
                command.Parameters.AddWithValue(input);
                if (PostgresFixture.Cluster.Installation.Version.Major < 14 && input == "Infinity")
                {
                    await AssertMatchingFailureAsync(command, transaction, "SELECT $1::numeric", "22P02", 3, token);
                    return;
                }

                await transaction.SaveAsync("decimal_input", token);
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
                Assert.AreEqual("38000", error.SqlState);
                await transaction.RollbackAsync("decimal_input", token);
                command.Parameters.Clear();
                command.CommandText = "SELECT datatype.exchange_decimal(1.2300, 3)::text";
                Assert.AreEqual("1.2300", await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Compares exact native rejection and verifies finite storage through the selected owner after rollback.
    /// </summary>
    private static async Task AssertMatchingFailureAsync(NpgsqlCommand command, NpgsqlTransaction transaction,
        string nativeSql, string expectedState, int mode, CancellationToken token)
    {
        NpgsqlConnection? connection = command.Connection;
        Assert.IsNotNull(connection);
        int backend = connection.ProcessID;
        string managedSql = command.CommandText;
        await transaction.SaveAsync("value_native_error", token);
        command.CommandText = nativeSql;
        PostgresException native = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(expectedState, native.SqlState);
        await transaction.RollbackAsync("value_native_error", token);
        await transaction.ReleaseAsync("value_native_error", token);

        await transaction.SaveAsync("value_managed_error", token);
        command.CommandText = managedSql;
        PostgresException managed = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(native.SqlState, managed.SqlState);
        Assert.AreEqual(native.MessageText, managed.MessageText);
        Assert.AreEqual(native.Detail, managed.Detail);
        Assert.AreEqual(native.Hint, managed.Hint);
        await transaction.RollbackAsync("value_managed_error", token);
        await transaction.ReleaseAsync("value_managed_error", token);

        command.Parameters.Clear();
        command.CommandText = $"SELECT datatype.exchange_numeric(1.2300, {mode})::text, pg_backend_pid()";
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
        Assert.IsTrue(await reader.ReadAsync(token));
        Assert.AreEqual("1.2300", reader.GetString(0));
        Assert.AreEqual(backend, reader.GetInt32(1));
        Assert.IsFalse(await reader.ReadAsync(token));
    }
}
