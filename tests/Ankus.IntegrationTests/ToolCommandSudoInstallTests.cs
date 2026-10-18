using System.Globalization;
using System.Runtime.Versioning;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Installation with sudo stages the publication, copies each file beside its destination with sudo and renames it
    /// into PostgreSQL's own directories, control file last; the server then loads the installed extension.
    /// </summary>
    /// <remarks>
    /// A stand-in <c>sudo</c> on PATH records and runs each command as the current account, so the staged installation
    /// receives exactly the commands a real <c>sudo</c> would run.
    /// </remarks>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task SudoInstallCopiesEachFileIntoPostgresDirectories()
    {
        CancellationToken token = context.CancellationToken;
        PublishedExtension manifest = PublishedExtension.Read(s_published);
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation, CreateDirectory(), token);
        PostgresInstallation installation = owner.Installation;
        (Dictionary<string, string?> environment, string log) = CreateSudoEnvironment(failControlRename: false);

        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool,
            ["install", "--home", s_home, "--pg-config", installation.PgConfigPath, "--from", s_published, "--sudo"],
            environment, token, workingDirectory: s_root);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        string library = Path.Join(installation.LibraryDirectory, manifest.Library);
        string script = Path.Join(installation.SharedDirectory, "extension", manifest.Sql);
        string control = Path.Join(installation.SharedDirectory, "extension", manifest.Control);
        Assert.AreEqual($"Installed {library}\nInstalled {script}\nInstalled {control}\n", result.StandardOutput.ReplaceLineEndings("\n"));
        string staging = SudoStagingDirectory(result);
        Assert.AreEqual(s_temporaryRoot, Path.GetDirectoryName(staging));
        Assert.IsFalse(Directory.Exists(staging));
        string[][] commands = await ReadSudoCommandsAsync(log, token);
        Assert.HasCount(6, commands);
        foreach ((int index, string destination) in new[] { (0, library), (2, script), (4, control) })
        {
            string[] copy = commands[index];
            Assert.HasCount(4, copy);
            Assert.AreEqual("cp", copy[0]);
            Assert.AreEqual("--", copy[1]);
            Assert.AreEqual(StagedPath(staging, destination), copy[2]);
            Assert.StartsWith(destination + ".", copy[3]);
            Assert.EndsWith(".tmp", copy[3]);
            Assert.AreSequenceEqual(["mv", "-f", "--", copy[3], destination], commands[index + 1]);
        }

        Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Join(s_published, manifest.Library), token),
            await File.ReadAllBytesAsync(library, token));
        Assert.IsEmpty(Directory.GetFiles(installation.LibraryDirectory, "*.tmp"));
        Assert.IsEmpty(Directory.GetFiles(Path.Join(installation.SharedDirectory, "extension"), "*.tmp"));
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
        {
            Installation = installation,
            DataDirectoryBase = Path.Join(s_root, "pgdata"),
            LogDirectory = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs"),
        }, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("CREATE EXTENSION ankus_tool_probe; SELECT public.add(40, 2)", connection);
        Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// A failed sudo command stops installation with its exit code, removes its temporary copy, publishes no control file
    /// and removes the staging directory; a destination root preserves PostgreSQL's paths and missing directories are created.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task SudoInstallReportsFailedCommandAndCreatesMissingDirectories()
    {
        CancellationToken token = context.CancellationToken;
        PublishedExtension manifest = PublishedExtension.Read(s_published);
        string stage = CreateDirectory();
        (Dictionary<string, string?> environment, string log) = CreateSudoEnvironment(failControlRename: true);

        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool,
            ["install", "--home", s_home, "--pg", MajorText(), "--from", s_published, "--destdir", stage, "--sudo"],
            environment, token, workingDirectory: s_root);

        Assert.AreEqual(7, result.ExitCode, result.StandardError);
        Assert.Contains("Ankus: sudo command failed with exit code 7.", result.StandardError);
        Assert.IsFalse(Directory.Exists(SudoStagingDirectory(result)));
        string libraries = StagedPath(stage, s_installation.LibraryDirectory);
        string extension = Path.Join(StagedPath(stage, s_installation.SharedDirectory), "extension");
        string control = Path.Join(extension, manifest.Control);
        string[][] commands = await ReadSudoCommandsAsync(log, token);
        Assert.HasCount(9, commands);
        Assert.AreSequenceEqual(["mkdir", "-p", "--", libraries], commands[0]);
        Assert.AreSequenceEqual(["mkdir", "-p", "--", extension], commands[3]);
        Assert.AreEqual("cp", commands[6][0]);
        Assert.AreSequenceEqual(["mv", "-f", "--", commands[6][3], control], commands[7]);
        Assert.AreSequenceEqual(["rm", "-f", "--", commands[6][3]], commands[8]);
        Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Join(s_published, manifest.Library), token),
            await File.ReadAllBytesAsync(Path.Join(libraries, manifest.Library), token));
        Assert.AreEqual(await File.ReadAllTextAsync(Path.Join(s_published, "extension", manifest.Sql), token),
            await File.ReadAllTextAsync(Path.Join(extension, manifest.Sql), token));
        Assert.AreSequenceEqual([Path.Join(extension, manifest.Sql)], Directory.GetFiles(extension));
    }

    /// <summary>
    /// An installation directory the current account cannot write fails with the access error and suggests --sudo.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task UnwritableInstallationSuggestsSudo()
    {
        string stage = CreateDirectory();
        string libraries = StagedPath(stage, s_installation.LibraryDirectory);
        Directory.CreateDirectory(libraries);
        File.SetUnixFileMode(libraries, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            ProcessResult result = await InvokeAsync(
                ["install", "--home", s_home, "--pg", MajorText(), "--from", s_published, "--destdir", stage], context.CancellationToken);

            Assert.AreEqual(1, result.ExitCode);
            Assert.StartsWith("Ankus: Unable to install " + libraries, result.StandardError);
            Assert.Contains(": Access to the path '" + libraries, result.StandardError);
            Assert.EndsWith("is denied. Add --sudo to copy the files with sudo.", result.StandardError.TrimEnd());
            Assert.IsEmpty(Directory.GetFileSystemEntries(libraries));
        }
        finally
        {
            File.SetUnixFileMode(libraries, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Windows rejects sudo installation before building or copying anything.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task SudoInstallIsRejectedOnWindows()
    {
        string stage = CreateDirectory();
        ProcessResult result = await InvokeAsync(
            ["install", "--home", s_home, "--pg", MajorText(), "--from", s_published, "--destdir", stage, "--sudo"],
            context.CancellationToken);

        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual("Ankus: --sudo is not supported on Windows; install from an elevated terminal instead.",
            result.StandardError.Trim());
        Assert.IsEmpty(Directory.GetFileSystemEntries(stage));
    }

    /// <summary>
    /// Reads the staging directory the tool reported before its first sudo command.
    /// </summary>
    /// <param name="result">The installation result.</param>
    /// <returns>The reported staging directory.</returns>
    private static string SudoStagingDirectory(ProcessResult result)
    {
        const string Prefix = "Using sudo to copy extension files from ";
        string line = Assert.ContainsSingle(static line => line.StartsWith(Prefix, StringComparison.Ordinal),
            result.StandardError.ReplaceLineEndings("\n").Split('\n'));
        return line[Prefix.Length..];
    }

    /// <summary>
    /// Reads each recorded sudo command as its exact arguments.
    /// </summary>
    /// <param name="log">The stand-in's command log.</param>
    /// <param name="token">Cancels reading.</param>
    /// <returns>The arguments of each command, in order.</returns>
    private static async Task<string[][]> ReadSudoCommandsAsync(string log, CancellationToken token)
        => [.. (await File.ReadAllLinesAsync(log, token)).Select(static line => line.TrimEnd('\u001f').Split('\u001f'))];

    /// <summary>
    /// Creates a stand-in sudo that records each command and runs it as the current account.
    /// </summary>
    /// <param name="failControlRename">Whether renaming a control file into place exits with status 7.</param>
    /// <returns>The tool environment with the stand-in first on PATH, and the command log.</returns>
    private (Dictionary<string, string?> Environment, string Log) CreateSudoEnvironment(bool failControlRename)
    {
        string directory = CreateDirectory();
        string log = Path.Join(directory, "commands.log");
        string sudo = Path.Join(directory, "sudo");
        File.WriteAllText(sudo, string.Create(CultureInfo.InvariantCulture, $"""
            #!/bin/sh
            printf '%s\037' "$@" >> '{log.Replace("'", "'\\''", StringComparison.Ordinal)}'
            printf '\n' >> '{log.Replace("'", "'\\''", StringComparison.Ordinal)}'
            {(failControlRename ? "case \"$1 $5\" in 'mv '*.control) exit 7;; esac" : string.Empty)}
            exec "$@"

            """));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(sudo, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var environment = new Dictionary<string, string?>(s_environment)
        {
            ["PATH"] = directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        };
        return (environment, log);
    }
}
