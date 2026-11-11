using Npgsql;
using NpgsqlTypes;

namespace Ankus.IntegrationTests;

/// <summary>
/// Compares new temporal conveniences with independent PostgreSQL expressions.
/// </summary>
/// <param name="context">The test context.</param>
[TestClass]
public sealed class TemporalConvenienceTests(TestContext context)
{
    /// <summary>
    /// Checks multi-field construction and individual unit factories, including signs and full-range microseconds.
    /// </summary>
    /// <param name="actualSql">The generated callback expression.</param>
    /// <param name="expectedSql">The independent SQL expression.</param>
    [TestMethod]
    [DataRow("datatype.temporal_factory('timestamp', -1, 2, 29, 12, 34, 56.1234567, NULL)", "make_timestamp(-1, 2, 29, 12, 34, 56.1234567)")]
    [DataRow("datatype.temporal_factory('timestamp', 294276, 12, 31, 23, 59, 59.999999, NULL)", "make_timestamp(294276, 12, 31, 23, 59, 59.999999)")]
    [DataRow("datatype.temporal_factory('timestamp', 2024, 2, 29, 24, 0, 0, NULL)", "make_timestamp(2024, 2, 29, 24, 0, 0)")]
    [DataRow("datatype.temporal_factory('timestamptz', 2024, 3, 10, 2, 30, 0, NULL)", "make_timestamptz(2024, 3, 10, 2, 30, 0)")]
    [DataRow("datatype.temporal_factory('timestamptz', 2024, 3, 10, 2, 30, 0, 'America/New_York')", "make_timestamptz(2024, 3, 10, 2, 30, 0, 'America/New_York')")]
    [DataRow("datatype.temporal_factory('timestamptz', 2024, 11, 3, 1, 30, 0, 'America/New_York')", "make_timestamptz(2024, 11, 3, 1, 30, 0, 'America/New_York')")]
    [DataRow("datatype.temporal_factory('timetz', 0, 0, 0, 12, 34, 56.123456, NULL)", "make_time(12, 34, 56.123456)::timetz")]
    [DataRow("datatype.temporal_factory('offset', 19817, 0, 0, 24, 0, 0, NULL)", "timetz '24:00+05:30:17'")]
    [DataRow("datatype.interval_factory(1, -2, 3, -4, 5, -6, 7.1234567)", "make_interval(1, -2, 3, -4, 5, -6, 7.1234567)")]
    [DataRow("datatype.interval_factory(0, 0, 0, 0, 0, 0, 0)", "interval '0'")]
    [DataRow("datatype.interval_unit('years', '-5')", "interval '-5 years'")]
    [DataRow("datatype.interval_unit('months', '-2147483648')", "interval '-2147483648 months'")]
    [DataRow("datatype.interval_unit('weeks', '-10')", "interval '-70 days'")]
    [DataRow("datatype.interval_unit('days', '2147483647')", "interval '2147483647 days'")]
    [DataRow("datatype.interval_unit('hours', '49')", "interval '49 hours'")]
    [DataRow("datatype.interval_unit('minutes', '-90')", "interval '-90 minutes'")]
    [DataRow("datatype.interval_unit('seconds', '0.1234567')", "make_interval(secs => 0.1234567)")]
    [DataRow("encode(interval_send(datatype.interval_unit('microseconds', '9223372036854775807')), 'hex')", "'7fffffffffffffff0000000000000000'")]
    [DataRow("encode(interval_send(datatype.interval_unit('microseconds', '-9223372036854775808')), 'hex')", "'80000000000000000000000000000000'")]
    [DataRow("datatype.interval_unit('abs', '1 month -2 days -3 microseconds')", "interval '1 month -2 days -3 microseconds'")]
    [DataRow("datatype.interval_unit('abs', '-1 month 2 days 3 microseconds')", "-interval '-1 month 2 days 3 microseconds'")]
    [DataRow("datatype.interval_unit('abs', '1 month -31 days')", "-interval '1 month -31 days'")]
    [DataRow("datatype.interval_unit('abs', '1 month -30 days')", "interval '1 month -30 days'")]
    [DataRow("datatype.interval_unit('abs', '-infinity')", "interval 'infinity'")]
    [DataRow("datatype.timestamp_time_tz(timestamptz '2024-03-10 07:30+00')", "timestamptz '2024-03-10 07:30+00'::timetz")]
    [DataRow("datatype.timestamp_time_tz('infinity')", "'infinity'::timestamptz::timetz")]
    public Task TemporalFactoriesMatchSql(string actualSql, string expectedSql)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TemporalFactoriesMatchSql), async (connection, transaction, token) =>
        {
            await using var settings = new NpgsqlCommand("SET LOCAL TIME ZONE 'America/New_York'", connection, transaction);
            await settings.ExecuteNonQueryAsync(token);
            await using var command = new NpgsqlCommand($"SELECT ({expectedSql})::text, ({actualSql})::text", connection, transaction);
            int major = PostgresFixture.Cluster.Installation.Version.Major;
            if (major < 14 && expectedSql.StartsWith("make_timestamp(-1,", StringComparison.Ordinal))
            {
                command.CommandText = $"SELECT {actualSql}";
                await AssertMatchingFailureAsync(command, transaction, $"SELECT {expectedSql}", "22008", token);
                return;
            }

            if (major < 17 && actualSql == "datatype.interval_unit('abs', '-infinity')")
            {
                command.CommandText = $"SELECT {actualSql}";
                await AssertMatchingFailureAsync(command, transaction, "SELECT interval '-infinity'", "22007", token);
                return;
            }

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.AreEqual(reader.GetValue(0), reader.GetValue(1));
            Assert.IsFalse(await reader.ReadAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// Checks all operator routes, including commuted overloads, negative operands, rollover and DST.
    /// </summary>
    /// <param name="type">The primary operand type.</param>
    /// <param name="operation">The operator route.</param>
    /// <param name="left">The primary input.</param>
    /// <param name="right">The secondary input.</param>
    /// <param name="expectedSql">The independent expression.</param>
    [TestMethod]
    [DataRow("date", "days", "2024-02-28", "2", "date '2024-02-28' + 2")]
    [DataRow("date", "rdays", "2024-02-28", "2", "2 + date '2024-02-28'")]
    [DataRow("date", "mdays", "2024-03-01", "2", "date '2024-03-01' - 2")]
    [DataRow("date", "mdays", "infinity", "-2147483648", "date 'infinity' - (-2147483648)")]
    [DataRow("date", "difference", "0001-01-01", "0001-12-31 BC", "date '0001-01-01' - date '0001-12-31 BC'")]
    [DataRow("date", "add", "2024-01-31", "1 month", "date '2024-01-31' + interval '1 month'")]
    [DataRow("date", "radd", "2024-01-31", "1 month", "interval '1 month' + date '2024-01-31'")]
    [DataRow("date", "subtract", "2024-03-31", "1 month", "date '2024-03-31' - interval '1 month'")]
    [DataRow("date", "time", "2024-02-29", "24:00", "date '2024-02-29' + time '24:00'")]
    [DataRow("date", "rtime", "2024-02-29", "24:00", "time '24:00' + date '2024-02-29'")]
    [DataRow("date", "timetz", "2024-02-29", "12:00+05:30:17", "date '2024-02-29' + timetz '12:00+05:30:17'")]
    [DataRow("date", "rtimetz", "2024-02-29", "12:00+05:30:17", "timetz '12:00+05:30:17' + date '2024-02-29'")]
    [DataRow("time", "add", "23:00", "2 hours", "time '23:00' + interval '2 hours'")]
    [DataRow("time", "radd", "23:00", "2 hours", "interval '2 hours' + time '23:00'")]
    [DataRow("time", "subtract", "00:00", "1 microsecond", "time '00:00' - interval '1 microsecond'")]
    [DataRow("time", "difference", "01:00", "23:00", "time '01:00' - time '23:00'")]
    [DataRow("timetz", "add", "23:00+05:30:17", "2 hours", "timetz '23:00+05:30:17' + interval '2 hours'")]
    [DataRow("timetz", "radd", "23:00+05:30:17", "2 hours", "interval '2 hours' + timetz '23:00+05:30:17'")]
    [DataRow("timetz", "subtract", "00:00-02", "1 second", "timetz '00:00-02' - interval '1 second'")]
    [DataRow("timestamp", "add", "2024-01-31 12:00", "1 month", "timestamp '2024-01-31 12:00' + interval '1 month'")]
    [DataRow("timestamp", "radd", "2024-01-31 12:00", "1 month", "interval '1 month' + timestamp '2024-01-31 12:00'")]
    [DataRow("timestamp", "subtract", "2024-03-31", "1 month", "timestamp '2024-03-31' - interval '1 month'")]
    [DataRow("timestamp", "difference", "2024-03-10", "2024-03-08 23:00", "timestamp '2024-03-10' - timestamp '2024-03-08 23:00'")]
    [DataRow("timestamptz", "add", "2024-03-09 12:00-05", "1 day", "timestamptz '2024-03-09 12:00-05' + interval '1 day'")]
    [DataRow("timestamptz", "radd", "2024-03-09 12:00-05", "24 hours", "interval '24 hours' + timestamptz '2024-03-09 12:00-05'")]
    [DataRow("timestamptz", "subtract", "2024-03-10 12:00-04", "1 day", "timestamptz '2024-03-10 12:00-04' - interval '1 day'")]
    [DataRow("timestamptz", "difference", "2024-03-10 12:00-04", "2024-03-09 12:00-05", "timestamptz '2024-03-10 12:00-04' - timestamptz '2024-03-09 12:00-05'")]
    [DataRow("interval", "add", "1 month -2 days", "3 days 4 hours", "interval '1 month -2 days' + interval '3 days 4 hours'")]
    [DataRow("interval", "subtract", "1 month -2 days", "3 days", "interval '1 month -2 days' - interval '3 days'")]
    [DataRow("interval", "negate", "1 month -2 days", "", "-interval '1 month -2 days'")]
    [DataRow("interval", "multiply", "1 month 1 day 1 second", "0.5", "interval '1 month 1 day 1 second' * 0.5")]
    [DataRow("interval", "rmultiply", "1 month 1 day 1 second", "0.5", "0.5 * interval '1 month 1 day 1 second'")]
    [DataRow("interval", "divide", "1 month 1 day 1 second", "2", "interval '1 month 1 day 1 second' / 2")]
    public Task TemporalOperatorsMatchSql(string type, string operation, string left, string right, string expectedSql)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TemporalOperatorsMatchSql), async (connection, transaction, token) =>
        {
            await using var settings = new NpgsqlCommand("SET LOCAL TIME ZONE 'America/New_York'", connection, transaction);
            await settings.ExecuteNonQueryAsync(token);
            await using var command = new NpgsqlCommand($"SELECT ({expectedSql})::text, datatype.temporal_operator($1,$2,$3,$4)", connection, transaction);
            command.Parameters.AddWithValue(type);
            command.Parameters.AddWithValue(operation);
            command.Parameters.AddWithValue(left);
            command.Parameters.AddWithValue(right);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.AreEqual(reader.GetString(0), reader.GetString(1));
        }, context.CancellationToken);

    /// <summary>
    /// Checks rounding before/at/after ties, negative timestamps, end-of-day rollover, and infinity.
    /// </summary>
    /// <param name="type">The temporal type.</param>
    /// <param name="input">The temporal input.</param>
    /// <param name="precision">The fractional precision.</param>
    [TestMethod]
    [DataRow("time", "23:59:59.999999", 0)]
    [DataRow("timetz", "23:59:59.999999+05:30:17", 3)]
    [DataRow("timestamp", "1999-12-31 23:59:59.499999", 0)]
    [DataRow("timestamp", "1999-12-31 23:59:59.500000", 0)]
    [DataRow("timestamp", "1999-12-31 23:59:59.500001", 0)]
    [DataRow("timestamp", "2000-01-01 00:00:00.499999", 0)]
    [DataRow("timestamp", "2000-01-01 00:00:00.500000", 0)]
    [DataRow("timestamp", "2000-01-01 00:00:00.500001", 0)]
    [DataRow("timestamp", "2024-12-31 23:59:59.999999", 3)]
    [DataRow("timestamptz", "2024-03-10 01:59:59.999999-05", 0)]
    [DataRow("timestamptz", "2000-01-01 00:00:00.123456+00", 6)]
    [DataRow("timestamp", "infinity", 0)]
    [DataRow("timestamptz", "-infinity", 3)]
    public Task PrecisionRoundingMatchesNativeModifiers(string type, string input, int precision)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(PrecisionRoundingMatchesNativeModifiers), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand($"SELECT ($1::{type})::{type}({precision})::text, datatype.temporal_round($2,$1,$3)", connection, transaction);
            command.Parameters.AddWithValue(input);
            command.Parameters.AddWithValue(type);
            command.Parameters.AddWithValue(precision);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.AreEqual(reader.GetString(0), reader.GetString(1));
        }, context.CancellationToken);

    /// <summary>
    /// Checks precision-qualified clocks against SQL and ensures explicit zone formatting cannot change session settings.
    /// </summary>
    /// <param name="precision">The clock precision.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(6)]
    public Task CurrentClocksMatchSqlWithinTransaction(int precision)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CurrentClocksMatchSqlWithinTransaction), async (connection, transaction, token) =>
        {
            await using var settings = new NpgsqlCommand("SET LOCAL TIME ZONE 'Asia/Kathmandu'; SET LOCAL DateStyle = 'German'", connection, transaction);
            await settings.ExecuteNonQueryAsync(token);
            await using var command = new NpgsqlCommand($"""
                SELECT datatype.temporal_current('date', {precision}) = current_date::text,
                    datatype.temporal_current('time', {precision}) = localtime({precision})::text,
                    datatype.temporal_current('timetz', {precision}) = current_time({precision})::text,
                    datatype.temporal_current('timestamp', {precision}) = localtimestamp({precision})::text,
                    datatype.temporal_current('timestamptz', {precision}) = current_timestamp({precision})::text,
                    datatype.temporal_iso_zone('timestamptz', '2024-03-10 07:30+00', 'America/New_York'),
                    current_setting('TimeZone'), current_setting('DateStyle')
                """, connection, transaction);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            for (int index = 0; index < 5; index++)
            {
                Assert.IsTrue(reader.GetBoolean(index), $"Clock {index} at precision {precision}");
            }

            Assert.AreEqual("2024-03-10T03:30:00-04:00", reader.GetString(5));
            Assert.AreEqual("Asia/Kathmandu", reader.GetString(6));
            Assert.AreEqual("German, DMY", reader.GetString(7));
        }, context.CancellationToken);

    /// <summary>
    /// Checks target-instant DST resolution, historical second offsets, BC dates and infinity formatting.
    /// </summary>
    /// <param name="type">The temporal type.</param>
    /// <param name="input">The input.</param>
    /// <param name="zone">The explicit zone.</param>
    /// <param name="expected">The exact ISO representation.</param>
    [TestMethod]
    [DataRow("timestamptz", "2024-01-01 12:00+00", "America/New_York", "2024-01-01T07:00:00-05:00")]
    [DataRow("timestamptz", "2024-07-01 12:00+00", "America/New_York", "2024-07-01T08:00:00-04:00")]
    [DataRow("timestamptz", "2024-11-03 05:30+00", "America/New_York", "2024-11-03T01:30:00-04:00")]
    [DataRow("timestamptz", "2024-11-03 06:30+00", "America/New_York", "2024-11-03T01:30:00-05:00")]
    [DataRow("timestamptz", "1880-01-01 12:00+00", "America/New_York", "1880-01-01T07:03:58-04:56:02")]
    [DataRow("timestamptz", "0001-02-29 12:00+00 BC", "UTC", "0001-02-29T12:00:00+00:00 BC")]
    [DataRow("timestamptz", "infinity", "America/New_York", "infinity")]
    [DataRow("timestamptz", "-infinity", "Asia/Kathmandu", "-infinity")]
    [DataRow("timetz", "12:00+05:30:17", "UTC", "06:29:43+00:00")]
    public Task ExplicitZoneIsoUsesInstantOffset(string type, string input, string zone, string expected)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ExplicitZoneIsoUsesInstantOffset), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT datatype.temporal_iso_zone($1,$2,$3)", connection, transaction);
            command.Parameters.AddWithValue(type);
            command.Parameters.AddWithValue(input);
            command.Parameters.AddWithValue(zone);
            Assert.AreEqual(expected, await command.ExecuteScalarAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// Checks interval sign against native comparison, including mixed components that cancel.
    /// </summary>
    /// <param name="input">The interval.</param>
    /// <param name="expected">The independent comparison sign.</param>
    [TestMethod]
    [DataRow("1 month -30 days", 0)]
    [DataRow("-1 month 31 days", 1)]
    [DataRow("1 month -31 days", -1)]
    [DataRow("maximum", 1)]
    [DataRow("-infinity", -1)]
    [DataRow("infinity", 1)]
    public Task IntervalSignMatchesPostgresComparison(string input, int expected)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(IntervalSignMatchesPostgresComparison), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT sign(interval_cmp($1::interval, interval '0')), datatype.interval_sign($1::interval), encode(interval_send($1::interval), 'hex')", connection, transaction);
            if (input == "maximum")
            {
                command.Parameters.AddWithValue(NpgsqlDbType.Interval, new NpgsqlInterval(int.MaxValue, int.MaxValue, long.MaxValue));
            }
            else
            {
                command.Parameters.AddWithValue(input);
            }

            if (PostgresFixture.Cluster.Installation.Version.Major < 17 && input is "infinity" or "-infinity")
            {
                command.CommandText = "SELECT datatype.interval_sign($1::interval)";
                await AssertMatchingFailureAsync(command, transaction, "SELECT $1::interval", "22007", token);
                return;
            }

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.AreEqual(expected, (int)reader.GetDouble(0));
            Assert.AreEqual(expected, reader.GetInt32(1));
            if (input == "maximum")
            {
                Assert.AreEqual("7fffffffffffffff7fffffff7fffffff", reader.GetString(2));
            }

            Assert.IsFalse(await reader.ReadAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// A statement the test cluster logs as slow keeps its result when a bind parameter has no text form, since the
    /// cluster leaves parameters out of that log line. PostgreSQL 13 can't format an interval whose hours exceed 32 bits.
    /// </summary>
    [TestMethod]
    public Task SlowStatementLogsLeaveParametersOut()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(SlowStatementLogsLeaveParametersOut), async (connection, transaction, token) =>
        {
            await using (var configure = new NpgsqlCommand("SET LOCAL log_min_duration_statement = 0", connection, transaction))
            {
                await configure.ExecuteNonQueryAsync(token);
            }

            await using var command = new NpgsqlCommand("SELECT datatype.interval_sign($1::interval)", connection, transaction);
            command.Parameters.AddWithValue(NpgsqlDbType.Interval, new NpgsqlInterval(int.MaxValue, int.MaxValue, long.MaxValue));
            Assert.AreEqual(1, await command.ExecuteScalarAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// Checks constructor, rounding and formatting failures preserve managed finally, active plans and writes.
    /// </summary>
    /// <param name="operation">The invalid operation.</param>
    /// <param name="code">The expected native SQLSTATE.</param>
    [TestMethod]
    [DataRow("year", "22008")]
    [DataRow("month", "22008")]
    [DataRow("day", "22008")]
    [DataRow("zone", "22023")]
    [DataRow("years", "22008")]
    [DataRow("weeks", "22008")]
    [DataRow("seconds", "22008")]
    [DataRow("round", "22008")]
    [DataRow("round-tz", "22008")]
    [DataRow("format", "22023")]
    [DataRow("format-range", "22008")]
    public Task TemporalConvenienceErrorsPreserveState(string operation, string code)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TemporalConvenienceErrorsPreserveState), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT datatype.temporal_convenience_recovery($1)", connection, transaction);
            command.Parameters.AddWithValue(operation);
            Assert.AreEqual($"{code}:50:0:2", await command.ExecuteScalarAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// Compares native rejection with the selected callback boundary and verifies exact values after rollback.
    /// </summary>
    private static async Task AssertMatchingFailureAsync(NpgsqlCommand command, NpgsqlTransaction transaction,
        string nativeSql, string expectedState, CancellationToken token)
    {
        NpgsqlConnection? connection = command.Connection;
        Assert.IsNotNull(connection);
        int backend = connection.ProcessID;
        string managedSql = command.CommandText;
        await transaction.SaveAsync("temporal_native_error", token);
        command.CommandText = nativeSql;
        PostgresException native = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(expectedState, native.SqlState);
        await transaction.RollbackAsync("temporal_native_error", token);
        await transaction.ReleaseAsync("temporal_native_error", token);

        await transaction.SaveAsync("temporal_managed_error", token);
        command.CommandText = managedSql;
        PostgresException managed = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(native.SqlState, managed.SqlState);
        Assert.AreEqual(native.MessageText, managed.MessageText);
        Assert.AreEqual(native.Detail, managed.Detail);
        Assert.AreEqual(native.Hint, managed.Hint);
        await transaction.RollbackAsync("temporal_managed_error", token);
        await transaction.ReleaseAsync("temporal_managed_error", token);

        command.Parameters.Clear();
        command.CommandText = """
            SELECT encode(interval_send(datatype.interval_unit('microseconds', '-1')), 'hex'),
                datatype.interval_sign(interval '1 month -31 days'), pg_backend_pid()
            """;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
        Assert.IsTrue(await reader.ReadAsync(token));
        Assert.AreEqual("ffffffffffffffff0000000000000000", reader.GetString(0));
        Assert.AreEqual(-1, reader.GetInt32(1));
        Assert.AreEqual(backend, reader.GetInt32(2));
        Assert.IsFalse(await reader.ReadAsync(token));
    }
}
