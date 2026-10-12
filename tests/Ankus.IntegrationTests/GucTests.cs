using System.Net.Sockets;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies PostgreSQL configuration semantics against published Native AOT declarations and hooks.
/// </summary>
/// <param name="context">The test cancellation and diagnostic context.</param>
[TestClass]
public sealed class GucTests(TestContext context)
{
    private static readonly string[] s_labels = ["idle", "rest", "fast", "Steady"];
    private static readonly string[] s_restorationEvents =
    [
        "check:21:Session", "assign:22:old=10:extra=00FF0016",
        "check:23:Session", "assign:23:old=22:extra=null",
        "check:24:Session", "assign:24:old=23:extra=",
        "assign:23:old=24:extra=null", "assign:22:old=23:extra=00FF0016",
    ];
    /// <summary>
    /// A scalar property reads the variable PostgreSQL assigns, so it sees a later <c>SET</c> in the same call, and its
    /// reads stay unavailable inside a memory reset callback whether or not the setting was read before.
    /// </summary>
    [TestMethod]
    public async Task LocatedScalarReadsFollowAssignmentsAndScopes()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ExecuteAsync(connection, "SET \"ankus_guc.limit\" = '23'");
        const string Unavailable = "Configuration reads are unavailable in this callback scope.";
        Assert.AreEqual($"23|31|{Unavailable}|{Unavailable}|31", await ScalarAsync(connection, "SELECT datatype.guc_located_reads()"));
        Assert.AreEqual("31", await ScalarAsync(connection, "SHOW \"ankus_guc.limit\""));
    }

    /// <summary>
    /// Pure configuration lookup errors can be caught while native calls remain usable in the same entry frame.
    /// </summary>
    /// <param name="mode">The unknown setting or incompatible typed reader.</param>
    /// <param name="state">The original diagnostic SQLSTATE.</param>
    /// <param name="message">The original diagnostic message.</param>
    [TestMethod]
    [DataRow(0, "42704", "Unknown Ankus configuration parameter")]
    [DataRow(1, "42804", "Ankus configuration type does not match its declaration")]
    [DataRow(2, "42804", "Ankus configuration type does not match its declaration")]
    [DataRow(3, "42804", "Ankus configuration type does not match its declaration")]
    [DataRow(4, "42804", "Ankus configuration type does not match its declaration")]
    public async Task PureReadValidationDoesNotPoisonBackend(int mode, string state, string message)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ExecuteAsync(connection, "SET \"ankus_guc.limit\" = '23'");
        Assert.AreEqual($"{state}|{message}|23|42", await ScalarAsync(connection,
            $"SELECT datatype.guc_validation_recovery({mode})"));
        Assert.AreEqual("23", await ScalarAsync(connection, "SHOW \"ankus_guc.limit\""));
        await ExecuteAsync(connection, "SET \"ankus_guc.limit\" = '24'");
        Assert.AreEqual("24", await ScalarAsync(connection, "SHOW \"ankus_guc.limit\""));
        Assert.AreEqual(42, await ScalarAsync(connection, "SELECT 42"));
    }

    /// <summary>
    /// Callback-scoped configuration lookup errors permit later native reads and SQL in the same check hook.
    /// </summary>
    /// <param name="mode">The unknown setting or incompatible typed reader.</param>
    /// <param name="state">The original diagnostic SQLSTATE.</param>
    /// <param name="message">The original diagnostic message.</param>
    [TestMethod]
    [DataRow(0, "42704", "Unknown Ankus configuration parameter")]
    [DataRow(1, "42804", "Ankus configuration type does not match its declaration")]
    [DataRow(2, "42804", "Ankus configuration type does not match its declaration")]
    [DataRow(3, "42804", "Ankus configuration type does not match its declaration")]
    [DataRow(4, "42804", "Ankus configuration type does not match its declaration")]
    public async Task HookReadValidationDoesNotPoisonBackend(int mode, string state, string message)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ExecuteAsync(connection, "SET \"ankus_guc.limit\" = '23'; SET ankus_guc.control = 'read-validation'");
        await ScalarAsync(connection, "SELECT datatype.guc_events(true)");
        await ExecuteAsync(connection, $"SET ankus_guc.hook_int = '{mode}'");
        Assert.Contains($"validation:{state}|{message}|23|42",
            Assert.IsInstanceOfType<string>(await ScalarAsync(connection, "SELECT datatype.guc_events(true)")));
        Assert.AreEqual($"False|{mode}|1.25|<null>|18446744073709551615", await ScalarAsync(connection,
            "SELECT datatype.guc_hook_values()"));
        Assert.AreEqual("23", await ScalarAsync(connection, "SHOW \"ankus_guc.limit\""));
        Assert.AreEqual(42, await ScalarAsync(connection, "SELECT 42"));
    }

    /// <summary>
    /// Native defaults, nullable storage, enum labels, and retained descriptions agree with independent catalogs.
    /// </summary>
    [TestMethod]
    public async Task DefaultsAndMetadataPreserveNativeTypes()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        Assert.AreEqual("True|10|-9223372036854775808|<null>|18446744073709551615", await ScalarAsync(connection, "SELECT datatype.guc_values()"));
        Assert.AreEqual("", await ScalarAsync(connection, "SHOW ankus_guc.text"));
        Assert.AreEqual("idle", await ScalarAsync(connection, "SHOW ankus_guc.mode"));
        Assert.AreEqual("integer|10|10|-50|100|user|default", await ScalarAsync(connection,
            "SELECT concat_ws('|', vartype, boot_val, reset_val, min_val, max_val, context, source) FROM pg_settings WHERE name = 'ankus_guc.limit'"));
        Assert.AreEqual("A retained description with café.", await ScalarAsync(connection,
            "SELECT extra_desc FROM pg_settings WHERE name = 'ankus_guc.enabled'"));
        string[] labels = Assert.IsInstanceOfType<string[]>(await ScalarAsync(connection,
            "SELECT enumvals FROM pg_settings WHERE name = 'ankus_guc.mode'"));
        Assert.AreSequenceEqual(s_labels, labels);
        string events = Assert.IsInstanceOfType<string>(await ScalarAsync(connection, "SELECT datatype.guc_events(true)"));
        Assert.Contains("check:10:Default\nassign:10:old=10:extra=0A000000", events);
    }

    /// <summary>
    /// Native parsing accepts synonyms, full-width enum values, signed numbers, and Unicode without managed coercion.
    /// </summary>
    [TestMethod]
    public async Task FiveTypesUseNativeParsingAndOwnedStorage()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ExecuteAsync(connection, "SET ankus_guc.enabled = 'OFF'; SET \"ankus_guc.limit\" = '-50'; SET ankus_guc.ratio = '-1.25'; SET ankus_guc.mode = 'FaSt'");
        await using var command = new NpgsqlCommand("SELECT set_config('ankus_guc.text', $1, false)", connection);
        string text = new string('x', 10000) + " café 🐘 ' \\ %";
        command.Parameters.AddWithValue(text);
        Assert.AreEqual(text, await command.ExecuteScalarAsync(context.CancellationToken));
        Assert.AreEqual(text, await ScalarAsync(connection, "SELECT datatype.guc_retained_text(true)"));
        Assert.AreEqual($"False|-50|{BitConverter.DoubleToInt64Bits(-1.25)}|{text}|9223372036854775808", await ScalarAsync(connection, "SELECT datatype.guc_values()"));
        await ExecuteAsync(connection, "SET ankus_guc.text = ''; SET ankus_guc.mode = 'rest'");
        Assert.AreEqual($"False|-50|{BitConverter.DoubleToInt64Bits(-1.25)}||18446744073709551615", await ScalarAsync(connection, "SELECT datatype.guc_values()"));
        Assert.AreEqual("idle", await ScalarAsync(connection, "SHOW ankus_guc.mode"));
        await ExecuteAsync(connection, "SET ankus_guc.mode = 'steady'");
        Assert.AreEqual("Steady", await ScalarAsync(connection, "SHOW ankus_guc.mode"));
        Assert.AreEqual($"False|-50|{BitConverter.DoubleToInt64Bits(-1.25)}||3", await ScalarAsync(connection, "SELECT datatype.guc_values()"));
        Assert.AreEqual(text, await ScalarAsync(connection, "SELECT datatype.guc_retained_text(false)"));
        await ExecuteAsync(connection, "RESET ALL");
        Assert.AreEqual("True|10|-9223372036854775808|<null>|18446744073709551615", await ScalarAsync(connection, "SELECT datatype.guc_values()"));
    }

    /// <summary>
    /// Parse and bounds errors preserve existing values and leave the same backend usable.
    /// </summary>
    /// <param name="name">The configuration name.</param>
    /// <param name="value">The invalid input.</param>
    /// <param name="sqlState">The expected native SQLSTATE.</param>
    [TestMethod]
    [DataRow("enabled", "perhaps", "22023")]
    [DataRow("limit", "101", "22023")]
    [DataRow("limit", "-51", "22023")]
    [DataRow("limit", "2147483648", "22023")]
    [DataRow("ratio", "NaN", "22023")]
    [DataRow("ratio", "Infinity", "22023")]
    [DataRow("ratio", "1e999", "22023")]
    [DataRow("mode", "unknown", "22023")]
    public async Task InvalidNativeValuesDoNotAssign(string name, string value, string sqlState)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT set_config($1, $2, false)", connection);
        command.Parameters.AddWithValue("ankus_guc." + name);
        command.Parameters.AddWithValue(value);
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(context.CancellationToken));
        Assert.AreEqual(sqlState, error.SqlState);
        if (name == "mode")
        {
            Assert.IsNotNull(error.Hint);
            Assert.DoesNotContain("secret", error.Hint);
            await ExecuteAsync(connection, "SET ankus_guc.mode = 'secret'");
            Assert.AreEqual("secret", await ScalarAsync(connection, "SHOW ankus_guc.mode"));
            await ExecuteAsync(connection, "RESET ankus_guc.mode");
        }

        Assert.AreEqual("True|10|-9223372036854775808|<null>|18446744073709551615", await ScalarAsync(connection, "SELECT datatype.guc_values()"));
        Assert.AreEqual(42, await ScalarAsync(connection, "SELECT 42"));
    }

    /// <summary>
    /// Typed definitions adopt all placeholder kinds while preserving native warning behavior for invalid inputs.
    /// </summary>
    [TestMethod]
    public async Task PlaceholderAdoptionPreservesValuesAndWarnings()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        var notices = new List<PostgresNotice>();
        connection.Notice += (_, args) => notices.Add(args.Notice);
        await ExecuteAsync(connection, "SET ankus_guc.enabled = 'off'; SET \"ankus_guc.limit\" = 'invalid'; SET ankus_guc.ratio = '2.5'; SET ankus_guc.text = 'préload'; SET ankus_guc.mode = 'fast'; LOAD 'Ankus.TestExtension'");
        Assert.AreEqual($"False|10|{BitConverter.DoubleToInt64Bits(2.5)}|préload|9223372036854775808", await ScalarAsync(connection, "SELECT datatype.guc_values()"));
        PostgresNotice warning = Assert.ContainsSingle(notices.Where(notice => notice.MessageText.Contains("ankus_guc.limit", StringComparison.Ordinal)));
        Assert.AreEqual("22023", warning.SqlState);
        Assert.AreEqual("WARNING", warning.InvariantSeverity);
    }

    /// <summary>
    /// Registration preserves placeholder masked and prior values through savepoint and transaction restoration.
    /// </summary>
    [TestMethod]
    public async Task PlaceholderStacksSurviveRegistrationAndRollback()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(connection, "SET \"ankus_guc.limit\" = '20'; BEGIN; SET \"ankus_guc.limit\" = '30'; SET LOCAL \"ankus_guc.limit\" = '40'; SAVEPOINT inner_guc; SET LOCAL \"ankus_guc.limit\" = '50'; LOAD 'Ankus.TestExtension'");
        Assert.AreEqual("50", await ScalarAsync(connection, "SHOW \"ankus_guc.limit\""));
        await ExecuteAsync(connection, "ROLLBACK TO inner_guc");
        Assert.AreEqual("40", await ScalarAsync(connection, "SHOW \"ankus_guc.limit\""));
        await ExecuteAsync(connection, "COMMIT");
        Assert.AreEqual("30", await ScalarAsync(connection, "SHOW \"ankus_guc.limit\""));
        await ExecuteAsync(connection, "BEGIN; SET \"ankus_guc.limit\" = '60'; SET LOCAL \"ankus_guc.limit\" = '70'; ROLLBACK");
        Assert.AreEqual("30", await ScalarAsync(connection, "SHOW \"ankus_guc.limit\""));
        Assert.Contains("|30|", Assert.IsInstanceOfType<string>(await ScalarAsync(connection, "SELECT datatype.guc_values()")));
    }

    /// <summary>
    /// Check normalization reaches all five native storage types and show hooks affect display only.
    /// </summary>
    [TestMethod]
    public async Task AllHookTypesNormalizeAndDisplayIndependently()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ExecuteAsync(connection, "SET ankus_guc.control = 'normalize'; SET ankus_guc.hook_bool = 'on'; SET ankus_guc.hook_int = '21'; SET ankus_guc.hook_real = '3.75'; SET ankus_guc.hook_text = '  café 🐘  '; SET ankus_guc.hook_mode = 'idle'");
        Assert.AreEqual("False|22|4|café 🐘|9223372036854775808", await ScalarAsync(connection, "SELECT datatype.guc_hook_values()"));
        Assert.AreEqual("integer=22;extra=00FF0016", await ScalarAsync(connection, "SHOW ankus_guc.hook_int"));
        Assert.AreEqual("boolean=False;extra=0D", await ScalarAsync(connection, "SHOW ankus_guc.hook_bool"));
        Assert.AreEqual("real=4;extra=0D", await ScalarAsync(connection, "SHOW ankus_guc.hook_real"));
        Assert.AreEqual("text=café 🐘;extra=2020636166C3A920F09F90982020", await ScalarAsync(connection, "SHOW ankus_guc.hook_text"));
        Assert.AreEqual("mode=9223372036854775808;extra=0D", await ScalarAsync(connection, "SHOW ankus_guc.hook_mode"));
        Assert.AreEqual("10|10|-50|100", await ScalarAsync(connection,
            "SELECT concat_ws('|', boot_val, reset_val, min_val, max_val) FROM pg_settings WHERE name = 'ankus_guc.hook_int'"));
    }

    /// <summary>
    /// Native bounds validate the proposed value but do not revalidate an accepted hook replacement.
    /// </summary>
    [TestMethod]
    public async Task AcceptedNumericNormalizationPreservesNativeHookSemantics()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ExecuteAsync(connection, "SET ankus_guc.hook_int = '25'; SET ankus_guc.control = 'normalize_nan'; SET ankus_guc.hook_real = '1'");
        Assert.AreEqual("False|1000|NaN|<null>|18446744073709551615", await ScalarAsync(connection, "SELECT datatype.guc_hook_values()"));
        Assert.AreEqual("integer=1000;extra=null", await ScalarAsync(connection, "SHOW ankus_guc.hook_int"));
        Assert.AreEqual("real=NaN;extra=0D", await ScalarAsync(connection, "SHOW ankus_guc.hook_real"));
        Assert.AreEqual("100", await ScalarAsync(connection, "SELECT max_val FROM pg_settings WHERE name = 'ankus_guc.hook_int'"));
        PostgresException error = await FailureAsync(connection, "SET ankus_guc.hook_int = '101'");
        Assert.AreEqual("22023", error.SqlState);
    }

    /// <summary>
    /// Native check failure levels and structured diagnostics survive repeated failures without stale detail or hint fields.
    /// </summary>
    [TestMethod]
    public async Task CheckErrorsPreserveDiagnosticsAndRecover()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ExecuteAsync(connection, "SET ankus_guc.hook_int = '20'");
        await ScalarAsync(connection, "SELECT datatype.guc_events(true)");
        for (int iteration = 0; iteration < 25; iteration++)
        {
            PostgresException structured = await FailureAsync(connection, "SET ankus_guc.hook_int = '13'");
            Assert.AreEqual("22003", structured.SqlState);
            Assert.AreEqual("Rejected 100% café 🐘", structured.MessageText);
            Assert.AreEqual("Owned detail", structured.Detail);
            Assert.AreEqual("Choose another integer.", structured.Hint);
            await AssertRejectedStateAsync(connection, 13);
            PostgresException native = await FailureAsync(connection, "SET ankus_guc.hook_int = '14'");
            Assert.AreEqual("22023", native.SqlState);
            Assert.Contains("invalid value", native.MessageText);
            Assert.AreEqual("Default native message", native.Detail);
            Assert.IsNull(native.Hint);
            await AssertRejectedStateAsync(connection, 14);
            PostgresException unexpected = await FailureAsync(connection, "SET ankus_guc.hook_int = '15'");
            Assert.AreEqual("38000", unexpected.SqlState);
            Assert.AreEqual("Unexpected check failure.", unexpected.MessageText);
            Assert.IsNull(unexpected.Detail);
            Assert.IsNull(unexpected.Hint);
            await AssertRejectedStateAsync(connection, 15);
            PostgresException thrown = await FailureAsync(connection, "SET ankus_guc.hook_int = '16'");
            Assert.AreEqual("22012", thrown.SqlState);
            Assert.AreEqual("Owned check exception.", thrown.MessageText);
            Assert.AreEqual("Check detail", thrown.Detail);
            Assert.AreEqual("Check hint", thrown.Hint);
            await AssertRejectedStateAsync(connection, 16);
            await ExecuteAsync(connection, "SET ankus_guc.hook_int = '20'");
            await ScalarAsync(connection, "SELECT datatype.guc_events(true)");
        }

        Assert.AreEqual("False|20|1.25|<null>|18446744073709551615", await ScalarAsync(connection, "SELECT datatype.guc_hook_values()"));
        Assert.AreEqual(42, await ScalarAsync(connection, "SELECT 42"));
    }

    /// <summary>
    /// Check hooks of every type reject with PostgreSQL's standard message, a custom message and detail, or a custom
    /// message and hint, as pgrx's per-type check tests require, and a rejected value is never assigned.
    /// </summary>
    /// <param name="control">The rejection form the hooks return.</param>
    [TestMethod]
    [DataRow("reject-default")]
    [DataRow("reject-message")]
    [DataRow("reject-hint")]
    public async Task CheckRejectionsCoverEveryType(string control)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ExecuteAsync(connection, $"SET ankus_guc.control = '{control}'");
        await ScalarAsync(connection, "SELECT datatype.guc_events(true)");
        foreach ((string name, string value, string type, string native) in new[]
        {
            ("ankus_guc.hook_bool", "on", "Boolean", "1"),
            ("ankus_guc.hook_real", "150.5", "real", "150.5"),
            ("ankus_guc.hook_text", "rejected", "text", "\"rejected\""),
            ("ankus_guc.hook_mode", "fast", "mode", "\"fast\""),
        })
        {
            object? before = await ScalarAsync(connection, $"SELECT current_setting('{name}')");
            PostgresException error = await FailureAsync(connection, $"SET {name} = '{value}'");
            Assert.AreEqual("22023", error.SqlState, name);
            Assert.AreEqual(control == "reject-default" ? $"invalid value for parameter \"{name}\": {native}" : $"Rejected {type}.",
                error.MessageText);
            Assert.AreEqual(control == "reject-message" ? $"{type} detail" : null, error.Detail, name);
            Assert.AreEqual(control == "reject-hint" ? $"Choose another {type}." : null, error.Hint, name);
            Assert.AreEqual(before, await ScalarAsync(connection, $"SELECT current_setting('{name}')"), name);
        }

        string events = (string)(await ScalarAsync(connection, "SELECT datatype.guc_events(true)"))!;
        foreach (string assigned in new[] { "bool:", "real:", "text:", "mode:" })
        {
            Assert.DoesNotContain(assigned, events);
        }
    }

    /// <summary>
    /// Restoration calls assign with the old owned extra without calling check again.
    /// </summary>
    [TestMethod]
    public async Task HooksRestoreValuesAndExtraAcrossLocalAndSavepoints()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ScalarAsync(connection, "SELECT datatype.guc_events(true)");
        await ExecuteAsync(connection, "BEGIN; SET ankus_guc.hook_int = '21'; SET LOCAL ankus_guc.hook_int = '23'; SAVEPOINT inner_guc; SET LOCAL ankus_guc.hook_int = '24'");
        Assert.AreEqual("integer=24;extra=", await ScalarAsync(connection, "SHOW ankus_guc.hook_int"));
        await ExecuteAsync(connection, "ROLLBACK TO inner_guc");
        Assert.AreEqual("integer=23;extra=null", await ScalarAsync(connection, "SHOW ankus_guc.hook_int"));
        await ExecuteAsync(connection, "COMMIT");
        Assert.AreEqual("integer=22;extra=00FF0016", await ScalarAsync(connection, "SHOW ankus_guc.hook_int"));
        string events = Assert.IsInstanceOfType<string>(await ScalarAsync(connection, "SELECT datatype.guc_events(true)"));
        Assert.AreSequenceEqual(s_restorationEvents, events.Split('\n'));
        await ExecuteAsync(connection, "RESET ankus_guc.hook_int");
        Assert.AreEqual("assign:10:old=22:extra=0A000000", await ScalarAsync(connection, "SELECT datatype.guc_events(true)"));
        string retained = Assert.IsInstanceOfType<string>(await ScalarAsync(connection, "SELECT datatype.guc_retained_extras()"));
        Assert.AreEqual("0A000000,00FF0016,,00FF0016,0A000000", retained);
    }

    /// <summary>
    /// Function SET validates with Test source and restores the caller's state even after a function error.
    /// </summary>
    [TestMethod]
    public async Task FunctionSettingsValidateWithoutAssignAndRestore()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ScalarAsync(connection, "SELECT datatype.guc_events(true)");
        await ExecuteAsync(connection, "CREATE FUNCTION pg_temp.guc_function(fail bool) RETURNS text LANGUAGE plpgsql AS $$ BEGIN IF fail THEN RAISE EXCEPTION 'function failure'; END IF; RETURN current_setting('ankus_guc.hook_int'); END $$; ALTER FUNCTION pg_temp.guc_function(bool) SET ankus_guc.hook_int = '21'");
        string validation = Assert.IsInstanceOfType<string>(await ScalarAsync(connection, "SELECT datatype.guc_events(true)"));
        Assert.Contains("check:21:Test", validation);
        Assert.DoesNotContain("assign:", validation);
        Assert.AreEqual("integer=22;extra=00FF0016", await ScalarAsync(connection, "SELECT pg_temp.guc_function(false)"));
        Assert.AreEqual("integer=10;extra=0A000000", await ScalarAsync(connection, "SHOW ankus_guc.hook_int"));
        PostgresException error = await FailureAsync(connection, "SELECT pg_temp.guc_function(true)");
        Assert.AreEqual("P0001", error.SqlState);
        Assert.AreEqual("integer=10;extra=0A000000", await ScalarAsync(connection, "SHOW ankus_guc.hook_int"));
    }

    /// <summary>
    /// Check can use guarded SQL while assign and report show callbacks reject it and retain safe typed reads.
    /// </summary>
    [TestMethod]
    public async Task HookSqlAvailabilityMatchesNativePhase()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ScalarAsync(connection, "SELECT datatype.guc_events(true)");
        await ExecuteAsync(connection, "SET ankus_guc.control = 'probe'; SET ankus_guc.hook_int = '17'; SET ankus_guc.reported = '2'");
        Assert.AreEqual("integer=42;extra=00FF002A", await ScalarAsync(connection, "SHOW ankus_guc.hook_int"));
        Assert.Contains("assign-sql:unavailable", Assert.IsInstanceOfType<string>(await ScalarAsync(connection, "SELECT datatype.guc_events(true)")));
        Assert.AreEqual("report=2;sql=unavailable", connection.PostgresParameters["ankus_guc.reported"]);
        await ExecuteAsync(connection, "BEGIN; SET LOCAL ankus_guc.reported = '3'");
        Assert.AreEqual("report=3;sql=unavailable", connection.PostgresParameters["ankus_guc.reported"]);
        await ExecuteAsync(connection, "ROLLBACK");
        Assert.AreEqual("report=2;sql=unavailable", connection.PostgresParameters["ankus_guc.reported"]);
        Assert.AreEqual(42, await ScalarAsync(connection, "SELECT 42"));
    }

    /// <summary>
    /// Display errors unwind before native ERROR and do not change typed storage.
    /// </summary>
    [TestMethod]
    public async Task ShowErrorLeavesBackendAndStorageUsable()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ExecuteAsync(connection, "SET ankus_guc.control = 'show-error'");
        PostgresException error = await FailureAsync(connection, "SHOW ankus_guc.hook_int");
        Assert.AreEqual("22023", error.SqlState);
        Assert.AreEqual("Show failed safely.", error.MessageText);
        Assert.AreEqual("False|10|1.25|<null>|18446744073709551615", await ScalarAsync(connection, "SELECT datatype.guc_hook_values()"));
        await ExecuteAsync(connection, "RESET ankus_guc.control");
        Assert.AreEqual("integer=10;extra=0A000000", await ScalarAsync(connection, "SHOW ankus_guc.hook_int"));
    }

    /// <summary>
    /// Unexpected assign exceptions terminate only their backend rather than allowing partially restored state.
    /// </summary>
    [TestMethod]
    public async Task AssignFailureTerminatesOnlyItsBackend()
    {
        await using NpgsqlConnection failing = await OpenAsync();
        await using NpgsqlConnection observer = await OpenAsync();
        string session = "ankus-guc-assign-failure-" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(failing, $"SET application_name = '{session}'; SET log_error_verbosity = verbose");
        await using var command = new NpgsqlCommand("SET ankus_guc.hook_int = '66'", failing);
        NpgsqlException failure = await Assert.ThrowsAsync<NpgsqlException>(() => command.ExecuteNonQueryAsync(context.CancellationToken));
        if (failure is PostgresException error)
        {
            Assert.AreEqual("FATAL", error.InvariantSeverity);
            Assert.AreEqual("38000", error.SqlState);
            Assert.AreEqual("Assign must not fail.", error.MessageText);
        }
        else
        {
            // PostgreSQL has already flushed FATAL, but Windows can reset the closing socket before it is read.
            Assert.IsTrue(OperatingSystem.IsWindows());
            IOException transport = Assert.IsInstanceOfType<IOException>(failure.InnerException);
            SocketException socket = Assert.IsInstanceOfType<SocketException>(transport.InnerException);
            Assert.AreEqual(SocketError.ConnectionReset, socket.SocketErrorCode);
        }

        string log = string.Join('\n', PostgresFixture.Cluster.ReadServerLog().Split('\n')
            .Where(line => line.Contains($"[{session}]:", StringComparison.Ordinal)));
        Assert.Contains("FATAL:  38000: Assign must not fail.", log);
        Assert.AreEqual(System.Data.ConnectionState.Closed, failing.State);
        Assert.AreEqual(42, await ScalarAsync(observer, "SELECT 42"));
        Assert.AreEqual("integer=10;extra=0A000000", await ScalarAsync(observer, "SHOW ankus_guc.hook_int"));
    }

    /// <summary>
    /// Rejection of a boot default retains PostgreSQL's native terminal initialization policy.
    /// </summary>
    [TestMethod]
    public async Task RejectedBootDefaultTerminatesOnlyItsBackend()
    {
        await using NpgsqlConnection failing = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(failing, "SET ankus_guc.control = 'reject-boot'");
        PostgresException error = await FailureAsync(failing, "LOAD 'Ankus.TestExtension'");
        Assert.AreEqual("FATAL", error.InvariantSeverity);
        Assert.AreEqual("XX000", error.SqlState);
        Assert.AreEqual("failed to initialize ankus_guc.hook_int to 10", error.MessageText);
        Assert.Contains("Rejected boot integer.", PostgresFixture.Cluster.ReadServerLog());
        await using NpgsqlConnection recovered = await OpenAsync();
        Assert.AreEqual("integer=10;extra=0A000000", await ScalarAsync(recovered, "SHOW ankus_guc.hook_int"));
    }

    /// <summary>
    /// A check that throws on its boot default while LOAD registers the setting fails that LOAD with the managed
    /// diagnostic, as pgrx's <c>test_check_hook_fail</c> does, and the same backend can load the library again.
    /// </summary>
    [TestMethod]
    public async Task ThrowingBootCheckFailsLoadAndKeepsTheBackend()
    {
        await using NpgsqlConnection failing = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int process = failing.ProcessID;
        await ExecuteAsync(failing, "SET ankus_guc.control = 'throw-boot'");
        PostgresException error = await FailureAsync(failing, "LOAD 'Ankus.TestExtension'");
        Assert.AreEqual("ERROR", error.InvariantSeverity, error.SqlState + " " + error.MessageText + " " + error.Detail);
        Assert.AreEqual("38000", error.SqlState);
        Assert.AreEqual("Boot check failure.", error.MessageText);
        Assert.AreEqual(42, await ScalarAsync(failing, "SELECT 42"));
        await ExecuteAsync(failing, "SET ankus_guc.control = ''; LOAD 'Ankus.TestExtension'");
        Assert.AreEqual("integer=10;extra=0A000000", await ScalarAsync(failing, "SHOW ankus_guc.hook_int"));
        Assert.AreEqual(process, failing.ProcessID);
    }

    /// <summary>
    /// A parameter-report display failure follows the server's transaction boundary without a report loop.
    /// </summary>
    [TestMethod]
    public async Task ReportShowFailurePreservesNativeTransactionBoundary()
    {
        await using NpgsqlConnection failing = await OpenAsync();
        await using NpgsqlConnection observer = await OpenAsync();
        bool reportsOutsideTransaction = PostgresFixture.Cluster.Installation.Version.Major >= 14;
        int process = failing.ProcessID;
        PostgresException error = await FailureAsync(failing, "SET ankus_guc.reported = '666'");
        Assert.AreEqual(reportsOutsideTransaction ? "FATAL" : "ERROR", error.InvariantSeverity);
        Assert.AreEqual("38000", error.SqlState);
        Assert.AreEqual("Report show must not fail.", error.MessageText);
        if (!reportsOutsideTransaction)
        {
            Assert.AreEqual(process, failing.ProcessID);
            Assert.AreEqual("report=1;sql=unavailable", await ScalarAsync(failing, "SHOW ankus_guc.reported"));
            await ExecuteAsync(failing, "SET ankus_guc.reported = '2'");
            Assert.AreEqual("report=2;sql=unavailable", await ScalarAsync(failing, "SHOW ankus_guc.reported"));
            Assert.AreEqual(42, await ScalarAsync(failing, "SELECT 42"));
        }

        Assert.AreEqual(42, await ScalarAsync(observer, "SELECT 42"));
        Assert.AreEqual("report=1;sql=unavailable", await ScalarAsync(observer, "SHOW ankus_guc.reported"));
    }

    /// <summary>
    /// Native visibility, reset, byte-length, and EXPLAIN flags retain their independent meanings.
    /// </summary>
    [TestMethod]
    public async Task FlagsPreserveNativeListingResetAndIdentifierRules()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT count(*) FROM pg_settings WHERE name = 'ankus_guc.hidden'"));
        Assert.AreEqual("1", await ScalarAsync(connection, "SHOW ankus_guc.hidden"));
        await ExecuteAsync(connection, "SET ankus_guc.retained = '9'; SET \"ankus_guc.limit\" = '20'; RESET ALL");
        Assert.AreEqual("9", await ScalarAsync(connection, "SHOW ankus_guc.retained"));
        Assert.AreEqual("10", await ScalarAsync(connection, "SHOW \"ankus_guc.limit\""));
        await ExecuteAsync(connection, "RESET ankus_guc.retained");
        Assert.AreEqual("1", await ScalarAsync(connection, "SHOW ankus_guc.retained"));
        await using var command = new NpgsqlCommand("SELECT set_config('ankus_guc.identifier', $1, false)", connection);
        command.Parameters.AddWithValue(new string('é', 40));
        Assert.AreEqual(new string('é', 31), await command.ExecuteScalarAsync(context.CancellationToken));
        await ExecuteAsync(connection, "SET ankus_guc.explain = '2'");
        Assert.Contains("ankus_guc.explain", Assert.IsInstanceOfType<string>(await ScalarAsync(connection, "EXPLAIN (SETTINGS, FORMAT JSON) SELECT 1")));
        PostgresException autoFile = await FailureAsync(connection, "ALTER SYSTEM SET ankus_guc.no_auto = '2'");
        Assert.AreEqual("55P02", autoFile.SqlState);
        await using var show = new NpgsqlCommand("SHOW ALL", connection);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        await using (NpgsqlDataReader reader = await show.ExecuteReaderAsync(context.CancellationToken))
        {
            while (await reader.ReadAsync(context.CancellationToken))
            {
                listed.Add(reader.GetString(0));
            }
        }

        Assert.DoesNotContain("ankus_guc.hidden", listed);
        Assert.Contains("ankus_guc.retained", listed);
        Assert.Contains("ankus_guc.limit", listed);
    }

    /// <summary>
    /// A failed managed initializer can retry without redefining already installed native settings.
    /// </summary>
    [TestMethod]
    public async Task RegistrationSurvivesInitializerFailureAndRetry()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(connection, "SET \"ankus_guc.limit\" = '27'; SET ankus_test.initialization = 'managed-error'");
        PostgresException error = await FailureAsync(connection, "LOAD 'Ankus.TestExtension'");
        Assert.AreEqual("38000", error.SqlState);
        Assert.AreEqual("27", await ScalarAsync(connection, "SHOW \"ankus_guc.limit\""));
        await ExecuteAsync(connection, "SET \"ankus_guc.limit\" = '31'; SET ankus_test.initialization = 'default'; LOAD 'Ankus.TestExtension'");
        Assert.Contains("|31|", Assert.IsInstanceOfType<string>(await ScalarAsync(connection, "SELECT datatype.guc_values()")));
        Assert.AreEqual("2|2|default|42", await ScalarAsync(connection, "SELECT datatype.initialization_state()"));
    }

    /// <summary>
    /// Native SET privileges, visibility roles, and security-definer restrictions remain independent.
    /// </summary>
    [TestMethod]
    public async Task PrivilegesAndVisibilityUseNativeChecks()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        string role = "guc_reader_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, $"CREATE ROLE {role}");
        try
        {
            await ExecuteAsync(connection, $"SET ROLE {role}");
            await ExecuteAsync(connection, "SET \"ankus_guc.limit\" = '25'");
            PostgresException write = await FailureAsync(connection, "SET ankus_guc.privileged = '2'");
            Assert.AreEqual("42501", write.SqlState);
            Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT count(*) FROM pg_settings WHERE name = 'ankus_guc.secret'"));
            PostgresException read = await FailureAsync(connection, "SHOW ankus_guc.secret");
            Assert.AreEqual("42501", read.SqlState);
            await ExecuteAsync(connection, $"RESET ROLE; GRANT pg_read_all_settings TO {role}; SET ROLE {role}");
            Assert.AreEqual("1", await ScalarAsync(connection, "SHOW ankus_guc.secret"));
            Assert.AreEqual("42501", (await FailureAsync(connection, "SET ankus_guc.privileged = '2'")).SqlState);
            string authorize = PostgresFixture.Cluster.Installation.Version.Major >= 15
                ? $"GRANT SET ON PARAMETER ankus_guc.privileged TO {role}"
                : $"ALTER ROLE {role} SUPERUSER";
            await ExecuteAsync(connection, $"RESET ROLE; {authorize}; SET ROLE {role}");
            await ExecuteAsync(connection, "SET ankus_guc.privileged = '2'");
            Assert.AreEqual("2", await ScalarAsync(connection, "SHOW ankus_guc.privileged"));
            await ExecuteAsync(connection, "RESET ROLE; CREATE FUNCTION pg_temp.guc_security() RETURNS void LANGUAGE sql SECURITY DEFINER AS $$ SET ankus_guc.restricted = '2' $$");
            PostgresException restricted = await FailureAsync(connection, "SELECT pg_temp.guc_security()");
            Assert.AreEqual("42501", restricted.SqlState);
            await ExecuteAsync(connection, "SET ankus_guc.restricted = '3'");
            Assert.AreEqual("3", await ScalarAsync(connection, "SHOW ankus_guc.restricted"));
        }
        finally
        {
            await ExecuteAsync(connection, $"RESET ROLE; DROP OWNED BY {role}; DROP ROLE {role}");
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        try
        {
            await ExecuteAsync(connection, "LOAD 'Ankus.TestExtension'");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task AssertRejectedStateAsync(NpgsqlConnection connection, int proposed)
    {
        Assert.AreEqual("integer=20;extra=14000000", await ScalarAsync(connection, "SHOW ankus_guc.hook_int"));
        Assert.AreEqual("False|20|1.25|<null>|18446744073709551615", await ScalarAsync(connection, "SELECT datatype.guc_hook_values()"));
        Assert.AreEqual($"check:{proposed}:Session", await ScalarAsync(connection, "SELECT datatype.guc_events(true)"));
    }

    private async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(context.CancellationToken);
    }

    private async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }

    private async Task<PostgresException> FailureAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteNonQueryAsync(context.CancellationToken));
    }
}
