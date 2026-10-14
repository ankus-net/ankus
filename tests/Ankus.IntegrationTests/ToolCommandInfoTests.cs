using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Each pgrx-compatible positional selection prints exactly one installation value.
    /// </summary>
    /// <param name="command">The single-value command.</param>
    /// <param name="prefixed">Whether the major uses pgrx's pg prefix.</param>
    [TestMethod]
    [DataRow("path", false)]
    [DataRow("path", true)]
    [DataRow("pg-config", false)]
    [DataRow("pg-config", true)]
    [DataRow("version", false)]
    [DataRow("version", true)]
    public async Task InfoSubcommandsReturnSingleValues(string command, bool prefixed)
    {
        string selection = (prefixed ? "pg" : string.Empty) + MajorText();
        ProcessResult result = await InvokeAsync(["info", command, selection, "--home", s_home], context.CancellationToken);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(await ExpectedInfoValueAsync(command) + Environment.NewLine, result.StandardOutput);
        Assert.IsEmpty(result.StandardError);
    }

    /// <summary>
    /// Shared installation options work before and after an information subcommand.
    /// </summary>
    /// <param name="command">The single-value command.</param>
    /// <param name="before">Whether installation options precede the subcommand.</param>
    [TestMethod]
    [DataRow("path", false)]
    [DataRow("path", true)]
    [DataRow("pg-config", false)]
    [DataRow("pg-config", true)]
    [DataRow("version", false)]
    [DataRow("version", true)]
    public async Task InfoSubcommandsHonorExplicitInstallation(string command, bool before)
    {
        string[] options = ["--pg", MajorText(), "--pg-config", s_installation.PgConfigPath, "--home", CreateDirectory()];
        ProcessResult result = await InvokeAsync(before ? ["info", .. options, command] : ["info", command, .. options],
            context.CancellationToken);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(await ExpectedInfoValueAsync(command) + Environment.NewLine, result.StandardOutput);
        Assert.IsEmpty(result.StandardError);
    }

    /// <summary>
    /// Nested information commands honor an evaluated project's relative executable selection before the registry.
    /// </summary>
    /// <param name="command">The single-value command.</param>
    [TestMethod]
    [DataRow("path")]
    [DataRow("pg-config")]
    [DataRow("version")]
    public async Task InfoSubcommandsUseProjectSelection(string command)
    {
        string directory = CreateDirectory();
        string home = CreateDirectory();
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), new XElement("PropertyGroup",
            new XElement("TargetFramework", "net10.0"), new XElement("AnkusPostgresMajor", MajorText()),
            new XElement("AnkusPgConfigPath", Path.GetRelativePath(directory, s_installation.PgConfigPath)))))
            .Save(Path.Join(directory, "Selection.csproj"));
        string configuration = Path.Join(home, "config.json");
        string original = new JsonObject { [s_postgresKey] = "missing-scriptable-registration" }.ToJsonString();
        await File.WriteAllTextAsync(configuration, original, context.CancellationToken);
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, ["info", command, "--home", home], s_environment,
            context.CancellationToken, workingDirectory: directory);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(await ExpectedInfoValueAsync(command) + Environment.NewLine, result.StandardOutput);
        Assert.IsEmpty(result.StandardError);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configuration, context.CancellationToken));
    }

    /// <summary>
    /// A contradictory project major is validated rather than replaced with the default version.
    /// </summary>
    [TestMethod]
    public async Task InfoSubcommandsValidateProjectMajor()
    {
        string directory = CreateDirectory();
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), new XElement("PropertyGroup",
            new XElement("TargetFramework", "net10.0"), new XElement("AnkusPostgresMajor", DifferentMajor()),
            new XElement("AnkusPgConfigPath", s_installation.PgConfigPath))))
            .Save(Path.Join(directory, "Selection.csproj"));
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, ["info", "version", "--home", s_home], s_environment,
            context.CancellationToken, workingDirectory: directory);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsEmpty(result.StandardOutput);
        Assert.Contains("Expected PostgreSQL " + DifferentMajor().ToString(CultureInfo.InvariantCulture), result.StandardError);
        Assert.Contains(s_installation.PgConfigPath, result.StandardError);
    }

    /// <summary>
    /// A private child environment selects the information command's registry without writes.
    /// </summary>
    [TestMethod]
    public async Task InfoSubcommandsUseEnvironmentHome()
    {
        string home = CreateDirectory();
        string configuration = Path.Join(home, "config.json");
        string original = new JsonObject { [s_postgresKey] = s_installation.PgConfigPath }.ToJsonString();
        await File.WriteAllTextAsync(configuration, original, context.CancellationToken);
        Dictionary<string, string?> environment = EnvironmentConfiguration(home);
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, ["info", "pg-config", MajorText()], environment,
            context.CancellationToken, workingDirectory: s_root);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(s_installation.PgConfigPath + Environment.NewLine, result.StandardOutput);
        Assert.IsEmpty(result.StandardError);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configuration, context.CancellationToken));
        Assert.HasCount(1, Directory.GetFileSystemEntries(home));
    }

    /// <summary>
    /// Contradictory explicit major selectors fail before lookup and leave the registry unchanged.
    /// </summary>
    [TestMethod]
    public async Task InfoSubcommandsRejectConflictingSelection()
    {
        string original = await File.ReadAllTextAsync(Path.Join(s_home, "config.json"), context.CancellationToken);
        ProcessResult result = await InvokeAsync(["info", "pg-config", MajorText(), "--pg",
            DifferentMajor().ToString(CultureInfo.InvariantCulture), "--home", s_home], context.CancellationToken);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsEmpty(result.StandardOutput);
        Assert.Contains("Select the same PostgreSQL major for the version argument and --pg.", result.StandardError);
        Assert.AreEqual(original, await File.ReadAllTextAsync(Path.Join(s_home, "config.json"), context.CancellationToken));
    }

    /// <summary>
    /// Every supported selector is parsed and checked against the actual explicitly selected installation.
    /// </summary>
    [TestMethod]
    public async Task InfoSubcommandsValidateSupportedMajorRange()
    {
        for (int major = 13; major <= 19; major++)
        {
            ProcessResult result = await InvokeAsync(["info", "pg-config", "pg" + major.ToString(CultureInfo.InvariantCulture),
                "--pg-config", s_installation.PgConfigPath, "--home", s_home], context.CancellationToken);
            if (major == s_installation.Version.Major)
            {
                Assert.AreEqual(0, result.ExitCode, result.StandardError);
                Assert.AreEqual(s_installation.PgConfigPath + Environment.NewLine, result.StandardOutput);
                Assert.IsEmpty(result.StandardError);
            }
            else
            {
                Assert.AreNotEqual(0, result.ExitCode);
                Assert.IsEmpty(result.StandardOutput);
                Assert.Contains("Expected PostgreSQL " + major.ToString(CultureInfo.InvariantCulture), result.StandardError);
                Assert.Contains(s_installation.PgConfigPath, result.StandardError);
                Assert.Contains(s_installation.Label, result.StandardError);
            }
        }
    }

    /// <summary>
    /// Invalid selectors cannot produce a successful information value.
    /// </summary>
    /// <param name="selection">The invalid positional selector.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("12")]
    [DataRow("20")]
    [DataRow("2147483648")]
    [DataRow("pg")]
    [DataRow("PG18")]
    [DataRow("18.6")]
    [DataRow("invalid")]
    public async Task InfoSubcommandsRejectInvalidMajor(string selection)
    {
        ProcessResult result = await InvokeAsync(["info", "version", selection, "--home", s_home], context.CancellationToken);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsEmpty(result.StandardOutput);
        Assert.Contains("Select a PostgreSQL major from 13 through 19", result.StandardError);
    }

    /// <summary>
    /// A broken registered installation fails without emitting a partial scripting value or changing its registration.
    /// </summary>
    [TestMethod]
    public async Task InfoSubcommandsRejectBrokenRegistration()
    {
        string home = CreateDirectory();
        string configuration = Path.Join(home, "config.json");
        string original = new JsonObject { [s_postgresKey] = "missing-info-pg-config" }.ToJsonString();
        await File.WriteAllTextAsync(configuration, original, context.CancellationToken);
        ProcessResult result = await InvokeAsync(["info", "path", MajorText(), "--home", home], context.CancellationToken);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsEmpty(result.StandardOutput);
        Assert.Contains("The specified pg_config executable was not found.", result.StandardError);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configuration, context.CancellationToken));
    }

    /// <summary>
    /// Supplies expected values from the authoritative executable and independently traversed filesystem path.
    /// </summary>
    /// <param name="command">The single-value command.</param>
    /// <returns>The exact successful stdout value before its newline.</returns>
    private async Task<string> ExpectedInfoValueAsync(string command)
    {
        if (command == "path")
        {
            return new FileInfo(s_installation.PgConfigPath).Directory!.Parent!.FullName;
        }

        if (command == "pg-config")
        {
            return s_installation.PgConfigPath;
        }

        ProcessResult version = await PackageProcessRunner.RunAsync(s_installation.PgConfigPath, ["--version"], s_environment,
            context.CancellationToken);
        version.EnsureSuccess(s_installation.PgConfigPath, ["--version"]);
        return version.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].Trim();
    }
}
