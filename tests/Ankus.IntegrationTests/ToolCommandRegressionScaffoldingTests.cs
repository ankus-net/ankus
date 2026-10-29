using System.Runtime.InteropServices;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Generated regression setup creates the named native extension, survives database reuse and retains expectations.
    /// </summary>
    /// <param name="projectName">The generated managed project name.</param>
    /// <param name="extensionName">An explicit SQL name, or null to use the derived name.</param>
    /// <param name="expectedName">The independently expected SQL extension name.</param>
    /// <param name="template">Whether to create through the installed dotnet-new template.</param>
    [RetryPortCollisionTestMethod]
    [DataRow("Acme.RegressProbe", null, "acme_regress_probe", false)]
    [DataRow("class.select", "regression_named", "regression_named", false)]
    [DataRow("Acme.RegressProbe", null, "acme_regress_probe", true)]
    [DataRow("class.select", null, "class_select", true)]
    [DataRow("1Ext", null, "_1_ext", false)]
    [DataRow("1Ext", null, "_1_ext", true)]
    [DataRow("Hello-World", null, "hello_world", false)]
    [DataRow("Hello-World", null, "hello_world", true)]
    [DataRow("Café.Δelta", null, "caf___elta", false)]
    [DataRow("Café.Δelta", null, "caf___elta", true)]
    public async Task NewSolutionScaffoldsRunnableRegressionSetup(string projectName, string? extensionName, string expectedName, bool template)
    {
        CancellationToken token = context.CancellationToken;
        string output = Path.Join(CreateDirectory(), "regression solution with spaces");
        string[] arguments = ["new", projectName, "--output", output,
            .. extensionName is null ? Array.Empty<string>() : ["--extension-name", extensionName]];
        if (template)
        {
            await CreateTemplateAsync("ankus", projectName, output, token);
        }
        else
        {
            (await InvokeAsync(arguments, token)).EnsureSuccess(s_tool, arguments);
        }

        string project = Path.Join(output, "src", projectName, projectName + ".csproj");
        string suite = Path.Join(Path.GetDirectoryName(project)!, "pg_regress");
        string expectedSetup = "-- This setup file runs when the regression database is created or recreated.\n" +
            "-- Create the extension before running the ordinary SQL tests.\nCREATE EXTENSION " + expectedName + ";\n";
        string setup = Path.Join(suite, "sql", "setup.sql");
        string setupOutput = Path.Join(suite, "expected", "setup.out");
        Assert.AreEqual(expectedSetup, (await File.ReadAllTextAsync(setup, token)).ReplaceLineEndings("\n"));
        Assert.AreEqual(expectedSetup, (await File.ReadAllTextAsync(setupOutput, token)).ReplaceLineEndings("\n"));
        Assert.IsLessThanOrEqualTo(File.GetLastWriteTimeUtc(setupOutput), File.GetLastWriteTimeUtc(setup));
        byte[] originalSql = await File.ReadAllBytesAsync(setup, token);
        byte[] originalExpected = await File.ReadAllBytesAsync(setupOutput, token);
        await WriteRegressionCaseAsync(suite, "addition", "SELECT add(17, 25);", "42\n", token);
        // The tool and installed template generate byte-identical solutions, so each distinct tree is built once.
        // Every case installs that publication into its own staged server and runs its own regression lifecycle.
        string relativeProject = Path.GetRelativePath(output, project);
        SharedOutput build = await GetSharedOutputAsync(ContentKey(output, "ankus", "build", relativeProject),
            shared => BuildSharedExtensionAsync(CopySharedSnapshot(output), relativeProject, shared), token);
        CopyFiles(build.Directory, Path.Join(Path.GetDirectoryName(project)!, "bin", "ankus", s_postgresKey,
            RuntimeInformation.RuntimeIdentifier, "Release"));

        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation, CreateDirectory(), token);
        string home = CreateDirectory();
        var cluster = new PostgresDevelopmentCluster(owner.Installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        string[] options = RegressionOptions(owner.Installation, home, project, port);
        try
        {
            ProcessResult initial = await InvokeAsync([.. options, "--no-build"], token);
            Assert.AreEqual(0, initial.ExitCode, initial.StandardOutput + initial.StandardError);
            Assert.Contains("Created database " + expectedName + "_regress", initial.StandardOutput);
            Assert.Contains("Selected 2 tests; skipped 0.", initial.StandardOutput);
            await using (NpgsqlConnection connection = await OpenRegressionConnectionAsync(port, expectedName + "_regress", token))
            {
                await using var command = new NpgsqlCommand("SELECT extname FROM pg_extension WHERE extname = $1", connection);
                command.Parameters.Add(new NpgsqlParameter { Value = expectedName });
                Assert.AreEqual(expectedName, await command.ExecuteScalarAsync(token));
                command.CommandText = "SELECT add(17, 25)";
                command.Parameters.Clear();
                Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
            }

            ProcessResult reused = await InvokeAsync([.. options, "--no-build"], token);
            Assert.AreEqual(0, reused.ExitCode, reused.StandardOutput + reused.StandardError);
            Assert.Contains("Reusing database " + expectedName + "_regress", reused.StandardOutput);
            Assert.Contains("Selected 1 tests; skipped 0.", reused.StandardOutput);
            Assert.AreSequenceEqual(originalSql, await File.ReadAllBytesAsync(setup, token));
            Assert.AreSequenceEqual(originalExpected, await File.ReadAllBytesAsync(setupOutput, token));
            string expectedResult = $"\\set ECHO none{Environment.NewLine}42{Environment.NewLine}";
            Assert.AreEqual(expectedResult, await File.ReadAllTextAsync(Path.Join(suite, "results", "addition.out"), token));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Regress adds nothing to a generated solution's Git changes while it runs or after repeated failures, removes the
    /// lock that earlier versions left in the suite, and still refuses a second run of the same suite.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task RegressLeavesGitChangesAlone()
    {
        CancellationToken token = context.CancellationToken;
        const string ProjectName = "Acme.RegressProbe";
        const string Database = "acme_regress_probe_regress";
        const string Release = "ankus regress release";
        string output = Path.Join(CreateDirectory(), "regression solution with spaces");
        string[] arguments = ["new", ProjectName, "--output", output];
        (await InvokeAsync(arguments, token)).EnsureSuccess(s_tool, arguments);
        string project = Path.Join(output, "src", ProjectName, ProjectName + ".csproj");
        string suite = Path.Join(Path.GetDirectoryName(project)!, "pg_regress");

        // The same files as NewSolutionScaffoldsRunnableRegressionSetup's first case share its build.
        await WriteRegressionCaseAsync(suite, "addition", "SELECT add(17, 25);", "42\n", token);
        string relativeProject = Path.GetRelativePath(output, project);
        SharedOutput build = await GetSharedOutputAsync(ContentKey(output, "ankus", "build", relativeProject),
            shared => BuildSharedExtensionAsync(CopySharedSnapshot(output), relativeProject, shared), token);
        CopyFiles(build.Directory, Path.Join(Path.GetDirectoryName(project)!, "bin", "ankus", s_postgresKey,
            RuntimeInformation.RuntimeIdentifier, "Release"));

        // This case runs until a session with the release name connects, so the test can read Git's changes meanwhile.
        await WriteRegressionCaseAsync(suite, "waits", "DO $$ BEGIN WHILE NOT EXISTS (SELECT FROM pg_stat_activity " +
            "WHERE application_name = '" + Release + "') LOOP PERFORM pg_stat_clear_snapshot(); PERFORM pg_sleep(0.05); " +
            "END LOOP; END $$;\nSELECT 42;", "42\n", token);
        await WriteRegressionCaseAsync(suite, "differs", "SELECT 7;", "0\n", token);
        await GitAsync(output, ["init", "--quiet", "--initial-branch=main"], token);
        await GitAsync(output, ["add", "--all"], token);
        await GitAsync(output, ["-c", "user.name=Ankus", "-c", "user.email=ankus@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "--quiet", "--message=Generated"], token);

        // Ankus 0.2.15 and earlier kept the lock in the suite, and a run killed on Linux or macOS left it there.
        string abandoned = Path.Join(suite, ".ankus-regress.lock");
        await File.WriteAllTextAsync(abandoned, "", token);
        Assert.AreEqual("?? src/" + ProjectName + "/pg_regress/.ankus-regress.lock", (await GitStatusAsync(output, token)).Trim());

        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation, CreateDirectory(), token);
        string home = CreateDirectory();
        var cluster = new PostgresDevelopmentCluster(owner.Installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        string[] options = [.. RegressionOptions(owner.Installation, home, project, port), "--no-build"];
        Task<ProcessResult>? running = null;
        try
        {
            running = InvokeAsync([.. options, "waits"], token);
            while (!await IsWaitingAsync())
            {
                if (running.IsCompleted)
                {
                    ProcessResult early = await running;
                    Assert.Fail("ankus regress ended before its waiting case ran: " + early.StandardOutput + early.StandardError);
                }

                await Task.Delay(100, token);
            }

            Assert.AreEqual("", await GitStatusAsync(output, token));
            ProcessResult second = await InvokeAsync([.. options, "waits"], token);
            Assert.AreEqual(1, second.ExitCode, second.StandardOutput + second.StandardError);
            Assert.Contains(Path.Join("bin", "ankus", "regress.lock"), second.StandardError);

            ProcessResult completed = await ReleaseAsync(running);
            Assert.AreEqual(0, completed.ExitCode, completed.StandardOutput + completed.StandardError);
            Assert.Contains("Selected 2 tests; skipped 0.", completed.StandardOutput);
            Assert.IsFalse(File.Exists(abandoned));
            Assert.AreEqual("", await GitStatusAsync(output, token));

            ProcessResult repeated = await InvokeAsync([.. options, "--repeat", "2", "differs"], token);
            Assert.AreEqual(1, repeated.ExitCode, repeated.StandardOutput + repeated.StandardError);
            Assert.IsTrue(File.Exists(Path.Join(suite, "regression.1.diffs")));
            Assert.IsTrue(File.Exists(Path.Join(suite, "regression.2.diffs")));
            Assert.AreEqual("", await GitStatusAsync(output, token));
        }
        finally
        {
            if (running is { IsCompleted: false })
            {
                _ = await ReleaseAsync(running);
            }

            await cluster.StopAsync(CancellationToken.None);
        }

        async Task<bool> IsWaitingAsync()
        {
            try
            {
                await using NpgsqlConnection connection = await OpenRegressionConnectionAsync(port, Database, token);
                await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT FROM pg_stat_activity " +
                    "WHERE state = 'active' AND query LIKE 'DO $$%' AND pid <> pg_backend_pid())", connection);
                return await command.ExecuteScalarAsync(token) is true;
            }
            catch (NpgsqlException)
            {
                // The server or the database isn't ready yet.
                return false;
            }
        }

        async Task<ProcessResult> ReleaseAsync(Task<ProcessResult> run)
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = port, Username = "postgres", Database = Database, Pooling = false,
                ApplicationName = Release,
            }.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            return await run;
        }
    }

    private static async Task GitAsync(string directory, string[] arguments, CancellationToken token)
        => (await PackageProcessRunner.RunAsync("git", arguments, s_environment, token, workingDirectory: directory))
            .EnsureSuccess("git", arguments);

    private static async Task<string> GitStatusAsync(string directory, CancellationToken token)
    {
        string[] arguments = ["status", "--porcelain", "--untracked-files=all"];
        ProcessResult status = await PackageProcessRunner.RunAsync("git", arguments, s_environment, token, workingDirectory: directory);
        status.EnsureSuccess("git", arguments);
        return status.StandardOutput;
    }

    /// <summary>
    /// Builds a generated extension through the installed tool in class-owned storage.
    /// </summary>
    /// <param name="solution">The class-owned copy of the generated solution.</param>
    /// <param name="relativeProject">The extension project beneath the solution.</param>
    /// <param name="token">Cancels the class-owned build.</param>
    /// <returns>The project's default Release publication and the build result.</returns>
    private static async Task<SharedOutput> BuildSharedExtensionAsync(string solution, string relativeProject, CancellationToken token)
    {
        string project = Path.Join(solution, relativeProject);
        string[] arguments = ["build", "--home", s_home, "--pg", MajorText(), "--project", project];
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, arguments, s_environment, token, workingDirectory: s_root);
        result.EnsureSuccess(s_tool, arguments);
        string publication = Path.Join(Path.GetDirectoryName(project)!, "bin", "ankus", s_postgresKey,
            RuntimeInformation.RuntimeIdentifier, "Release");
        Assert.AreEqual(s_installation.Version.Major, PublishedExtension.Read(publication).PostgresMajor);
        return new SharedOutput(publication, result);
    }
}
