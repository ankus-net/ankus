using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Ankus.PgConfig;

namespace Ankus.Build.Tests;

/// <summary>
/// Evaluates the SDK's actual import order to verify where an authored PostgreSQL major is honored.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed class SdkPostgresSelectionTests(TestContext context)
{
    /// <summary>
    /// With the packaged SDK, a major authored in Directory.Build.props, the project file or Directory.Build.targets is
    /// explicit for the build and for ankus project selection. A supplied pg_config selects the major only when none was
    /// authored, without being executed otherwise, and a command-line major wins over every project import.
    /// </summary>
    /// <param name="props">The major authored in Directory.Build.props, or empty.</param>
    /// <param name="project">The major authored in the project file, or empty.</param>
    /// <param name="targets">The property authored in Directory.Build.targets, or empty.</param>
    /// <param name="pgConfig">Whether the project supplies a pg_config that reports PostgreSQL 15.</param>
    /// <param name="global">The command-line major, or empty.</param>
    /// <param name="expected">The major selected for compiler constants and native targets.</param>
    /// <param name="specified">Whether the selection is explicit rather than the SDK default.</param>
    [TestMethod]
    [DataRow("", "", "", false, "", "18", false)]
    [DataRow("", "", "", true, "", "15", false)]
    [DataRow("17", "", "", true, "", "17", true)]
    [DataRow("", "16", "", true, "", "16", true)]
    [DataRow("", "", "<AnkusPostgresMajor>14</AnkusPostgresMajor>", false, "", "14", true)]
    [DataRow("", "", "<AnkusPostgresMajor>14</AnkusPostgresMajor>", true, "", "14", true)]
    [DataRow("", "", "<AnkusPostgresMajor Condition=\"'$(AnkusPostgresMajor)' == ''\">13</AnkusPostgresMajor>", true, "", "13", true)]
    [DataRow("17", "", "<AnkusPostgresMajor>14</AnkusPostgresMajor>", false, "", "14", true)]
    [DataRow("", "", "<AnkusPostgresMajor>14</AnkusPostgresMajor>", true, "19", "19", true)]
    public Task PackagedSdkHonorsAuthoredMajorFromEveryProjectImport(string props, string project, string targets, bool pgConfig,
        string global, string expected, bool specified)
        => AssertSelectionAsync(development: false, props, project, targets, pgConfig, global, expected, specified);

    /// <summary>
    /// Repository projects import the SDK targets from their body; their Directory.Build.targets and later project
    /// properties are honored in the same way as with the packaged SDK.
    /// </summary>
    /// <param name="project">The major authored after the SDK targets import, or empty.</param>
    /// <param name="targets">The property authored in Directory.Build.targets, or empty.</param>
    /// <param name="pgConfig">Whether the project supplies a pg_config that reports PostgreSQL 15.</param>
    /// <param name="expected">The major selected for compiler constants and native targets.</param>
    /// <param name="specified">Whether the selection is explicit rather than the SDK default.</param>
    [TestMethod]
    [DataRow("", "", false, "18", false)]
    [DataRow("", "", true, "15", false)]
    [DataRow("16", "", true, "16", true)]
    [DataRow("", "<AnkusPostgresMajor>14</AnkusPostgresMajor>", true, "14", true)]
    public Task RepositorySdkHonorsAuthoredMajorFromEveryProjectImport(string project, string targets, bool pgConfig,
        string expected, bool specified)
        => AssertSelectionAsync(development: true, string.Empty, project, targets, pgConfig, string.Empty, expected, specified);

    /// <summary>
    /// Test and benchmark publications define <c>ANKUS_TESTS</c> and <c>ANKUS_BENCHMARKS</c> for conditional compilation, as
    /// pgrx's <c>pg_test</c> feature does; ordinary builds define neither.
    /// </summary>
    /// <param name="includeTests">The AnkusIncludeTests value, or empty.</param>
    /// <param name="includeBenchmarks">The AnkusIncludeBenchmarks value, or empty.</param>
    /// <param name="expected">The defined symbols, separated by commas.</param>
    [TestMethod]
    [DataRow("", "", "")]
    [DataRow("false", "false", "")]
    [DataRow("true", "", "ANKUS_TESTS")]
    [DataRow("True", "false", "ANKUS_TESTS")]
    [DataRow("", "true", "ANKUS_BENCHMARKS")]
    public async Task TestAndBenchmarkPublicationsDefineTheirSymbols(string includeTests, string includeBenchmarks, string expected)
    {
        CancellationToken token = context.CancellationToken;
        string directory = Directory.CreateTempSubdirectory("ankus-sdk-symbols-").FullName;
        try
        {
            (string sdk, string root) = await CreateWorkspaceAsync(directory, token);
            string path = Path.Join(root, "Extension.csproj");
            new XDocument(new XElement("Project",
                new XElement("Import", new XAttribute("Project", Path.Join(sdk, "Sdk", "Sdk.props"))),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("AnkusPostgresMajor", "17")),
                new XElement("Import", new XAttribute("Project", Path.Join(sdk, "Sdk", "Sdk.targets"))))).Save(path);
            List<string> arguments = ["msbuild", path, "-nologo", "-nodeReuse:false", "-target:_AddAnkusPostgresDefineConstants",
                "-getProperty:DefineConstants"];
            if (includeTests.Length != 0)
            {
                arguments.Add("-property:AnkusIncludeTests=" + includeTests);
            }

            if (includeBenchmarks.Length != 0)
            {
                arguments.Add("-property:AnkusIncludeBenchmarks=" + includeBenchmarks);
            }

            string output = await NativeBindingLayoutCommand.RunProcessAsync("dotnet", arguments, root, token);
            string[] symbols = output.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("ANKUS_PG17", symbols);
            Assert.AreSequenceEqual(expected.Split(',', StringSplitOptions.RemoveEmptyEntries),
                [.. symbols.Where(static symbol => symbol is "ANKUS_TESTS" or "ANKUS_BENCHMARKS")]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Copies the packaged SDK and pins the test's .NET SDK for an isolated extension project.
    /// </summary>
    /// <param name="directory">The test-owned directory.</param>
    /// <param name="token">Cancels the file writes.</param>
    /// <returns>The copied SDK directory and the extension project's directory.</returns>
    private static async Task<(string Sdk, string Root)> CreateWorkspaceAsync(string directory, CancellationToken token)
    {
        string source = Path.Join(AppContext.BaseDirectory, "AnkusSdk");
        string sdk = Path.Join(directory, "sdk");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Join(sdk, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        // Packing supplies this file; the selection does not depend on the package version.
        await File.WriteAllTextAsync(Path.Join(sdk, "Sdk", "Ankus.Version.props"),
            "<Project><PropertyGroup><AnkusVersion>0.0.0-test</AnkusVersion></PropertyGroup></Project>", token);
        string root = Directory.CreateDirectory(Path.Join(directory, "extension")).FullName;
        string? version = Assert.ContainsSingle(typeof(SdkPostgresSelectionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(static metadata => metadata.Key == "AnkusTestSdkVersion")).Value;
        Assert.IsNotNull(version);
        await File.WriteAllTextAsync(Path.Join(root, "global.json"),
            JsonSerializer.Serialize(new { sdk = new { version, rollForward = "disable", allowPrerelease = false } }), token);
        return (sdk, root);
    }

    /// <summary>
    /// Creates an extension with the requested authored values, then checks the built selection and the ankus tool's
    /// evaluation-only view of the same project.
    /// </summary>
    /// <param name="development">Whether to import the repository targets from the project body instead of the packaged SDK.</param>
    /// <param name="props">The major authored in Directory.Build.props, or empty.</param>
    /// <param name="project">The major authored in the project file, or empty.</param>
    /// <param name="targets">The property authored in Directory.Build.targets, or empty.</param>
    /// <param name="pgConfig">Whether the project supplies a pg_config that reports PostgreSQL 15.</param>
    /// <param name="global">The command-line major, or empty.</param>
    /// <param name="expected">The major selected for compiler constants and native targets.</param>
    /// <param name="specified">Whether the selection is explicit rather than the SDK default.</param>
    /// <returns>A task that completes after both views are verified and the project is removed.</returns>
    private async Task AssertSelectionAsync(bool development, string props, string project, string targets, bool pgConfig,
        string global, string expected, bool specified)
    {
        CancellationToken token = context.CancellationToken;
        string directory = Directory.CreateTempSubdirectory("ankus-sdk-selection-").FullName;
        try
        {
            (string sdk, string root) = await CreateWorkspaceAsync(directory, token);

            // Isolate the SDK default from CI's environment selection before any authored value.
            new XDocument(new XElement("Project",
                new XElement("PropertyGroup", new XElement("AnkusPostgresMajor", string.Empty), new XElement("AnkusPgConfigPath", string.Empty)),
                new XElement("PropertyGroup", props.Length == 0 ? null : new XElement("AnkusPostgresMajor", props))))
                .Save(Path.Join(root, "Directory.Build.props"));
            await File.WriteAllTextAsync(Path.Join(root, "Directory.Build.targets"),
                "<Project><PropertyGroup>" + targets + "</PropertyGroup></Project>", token);
            string tools = Directory.CreateDirectory(Path.Join(directory, "postgres", "bin")).FullName;
            string? executable = pgConfig ? await WritePgConfigAsync(tools, token) : null;
            var authored = new XElement("PropertyGroup",
                project.Length == 0 ? null : new XElement("AnkusPostgresMajor", project),
                executable is null ? null : new XElement("AnkusPgConfigPath", executable));
            var framework = new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"));
            string path = Path.Join(root, "Extension.csproj");
            (development
                ? new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), framework,
                    new XElement("Import", new XAttribute("Project", Path.Join(sdk, "Ankus.Sdk.targets"))), authored))
                : new XDocument(new XElement("Project",
                    new XElement("Import", new XAttribute("Project", Path.Join(sdk, "Sdk", "Sdk.props"))), framework, authored,
                    new XElement("Import", new XAttribute("Project", Path.Join(sdk, "Sdk", "Sdk.targets")))))).Save(path);

            List<string> arguments = ["msbuild", path, "-nologo", "-nodeReuse:false", "-target:_AddAnkusPostgresDefineConstants",
                "-getProperty:AnkusPostgresMajor,_AnkusPostgresMajorWasSpecified,DefineConstants"];
            if (global.Length != 0)
            {
                arguments.Add("-property:AnkusPostgresMajor=" + global);
            }

            string output = await NativeBindingLayoutCommand.RunProcessAsync("dotnet", arguments, root, token);
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement properties = document.RootElement.GetProperty("Properties");
            Assert.AreEqual(expected, properties.GetProperty("AnkusPostgresMajor").GetString());
            Assert.AreEqual(specified, bool.Parse(properties.GetProperty("_AnkusPostgresMajorWasSpecified").GetString()!));
            Assert.AreSequenceEqual<string>(["ANKUS_PG" + expected], [.. properties.GetProperty("DefineConstants").GetString()!
                .Split(';', StringSplitOptions.RemoveEmptyEntries).Where(static symbol => symbol.StartsWith("ANKUS_PG", StringComparison.Ordinal))]);
            Assert.AreEqual(pgConfig && !specified, File.Exists(Path.Join(tools, "invoked")));

            PostgresProjectSettings settings = await PostgresProjectSettings.ReadAsync(path, "Debug",
                global.Length == 0 ? null : int.Parse(global, CultureInfo.InvariantCulture), token);
            Assert.AreEqual(specified, settings.HasExplicitPostgresMajor);
            Assert.AreEqual(specified ? int.Parse(expected, CultureInfo.InvariantCulture) : 18, settings.PostgresMajor);
            Assert.AreEqual(executable, settings.PgConfigPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Writes a pg_config stand-in that records its execution and reports PostgreSQL 15.
    /// </summary>
    /// <param name="directory">The installation's binary directory.</param>
    /// <param name="cancellationToken">Cancels writing the executable.</param>
    /// <returns>The absolute executable path.</returns>
    private static async Task<string> WritePgConfigAsync(string directory, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            string command = Path.Join(directory, "pg_config.cmd");
            await File.WriteAllTextAsync(command, "@echo off\r\ntype nul > \"%~dp0invoked\"\r\necho PostgreSQL 15.7\r\n", cancellationToken);
            return command;
        }

        string executable = Path.Join(directory, "pg_config");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\n: > \"$(dirname \"$0\")/invoked\"\necho 'PostgreSQL 15.7'\n", cancellationToken);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return executable;
    }
}
