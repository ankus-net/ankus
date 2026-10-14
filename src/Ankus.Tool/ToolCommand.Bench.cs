using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Tool;

internal static partial class ToolCommand
{
    private static Command CreateBench(Option<string?> home)
    {
        var command = new Command("bench", "Run in-process benchmarks inside PostgreSQL.");
        AddSelectionOptions(command);
        AddBuildOptions(command);
        AddValgrindOption(command);
        var filter = new Argument<string?>("filter")
        {
            Description = "Optional ordinal substring of the managed benchmark name.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var database = new Option<string?>("--database", "--dbname") { Description = "Benchmark database (default: <extension>_benches)." };
        var group = new Option<string?>("--group-name") { Description = "Name for this benchmark run group (default: timestamp and Git commit)." };
        var compare = new Option<string?>("--compare-group") { Description = "Named comparison baseline (default: latest compatible retained group)." };
        var reset = new Option<bool>("--resetdb") { Description = "Recreate the benchmark database before running." };
        var cascade = new Option<bool>("--cascade") { Description = "Use CASCADE while refreshing the extension." };
        var list = new Option<bool>("--list") { Description = "List discovered benchmarks without measuring them." };
        var report = new Option<bool>("--report") { Description = "Render recent persisted benchmark history without rebuilding." };
        var json = new Option<bool>("--json") { Description = "Write the final benchmark summary as JSON." };
        var wait = new Option<int>("--wait") { Description = "Seconds to wait after printing the backend PID (0–3600)." };
        var noBuild = new Option<bool>("--no-build") { Description = "Use the existing benchmark publication without rebuilding." };
        var port = new Option<int?>("--port") { Description = "Port when starting a stopped server." };
        var timeout = new Option<int>("--timeout") { Description = "Startup timeout in seconds (1–600).", DefaultValueFactory = _ => 60 };
        var settings = new Option<string[]>("--postgresql-conf") { Description = "Literal name=value setting; repeat for multiple settings." };
        command.Arguments.Add(filter);
        command.Options.Add(database);
        command.Options.Add(group);
        command.Options.Add(compare);
        command.Options.Add(reset);
        command.Options.Add(cascade);
        command.Options.Add(list);
        command.Options.Add(report);
        command.Options.Add(json);
        command.Options.Add(wait);
        command.Options.Add(noBuild);
        command.Options.Add(port);
        command.Options.Add(timeout);
        command.Options.Add(settings);
        command.SetAction(async (result, token) =>
        {
            int waitSeconds = result.GetValue(wait);
            ArgumentOutOfRangeException.ThrowIfNegative(waitSeconds);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(waitSeconds, 3_600);
            if (result.GetValue(report))
            {
                string[] incompatible = ["--group-name", "--compare-group", "--resetdb", "--cascade", "--list", "--wait", "--no-build"];
                string[] selected = [.. incompatible.Where(option => result.GetResult(option) is System.CommandLine.Parsing.OptionResult { Implicit: false })];
                if (selected.Length != 0)
                {
                    throw new ArgumentException("--report cannot be combined with " + string.Join(", ", selected) + ".");
                }
            }

            var properties = new Dictionary<string, string>(BuildProperties(result), StringComparer.OrdinalIgnoreCase);
            if (properties.TryGetValue("AnkusIncludeTests", out string? includeTests) &&
                string.Equals(includeTests, "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("ankus bench cannot combine AnkusIncludeTests=true with benchmark publication.");
            }

            properties["AnkusIncludeBenchmarks"] = "true";
            ExtensionSelection selection = await SelectExtensionAsync(result, home, token, properties: properties);
            PostgresInstallation installation = selection.Installation;
            string extension = await ExtensionBuilder.GetExtensionNameAsync(selection.Project, selection.Configuration,
                installation, token, properties);
            string benchmarkDatabase = result.GetValue(database) ?? BenchmarkDatabaseName(extension);
            bool jsonOutput = result.GetValue(json);
            var cluster = new PostgresDevelopmentCluster(installation, result.GetValue(home));
            string output = Path.GetFullPath(Path.Join(Path.GetDirectoryName(selection.Project)!, "bin", "ankus-bench",
                installation.Label, RuntimeInformation.RuntimeIdentifier, selection.Configuration));
            Dictionary<string, string> serverSettings = ParseServerSettings(result.GetValue(settings) ?? []);
            if (!result.GetValue(report))
            {
                await cluster.StopAsync(token);
                if (!result.GetValue(noBuild))
                {
                    int code = await PublishAsync(selection, output, token,
                        diagnosticsToStandardError: jsonOutput, announce: !jsonOutput);
                    if (code != 0)
                    {
                        return code;
                    }
                }

                IReadOnlyList<string> installed;
                if (installation.Version.Major >= 18)
                {
                    string controlBase = StageBenchmarkExtension(output, token, out installed);
                    char separator = OperatingSystem.IsWindows() ? ';' : ':';
                    PrependSearchPath(serverSettings, "extension_control_path", controlBase, "$system", separator);
                    PrependSearchPath(serverSettings, "dynamic_library_path", output, "$libdir", separator);
                }
                else
                {
                    installed = ExtensionInstaller.Install(output, installation, null, token);
                }

                foreach (string path in installed)
                {
                    if (jsonOutput)
                    {
                        Console.Error.WriteLine($"Installed {path}");
                    }
                    else
                    {
                        Console.WriteLine($"Installed {path}");
                    }
                }
            }

            var server = new PostgresDevelopmentOptions
            {
                Port = result.GetValue(port),
                TimeoutSeconds = result.GetValue(timeout),
                UseValgrind = result.GetValue<bool>("--valgrind"),
                Settings = serverSettings,
            };
            bool reportOnly = result.GetValue(report);
            await cluster.StartAsync(server, token);
            if (result.GetValue(reset))
            {
                await cluster.DropDatabaseAsync(benchmarkDatabase, force: true, token);
            }

            if (!reportOnly)
            {
                _ = await cluster.CreateDatabaseAsync(benchmarkDatabase, token);
            }

            string connection = await cluster.GetConnectionStringAsync(benchmarkDatabase, token);
            await using BenchmarkSqlSession sql = await BenchmarkSqlSession.StartAsync(installation.PsqlPath, connection, token);
            if (reportOnly)
            {
                return await ReportBenchmarksAsync(sql, benchmarkDatabase, extension, result.GetValue(filter), jsonOutput, token);
            }

            await EnsureBenchmarkStoreAsync(sql, token);
            string cascadeClause = result.GetValue(cascade) ? " CASCADE" : string.Empty;
            try
            {
                _ = await sql.ExecuteAsync("DROP EXTENSION IF EXISTS " + QuoteIdentifier(extension) + cascadeClause + ";\n", token);
            }
            catch (InvalidOperationException error) when (cascadeClause.Length == 0)
            {
                throw new InvalidOperationException("Failed to drop extension '" + extension + "' before the benchmark refresh. " +
                    "Rerun with `ankus bench --cascade` to drop the objects that depend on it.", error);
            }

            _ = await sql.ExecuteAsync("CREATE EXTENSION " + QuoteIdentifier(extension) + cascadeClause + ";\n", token);
            List<BenchmarkDescriptor> benchmarks = await DiscoverBenchmarksAsync(sql, result.GetValue(filter), token);
            if (result.GetValue(list))
            {
                foreach (BenchmarkDescriptor benchmark in benchmarks)
                {
                    Console.WriteLine($"{benchmark.Name} [{benchmark.Transaction}; {benchmark.SampleSize} samples]");
                }

                return 0;
            }

            if (benchmarks.Count == 0)
            {
                throw new InvalidOperationException("No benchmarks were discovered in the benches schema.");
            }

            BenchmarkGitMetadata git = await CollectGitMetadataAsync(selection.Project, token);
            string groupName = result.GetValue(group) ?? await DefaultBenchmarkGroupAsync(sql, git.Commit, token);
            string? compareGroup = await ResolveBenchmarkComparisonAsync(sql, result.GetValue(compare),
                groupName, selection.Configuration, token);
            long runGroup = await InsertBenchmarkGroupAsync(sql, groupName, compareGroup, extension, selection, git, token);
            string backendPid = (await sql.ExecuteAsync("SELECT pg_backend_pid();\n", token)).Trim();
            Stream attachmentOutput = jsonOutput ? Console.OpenStandardError() : Console.OpenStandardOutput();
            byte[] attachmentAnnouncement = Encoding.UTF8.GetBytes("Benchmark backend PID: " + backendPid + Environment.NewLine);
            await attachmentOutput.WriteAsync(attachmentAnnouncement, token);
            await attachmentOutput.FlushAsync(token);

            if (waitSeconds != 0)
            {
                await Console.Error.WriteLineAsync(BenchmarkConsoleFormat.Waiting(waitSeconds));
                await Console.Error.FlushAsync(token);
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), token);
            }

            var results = new List<JsonElement>();
            int failures = 0;
            foreach (BenchmarkDescriptor benchmark in benchmarks)
            {
                if (!jsonOutput)
                {
                    Console.WriteLine(benchmark.Running);
                }

                (string? baseline, string? comparisonNote) = compareGroup is null ? (null, null) : await LoadBenchmarkBaselineAsync(
                    sql, compareGroup, benchmark.Name, token);
                string baselineSql = baseline is null ? "NULL" : TextExpression(baseline) + "::jsonb";
                string measured = (await sql.ExecuteAsync(
                    "BEGIN;\nSELECT benches." + QuoteIdentifier(benchmark.FunctionName) + "(" + baselineSql + ")::text;\nROLLBACK;\n", token)).Trim();
                using JsonDocument document = JsonDocument.Parse(measured);
                JsonElement resultJson = document.RootElement.Clone();
                results.Add(resultJson);
                bool passed = resultJson.GetProperty("status").GetString() == "ok";
                if (!passed)
                {
                    failures++;
                }

                await PersistBenchmarkAsync(sql, runGroup, benchmark.Name, measured, token);
                if (!jsonOutput)
                {
                    Console.Write(BenchmarkConsoleFormat.Format(benchmark.Name, resultJson, comparisonNote));
                }
            }

            string status = failures == 0 ? "completed" : failures == benchmarks.Count ? "failed" : "partial";
            await sql.ExecuteAsync("UPDATE ankus_bench.run_group SET status = " +
                TextExpression(status) + " WHERE id = " +
                runGroup.ToString(CultureInfo.InvariantCulture) + ";\n", token);
            List<string> missing = compareGroup is null ? [] : await LoadMissingBenchmarksAsync(sql, runGroup, compareGroup, token);
            if (jsonOutput)
            {
                WriteBenchmarkSummary(groupName, compareGroup, results, missing);
            }
            else
            {
                Console.Write(BenchmarkConsoleFormat.Summary(groupName, compareGroup, benchmarks.Count, failures, missing));
            }

            return failures == 0 ? 0 : 1;
        });
        return command;
    }

    private static string StageBenchmarkExtension(string output, CancellationToken token,
        out IReadOnlyList<string> installed)
    {
        PublishedExtension manifest = PublishedExtension.Read(output);
        string source = Path.Join(output, "extension");
        string stage = Path.Join(output, ".ankus-bench");
        string temporary = stage + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string controls = Path.Join(temporary, "extension");
        string scripts = manifest.ScriptDirectory is null ? controls : Path.Join(temporary, "scripts");
        string[] scriptNames = [manifest.Sql, .. manifest.UpgradeScripts, .. manifest.VersionControlFiles];
        string[] sourceFiles =
        [
            Path.Join(output, manifest.Library),
            Path.Join(source, manifest.Control),
            .. scriptNames.Select(name => Path.Join(source, name)),
        ];
        foreach (string path in sourceFiles)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The published extension is incomplete.", path);
            }
        }

        _ = manifest.GetScriptDirectory(output, temporary);
        IReadOnlyDictionary<string, string> authoredControl = ExtensionControlFile.Read(sourceFiles[1]);
        string? stagedControl = null;
        if (manifest.ScriptDirectory is not null)
        {
            var values = new Dictionary<string, string>(authoredControl, StringComparer.Ordinal)
            {
                ["directory"] = "scripts",
            };
            stagedControl = ExtensionControlFile.Format(values);
        }

        try
        {
            Directory.CreateDirectory(controls);
            Directory.CreateDirectory(scripts);
            var destinations = new List<string>(scriptNames.Length + 1);
            foreach (string name in scriptNames)
            {
                token.ThrowIfCancellationRequested();
                string destination = Path.Join(scripts, name);
                File.Copy(Path.Join(source, name), destination);
                destinations.Add(destination);
            }

            string control = Path.Join(controls, manifest.Control);
            if (stagedControl is null)
            {
                File.Copy(sourceFiles[1], control);
            }
            else
            {
                File.WriteAllText(control, stagedControl);
            }

            destinations.Add(control);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, recursive: true);
            }

            Directory.Move(temporary, stage);
            installed = [.. destinations.Select(path => Path.Join(stage, Path.GetRelativePath(temporary, path)))];
            return stage;
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static void PrependSearchPath(Dictionary<string, string> settings, string name,
        string path, string defaultValue, char separator)
    {
        string existing = settings.GetValueOrDefault(name, defaultValue);
        settings[name] = path + separator + existing;
    }

    private static async Task EnsureBenchmarkStoreAsync(BenchmarkSqlSession sql, CancellationToken token)
        => _ = await sql.ExecuteAsync("""
            CREATE SCHEMA IF NOT EXISTS ankus_bench;
            CREATE TABLE IF NOT EXISTS ankus_bench.run_group (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                group_name text NOT NULL UNIQUE,
                compare_group_name text,
                extension_name text NOT NULL,
                configuration text NOT NULL,
                postgres_version text NOT NULL,
                runtime_identifier text NOT NULL,
                pg_settings jsonb NOT NULL,
                created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                status text NOT NULL DEFAULT 'running'
            );
            CREATE TABLE IF NOT EXISTS ankus_bench.benchmark_run (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                group_id bigint NOT NULL REFERENCES ankus_bench.run_group(id) ON DELETE CASCADE,
                benchmark_name text NOT NULL,
                result jsonb NOT NULL
            );
            ALTER TABLE ankus_bench.run_group
                ADD COLUMN IF NOT EXISTS extension_version text,
                ADD COLUMN IF NOT EXISTS command_line text,
                ADD COLUMN IF NOT EXISTS dotnet_sdk_version text,
                ADD COLUMN IF NOT EXISTS tool_version text,
                ADD COLUMN IF NOT EXISTS git_commit text,
                ADD COLUMN IF NOT EXISTS git_branch text,
                ADD COLUMN IF NOT EXISTS git_dirty boolean NOT NULL DEFAULT false,
                ADD COLUMN IF NOT EXISTS git_describe text;
            """, token);

    private static async Task<List<BenchmarkDescriptor>> DiscoverBenchmarksAsync(
        BenchmarkSqlSession sql,
        string? filter,
        CancellationToken token)
    {
        string functions = await sql.ExecuteAsync("""
            SELECT p.proname
            FROM pg_catalog.pg_proc AS p
            JOIN pg_catalog.pg_namespace AS n ON n.oid = p.pronamespace
            WHERE n.nspname = 'benches' AND p.pronargs = 0 AND p.proname LIKE 'ankus_bench_%_describe'
            ORDER BY p.proname;
            """, token);
        var descriptors = new List<BenchmarkDescriptor>();
        foreach (string function in functions.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string json = (await sql.ExecuteAsync(
                "SELECT benches." + QuoteIdentifier(function) + "()::text;\n", token)).Trim();
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string name = root.GetProperty("bench_name").GetString()!;
            if (filter is not null && !name.Contains(filter, StringComparison.Ordinal))
            {
                continue;
            }

            descriptors.Add(new(name, root.GetProperty("function_name").GetString()!,
                root.GetProperty("transaction_mode").GetString()!, root.GetProperty("config").GetProperty("sample_size").GetInt32(),
                BenchmarkConsoleFormat.Running(root)));
        }

        return descriptors;
    }

    private static async Task<long> InsertBenchmarkGroupAsync(
        BenchmarkSqlSession sql,
        string group,
        string? compareGroup,
        string extension,
        ExtensionSelection selection,
        BenchmarkGitMetadata git,
        CancellationToken token)
    {
        string tool = typeof(BenchmarkHistoryReport).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        string? sdk = await CommandOutputAsync("dotnet", ["--version"], Path.GetDirectoryName(selection.Project), token);
        // Each setting keeps its unit, source and boot value, so reports can find the settings that differ from defaults.
        string statement = "INSERT INTO ankus_bench.run_group (group_name, compare_group_name, extension_name, extension_version, " +
            "configuration, postgres_version, runtime_identifier, pg_settings, command_line, dotnet_sdk_version, tool_version, " +
            "git_commit, git_branch, git_dirty, git_describe) SELECT " + TextExpression(group) + ", " + NullableText(compareGroup) +
            ", " + TextExpression(extension) + ", (SELECT extversion FROM pg_catalog.pg_extension WHERE extname = " +
            TextExpression(extension) + "), " + TextExpression(selection.Configuration) + ", current_setting('server_version'), " +
            TextExpression(RuntimeInformation.RuntimeIdentifier) +
            ", (SELECT jsonb_object_agg(name, jsonb_build_object('setting', setting, 'unit', unit, 'source', source, " +
            "'sourcefile', sourcefile, 'boot_val', boot_val, 'reset_val', reset_val) ORDER BY name) FROM pg_catalog.pg_settings), " +
            TextExpression(Environment.CommandLine) + ", " + NullableText(sdk) + ", " + TextExpression(tool) + ", " +
            NullableText(git.Commit) + ", " + NullableText(git.Branch) + ", " + (git.Dirty ? "true" : "false") + ", " +
            NullableText(git.Describe) + " RETURNING id;\n";
        string value = (await sql.ExecuteAsync(statement, token)).Trim();
        return long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static string NullableText(string? value) => value is null ? "NULL" : TextExpression(value);

    /// <summary>
    /// Loads the compared group's latest run of a benchmark, or the reason no comparison is possible, as
    /// <c>cargo pgrx bench</c> reports it.
    /// </summary>
    private static async Task<(string? Baseline, string? Note)> LoadBenchmarkBaselineAsync(
        BenchmarkSqlSession sql,
        string group,
        string benchmark,
        CancellationToken token)
    {
        string result = (await sql.ExecuteAsync(
            "SELECT r.result::text FROM ankus_bench.benchmark_run AS r JOIN ankus_bench.run_group AS g ON g.id = r.group_id " +
            "WHERE g.group_name = " + TextExpression(group) + " AND r.benchmark_name = " + TextExpression(benchmark) +
            " ORDER BY g.created_at DESC, r.id DESC LIMIT 1;\n", token)).Trim();
        if (result.Length == 0)
        {
            return (null, "New benchmark; no baseline comparison available.");
        }

        using JsonDocument document = JsonDocument.Parse(result);
        return document.RootElement.GetProperty("status").GetString() == "ok"
            ? (result, null)
            : (null, "Comparison unavailable because the baseline benchmark did not complete successfully.");
    }

    private static async Task PersistBenchmarkAsync(
        BenchmarkSqlSession sql,
        long group,
        string benchmark,
        string result,
        CancellationToken token)
        => _ = await sql.ExecuteAsync(
            "INSERT INTO ankus_bench.benchmark_run (group_id, benchmark_name, result) VALUES (" +
            group.ToString(CultureInfo.InvariantCulture) + ", " + TextExpression(benchmark) + ", " +
            TextExpression(result) + "::jsonb);\n", token);

    private static async Task<int> ReportBenchmarksAsync(
        BenchmarkSqlSession sql,
        string database,
        string extension,
        string? filter,
        bool json,
        CancellationToken token)
    {
        string available = (await sql.ExecuteAsync(
            "SELECT pg_catalog.to_regclass('ankus_bench.benchmark_run') IS NOT NULL " +
            "AND pg_catalog.to_regclass('ankus_bench.run_group') IS NOT NULL;\n", token)).Trim();
        if (available != "t")
        {
            throw new InvalidOperationException("No benchmark history is available in the selected database.");
        }

        string condition = filter is null ? string.Empty : " AND r.benchmark_name LIKE '%' || " + TextExpression(filter) + " || '%'";
        if (json)
        {
            string rows = await sql.ExecuteAsync(
                "SELECT jsonb_build_object('group_name', g.group_name, 'compare_group_name', g.compare_group_name, " +
                "'created_at', g.created_at, 'benchmark_name', r.benchmark_name, 'result', r.result)::text " +
                "FROM ankus_bench.benchmark_run AS r JOIN ankus_bench.run_group AS g ON g.id = r.group_id " +
                "WHERE g.extension_name = " + TextExpression(extension) + condition +
                " ORDER BY g.created_at DESC, r.id LIMIT 10;\n", token);
            string[] entries = rows.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entries.Length == 0)
            {
                throw NoHistory(filter);
            }

            Console.WriteLine("[" + string.Join(",", entries) + "]");
            return 0;
        }

        // The primary estimate is the slope when the sampling plan produced one, otherwise the mean. As in pgrx, a
        // group's settings are those whose value or unit differs from PostgreSQL's defaults; groups recorded before
        // sources were kept have none to compare.
        string history = await sql.ExecuteAsync(
            "SELECT jsonb_build_object('benchmark', r.benchmark_name, 'group_id', g.id, 'group_name', g.group_name, " +
            "'succeeded', r.result->>'status' = 'ok', 'point', COALESCE(" +
            "(SELECT (e->>'point_estimate_ns')::float8 FROM jsonb_array_elements(r.result->'estimates') AS e WHERE e->>'estimate_kind' = 'slope' LIMIT 1), " +
            "(SELECT (e->>'point_estimate_ns')::float8 FROM jsonb_array_elements(r.result->'estimates') AS e WHERE e->>'estimate_kind' = 'mean' LIMIT 1)), " +
            "'configuration', g.configuration, 'major', split_part(g.postgres_version, '.', 1), 'runtime', g.runtime_identifier, " +
            "'settings', CASE WHEN EXISTS (SELECT 1 FROM jsonb_each(g.pg_settings) AS s WHERE jsonb_typeof(s.value) = 'object') " +
            "THEN COALESCE((SELECT jsonb_object_agg(s.key, jsonb_build_object('setting', s.value->'setting', 'unit', s.value->'unit')) FROM jsonb_each(g.pg_settings) AS s " +
            "WHERE jsonb_typeof(s.value) = 'object' AND (s.value->>'source' IS DISTINCT FROM 'default' OR s.value->>'sourcefile' IS NOT NULL " +
            "OR s.value->>'setting' IS DISTINCT FROM s.value->>'boot_val')), '{}'::jsonb)::text END)::text " +
            "FROM ankus_bench.benchmark_run AS r JOIN ankus_bench.run_group AS g ON g.id = r.group_id " +
            "WHERE g.extension_name = " + TextExpression(extension) + condition +
            " ORDER BY r.benchmark_name, g.created_at, r.id;\n", token);
        List<BenchmarkHistoryRun> runs = [.. history.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseHistoryRun)];
        if (runs.Count == 0)
        {
            throw NoHistory(filter);
        }

        Console.Write(BenchmarkHistoryReport.Format(database, filter, runs));
        return 0;
    }

    /// <summary>
    /// Reads one retained run from the JSON line the history query returns.
    /// </summary>
    private static BenchmarkHistoryRun ParseHistoryRun(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        JsonElement point = root.GetProperty("point");
        JsonElement settings = root.GetProperty("settings");
        return new BenchmarkHistoryRun(root.GetProperty("benchmark").GetString()!, root.GetProperty("group_id").GetInt64(),
            root.GetProperty("group_name").GetString()!, root.GetProperty("succeeded").GetBoolean(),
            point.ValueKind == JsonValueKind.Number ? point.GetDouble() : null, root.GetProperty("configuration").GetString()!,
            root.GetProperty("major").GetString()!, root.GetProperty("runtime").GetString()!,
            settings.ValueKind == JsonValueKind.String ? settings.GetString() : null);
    }

    private static InvalidOperationException NoHistory(string? filter)
        => new(filter is null
            ? "No benchmark history is available in the selected database."
            : "No benchmark history matches '" + filter + "'.");

    private static async Task<string> DefaultBenchmarkGroupAsync(
        BenchmarkSqlSession sql,
        string? commit,
        CancellationToken token)
    {
        string suffix = commit is null ? "nogit" : commit[..Math.Min(7, commit.Length)];
        string timestamp = (await sql.ExecuteAsync(
            "SELECT to_char(clock_timestamp(), 'YYYYMMDD_HH24MISS');\n", token)).Trim();
        return timestamp + "_" + suffix;
    }

    /// <summary>
    /// Reads the project's Git state as pgrx records it, using the commit GitHub Actions is building when set.
    /// </summary>
    private static async Task<BenchmarkGitMetadata> CollectGitMetadataAsync(string project, CancellationToken token)
    {
        string? directory = Path.GetDirectoryName(project);
        string? github = Environment.GetEnvironmentVariable("GITHUB_SHA");
        string? commit = string.IsNullOrWhiteSpace(github) ? await CommandOutputAsync("git", ["rev-parse", "HEAD"], directory, token) : github;
        string? branch = await CommandOutputAsync("git", ["rev-parse", "--abbrev-ref", "HEAD"], directory, token);
        string? describe = await CommandOutputAsync("git", ["describe", "--always", "--dirty", "--tags"], directory, token);
        string? status = await CommandOutputAsync("git", ["status", "--porcelain", "--untracked-files=no"], directory, token);
        return new BenchmarkGitMetadata(commit, branch, describe, !string.IsNullOrEmpty(status));
    }

    /// <summary>
    /// Runs a command for its trimmed output, or returns null when it cannot start, fails or prints nothing.
    /// </summary>
    private static async Task<string?> CommandOutputAsync(string executable, string[] arguments, string? directory, CancellationToken token)
    {
        using var output = new MemoryStream();
        int code;
        try
        {
            code = await ToolProcess.RunAsync(executable, arguments, token, outputStream: output, workingDirectory: directory,
                errorStream: Stream.Null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        string text = Encoding.UTF8.GetString(output.ToArray()).Trim();
        return code == 0 && text.Length != 0 ? text : null;
    }

    /// <summary>
    /// The Git state recorded with a run group.
    /// </summary>
    /// <param name="Commit">The full commit hash, or null outside Git.</param>
    /// <param name="Branch">The branch name, or <c>HEAD</c> when detached.</param>
    /// <param name="Describe">The <c>git describe --always --dirty --tags</c> output.</param>
    /// <param name="Dirty">Whether tracked files have uncommitted changes.</param>
    private sealed record BenchmarkGitMetadata(string? Commit, string? Branch, string? Describe, bool Dirty);

    private static async Task<string?> ResolveBenchmarkComparisonAsync(
        BenchmarkSqlSession sql,
        string? requested,
        string currentGroup,
        string configuration,
        CancellationToken token)
    {
        string condition = requested is not null
            ? "group_name = " + TextExpression(requested)
            : "status IN ('completed', 'partial') AND configuration = " + TextExpression(configuration) +
                " AND group_name <> " + TextExpression(currentGroup);

        string resolved = (await sql.ExecuteAsync(
            "SELECT group_name FROM ankus_bench.run_group WHERE " + condition +
            " ORDER BY created_at DESC, id DESC LIMIT 1;\n", token)).Trim();
        if (requested is not null && resolved.Length == 0)
        {
            throw new InvalidOperationException("Benchmark comparison group '" + requested + "' was not found.");
        }

        return resolved.Length == 0 ? null : resolved;
    }

    /// <summary>
    /// Lists benchmarks the compared group ran that this run did not, as <c>cargo pgrx bench</c> reports them.
    /// </summary>
    private static async Task<List<string>> LoadMissingBenchmarksAsync(
        BenchmarkSqlSession sql,
        long runGroup,
        string compareGroup,
        CancellationToken token)
    {
        string names = await sql.ExecuteAsync(
            "SELECT DISTINCT baseline.benchmark_name FROM ankus_bench.benchmark_run AS baseline " +
            "WHERE baseline.group_id = (SELECT id FROM ankus_bench.run_group WHERE group_name = " + TextExpression(compareGroup) +
            " ORDER BY created_at DESC, id DESC LIMIT 1) AND NOT EXISTS (SELECT 1 FROM ankus_bench.benchmark_run AS current_run " +
            "WHERE current_run.group_id = " + runGroup.ToString(CultureInfo.InvariantCulture) +
            " AND current_run.benchmark_name = baseline.benchmark_name) ORDER BY 1;\n", token);
        return [.. names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static void WriteBenchmarkSummary(string group, string? comparison, List<JsonElement> results, List<string> missing)
    {
        using Stream output = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("group_name", group);
        if (comparison is null)
        {
            writer.WriteNull("compare_group_name");
        }
        else
        {
            writer.WriteString("compare_group_name", comparison);
        }

        writer.WriteStartArray("benchmarks");
        foreach (JsonElement result in results)
        {
            result.WriteTo(writer);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("missing_from_current");
        foreach (string name in missing)
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
    }

    private static string BenchmarkDatabaseName(string extension)
    {
        const string Suffix = "_benches";
        int available = 63 - Suffix.Length;
        string prefix = extension.Length <= available ? extension : extension[..available];
        return prefix + Suffix;
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string TextExpression(string value)
        => "convert_from(decode('" + Convert.ToHexString(Encoding.UTF8.GetBytes(value)) + "', 'hex'), 'UTF8')";

    private sealed class BenchmarkSqlSession(Process process) : IAsyncDisposable
    {
        private readonly Process _process = process;
        private readonly Task _errorPump = PumpErrorsAsync(process);
        private bool _disposed;

        internal static async Task<BenchmarkSqlSession> StartAsync(
            string executable,
            string connection,
            CancellationToken token)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            string[] arguments =
            [
                "--no-psqlrc", "--no-password", "--quiet", "--tuples-only", "--no-align", "--set=ON_ERROR_STOP=1",
                "--dbname=" + connection,
            ];
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (string key in startInfo.Environment.Keys
                .Where(static key => key.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                startInfo.Environment.Remove(key);
            }

            startInfo.Environment["PGCLIENTENCODING"] = "UTF8";
            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the PostgreSQL benchmark session.");
            var session = new BenchmarkSqlSession(process);
            try
            {
                _ = await session.ExecuteAsync("SET client_min_messages = warning;\n", token);
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        internal async Task<string> ExecuteAsync(string command, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string marker = "__ankus_benchmark_" + Guid.NewGuid().ToString("N");
            try
            {
                await _process.StandardInput.WriteAsync(command.AsMemory(), token);
                if (!command.EndsWith('\n'))
                {
                    await _process.StandardInput.WriteAsync("\n".AsMemory(), token);
                }

                await _process.StandardInput.WriteLineAsync(("\\echo " + marker).AsMemory(), token);
                await _process.StandardInput.FlushAsync(token);
                var output = new StringBuilder();
                while (await _process.StandardOutput.ReadLineAsync(token) is string line)
                {
                    if (line == marker)
                    {
                        return output.ToString().ReplaceLineEndings("\n");
                    }

                    output.AppendLine(line);
                }
            }
            catch (OperationCanceledException)
            {
                Stop();
                await _process.WaitForExitAsync(CancellationToken.None);
                await _errorPump;
                throw;
            }

            await _process.WaitForExitAsync(CancellationToken.None);
            await _errorPump;
            throw new InvalidOperationException("PostgreSQL benchmark session failed with exit code " +
                _process.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
            }

            await _process.WaitForExitAsync(CancellationToken.None);
            await _errorPump;
            _process.Dispose();
        }

        private static async Task PumpErrorsAsync(Process process)
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None) is string line)
            {
                await Console.Error.WriteLineAsync(line);
            }
        }

        private void Stop()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (_process.HasExited)
            {
                // The client exited while cancellation was delivered.
            }
        }
    }

    private sealed record BenchmarkDescriptor(string Name, string FunctionName, string Transaction, int SampleSize, string Running);
}
