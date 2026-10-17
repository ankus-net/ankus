using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// A real publication executes native SQL, reuses existing setup, and resets only the selected database when requested or changed.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task RegressBuildsAndPreservesSetupLifecycle()
    {
        CancellationToken token = context.CancellationToken;
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation, CreateDirectory(), token);
        string home = CreateDirectory();
        string project = PrepareRegressionProject();
        string suite = Path.Join(Path.GetDirectoryName(project)!, "pg_regress");
        const string Database = "regress café,'\"\\db";
        await WriteRegressionCaseAsync(suite, "setup", "CREATE EXTENSION ankus_tool_probe; CREATE TABLE seed(value integer); INSERT INTO seed VALUES (42);", "", token);
        await WriteRegressionCaseAsync(suite, "native", "SELECT add(value, 0) FROM seed;", "42\n", token);
        var cluster = new PostgresDevelopmentCluster(owner.Installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        string[] options = RegressionOptions(owner.Installation, home, project, port, Database);
        try
        {
            ProcessResult initial = await InvokeAsync([.. options, "--postgresql-conf", "ankus.regress_probe=exact value"], token);
            Assert.AreEqual(0, initial.ExitCode, initial.StandardOutput + initial.StandardError);
            Assert.Contains("Created database " + Database, initial.StandardOutput);
            Assert.AreEqual("\\set ECHO none\n42\n", (await File.ReadAllTextAsync(Path.Join(suite, "results", "native.out"), token)).ReplaceLineEndings("\n"));
            await using (NpgsqlConnection connection = await OpenRegressionConnectionAsync(port, Database, token))
            {
                await using var command = new NpgsqlCommand("CREATE TABLE retained(value integer); INSERT INTO retained VALUES (99); SELECT current_setting('ankus.regress_probe')", connection);
                Assert.AreEqual("exact value", await command.ExecuteScalarAsync(token));
            }

            ProcessResult reused = await InvokeAsync([.. options, "--no-build"], token);
            Assert.AreEqual(0, reused.ExitCode, reused.StandardOutput + reused.StandardError);
            Assert.Contains("Reusing database " + Database, reused.StandardOutput);
            await using (NpgsqlConnection connection = await OpenRegressionConnectionAsync(port, Database, token))
            {
                await using var command = new NpgsqlCommand("SELECT value FROM retained", connection);
                Assert.AreEqual(99, await command.ExecuteScalarAsync(token));
            }

            ProcessResult reset = await InvokeAsync([.. options, "--no-build", "--resetdb"], token);
            Assert.AreEqual(0, reset.ExitCode, reset.StandardOutput + reset.StandardError);
            Assert.Contains("Created database " + Database, reset.StandardOutput);
            await using (NpgsqlConnection connection = await OpenRegressionConnectionAsync(port, Database, token))
            {
                await using var command = new NpgsqlCommand("SELECT to_regclass('public.retained') IS NULL", connection);
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            }

            string setup = Path.Join(suite, "sql", "setup.sql");
            await File.WriteAllTextAsync(setup, "\\set ECHO none\nCREATE EXTENSION ankus_tool_probe; CREATE TABLE seed(value integer); INSERT INTO seed VALUES (84);\n", token);
            File.SetLastWriteTimeUtc(setup, File.GetLastWriteTimeUtc(Path.Join(suite, "expected", "setup.out")).AddSeconds(5));
            await File.WriteAllTextAsync(Path.Join(suite, "expected", "native.out"), "\\set ECHO none\n84\n", token);
            ProcessResult changed = await InvokeAsync([.. options, "--no-build", "native"], token);
            Assert.AreEqual(0, changed.ExitCode, changed.StandardOutput + changed.StandardError);
            Assert.Contains("Created database " + Database, changed.StandardOutput);
            Assert.AreEqual("\\set ECHO none\n84\n", (await File.ReadAllTextAsync(Path.Join(suite, "results", "native.out"), token)).ReplaceLineEndings("\n"));
            Assert.IsTrue(await cluster.IsRunningAsync(token));
            Assert.IsFalse(File.Exists(Path.Join(suite, ".ankus-regress.lock")));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// On PostgreSQL 18 and later, regress stages the extension beside its publication, so it runs against an installation
    /// this account can't write, such as a distribution's, and leaves it unchanged. Earlier majors have no
    /// <c>extension_control_path</c>, and their failure says why they need write access and what to use instead.
    /// </summary>
    [RetryPortCollisionTestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task RegressLeavesAReadOnlyInstallationAlone()
    {
        CancellationToken token = context.CancellationToken;
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation, CreateDirectory(), token);
        string home = CreateDirectory();
        string project = PrepareRegressionProject();
        string suite = Path.Join(Path.GetDirectoryName(project)!, "pg_regress");
        await WriteRegressionCaseAsync(suite, "setup", "CREATE EXTENSION ankus_tool_probe;", "", token);
        await WriteRegressionCaseAsync(suite, "native", "SELECT add(19, 23);", "42\n", token);
        string extensions = Path.Join(owner.Installation.SharedDirectory, "extension");
        Dictionary<string, UnixFileMode> modes = new[] { extensions, owner.Installation.LibraryDirectory }
            .ToDictionary(static path => path, File.GetUnixFileMode);
        // The staged copy can already hold the probe from an earlier test, so compare contents instead of looking for it.
        string[] before = Snapshot(modes.Keys);
        const UnixFileMode ReadOnly = UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        foreach (string path in modes.Keys)
        {
            File.SetUnixFileMode(path, ReadOnly);
        }

        var cluster = new PostgresDevelopmentCluster(owner.Installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        try
        {
            ProcessResult result = await InvokeAsync(RegressionOptions(owner.Installation, home, project, port), token);
            if (owner.Installation.Version.Major >= 18)
            {
                Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
                Assert.AreSequenceEqual(before, Snapshot(modes.Keys));
            }
            else
            {
                Assert.AreNotEqual(0, result.ExitCode, result.StandardOutput);
                Assert.Contains("Before PostgreSQL 18, ankus regress installs the extension into the installation", result.StandardError);
            }
        }
        finally
        {
            foreach ((string path, UnixFileMode mode) in modes)
            {
                File.SetUnixFileMode(path, mode);
            }

            await cluster.StopAsync(CancellationToken.None);
        }

        static string[] Snapshot(IEnumerable<string> directories)
            => [.. directories.SelectMany(static directory => Directory.EnumerateFiles(directory))
                .Select(static file => file + "|" + File.GetLastWriteTimeUtc(file).Ticks.ToString(CultureInfo.InvariantCulture))
                .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Bootstrap runs setup first, writes exact native output, honors SQL error verbosity and rejects replacing an existing expectation.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task RegressBootstrapsSetupAndPreservesErrorVerbosity()
    {
        CancellationToken token = context.CancellationToken;
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation, CreateDirectory(), token);
        string home = CreateDirectory();
        string project = PrepareRegressionProject();
        string suite = Path.Join(Path.GetDirectoryName(project)!, "pg_regress");
        await WriteRegressionCaseAsync(suite, "setup", "CREATE EXTENSION ankus_tool_probe;", null, token);
        await WriteRegressionCaseAsync(suite, "error and recovery", "SELECT 1 / 0; SELECT add(3, 4);", null, token);
        var cluster = new PostgresDevelopmentCluster(owner.Installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        string[] options = [.. RegressionOptions(owner.Installation, home, project, port), "--no-build", "--psql-verbosity", "sqlstate"];
        try
        {
            ProcessResult added = await InvokeAsync([.. options, "--add", "error and recovery"], token);
            Assert.AreEqual(0, added.ExitCode, added.StandardOutput + added.StandardError);
            Assert.Contains("Created expected output for setup.", added.StandardOutput);
            string expected = Path.Join(suite, "expected", "error and recovery.out");
            Assert.AreEqual("\\set ECHO none\nERROR:  22012\n7\n", (await File.ReadAllTextAsync(expected, token)).ReplaceLineEndings("\n"));
            Assert.AreEqual("\\set ECHO none\n", (await File.ReadAllTextAsync(Path.Join(suite, "expected", "setup.out"), token)).ReplaceLineEndings("\n"));
            byte[] original = await File.ReadAllBytesAsync(expected, token);
            ProcessResult rerun = await InvokeAsync([.. options, "error and recovery"], token);
            Assert.AreEqual(0, rerun.ExitCode, rerun.StandardOutput + rerun.StandardError);
            ProcessResult duplicate = await InvokeAsync([.. options, "--add", "error and recovery"], token);
            Assert.AreEqual(1, duplicate.ExitCode);
            Assert.Contains("without existing expected output", duplicate.StandardError);
            Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(expected, token));
            Assert.IsEmpty(Directory.GetFiles(suite, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Native alternate expectations, ordered selections, repeat diffs and automatic promotion retain exact failure and ownership semantics.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task RegressRepeatsNativeDiffsAndPromotesOnlyFailedSelections()
    {
        CancellationToken token = context.CancellationToken;
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation, CreateDirectory(), token);
        string home = CreateDirectory();
        string project = PrepareRegressionProject();
        string suite = Path.Join(Path.GetDirectoryName(project)!, "pg_regress");
        await WriteRegressionCaseAsync(suite, "b_fail", "SELECT 7;", "0\n", token);
        await WriteRegressionCaseAsync(suite, "a_match", "SELECT 42;", "0\n", token);
        await WriteRegressionCaseAsync(suite, "z_missing", "SELECT 99;", null, token);
        await File.WriteAllTextAsync(Path.Join(suite, "expected", "a_match_0.out"), "\\set ECHO none\n42\n", token);
        await File.WriteAllTextAsync(Path.Join(suite, "expected", "stale.out"), "retained expectation", token);
        Directory.CreateDirectory(Path.Join(suite, "results"));
        await File.WriteAllTextAsync(Path.Join(suite, "results", "stale.out"), "stale output", token);
        var cluster = new PostgresDevelopmentCluster(owner.Installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        string[] options = [.. RegressionOptions(owner.Installation, home, project, port), "--no-build"];
        try
        {
            ProcessResult repeated = await InvokeAsync([.. options, "--repeat", "2", "--verbose"], token);
            Assert.AreEqual(1, repeated.ExitCode, repeated.StandardOutput + repeated.StandardError);
            Assert.Contains("SKIP z_missing", repeated.StandardOutput);
            Assert.Contains("Selected 2 tests; skipped 1.", repeated.StandardOutput);
            Assert.Contains("a_match", repeated.StandardOutput);
            Assert.Contains("b_fail", repeated.StandardOutput);
            Assert.IsLessThan(repeated.StandardOutput.IndexOf("b_fail", StringComparison.Ordinal), repeated.StandardOutput.IndexOf("a_match", StringComparison.Ordinal));
            foreach (int iteration in new[] { 1, 2 })
            {
                string differences = await File.ReadAllTextAsync(Path.Join(suite, $"regression.{iteration}.diffs"), token);
                Assert.Contains("b_fail.out", differences);
                Assert.DoesNotContain("a_match.out", differences);
            }

            Assert.Contains("b_fail.out", repeated.StandardError);
            ProcessResult promoted = await InvokeAsync([.. options, "--repeat", "2", "--auto"], token);
            Assert.AreEqual(1, promoted.ExitCode, promoted.StandardOutput + promoted.StandardError);
            Assert.Contains("Updated expected output for b_fail.", promoted.StandardOutput);
            Assert.DoesNotContain("Updated expected output for a_match", promoted.StandardOutput);
            Assert.AreEqual("\\set ECHO none\n7\n", (await File.ReadAllTextAsync(Path.Join(suite, "expected", "b_fail.out"), token)).ReplaceLineEndings("\n"));
            Assert.AreEqual("\\set ECHO none\n0\n", await File.ReadAllTextAsync(Path.Join(suite, "expected", "a_match.out"), token));
            Assert.AreEqual("retained expectation", await File.ReadAllTextAsync(Path.Join(suite, "expected", "stale.out"), token));
            Assert.IsTrue(File.Exists(Path.Join(suite, "regression.1.diffs")));
            Assert.IsFalse(File.Exists(Path.Join(suite, "regression.2.diffs")));
            ProcessResult selected = await InvokeAsync([.. options, "b_fail"], token);
            Assert.AreEqual(0, selected.ExitCode, selected.StandardOutput + selected.StandardError);
            Assert.Contains("Selected 1 tests; skipped 0.", selected.StandardOutput);
            Assert.DoesNotContain("z_missing", selected.StandardOutput);
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Failed psql clients cannot bootstrap or replace expectations from stale or partial output; corrected SQL recovers.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task RegressRejectsFailedClientOutput()
    {
        CancellationToken token = context.CancellationToken;
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation, CreateDirectory(), token);
        string home = CreateDirectory();
        string project = PrepareRegressionProject();
        string suite = Path.Join(Path.GetDirectoryName(project)!, "pg_regress");
        await WriteRegressionCaseAsync(suite, "broken", "\\set ON_ERROR_STOP on\nSELECT 1 / 0;", null, token);
        Directory.CreateDirectory(Path.Join(suite, "results"));
        await File.WriteAllTextAsync(Path.Join(suite, "results", "broken.out"), "stale success", token);
        var cluster = new PostgresDevelopmentCluster(owner.Installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        string[] options = [.. RegressionOptions(owner.Installation, home, project, port), "--no-build"];
        try
        {
            ProcessResult bootstrap = await InvokeAsync([.. options, "--add", "broken"], token);
            Assert.AreEqual(2, bootstrap.ExitCode, bootstrap.StandardOutput + bootstrap.StandardError);
            Assert.Contains("did not complete successfully", bootstrap.StandardError);
            string expected = Path.Join(suite, "expected", "broken.out");
            Assert.IsFalse(File.Exists(expected));
            await File.WriteAllTextAsync(expected, "original expectation", token);
            ProcessResult auto = await InvokeAsync([.. options, "--auto"], token);
            Assert.AreEqual(2, auto.ExitCode, auto.StandardOutput + auto.StandardError);
            Assert.AreEqual("original expectation", await File.ReadAllTextAsync(expected, token));
            File.Delete(expected);
            await WriteRegressionCaseAsync(suite, "broken", "SELECT 42;", null, token);
            ProcessResult recovered = await InvokeAsync([.. options, "--add", "broken"], token);
            Assert.AreEqual(0, recovered.ExitCode, recovered.StandardOutput + recovered.StandardError);
            Assert.AreEqual("\\set ECHO none\n42\n", (await File.ReadAllTextAsync(expected, token)).ReplaceLineEndings("\n"));
            Assert.IsFalse(File.Exists(Path.Join(suite, ".ankus-regress.lock")));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Dry runs report stable selection without compilation, registration, database changes or creating a missing suite.
    /// </summary>
    [TestMethod]
    public async Task RegressDryRunAndSelectionAreReadOnly()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string home = Path.Join(root, "unregistered");
        string project = Path.Join(root, "Dry.csproj");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AnkusExtensionName>dry_probe</AnkusExtensionName>
                <AnkusPostgresMajor />
                <AnkusPgConfigPath />
              </PropertyGroup>
            </Project>
            """, token);
        await File.WriteAllTextAsync(Path.Join(root, "Invalid.cs"), "invalid C# source", token);
        string[] options = ["regress", "--project", project, "--home", home, "--dry-run"];
        ProcessResult empty = await InvokeAsync(options, token);
        Assert.AreEqual(0, empty.ExitCode, empty.StandardError);
        Assert.Contains("dry_probe_regress", empty.StandardOutput);
        string suite = Path.Join(root, "pg_regress");
        Assert.IsFalse(Directory.Exists(suite));
        await WriteRegressionCaseAsync(suite, "z_missing", "SELECT 1;", null, token);
        await WriteRegressionCaseAsync(suite, "b_second", "SELECT 1;", "1\n", token);
        await WriteRegressionCaseAsync(suite, "a_first", "SELECT 1;", "1\n", token);
        string[] originalFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        ProcessResult ordered = await InvokeAsync([.. options, "--repeat", "2", "--auto", "--configuration", "Shipping"], token);
        Assert.AreEqual(0, ordered.ExitCode, ordered.StandardError);
        Assert.Contains("(Shipping, pg18)", ordered.StandardOutput);
        Assert.Contains("Would run a_first.", ordered.StandardOutput);
        Assert.Contains("Would run b_second.", ordered.StandardOutput);
        Assert.IsLessThan(ordered.StandardOutput.IndexOf("b_second", StringComparison.Ordinal), ordered.StandardOutput.IndexOf("a_first", StringComparison.Ordinal));
        Assert.Contains("Would skip without expected output z_missing.", ordered.StandardOutput);
        Assert.Contains("Would run the selection 2 time(s).", ordered.StandardOutput);
        Assert.Contains("Would update expected outputs", ordered.StandardOutput);
        ProcessResult noMatch = await InvokeAsync([.. options, "absent"], token);
        Assert.AreEqual(1, noMatch.ExitCode);
        Assert.Contains("No regression tests match", noMatch.StandardError);
        ProcessResult wrongCase = await InvokeAsync([.. options, "A_first"], token);
        Assert.AreEqual(1, wrongCase.ExitCode);
        Assert.Contains("No regression tests match", wrongCase.StandardError);
        ProcessResult missingExpected = await InvokeAsync([.. options, "z_missing"], token);
        Assert.AreEqual(1, missingExpected.ExitCode);
        Assert.Contains("no expected output", missingExpected.StandardError);
        ProcessResult add = await InvokeAsync([.. options, "--add", "z_missing"], token);
        Assert.AreEqual(0, add.ExitCode, add.StandardError);
        Assert.Contains("Would recreate database", add.StandardOutput);
        Assert.Contains("Would bootstrap z_missing", add.StandardOutput);
        string[][] invalid = [["--repeat", "0"], ["--repeat", "-1"], ["--psql-verbosity", "invalid"], ["--add", "absent"],
            ["--add", "z_missing", "--auto"], ["--add", "z_missing", "--repeat", "2"], ["a_first", "--add", "z_missing"],
            ["--port", "0"], ["--port", "65536"], ["--timeout", "0"], ["--timeout", "601"]];
        foreach (string[] arguments in invalid)
        {
            ProcessResult rejected = await InvokeAsync([.. options, .. arguments], token);
            Assert.AreEqual(1, rejected.ExitCode, rejected.StandardOutput + rejected.StandardError);
            Assert.IsNotEmpty(rejected.StandardError);
        }

        Assert.AreSequenceEqual(originalFiles.Order(StringComparer.Ordinal), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.IsFalse(Directory.Exists(home));
        Assert.IsFalse(Directory.Exists(Path.Join(root, "obj")));
        Assert.IsFalse(Directory.Exists(Path.Join(suite, "results")));
    }

    /// <summary>
    /// With --runas, regression databases are created and dropped as another Unix account and its role through sudo,
    /// as cargo pgrx regress --runas runs createdb and dropdb; a missing role fails as createdb would.
    /// </summary>
    [RetryPortCollisionTestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task RegressCreatesDatabasesAsAnotherAccount()
    {
        CancellationToken token = context.CancellationToken;
        string account = IntegrationEnvironment.RequireRunAsAccount();
        // The account runs psql from the selected installation, so stage it where every account can read it.
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation,
            Path.Join("/tmp", "ankus-runas-pg-" + Guid.NewGuid().ToString("N")), token);
        string home = CreateDirectory();
        string project = PrepareRegressionProject();
        string suite = Path.Join(Path.GetDirectoryName(project)!, "pg_regress");
        await WriteRegressionCaseAsync(suite, "setup", "CREATE EXTENSION ankus_tool_probe;", "", token);
        await WriteRegressionCaseAsync(suite, "native", "SELECT add(40, 2);", "42\n", token);
        var cluster = new PostgresDevelopmentCluster(owner.Installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        const string Database = "regress_runas";
        string[] options = [.. RegressionOptions(owner.Installation, home, project, port, Database), "--no-build", "--runas", account];
        try
        {
            ProcessResult missing = await InvokeAsync(options, token);
            Assert.AreNotEqual(0, missing.ExitCode, missing.StandardOutput);
            Assert.Contains($"role \"{account}\" does not exist", missing.StandardError);
            await using (NpgsqlConnection connection = await OpenRegressionConnectionAsync(port, "postgres", token))
            {
                await using var command = new NpgsqlCommand($"CREATE ROLE \"{account}\" LOGIN CREATEDB", connection);
                await command.ExecuteNonQueryAsync(token);
            }

            ProcessResult created = await InvokeAsync(options, token);
            Assert.AreEqual(0, created.ExitCode, created.StandardOutput + created.StandardError);
            Assert.Contains("Created database " + Database, created.StandardOutput);
            Assert.AreEqual(account, await ReadDatabaseOwnerAsync(port, Database, token));
            await using (NpgsqlConnection connection = await OpenRegressionConnectionAsync(port, Database, token))
            {
                await using var command = new NpgsqlCommand("CREATE TABLE retained(value integer)", connection);
                await command.ExecuteNonQueryAsync(token);
            }

            ProcessResult reset = await InvokeAsync([.. options, "--resetdb"], token);
            Assert.AreEqual(0, reset.ExitCode, reset.StandardOutput + reset.StandardError);
            Assert.Contains("Created database " + Database, reset.StandardOutput);
            Assert.AreEqual(account, await ReadDatabaseOwnerAsync(port, Database, token));
            await using (NpgsqlConnection connection = await OpenRegressionConnectionAsync(port, Database, token))
            {
                await using var command = new NpgsqlCommand("SELECT to_regclass('public.retained') IS NULL", connection);
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            }
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<string> ReadDatabaseOwnerAsync(int port, string database, CancellationToken token)
    {
        await using NpgsqlConnection connection = await OpenRegressionConnectionAsync(port, "postgres", token);
        await using var command = new NpgsqlCommand("SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = $1", connection);
        command.Parameters.AddWithValue(database);
        return Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token));
    }

    private string PrepareRegressionProject()
    {
        string directory = CreateDirectory();
        string project = Path.Join(directory, "RegressionProbe.csproj");
        XDocument.Load(s_project).Save(project);
        File.Copy(Path.Join(Path.GetDirectoryName(s_project)!, "Hello.cs"), Path.Join(directory, "Hello.cs"));
        string publication = Path.Join(directory, "bin", "ankus", s_postgresKey, RuntimeInformation.RuntimeIdentifier, "Release");
        foreach (string file in Directory.GetFiles(s_published, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Join(publication, Path.GetRelativePath(s_published, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return project;
    }

    private static string[] RegressionOptions(PostgresInstallation installation, string home, string project, int port,
        string? database = null)
        => ["regress", "--home", home, "--pg", MajorText(), "--pg-config", installation.PgConfigPath, "--project", project,
            "--port", port.ToString(CultureInfo.InvariantCulture), .. database is null ? Array.Empty<string>() : ["--database", database]];

    private static async Task WriteRegressionCaseAsync(string suite, string name, string sql, string? expected, CancellationToken token)
    {
        Directory.CreateDirectory(Path.Join(suite, "sql"));
        await File.WriteAllTextAsync(Path.Join(suite, "sql", name + ".sql"),
            "\\set ECHO none\n\\pset format unaligned\n\\pset tuples_only on\n" + sql + "\n", token);
        if (expected is not null)
        {
            Directory.CreateDirectory(Path.Join(suite, "expected"));
            await File.WriteAllTextAsync(Path.Join(suite, "expected", name + ".out"), "\\set ECHO none\n" + expected, token);
        }
    }

    private static async Task<NpgsqlConnection> OpenRegressionConnectionAsync(int port, string database, CancellationToken token)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = port, Username = "postgres", Database = database, Pooling = false,
        }.ConnectionString);
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
