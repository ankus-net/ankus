using System.Runtime.Versioning;
using System.Text.Json;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Keeps registered and managed installation discovery inside the selected home without process-wide mutation.
/// </summary>
/// <param name="context">The current test's cancellation scope.</param>
[TestClass]
public sealed class PostgresDiscoveryHomeTests(TestContext context)
{
    /// <summary>
    /// A major with no registration says how to install one or register an existing one.
    /// </summary>
    [TestMethod]
    public async Task UnregisteredMajorNamesBothWaysToRegisterIt()
    {
        string home = Directory.CreateTempSubdirectory("ankus discovery ").FullName;
        try
        {
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                new PostgresRegistry(home).GetAsync(18, context.CancellationToken));
            Assert.AreEqual("PostgreSQL 18 is not registered. Run 'ankus init --pg18 download' to install it, or " +
                "'ankus init --pg18 /path/to/pg_config' to use an existing installation.", error.Message);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// A <c>pg_config</c> without its server headers, as a client-only package installs, cannot build extensions. Discovery
    /// skips it, says why when nothing else is found, and selects it once the headers are present.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task DiscoverySkipsInstallationsWithoutServerHeaders()
    {
        string root = Directory.CreateTempSubdirectory("ankus discovery ").FullName;
        try
        {
            string include = Directory.CreateDirectory(Path.Join(root, "include", "server")).FullName;
            string pgConfig = Path.Join(Directory.CreateDirectory(Path.Join(root, "bin")).FullName, "pg_config");
            await File.WriteAllTextAsync(pgConfig, $"""
                #!/bin/sh
                case "$1" in
                  --version) echo "PostgreSQL 18.6" ;;
                  --includedir-server) echo "{include}" ;;
                  --cppflags) echo "" ;;
                  *) echo "{root}" ;;
                esac

                """, context.CancellationToken);
            File.SetUnixFileMode(pgConfig, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            FileNotFoundException error = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
                PostgresInstallation.DiscoverAsync(18, [pgConfig], context.CancellationToken));
            Assert.AreEqual($"PostgreSQL 18 was not found. '{pgConfig}' is PostgreSQL 18.6 without its server headers " +
                $"('{Path.Join(include, "postgres.h")}' is missing); install its server package to use it. Run 'ankus init " +
                "--pg18 download' to install it, or 'ankus init --pg18 /path/to/pg_config' to use an existing installation.", error.Message);
            await File.WriteAllTextAsync(Path.Join(include, "postgres.h"), "", context.CancellationToken);
            PostgresInstallation installation = await PostgresInstallation.DiscoverAsync(18, [pgConfig], context.CancellationToken);
            Assert.AreEqual(pgConfig, installation.PgConfigPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// An unavailable explicitly registered server cannot be replaced by a conventional installation.
    /// </summary>
    [TestMethod]
    public async Task MissingRegisteredServerDoesNotFallBackToAnotherHome()
    {
        string home = Directory.CreateTempSubdirectory("ankus discovery ").FullName;
        try
        {
            const string Missing = "missing/bin/pg_config";
            string configuration = JsonSerializer.Serialize(new Dictionary<string, string> { ["pg18"] = Missing });
            await File.WriteAllTextAsync(Path.Join(home, "config.json"), configuration, context.CancellationToken);
            FileNotFoundException error = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
                PostgresInstallation.DiscoverAsync(18, home, context.CancellationToken));
            Assert.AreEqual(Path.GetFullPath(Missing, home), error.FileName);
            Assert.Contains(Path.GetFullPath(Missing, home), error.Message);
            Assert.AreEqual(configuration, await File.ReadAllTextAsync(Path.Join(home, "config.json"), context.CancellationToken));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// Managed candidates precede conventional paths and come from the same home used by registration lookup.
    /// </summary>
    [TestMethod]
    public void ManagedCandidatesUseTheSelectedHome()
    {
        string home = Directory.CreateTempSubdirectory("ankus discovery ").FullName;
        try
        {
            string first = Path.Join(home, "postgres", "18.6");
            string second = Path.Join(home, "postgres", "18.5");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            string executable = OperatingSystem.IsWindows() ? "pg_config.exe" : "pg_config";
            string[] candidates = [.. PostgresDiscovery.GetCandidates(18, home)];
            Assert.AreSequenceEqual([Path.Join(first, "bin", executable), Path.Join(second, "bin", executable)], candidates.Take(2));
            Assert.IsFalse(File.Exists(Path.Join(home, "config.json")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
