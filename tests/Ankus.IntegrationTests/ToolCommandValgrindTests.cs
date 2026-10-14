using System.Diagnostics;
using System.Globalization;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Installed run, connect, start and regress commands execute native extension SQL under actual Memcheck processes.
    /// </summary>
    /// <param name="configuredRange">The caller's optional GC region range.</param>
    /// <param name="expectedRange">The actual instrumented runtime range in bytes.</param>
    [RetryPortCollisionTestMethod]
    [DataRow(null, 34359738368L)]
    [DataRow("", 34359738368L)]
    [DataRow("200000000", 8589934592L)]
    [OSCondition(OperatingSystems.Linux)]
    public async Task ValgrindCommandsExecuteNativeExtensionAndPreserveData(string? configuredRange, long expectedRange)
    {
        CancellationToken token = context.CancellationToken;
        string? parentRange = Environment.GetEnvironmentVariable("DOTNET_GCRegionRange");
        var environment = new Dictionary<string, string?>(s_environment) { ["DOTNET_GCRegionRange"] = configuredRange };
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation, CreateDirectory(), token);
        string home = CreateDirectory();
        string project = PrepareRegressionProject();
        string suite = Path.Join(Path.GetDirectoryName(project)!, "pg_regress");
        await WriteRegressionCaseAsync(suite, "native", "SELECT add(19, 23);", "42\n", token);
        var cluster = new PostgresDevelopmentCluster(owner.Installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        string[] selection = ["--home", home, "--pg", MajorText(), "--pg-config", owner.Installation.PgConfigPath,
            "--port", port.ToString(CultureInfo.InvariantCulture), "--valgrind"];
        try
        {
            // Prepare catalog and WAL writes before instrumentation. PostgreSQL documents
            // uninitialized WAL padding in src/tools/valgrind.supp. The query phases
            // below isolate Native AOT execution; all native diagnostics remain visible.
            ProcessResult installed = await InvokeValgrindCommandAsync(["run", .. selection.Where(static item => item != "--valgrind"),
                "--project", project, "--no-build", "--install-only"], environment, token);
            Assert.AreEqual(0, installed.ExitCode, installed.StandardOutput + installed.StandardError);
            ProcessResult ordinary = await InvokeValgrindCommandAsync(["start", .. selection.Where(static item => item != "--valgrind")], environment, token);
            Assert.AreEqual(0, ordinary.ExitCode, ordinary.StandardOutput + ordinary.StandardError);
            Assert.IsTrue(await cluster.CreateDatabaseAsync("ankus_tool_probe", token));
            await using (NpgsqlConnection seed = await OpenRegressionConnectionAsync(port, "ankus_tool_probe", token))
            {
                await using var setup = new NpgsqlCommand("CREATE EXTENSION ankus_tool_probe; CREATE TABLE retained AS SELECT 42 AS value", seed);
                await setup.ExecuteNonQueryAsync(token);
                await using var setting = new NpgsqlCommand("SELECT tool_gc_region_setting()", seed);
                Assert.AreEqual((object?)configuredRange ?? DBNull.Value, await setting.ExecuteScalarAsync(token));
            }

            Assert.IsTrue(await cluster.StopAsync(token));
            File.Delete(cluster.LogFilePath);
            ProcessResult run = await InvokeValgrindCommandAsync(["run", .. selection, "--project", project, "--no-build", "--", "-X", "-A", "-t",
                "-v", "ON_ERROR_STOP=1", "-c", "SELECT add(value, 0)::text || ':' || tool_gc_region_range()::text FROM retained"], environment, token);
            Assert.AreEqual(0, run.ExitCode, run.StandardOutput + run.StandardError);
            Assert.EndsWith("42:" + expectedRange.ToString(CultureInfo.InvariantCulture) + Environment.NewLine, run.StandardOutput);
            Assert.IsTrue(await cluster.StopAsync(token));
            await AssertValgrindLogAsync(cluster.LogFilePath, token);
            File.Delete(cluster.LogFilePath);

            ProcessResult connect = await InvokeValgrindCommandAsync(["connect", .. selection, "--database", "ankus_tool_probe", "--",
                "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-c", "SELECT add(value, 1)::text || ':' || tool_gc_region_range()::text FROM retained"], environment, token);
            Assert.AreEqual(0, connect.ExitCode, connect.StandardOutput + connect.StandardError);
            Assert.EndsWith("43:" + expectedRange.ToString(CultureInfo.InvariantCulture) + Environment.NewLine, connect.StandardOutput);
            Assert.IsTrue(await cluster.StopAsync(token));
            await AssertValgrindLogAsync(cluster.LogFilePath, token);
            File.Delete(cluster.LogFilePath);

            ProcessResult start = await InvokeValgrindCommandAsync(["start", .. selection], environment, token);
            Assert.AreEqual(0, start.ExitCode, start.StandardOutput + start.StandardError);
            Assert.IsTrue(await cluster.IsRunningAsync(token));
            Assert.IsTrue(await cluster.StopAsync(token));
            await AssertValgrindLogAsync(cluster.LogFilePath, token);
            File.Delete(cluster.LogFilePath);

            ProcessResult regress = await InvokeValgrindCommandAsync([.. RegressionOptions(owner.Installation, home, project, port, "ankus_tool_probe"), "--valgrind", "--no-build"], environment, token);
            Assert.AreEqual(0, regress.ExitCode, regress.StandardOutput + regress.StandardError);
            Assert.AreEqual("\\set ECHO none\n42\n", (await File.ReadAllTextAsync(Path.Join(suite, "results", "native.out"), token)).ReplaceLineEndings("\n"));
            Assert.IsTrue(await cluster.StopAsync(token));
            await AssertValgrindLogAsync(cluster.LogFilePath, token);
            Assert.AreEqual(parentRange, Environment.GetEnvironmentVariable("DOTNET_GCRegionRange"));
        }
        finally
        {
            try
            {
                await cluster.StopAsync(CancellationToken.None);
            }
            finally
            {
                if (File.Exists(cluster.LogFilePath))
                {
                    string logs = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs");
                    Directory.CreateDirectory(logs);
                    string retainedLog = Path.Join(logs, "valgrind-commands-" + Guid.NewGuid().ToString("N") + ".log");
                    File.Copy(cluster.LogFilePath, retainedLog);
                    context.AddResultFile(retainedLog);
                }
            }
        }
    }

    /// <summary>
    /// Runs a packaged command with a caller-owned environment instead of changing the parallel test host.
    /// </summary>
    /// <param name="arguments">The installed-tool arguments.</param>
    /// <param name="environment">The child process settings.</param>
    /// <param name="token">Cancels the command.</param>
    /// <returns>The actual process result.</returns>
    private static Task<ProcessResult> InvokeValgrindCommandAsync(string[] arguments, Dictionary<string, string?> environment, CancellationToken token)
        => PackageProcessRunner.RunAsync(s_tool, arguments, environment, token, workingDirectory: s_root);

    /// <summary>
    /// Missing instrumentation fails before initialization; dry-run selection requires neither a tool nor a server.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task ValgrindMissingToolPreservesDataAndDryRuns()
    {
        CancellationToken token = context.CancellationToken;
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation, CreateDirectory(), token);
        PostgresInstallation installation = owner.Installation;
        string home = CreateDirectory();
        string project = PrepareRegressionProject();
        string suite = Path.Join(Path.GetDirectoryName(project)!, "pg_regress");
        await WriteRegressionCaseAsync(suite, "native", "SELECT 42;", "42\n", token);
        string executables = CreateDirectory();
        // Staging selects the installation's real pg_config binary. Distribution
        // wrappers may require other commands that this intentionally empty PATH hides.
        var environment = new Dictionary<string, string?>(s_environment) { ["PATH"] = executables };
        ProcessResult missing = await PackageProcessRunner.RunAsync(s_tool, ["start", "--home", home, "--pg", MajorText(),
            "--pg-config", installation.PgConfigPath, "--valgrind"], environment, token, workingDirectory: s_root);
        Assert.AreEqual(1, missing.ExitCode, missing.StandardOutput + missing.StandardError);
        Assert.Contains("Install Valgrind", missing.StandardError);
        var cluster = new PostgresDevelopmentCluster(installation, home);
        Assert.IsFalse(Directory.Exists(cluster.DataDirectory));
        Assert.IsFalse(File.Exists(cluster.LogFilePath));
        string[] before = Directory.GetFiles(home, "*", SearchOption.AllDirectories);
        string dotnet = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
            .Select(static directory => Path.Join(directory, "dotnet")).First(File.Exists);
        File.CreateSymbolicLink(Path.Join(executables, "dotnet"), dotnet);
        ProcessResult dry = await PackageProcessRunner.RunAsync(s_tool, [.. RegressionOptions(installation, home, project, 12345),
            "--valgrind", "--dry-run"], environment, token, workingDirectory: s_root);
        Assert.AreEqual(0, dry.ExitCode, dry.StandardOutput + dry.StandardError);
        Assert.Contains("Would start PostgreSQL under Valgrind Memcheck", dry.StandardOutput);
        Assert.AreEquivalent(before, Directory.GetFiles(home, "*", SearchOption.AllDirectories));
        Assert.IsFalse(Directory.Exists(Path.Join(suite, "results")));
    }

    /// <summary>
    /// Startup timeout terminates the launcher even before the real server can create a PostgreSQL PID file.
    /// </summary>
    [RetryPortCollisionTestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task ValgrindTimeoutBeforePostgresStartsTerminatesChild()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string source = Path.Join(root, "delay.c");
        string launcher = Path.Join(root, "valgrind");
        string marker = Path.Join(root, "launcher.pid");
        await File.WriteAllTextAsync(source, """
            #include <stdio.h>
            #include <stdlib.h>
            #include <string.h>
            #include <unistd.h>
            int main(int argc, char **argv)
            {
                if (argc == 2 && strcmp(argv[1], "--version") == 0)
                    return 0;
                const char *destination = getenv("ANKUS_TEST_VALGRIND_MARKER");
                char temporary[4096];
                if (snprintf(temporary, sizeof(temporary), "%s.tmp", destination) >= sizeof(temporary))
                    return 2;
                FILE *marker = fopen(temporary, "w");
                if (marker == NULL)
                    return 2;
                fprintf(marker, "%ld", (long)getpid());
                fclose(marker);
                if (rename(temporary, destination) != 0)
                    return 2;
                sleep(60);
                return 3;
            }
            """, token);
        ProcessResult compile = await PackageProcessRunner.RunAsync("cc", [source, "-o", launcher], new Dictionary<string, string?>(), token);
        Assert.AreEqual(0, compile.ExitCode, compile.StandardError);
        string home = CreateDirectory();
        var cluster = new PostgresDevelopmentCluster(s_installation, home);
        var environment = new Dictionary<string, string?>(s_environment)
        {
            ["PATH"] = root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["ANKUS_TEST_VALGRIND_MARKER"] = marker,
        };
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        // codeql[cs/missed-using-statement] The child process is found partway through the test, and it must be ended before it is disposed.
        Process? child = null;
        Task<ProcessResult>? starting = null;
        try
        {
            starting = PackageProcessRunner.RunAsync(s_tool, ["start", "--home", home, "--pg", MajorText(),
                "--pg-config", s_installation.PgConfigPath, "--port", port.ToString(CultureInfo.InvariantCulture),
                "--valgrind", "--timeout", "3"], environment, token, workingDirectory: s_root);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while (!File.Exists(marker) && !starting.IsCompleted)
            {
                await Task.Delay(20, deadline.Token);
            }

            Assert.IsTrue(File.Exists(marker), "The delayed launcher must run before timeout.");
            int id = int.Parse(await File.ReadAllTextAsync(marker, token), CultureInfo.InvariantCulture);
            child = Process.GetProcessById(id);
            Assert.IsFalse(File.Exists(Path.Join(cluster.DataDirectory, "postmaster.pid")));
            ProcessResult result = await starting.WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.AreEqual(1, result.ExitCode, result.StandardOutput + result.StandardError);
            Assert.Contains("did not start within 3 seconds", result.StandardError);
            await child.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.IsTrue(child.HasExited);
            Assert.IsFalse(await cluster.IsRunningAsync(token));
            Assert.IsTrue(await cluster.StartAsync(new PostgresDevelopmentOptions { Port = port }, token));
            await using NpgsqlConnection connection = await OpenDevelopmentConnectionAsync(port, token);
            await using var command = new NpgsqlCommand("SELECT 42", connection);
            Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(CancellationToken.None);
                }

                child.Dispose();
            }

            if (starting is not null)
            {
                Task pendingStartup = starting;
                await pendingStartup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await cluster.StopAsync(CancellationToken.None);
        }
    }

    private static async Task AssertValgrindLogAsync(string path, CancellationToken token)
    {
        string log = await File.ReadAllTextAsync(path, token);
        Assert.Contains("Memcheck, a memory error detector", log);
        Assert.Contains("ERROR SUMMARY:", log);
    }
}
