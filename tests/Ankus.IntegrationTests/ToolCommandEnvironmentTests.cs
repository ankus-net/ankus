using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Init registers the environment-selected installation and home, including relative executable paths.
    /// </summary>
    /// <param name="relative">Whether the executable path is relative to the caller's directory.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EnvironmentInitRegistersSelectedVersion(bool relative)
    {
        string home = CreateDirectory();
        Dictionary<string, string?> environment = EnvironmentConfiguration(home);
        environment["PG" + MajorText() + "_PG_CONFIG"] = relative
            ? Path.GetRelativePath(s_root, s_installation.PgConfigPath)
            : s_installation.PgConfigPath;
        // An unintended download selection must fail before HTTP requests or installation creation.
        ProcessResult initialized = await PackageProcessRunner.RunAsync(s_tool, ["init", "--jobs", "0"], environment,
            context.CancellationToken, workingDirectory: s_root);
        Assert.AreEqual(0, initialized.ExitCode, initialized.StandardOutput + initialized.StandardError);
        var registry = new PostgresRegistry(home);
        Assert.AreEqual(s_installation.PgConfigPath, registry.GetPath(s_installation.Version.Major));
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(registry.ConfigurationPath, context.CancellationToken));
        Assert.HasCount(1, document.RootElement.EnumerateObject());
        Assert.HasCount(2, Directory.GetFileSystemEntries(home));
        ProcessResult info = await PackageProcessRunner.RunAsync(s_tool, ["info", "--pg", MajorText()], environment,
            context.CancellationToken, workingDirectory: s_root);
        Assert.AreEqual(0, info.ExitCode, info.StandardError);
        Assert.Contains(s_installation.PgConfigPath, info.StandardOutput);
        Assert.DoesNotContain("download", initialized.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A process-local home controls read-only registry lookup, including caller-relative paths.
    /// </summary>
    /// <param name="relative">Whether the environment home is relative to the command's working directory.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EnvironmentHomeControlsRegistryLookup(bool relative)
    {
        string directory = CreateDirectory();
        string home = Path.Join(directory, "environment home");
        Directory.CreateDirectory(home);
        string original = new JsonObject
        {
            [s_postgresKey] = s_installation.PgConfigPath,
            ["basePort"] = 65517,
        }.ToJsonString();
        string configuration = Path.Join(home, "config.json");
        await File.WriteAllTextAsync(configuration, original, context.CancellationToken);
        Dictionary<string, string?> environment = EnvironmentConfiguration(home);
        if (relative)
        {
            environment["ANKUS_HOME"] = Path.GetRelativePath(s_root, home);
        }

        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, ["info", "--pg", MajorText()], environment,
            context.CancellationToken, workingDirectory: s_root);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains(configuration, result.StandardError);
        Assert.Contains("'basePort' must be an integer from 0 through 65516", result.StandardError);
        Assert.Contains(s_installation.PgConfigPath, result.StandardOutput);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configuration, context.CancellationToken));
        Assert.HasCount(1, Directory.GetFileSystemEntries(home));
    }

    /// <summary>
    /// Unselected version environment defaults participate in init validation without triggering downloads or partial registration.
    /// </summary>
    [TestMethod]
    public async Task EnvironmentInitRejectsMismatchedVersionDefaultsAtomically()
    {
        for (int major = 13; major <= 19; major++)
        {
            if (major == s_installation.Version.Major)
            {
                continue;
            }

            string home = CreateDirectory();
            string configuration = Path.Join(home, "config.json");
            const string Original = "{\"custom\":\"preserve environment failure\"}";
            await File.WriteAllTextAsync(configuration, Original, context.CancellationToken);
            Dictionary<string, string?> environment = EnvironmentConfiguration(home);
            environment["PG" + major.ToString(CultureInfo.InvariantCulture) + "_PG_CONFIG"] = s_installation.PgConfigPath;
            ProcessResult result = await PackageProcessRunner.RunAsync(s_tool,
                ["init", "--home", home, s_postgresOption, s_installation.PgConfigPath], environment,
                context.CancellationToken, workingDirectory: s_root);
            Assert.AreNotEqual(0, result.ExitCode, "Environment default for PostgreSQL " + major.ToString(CultureInfo.InvariantCulture));
            Assert.Contains(s_installation.PgConfigPath, result.StandardError);
            Assert.Contains("is PostgreSQL " + MajorText() + "; PostgreSQL " + major.ToString(CultureInfo.InvariantCulture) + " was expected",
                result.StandardError);
            Assert.AreEqual(Original, await File.ReadAllTextAsync(configuration, context.CancellationToken));
            Assert.HasCount(1, Directory.GetFileSystemEntries(home));
            Assert.DoesNotContain("download", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Explicit init options override environment defaults and preserve the environment-selected registry.
    /// </summary>
    [TestMethod]
    public async Task ExplicitEnvironmentOptionsRetainPrecedence()
    {
        string home = CreateDirectory();
        string other = CreateDirectory();
        string configuration = Path.Join(other, "config.json");
        const string Original = "{\"custom\":true}";
        await File.WriteAllTextAsync(configuration, Original, context.CancellationToken);
        Dictionary<string, string?> environment = EnvironmentConfiguration(other);
        environment["PG" + MajorText() + "_PG_CONFIG"] = "missing-environment-config";
        ProcessResult initialized = await PackageProcessRunner.RunAsync(s_tool,
            ["init", "--home", home, s_postgresOption, s_installation.PgConfigPath], environment,
            context.CancellationToken, workingDirectory: s_root);
        Assert.AreEqual(0, initialized.ExitCode, initialized.StandardOutput + initialized.StandardError);
        Assert.AreEqual(s_installation.PgConfigPath, new PostgresRegistry(home).GetPath(s_installation.Version.Major));
        Assert.AreEqual(Original, await File.ReadAllTextAsync(configuration, context.CancellationToken));
        ProcessResult info = await PackageProcessRunner.RunAsync(s_tool, ["info", "--home", home, "--pg", MajorText()], environment,
            context.CancellationToken, workingDirectory: s_root);
        Assert.AreEqual(0, info.ExitCode, info.StandardError);
        Assert.Contains(s_installation.PgConfigPath, info.StandardOutput);
    }

    /// <summary>
    /// Invalid environment paths and mismatched versions preserve the registry and never select implicit downloads.
    /// </summary>
    /// <param name="value">The invalid path, or a marker selecting the actual installation under another major.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("wrong-major")]
    public async Task EnvironmentInitRejectsInvalidDefaults(string value)
    {
        string home = CreateDirectory();
        string configuration = Path.Join(home, "config.json");
        const string Original = "{\"custom\":\"preserve invalid default\"}";
        await File.WriteAllTextAsync(configuration, Original, context.CancellationToken);
        Dictionary<string, string?> environment = EnvironmentConfiguration(home);
        int major = value == "wrong-major" ? (s_installation.Version.Major == 13 ? 14 : 13) : s_installation.Version.Major;
        environment["PG" + major.ToString(CultureInfo.InvariantCulture) + "_PG_CONFIG"] = value == "wrong-major"
            ? s_installation.PgConfigPath : value;
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, ["init", "--jobs", "0"], environment,
            context.CancellationToken, workingDirectory: s_root);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.AreEqual(Original, await File.ReadAllTextAsync(configuration, context.CancellationToken));
        Assert.HasCount(1, Directory.GetFileSystemEntries(home));
        Assert.DoesNotContain("download", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        if (value == "wrong-major")
        {
            Assert.Contains("; PostgreSQL " + major.ToString(CultureInfo.InvariantCulture) + " was expected.", result.StandardError);
        }
        else
        {
            Assert.DoesNotContain("Jobs", result.StandardError);
        }
    }

    /// <summary>
    /// Creates isolated per-child environment selections without changing the parallel test host's environment.
    /// </summary>
    /// <param name="home">The environment-selected registry.</param>
    /// <returns>The complete child-process environment overrides.</returns>
    private static Dictionary<string, string?> EnvironmentConfiguration(string home)
    {
        var environment = new Dictionary<string, string?>(s_environment) { ["ANKUS_HOME"] = home };
        for (int major = 13; major <= 19; major++)
        {
            environment["PG" + major.ToString(CultureInfo.InvariantCulture) + "_PG_CONFIG"] = null;
        }

        return environment;
    }
}
