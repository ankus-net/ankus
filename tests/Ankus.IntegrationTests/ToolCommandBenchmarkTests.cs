using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Publishes benchmark-only wrappers, measures inside PostgreSQL, rolls author writes back, and retains history.
    /// </summary>
    [RetryPortCollisionTestMethod]
    [DoNotParallelize]
    public async Task BenchRunsInBackendAndPersistsResultsOutsideMeasurementTransaction()
    {
        CancellationToken token = context.CancellationToken;
        const string InsertBenchmark = "Ankus.Examples.Hello.BenchmarkProbe.SuccessInsertRow(Ankus.PgBencher)";
        const string AddNumericBenchmark = "Ankus.Examples.Hello.BenchmarkProbe.SuccessAddNumeric(Ankus.PgBencher)";
        string installationSql = await File.ReadAllTextAsync(
            Directory.GetFiles(Path.Join(s_published, "extension"), "ankus_tool_probe--*.sql").Single(), token);
        Assert.DoesNotContain("CREATE SCHEMA benches", installationSql);
        Assert.DoesNotContain("ankus_bench_", installationSql);

        const string Group = "integration-benchmark";
        PostgresInstallation installation = await ReserveCaseInstallationAsync(token);
        string benchmarkDirectory = CreateDirectory();
        string benchmarkProject = Path.Join(benchmarkDirectory, "BenchmarkProbe.csproj");
        XDocument definition = XDocument.Load(s_project);
        definition.Root!.Add(new XElement("PropertyGroup",
            new XElement("AnkusPostgresMajor", new XAttribute("Condition",
                "'$(AnkusIncludeBenchmarks)' == 'true' and '$(BenchmarkProbe)' == 'enabled'"), MajorText()),
            new XElement("AnkusPgConfigPath", new XAttribute("Condition",
                "'$(AnkusIncludeBenchmarks)' == 'true' and '$(BenchmarkProbe)' == 'enabled'"), installation.PgConfigPath),
            new XElement("AnkusPostgresMajor", new XAttribute("Condition",
                "'$(AnkusIncludeBenchmarks)' != 'true' or '$(BenchmarkProbe)' != 'enabled'"), DifferentMajor()),
            new XElement("AnkusPgConfigPath", new XAttribute("Condition",
                "'$(AnkusIncludeBenchmarks)' != 'true' or '$(BenchmarkProbe)' != 'enabled'"), string.Empty)));
        definition.Save(benchmarkProject);
        File.Copy(Path.Join(Path.GetDirectoryName(s_project)!, "BenchmarkProbe.cs"),
            Path.Join(benchmarkDirectory, "BenchmarkProbe.cs"));
        // The default development port can fall inside an operating-system exclusion, such as a Windows Hyper-V range.
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        string[] options = ["--home", s_home, "--project", benchmarkProject, "--configuration", "Release",
            "--property", "BenchmarkProbe=enabled", "--port", port.ToString(CultureInfo.InvariantCulture)];
        var cluster = new PostgresDevelopmentCluster(installation, s_home);
        try
        {
            ProcessResult run = await InvokeAsync(
                ["bench", "Success", .. options, "--group-name", Group, "--resetdb"], token);
            Assert.AreEqual(0, run.ExitCode, run.StandardOutput + run.StandardError);
            Assert.Contains("     Running " + InsertBenchmark + " [transaction=", run.StandardOutput);
            Assert.Contains("Bench group " + Group + Environment.NewLine, run.StandardOutput);
            Assert.MatchesRegex(@"    Result (\d+) total, \1 ok, 0 failed", run.StandardOutput);
            Assert.Contains(new string(' ', 28) + "time:   [", run.StandardOutput);
            Assert.MatchesRegex(@"\n {28}time:   \[[^\n]*\r?\n {28}thrpt:  \[\d+\.\d+ [KMG]?elem/s \d+\.\d+ [KMG]?elem/s \d+\.\d+ [KMG]?elem/s\]",
                run.StandardOutput);
            Assert.Contains(new string(' ', 28) + "mean:   [", run.StandardOutput);
            Assert.Contains("] std. dev. [", run.StandardOutput);
            Assert.Contains(new string(' ', 28) + "median: [", run.StandardOutput);
            Assert.Contains("] med. abs. dev. [", run.StandardOutput);
            Assert.IsEmpty(run.StandardError);
            if (s_installation.Version.Major >= 18)
            {
                string benchmarkOutput = Path.Join(benchmarkDirectory, "bin", "ankus-bench",
                    s_installation.Label, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "Release");
                string stagedControl = Path.Join(benchmarkOutput, ".ankus-bench", "extension", "ankus_tool_probe.control");
                Assert.IsTrue(File.Exists(stagedControl), stagedControl);
                Assert.Contains(Path.GetDirectoryName(stagedControl)!, run.StandardOutput);
                Assert.DoesNotContain(s_installation.LibraryDirectory, run.StandardOutput);
            }

            string connectionString = await cluster.GetConnectionStringAsync("ankus_tool_probe_benches", token);
            await using (Npgsql.NpgsqlConnection connection = BenchmarkConnection(connectionString, "ankus_tool_probe_benches"))
            {
                await connection.OpenAsync(token);
                await using var command = new Npgsql.NpgsqlCommand(
                    "SELECT pg_catalog.to_regclass('public.ankus_benchmark_probe') IS NULL", connection);
                Assert.IsTrue((bool)(await command.ExecuteScalarAsync(token))!);
            }

            const string EmptyDatabase = "ankus_tool_probe_empty_benches";
            await cluster.DropDatabaseAsync(EmptyDatabase, force: true, token);
            _ = await cluster.CreateDatabaseAsync(EmptyDatabase, token);
            try
            {
                ProcessResult emptyReport = await InvokeAsync(
                    ["bench", .. options, "--database", EmptyDatabase, "--report"], token);
                Assert.AreEqual(1, emptyReport.ExitCode, emptyReport.StandardOutput + emptyReport.StandardError);
                Assert.Contains("No benchmark history is available", emptyReport.StandardError);
                string emptyConnection = await cluster.GetConnectionStringAsync(EmptyDatabase, token);
                await using Npgsql.NpgsqlConnection empty = BenchmarkConnection(emptyConnection, EmptyDatabase);
                await empty.OpenAsync(token);
                await using var schema = new Npgsql.NpgsqlCommand(
                    "SELECT pg_catalog.to_regnamespace('ankus_bench') IS NULL", empty);
                Assert.IsTrue((bool)(await schema.ExecuteScalarAsync(token))!);
            }
            finally
            {
                await cluster.DropDatabaseAsync(EmptyDatabase, force: true, CancellationToken.None);
            }

            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo(s_tool)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = s_root,
                },
            })
            {
                string[] arguments = ["bench", "SuccessAddNumeric", .. options, "--group-name", "persistent-session",
                    "--no-build", "--wait", "10"];
                foreach (string argument in arguments)
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }

                foreach ((string name, string? value) in s_environment)
                {
                    process.StartInfo.Environment[name] = value;
                }

                Assert.IsTrue(process.Start());
                Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(token);
                Task<string> standardError = process.StandardError.ReadToEndAsync(token);
                try
                {
                    string observerConnection = await cluster.GetConnectionStringAsync("ankus_tool_probe_benches", token);
                    int backendPid = await WaitForBenchmarkBackendAsync(observerConnection, process, token);
                    Assert.IsFalse(process.HasExited, "The benchmark backend was observed only after the tool exited.");
                    await process.WaitForExitAsync(token);
                    string output = await standardOutput;
                    Assert.AreEqual(0, process.ExitCode, output + await standardError);
                    Assert.Contains("Benchmark backend PID: " + backendPid.ToString(CultureInfo.InvariantCulture), output);
                    Assert.Contains("     Waiting 10 seconds before starting benchmarks" + Environment.NewLine, await standardError);
                    Assert.Contains(new string(' ', 28) + "change: [", output);
                    Assert.Contains("   Compared " + Group + Environment.NewLine, output);
                    Assert.Contains("Missing From Current Run" + Environment.NewLine + "  " + InsertBenchmark, output);
                    Assert.MatchesRegex(@"\] \(p = \d\.\d\d [<>] \d\.\d\d\)", output);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
            }

            ProcessResult report = await InvokeAsync(
                ["bench", .. options, "--report", "--json"], token);
            Assert.AreEqual(0, report.ExitCode, report.StandardOutput + report.StandardError);
            using JsonDocument history = JsonDocument.Parse(report.StandardOutput);
            JsonElement entry = history.RootElement.EnumerateArray().Single(value =>
                value.GetProperty("benchmark_name").GetString() == InsertBenchmark);
            Assert.AreEqual(Group, entry.GetProperty("group_name").GetString());
            Assert.AreEqual(InsertBenchmark, entry.GetProperty("benchmark_name").GetString());
            JsonElement result = entry.GetProperty("result");
            Assert.AreEqual("ok", result.GetProperty("status").GetString());
            Assert.IsGreaterThan(1, result.GetProperty("source_line").GetInt32());
            Assert.AreEqual(10, result.GetProperty("samples").GetArrayLength());
            Assert.IsNull(result.GetProperty("error_text").GetString());
            string? samplingMode = result.GetProperty("sampling_mode").GetString();
            Assert.IsTrue(samplingMode is "linear" or "flat");
            JsonElement[] estimates = [.. result.GetProperty("estimates").EnumerateArray()];
            JsonElement primary = estimates.FirstOrDefault(static value =>
                value.GetProperty("estimate_kind").GetString() == "slope");
            if (primary.ValueKind == JsonValueKind.Undefined)
            {
                primary = estimates.First(static value => value.GetProperty("estimate_kind").GetString() == "mean");
            }

            Assert.Contains(Environment.NewLine + InsertBenchmark + Environment.NewLine +
                new string(' ', 28) + "time:   [" + BenchmarkTime(primary.GetProperty("ci_lower_bound_ns").GetDouble()) + " " +
                BenchmarkTime(primary.GetProperty("point_estimate_ns").GetDouble()) + " ", run.StandardOutput);
            Assert.HasCount(3, history.RootElement.EnumerateArray());
            JsonElement automatic = history.RootElement.EnumerateArray().Single(value =>
                value.GetProperty("group_name").GetString() == "persistent-session");
            Assert.AreEqual(Group, automatic.GetProperty("compare_group_name").GetString());
            Assert.AreEqual(JsonValueKind.Object,
                automatic.GetProperty("result").GetProperty("comparison").ValueKind);

            ProcessResult comparisonRun = await InvokeAsync(
                ["bench", "SuccessAddNumeric", .. options, "--group-name", "comparison", "--compare-group", Group,
                    "--no-build", "--json"], token);
            Assert.AreEqual(0, comparisonRun.ExitCode, comparisonRun.StandardOutput + comparisonRun.StandardError);
            using JsonDocument comparisonOutput = JsonDocument.Parse(comparisonRun.StandardOutput);
            JsonElement benchmark = Assert.ContainsSingle(comparisonOutput.RootElement.GetProperty("benchmarks").EnumerateArray());
            JsonElement comparison = benchmark.GetProperty("comparison");
            Assert.IsTrue(double.IsFinite(comparison.GetProperty("p_value").GetDouble()));
            Assert.AreEqual(0.95, comparison.GetProperty("mean").GetProperty("confidence_level").GetDouble(), 0.000_001);
            Assert.Contains(InsertBenchmark, comparisonOutput.RootElement.GetProperty("missing_from_current").EnumerateArray()
                .Select(static name => name.GetString()));

            ProcessResult newBenchmarkRun = await InvokeAsync(
                ["bench", "SuccessInsertRow", .. options, "--group-name", "new-benchmark", "--compare-group", "comparison", "--no-build"], token);
            Assert.AreEqual(0, newBenchmarkRun.ExitCode, newBenchmarkRun.StandardOutput + newBenchmarkRun.StandardError);
            Assert.Contains(new string(' ', 28) + "New benchmark; no baseline comparison available.", newBenchmarkRun.StandardOutput);
            Assert.Contains("Missing From Current Run" + Environment.NewLine + "  " + AddNumericBenchmark, newBenchmarkRun.StandardOutput);

            ProcessResult failureRun = await InvokeAsync(
                ["bench", "Failure", .. options, "--group-name", "failure", "--no-build", "--json"], token);
            Assert.AreEqual(1, failureRun.ExitCode, failureRun.StandardOutput + failureRun.StandardError);
            using JsonDocument failureOutput = JsonDocument.Parse(failureRun.StandardOutput);
            JsonElement failed = Assert.ContainsSingle(failureOutput.RootElement.GetProperty("benchmarks").EnumerateArray());
            Assert.AreEqual("failed", failed.GetProperty("status").GetString());
            string? errorText = failed.GetProperty("error_text").GetString();
            Assert.IsNotNull(errorText);
            Assert.Contains("division by zero", errorText);

            // Automatic comparison skips failed groups and groups from another build configuration. Marking a group as
            // Debug stands in for publishing a Debug benchmark build. A database-level setting and --postgresql-conf
            // are non-default settings, so the next run is marked as drifted.
            await using (Npgsql.NpgsqlConnection connection = BenchmarkConnection(connectionString, "ankus_tool_probe_benches"))
            {
                await connection.OpenAsync(token);
                await using var command = new Npgsql.NpgsqlCommand("""
                    SELECT string_agg(group_name || '=' || status, ',' ORDER BY id) FROM ankus_bench.run_group;
                    UPDATE ankus_bench.run_group SET configuration = 'Debug' WHERE group_name = 'new-benchmark';
                    ALTER DATABASE ankus_tool_probe_benches SET work_mem = '8MB';
                    """, connection);
                Assert.AreEqual(Group + "=completed,persistent-session=completed,comparison=completed,new-benchmark=completed,failure=failed",
                    await command.ExecuteScalarAsync(token));
            }

            // Without --group-name, the group is named for the time and the project's commit. The run also records
            // the branch, describe output and whether tracked files have uncommitted changes.
            var git = new Dictionary<string, string?>(s_environment) { ["GITHUB_SHA"] = null };
            string commit;
            ProcessResult tunedRun;
            try
            {
                string[] identity = ["-c", "user.name=Ankus", "-c", "user.email=ankus@example.invalid", "-c", "commit.gpgsign=false"];
                await File.WriteAllTextAsync(Path.Join(benchmarkDirectory, "notes.txt"), "first", token);
                (await PackageProcessRunner.RunAsync("git", ["init", "-q", "-b", "main"], git, token, workingDirectory: benchmarkDirectory))
                    .EnsureSuccess("git", ["init"]);
                (await PackageProcessRunner.RunAsync("git", ["add", "notes.txt"], git, token, workingDirectory: benchmarkDirectory))
                    .EnsureSuccess("git", ["add"]);
                (await PackageProcessRunner.RunAsync("git", [.. identity, "commit", "-q", "--no-verify", "-m", "Benchmark history"], git, token,
                    workingDirectory: benchmarkDirectory)).EnsureSuccess("git", ["commit"]);
                ProcessResult head = await PackageProcessRunner.RunAsync("git", ["rev-parse", "HEAD"], git, token, workingDirectory: benchmarkDirectory);
                head.EnsureSuccess("git", ["rev-parse"]);
                commit = head.StandardOutput.Trim();
                await File.WriteAllTextAsync(Path.Join(benchmarkDirectory, "notes.txt"), "changed", token);
                tunedRun = await PackageProcessRunner.RunAsync(s_tool,
                    ["bench", "SuccessAddNumeric", .. options, "--no-build", "--postgresql-conf", "random_page_cost=1.5"],
                    git, token, workingDirectory: s_root);
            }
            finally
            {
                DeleteGitDirectory(benchmarkDirectory);
            }

            Assert.AreEqual(0, tunedRun.ExitCode, tunedRun.StandardOutput + tunedRun.StandardError);
            Assert.Contains("   Compared comparison" + Environment.NewLine, tunedRun.StandardOutput);
            Match named = DefaultBenchmarkGroup().Match(tunedRun.StandardOutput);
            Assert.IsTrue(named.Success, tunedRun.StandardOutput);
            Assert.AreEqual(commit[..7], named.Groups["commit"].Value);
            string tuned = named.Groups[1].Value;
            ProcessResult textReport = await InvokeAsync(["bench", .. options, "--report"], token);
            Assert.AreEqual(0, textReport.ExitCode, textReport.StandardOutput + textReport.StandardError);
            string reportText = textReport.StandardOutput;
            string newLine = Environment.NewLine;
            Assert.StartsWith("Bench history report" + newLine + "  Database ankus_tool_probe_benches" + newLine +
                "     Scope last 10 groups per benchmark" + newLine + newLine, reportText);
            const string FailureBenchmark = "Ankus.Examples.Hello.BenchmarkProbe.Failure(Ankus.PgBencher)";
            Assert.Contains(newLine + FailureBenchmark + newLine +
                "  no successful baseline has been recorded for this benchmark yet" + newLine +
                "  no successful runs to display" + newLine + "  1 failed run omitted from the last 10 groups" + newLine, reportText);
            Assert.Contains(newLine + AddNumericBenchmark + newLine + "  baseline: " + Group + " (", reportText);
            Assert.MatchesRegex(@"\n  integration-benchmark +\|#+ *\| \d+\.\d+ (ps|ns|us|ms|s) \(baseline\)\r?\n", reportText);
            Assert.MatchesRegex(@"\n  comparison +\|#+ *\| \d+\.\d+ (ps|ns|us|ms|s) \([+-]\d+\.\d+%\)\r?\n", reportText);
            Assert.MatchesRegex(@"\n  " + tuned + @"\* +\|#+ *\| \d+\.\d+ (ps|ns|us|ms|s) \([+-]\d+\.\d+%\)\r?\n" +
                @"  \* broad drift vs baseline in: pg_settings\r?\n", reportText);
            Assert.MatchesRegex(@"\n  new-benchmark\* +\|#+ *\| \d+\.\d+ (ps|ns|us|ms|s) \([+-]\d+\.\d+%\)\r?\n" +
                @"  \* broad drift vs baseline in: configuration\r?\n", reportText);
            ProcessResult filteredReport = await InvokeAsync(["bench", "Failure", .. options, "--report"], token);
            Assert.AreEqual(0, filteredReport.ExitCode, filteredReport.StandardOutput + filteredReport.StandardError);
            Assert.Contains("    Filter Failure" + newLine + newLine + FailureBenchmark + newLine, filteredReport.StandardOutput);
            Assert.DoesNotContain(AddNumericBenchmark, filteredReport.StandardOutput);
            await using (Npgsql.NpgsqlConnection connection = BenchmarkConnection(connectionString, "ankus_tool_probe_benches"))
            {
                await connection.OpenAsync(token);
                await using var command = new Npgsql.NpgsqlCommand("""
                    SELECT concat_ws('|', git_commit, git_branch, git_dirty, git_describe, extension_version,
                        tool_version IS NOT NULL, dotnet_sdk_version IS NOT NULL,
                        command_line LIKE '%bench%SuccessAddNumeric%', pg_settings->'work_mem'->>'setting',
                        pg_settings->'work_mem'->>'unit', pg_settings->'work_mem'->>'source', pg_settings->'work_mem'->>'boot_val',
                        pg_settings->'random_page_cost'->>'setting', pg_settings->'random_page_cost'->>'source')
                    FROM ankus_bench.run_group WHERE group_name = $1
                    """, connection);
                command.Parameters.AddWithValue(tuned);
                Assert.AreEqual(commit + "|main|t|" + commit[..7] + "-dirty|0.1.0|t|t|t|8192|kB|database|4096|1.5|configuration file",
                    await command.ExecuteScalarAsync(token));
                await using var dependency = new Npgsql.NpgsqlCommand("""
                    DO $$
                    BEGIN
                        EXECUTE format('CREATE VIEW public.benchmark_dependency AS SELECT benches.%I() AS descriptor',
                            (SELECT p.proname FROM pg_catalog.pg_proc AS p JOIN pg_catalog.pg_namespace AS n ON n.oid = p.pronamespace
                             WHERE n.nspname = 'benches' AND p.proname LIKE 'ankus_bench_%_describe' ORDER BY 1 LIMIT 1));
                    END
                    $$
                    """, connection);
                _ = await dependency.ExecuteNonQueryAsync(token);
            }

            // A dependent object blocks the refresh unless --cascade drops it. A group where some benchmarks fail is
            // partial, and --resetdb discards retained history.
            ProcessResult blocked = await InvokeAsync(["bench", "SuccessAddNumeric", .. options, "--group-name", "blocked", "--no-build"], token);
            Assert.AreNotEqual(0, blocked.ExitCode);
            Assert.Contains("Rerun with `ankus bench --cascade` to drop the objects that depend on it.", blocked.StandardError);
            ProcessResult cascaded = await InvokeAsync(["bench", .. options, "--group-name", "cascaded", "--cascade", "--no-build"], token);
            Assert.AreEqual(1, cascaded.ExitCode, cascaded.StandardOutput + cascaded.StandardError);
            Assert.MatchesRegex(@"    Result 3 total, 2 ok, 1 failed", cascaded.StandardOutput);
            await using (Npgsql.NpgsqlConnection connection = BenchmarkConnection(connectionString, "ankus_tool_probe_benches"))
            {
                await connection.OpenAsync(token);
                await using var command = new Npgsql.NpgsqlCommand("""
                    SELECT concat_ws('|', pg_catalog.to_regclass('public.benchmark_dependency') IS NULL,
                        (SELECT string_agg(status, ',') FROM ankus_bench.run_group WHERE group_name IN ('blocked', 'cascaded')))
                    """, connection);
                Assert.AreEqual("t|partial", await command.ExecuteScalarAsync(token));
            }

            ProcessResult reset = await InvokeAsync(
                ["bench", "SuccessAddNumeric", .. options, "--group-name", "fresh", "--resetdb", "--no-build"], token);
            Assert.AreEqual(0, reset.ExitCode, reset.StandardOutput + reset.StandardError);
            Assert.DoesNotContain("   Compared ", reset.StandardOutput);
            ProcessResult resetReport = await InvokeAsync(["bench", .. options, "--report", "--json"], token);
            Assert.AreEqual(0, resetReport.ExitCode, resetReport.StandardOutput + resetReport.StandardError);
            using (JsonDocument retained = JsonDocument.Parse(resetReport.StandardOutput))
            {
                JsonElement only = Assert.ContainsSingle(retained.RootElement.EnumerateArray());
                Assert.AreEqual("fresh", only.GetProperty("group_name").GetString());
            }

            ProcessResult listed = await InvokeAsync(["bench", "Success", .. options, "--no-build", "--list"], token);
            Assert.AreEqual(0, listed.ExitCode, listed.StandardOutput + listed.StandardError);
            Assert.Contains(InsertBenchmark + " [", listed.StandardOutput);
            Assert.Contains(AddNumericBenchmark + " [", listed.StandardOutput);
            Assert.DoesNotContain("Running", listed.StandardOutput);
            Assert.DoesNotContain("Failure", listed.StandardOutput);
            ProcessResult unknown = await InvokeAsync(["bench", "SuccessAddNumeric", .. options, "--no-build",
                "--group-name", "unknown-comparison", "--compare-group", "no-such-group"], token);
            Assert.AreNotEqual(0, unknown.ExitCode);
            Assert.Contains("Benchmark comparison group 'no-such-group' was not found.", unknown.StandardOutput + unknown.StandardError);
            ProcessResult none = await InvokeAsync(["bench", "NoSuchBenchmark", .. options, "--no-build"], token);
            Assert.AreNotEqual(0, none.ExitCode);
            Assert.Contains("No benchmarks were discovered in the benches schema.", none.StandardOutput + none.StandardError);
            ProcessResult combined = await InvokeAsync(["bench", .. options, "--report", "--list"], token);
            Assert.AreNotEqual(0, combined.ExitCode);
            Assert.Contains("--report cannot be combined with --list.", combined.StandardOutput + combined.StandardError);
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Removes a test's Git repository, whose object files Git marks read-only, which Windows will not delete.
    /// </summary>
    private static void DeleteGitDirectory(string workTree)
    {
        string repository = Path.Join(workTree, ".git");
        if (!Directory.Exists(repository))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(repository, recursive: true);
    }

    [GeneratedRegex(@"Bench group (\d{8}_\d{6}_(?<commit>[0-9a-f]{7}))\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex DefaultBenchmarkGroup();

    private static Npgsql.NpgsqlConnection BenchmarkConnection(
        string libpqConnection,
        string database,
        string? applicationName = null,
        int timeout = 15)
    {
        var endpoint = new Uri(libpqConnection);
        return new(new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            Username = "postgres",
            Database = database,
            ApplicationName = applicationName,
            Pooling = false,
            Timeout = timeout,
            CommandTimeout = timeout,
        }.ConnectionString);
    }

    private static async Task<int> WaitForBenchmarkBackendAsync(
        string connectionString,
        Process process,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!process.HasExited)
        {
            try
            {
                await using Npgsql.NpgsqlConnection observer = BenchmarkConnection(connectionString,
                    "ankus_tool_probe_benches", "ankus benchmark integration observer", timeout: 1);
                await observer.OpenAsync(timeout.Token);
                await using var active = new Npgsql.NpgsqlCommand(
                    "SELECT pid FROM pg_catalog.pg_stat_activity WHERE datname = current_database() " +
                    "AND application_name = 'psql' ORDER BY backend_start DESC LIMIT 1", observer);
                if (await active.ExecuteScalarAsync(timeout.Token) is int backendPid)
                {
                    return backendPid;
                }
            }
            catch (Exception error) when (error is Npgsql.NpgsqlException or System.Net.Sockets.SocketException or IOException)
            {
                // The tool restarts the cluster before its run; a connection racing that restart can fail in the
                // socket layer, which Npgsql does not always wrap. macOS reports it as an invalid socket option.
            }

            await Task.Delay(100, timeout.Token);
        }

        throw new InvalidOperationException("The benchmark tool exited before its backend could be observed.");
    }

    /// <summary>
    /// Formats a duration independently of the tool, with the unit and digits that cargo pgrx bench prints.
    /// </summary>
    private static string BenchmarkTime(double nanoseconds)
    {
        (double value, string unit) = nanoseconds >= 1e9 ? (nanoseconds / 1e9, "s") : nanoseconds >= 1e6 ? (nanoseconds / 1e6, "ms")
            : nanoseconds >= 1e3 ? (nanoseconds / 1e3, "us") : nanoseconds >= 1 ? (nanoseconds, "ns") : (nanoseconds * 1e3, "ps");
        string text = value.ToString(value >= 100 ? "0.00" : value >= 10 ? "0.000" : "0.0000", CultureInfo.InvariantCulture).TrimEnd('0');
        return (text.EndsWith('.') ? text + "0" : text) + " " + unit;
    }
}
