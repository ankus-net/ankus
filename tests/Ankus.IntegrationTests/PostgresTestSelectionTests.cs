using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    private static readonly SelectionProjectPool s_selectionProjects = new();

    /// <summary>
    /// Plain dotnet test preserves project defaults and build overrides through publication and actual server execution.
    /// </summary>
    /// <param name="selection">The project or command-line property selecting the installation.</param>
    [TestMethod]
    [DataRow("project")]
    [DataRow("extension-project")]
    [DataRow("extension-relative")]
    [DataRow("project-relative")]
    [DataRow("import-relative")]
    [DataRow("build-major")]
    [DataRow("build-path")]
    [DataRow("project-path-only")]
    public async Task PlainTestHonorsProjectPostgresSelection(string selection)
    {
        CancellationToken token = context.CancellationToken;
        using SelectionProjectLease lease = await AcquireSelectionProjectAsync(s_selectionProjects, token);
        string output = lease.Directory;
        string properties = selection is "extension-project" or "extension-relative"
            ? Path.Join(output, "src", "TestCommandProbe", "TestCommandProbe.csproj")
            : Path.Join(output, "Directory.Build.props");
        string extensionDirectory = Path.Join(output, "src", "TestCommandProbe");
        string pgConfig = selection switch
        {
            "extension-relative" or "project-relative" => Path.GetRelativePath(extensionDirectory, s_installation.PgConfigPath),
            "import-relative" => "$(MSBuildThisFileDirectory)" + Path.GetRelativePath(output, s_installation.PgConfigPath),
            "build-path" => "missing-pg-config",
            _ => s_installation.PgConfigPath,
        };
        if (selection is "extension-relative" or "project-relative")
        {
            Assert.IsFalse(Path.IsPathRooted(pgConfig), "The consumer must exercise a genuinely relative installation path.");
            Assert.AreEqual(s_installation.PgConfigPath, Path.GetFullPath(pgConfig, extensionDirectory));
            if (selection == "project-relative")
            {
                Assert.AreEqual(s_installation.PgConfigPath, Path.GetFullPath(pgConfig,
                    Path.Join(output, "tests", "TestCommandProbe.Tests")));
            }
        }

        XDocument document = XDocument.Load(properties);
        var selectionProperties = new XElement("PropertyGroup");
        if (selection != "project-path-only")
        {
            selectionProperties.Add(new XElement("AnkusPostgresMajor",
                selection == "build-major" ? DifferentMajor() : s_installation.Version.Major));
        }

        selectionProperties.Add(new XElement("AnkusPgConfigPath", pgConfig));
        document.Root!.Add(selectionProperties);
        document.Save(properties);
        string[] arguments = selection switch
        {
            "build-major" => ["-p:AnkusPostgresMajor=" + MajorText()],
            "build-path" => ["-p:AnkusPgConfigPath=" + s_installation.PgConfigPath],
            _ => [],
        };
        Dictionary<string, string?> environment = SelectionEnvironment();
        ProcessResult result = await PackageProcessRunner.RunAsync("dotnet",
            ["test", .. arguments, "--filter", "FullyQualifiedName~SelectedVersionReachesPostgres", "--report-trx"],
            environment, token, workingDirectory: output);

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.Contains("succeeded: 1", result.StandardOutput);
        string[] reports = Directory.GetFiles(output, "*.trx", SearchOption.AllDirectories);
        string report = Assert.ContainsSingle(reports);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        XElement outcome = Assert.ContainsSingle(XDocument.Load(report).Descendants(ns + "UnitTestResult"));
        Assert.AreEqual("SelectedVersionReachesPostgres", (string?)outcome.Attribute("testName"));
        Assert.AreEqual("Passed", (string?)outcome.Attribute("outcome"));
    }

    /// <summary>
    /// CLI defaults use imported project settings, while explicit major and path selections override them.
    /// </summary>
    /// <param name="selection">The implicit project or explicit command selector.</param>
    [TestMethod]
    [DataRow("project")]
    [DataRow("extension-project")]
    [DataRow("explicit-major")]
    [DataRow("explicit-path")]
    [DataRow("forwarded-major")]
    [DataRow("forwarded-path")]
    [DataRow("forwarded-properties")]
    [DataRow("forwarded-last-major")]
    [DataRow("forwarded-quoted-major")]
    [DataRow("project-path-only")]
    public async Task CommandSelectionHonorsProjectPostgresVersion(string selection)
    {
        CancellationToken token = context.CancellationToken;
        using SelectionProjectLease lease = await AcquireSelectionProjectAsync(s_selectionProjects, token);
        string output = lease.Directory;
        string properties = selection == "extension-project"
            ? Path.Join(output, "src", "TestCommandProbe", "TestCommandProbe.csproj")
            : Path.Join(output, "Directory.Build.props");
        XDocument document = XDocument.Load(properties);
        var selectionProperties = new XElement("PropertyGroup");
        if (selection == "project-path-only")
        {
            selectionProperties.Add(new XElement("AnkusPgConfigPath", s_installation.PgConfigPath));
        }
        else
        {
            selectionProperties.Add(new XElement("AnkusPostgresMajor",
                selection is "project" or "extension-project" ? s_installation.Version.Major : DifferentMajor()));
        }

        document.Root!.Add(selectionProperties);
        document.Save(properties);
        string[] arguments = selection switch
        {
            "explicit-major" => ["--pg", MajorText()],
            "explicit-path" => ["--pg-config", s_installation.PgConfigPath],
            _ => [],
        };
        string project = Path.Join(output, "tests", "TestCommandProbe.Tests", "TestCommandProbe.Tests.csproj");
        string[] forwarded = selection switch
        {
            "forwarded-major" => ["-p:AnkusPostgresMajor=" + MajorText()],
            "forwarded-path" => ["-p:AnkusPgConfigPath=" + s_installation.PgConfigPath],
            "forwarded-properties" => ["/property:AnkusPostgresMajor=" + MajorText() + ";Configuration=Debug"],
            "forwarded-last-major" => ["-p:AnkusPostgresMajor=" + DifferentMajor(), "-p:AnkusPostgresMajor=" + MajorText()],
            "forwarded-quoted-major" => ["-p:AnkusPostgresMajor=\"" + MajorText() + "\""],
            _ => [],
        };
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool,
            ["test", "--home", s_home, .. arguments, "--", "--project", project,
                .. forwarded, "--filter", "FullyQualifiedName~SelectedVersionReachesPostgres"],
            SelectionEnvironment(), token, workingDirectory: CreateDirectory());

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.Contains($"Testing PostgreSQL {s_installation.Version}", result.StandardOutput);
        Assert.Contains(s_postgresKey + ": dotnet test exited 0.", result.StandardOutput);
        Assert.Contains("succeeded: 1", result.StandardOutput);
    }

    /// <summary>
    /// Implicit test selection follows solution files and evaluated SDK imports rather than literal SDK attributes.
    /// </summary>
    /// <param name="layout">The ordinary test project or solution layout.</param>
    [TestMethod]
    [DataRow("directory")]
    [DataRow("slnx")]
    [DataRow("sln")]
    [DataRow("sdk-element")]
    public async Task CommandSelectionAcceptsOrdinaryProjectLayouts(string layout)
    {
        CancellationToken token = context.CancellationToken;
        using SelectionProjectLease lease = await AcquireSelectionProjectAsync(s_selectionProjects, token);
        string output = lease.Directory;
        string extension = Path.Join(output, "src", "TestCommandProbe", "TestCommandProbe.csproj");
        XDocument document = XDocument.Load(extension);
        document.Root!.Add(new XElement("PropertyGroup", new XElement("AnkusPostgresMajor", s_installation.Version.Major),
            new XElement("AnkusPgConfigPath", s_installation.PgConfigPath)));
        if (layout == "sdk-element")
        {
            document.Root.Attribute("Sdk")!.Remove();
            document.Root.AddFirst(new XElement("Sdk", new XAttribute("Name", "Ankus.Sdk")));
        }

        document.Save(extension);
        string project = Path.Join(output, "tests", "TestCommandProbe.Tests", "TestCommandProbe.Tests.csproj");
        string[] selection = layout switch
        {
            "directory" => [],
            "slnx" => ["--solution", Path.Join(output, "TestCommandProbe.slnx")],
            "sdk-element" => ["--project", project],
            _ => ["--solution", Path.Join(output, "Selection.sln")],
        };
        if (layout == "sln")
        {
            (await RunDotnetAsync(["new", "sln", "--format", "sln", "--name", "Selection", "--output", output], token))
                .EnsureSuccess("dotnet", ["new", "sln"]);
            (await RunDotnetAsync(["sln", selection[1], "add", extension, project], token))
                .EnsureSuccess("dotnet", ["sln", "add"]);
        }

        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool,
            ["test", "--home", s_home, "--", .. selection,
                "--filter", "FullyQualifiedName~SelectedVersionReachesPostgres"],
            SelectionEnvironment(), token, workingDirectory: layout == "directory" ? output : CreateDirectory());

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.Contains($"Testing PostgreSQL {s_installation.Version}", result.StandardOutput);
        Assert.Contains("succeeded: 1", result.StandardOutput);
    }

    /// <summary>
    /// Conflicting CLI and build selectors fail before a test process starts or rewrites the caller's properties.
    /// </summary>
    /// <param name="conflict">The conflicting selection.</param>
    [TestMethod]
    [DataRow("major")]
    [DataRow("path")]
    [DataRow("configuration")]
    [DataRow("all")]
    [DataRow("empty-property")]
    [DataRow("invalid-property-name")]
    [DataRow("runtime")]
    [DataRow("self-contained")]
    public async Task CommandSelectionRejectsConflictingBuildProperties(string conflict)
    {
        string[] arguments = conflict switch
        {
            "major" => ["--pg", MajorText(), "--", "-p:AnkusPostgresMajor=" + DifferentMajor()],
            "path" => ["--pg-config", s_installation.PgConfigPath, "--", "-p:AnkusPgConfigPath=missing-pg-config"],
            "configuration" => ["-c", "Release", "--", "-p:Configuration=Debug"],
            "empty-property" => ["--", "-p:;AnkusPostgresMajor=" + MajorText() + ";;"],
            "invalid-property-name" => ["--", "-p:invalid name=value"],
            "runtime" => ["--", "-p:RuntimeIdentifier=unavailable-target"],
            "self-contained" => ["--", "-p:SelfContained=false"],
            _ => ["--all", "--", "-p:AnkusPostgresMajor=" + MajorText()],
        };
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, ["test", "--home", s_home, .. arguments],
            SelectionEnvironment(), context.CancellationToken, workingDirectory: CreateDirectory());

        Assert.AreNotEqual(0, result.ExitCode);
        string expected = conflict switch
        {
            "all" => "Use --all without",
            "empty-property" => "Forwarded MSBuild properties must contain name=value",
            "invalid-property-name" => "Forwarded MSBuild properties must contain name=value",
            "runtime" => "host RuntimeIdentifier",
            "self-contained" => "SelfContained=true",
            _ => "Select the same",
        };
        Assert.Contains(expected, result.StandardOutput + result.StandardError);
        Assert.DoesNotContain("Testing PostgreSQL", result.StandardOutput);
    }

    /// <summary>
    /// Cluster inspection uses the current project's major and installation without requiring --pg.
    /// </summary>
    /// <param name="command">The non-starting cluster command.</param>
    [TestMethod]
    [DataRow("info")]
    [DataRow("status")]
    [DataRow("stop")]
    public async Task ClusterCommandsHonorProjectSelection(string command)
    {
        string directory = CreateDirectory();
        new XDocument(new XElement("Project", new XElement("PropertyGroup",
            new XElement("AnkusPostgresMajor", s_installation.Version.Major),
            new XElement("AnkusPgConfigPath", s_installation.PgConfigPath)))).Save(Path.Join(directory, "Selection.csproj"));
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, [command, "--home", directory],
            SelectionEnvironment(), context.CancellationToken, workingDirectory: directory);

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.Contains(command == "info" ? "PostgreSQL: " + s_installation.Version : s_postgresKey + ":", result.StandardOutput);
        if (command == "info")
        {
            Assert.Contains(s_installation.PgConfigPath, result.StandardOutput);
        }
        else
        {
            Assert.Contains("stopped", result.StandardOutput);
        }

        Assert.IsFalse(File.Exists(Path.Join(directory, "config.json")));
    }

    /// <summary>
    /// Standalone directories and conflicting project layouts use the documented PostgreSQL 18 fallback.
    /// </summary>
    /// <param name="layout">The absence or ambiguity of project selection.</param>
    [TestMethod]
    [DataRow("empty")]
    [DataRow("unrelated")]
    [DataRow("projects")]
    public async Task UnselectedProjectLayoutsRetainDefaultMajor(string layout)
    {
        string directory = CreateDirectory();
        if (layout == "unrelated")
        {
            await File.WriteAllTextAsync(Path.Join(directory, "Library.csproj"), "<Project />", context.CancellationToken);
        }
        else if (layout == "projects")
        {
            await File.WriteAllTextAsync(Path.Join(directory, "First.csproj"),
                "<Project><PropertyGroup><AnkusPostgresMajor>13</AnkusPostgresMajor></PropertyGroup></Project>", context.CancellationToken);
            await File.WriteAllTextAsync(Path.Join(directory, "Second.csproj"),
                "<Project><PropertyGroup><AnkusPostgresMajor>19</AnkusPostgresMajor></PropertyGroup></Project>", context.CancellationToken);
        }

        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, ["info", "--home", directory],
            SelectionEnvironment(), context.CancellationToken, workingDirectory: directory);
        string output = result.StandardOutput + result.StandardError;
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("PostgreSQL 18 is not registered", output);
        Assert.DoesNotContain("Specify --project or --solution", output);
        Assert.DoesNotContain("different PostgreSQL installations", output);
    }

    /// <summary>
    /// Selected solutions cannot silently replace conflicting PostgreSQL versions with the default major.
    /// </summary>
    /// <param name="format">The ordinary .NET solution format.</param>
    [TestMethod]
    [DataRow("slnx")]
    [DataRow("sln")]
    public async Task ConflictingSolutionSelectionRequiresExplicitMajor(string format)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        await File.WriteAllTextAsync(Path.Join(directory, "First.csproj"),
            "<Project><PropertyGroup><AnkusPostgresMajor>17</AnkusPostgresMajor></PropertyGroup></Project>", token);
        await File.WriteAllTextAsync(Path.Join(directory, "Second.csproj"),
            "<Project><PropertyGroup><AnkusPostgresMajor>16</AnkusPostgresMajor></PropertyGroup></Project>", token);
        string solution = format == "slnx"
            ? "<Solution><Project Path=\"First.csproj\" /><Project Path=\"Second.csproj\" /></Solution>"
            : """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "First", "First.csproj", "{E133CE62-9517-4A44-A61D-220E7F858891}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Second", "Second.csproj", "{D7D20E80-46BD-4826-9D23-AF28B7804D27}"
                EndProject
                Global
                EndGlobal
                """;
        await File.WriteAllTextAsync(Path.Join(directory, "Selection." + format), solution, token);
        string[] commands = ["info", "test", "start", "stop", "status"];
        foreach (string command in commands)
        {
            ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, [command, "--home", directory],
                SelectionEnvironment(), token, workingDirectory: directory);
            Assert.AreNotEqual(0, result.ExitCode, command);
            Assert.IsEmpty(result.StandardOutput, command);
            Assert.Contains("different PostgreSQL installations", result.StandardError, command);
            Assert.DoesNotContain("PostgreSQL 18 is not registered", result.StandardError, command);
        }

        ProcessResult explicitSelection = await PackageProcessRunner.RunAsync(s_tool,
            ["info", "--home", s_home, "--pg", MajorText()], SelectionEnvironment(), token, workingDirectory: directory);
        Assert.AreEqual(0, explicitSelection.ExitCode, explicitSelection.StandardError);
        Assert.DoesNotContain("different PostgreSQL installations", explicitSelection.StandardError);
        Assert.Contains(s_installation.Version.ToString(), explicitSelection.StandardOutput);
    }

    /// <summary>
    /// Runtime configuration follows property changes and removals on incremental builds of a packed consumer.
    /// </summary>
    [TestMethod]
    public async Task TestHostSelectionTracksBuildProperties()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "Host.csproj");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("OutputType", "Exe")),
            new XElement("ItemGroup", new XElement("PackageReference", new XAttribute("Include", "Ankus.Testing"),
                new XAttribute("Version", s_version)))))
            .Save(project);
        await File.WriteAllTextAsync(Path.Join(directory, "Program.cs"), "System.Console.WriteLine(42);", token);
        foreach (string major in new[] { "17", "18", "" })
        {
            string path = major.Length == 0 ? "" : "postgres " + major + "/pg_config";
            ProcessResult result = await RunDotnetAsync(["build", project, "-p:AnkusPostgresMajor=" + major,
                "-p:AnkusPgConfigPath=" + path], token);
            Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
            using JsonDocument config = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Join(directory, "bin", "Debug", "net10.0", "Host.runtimeconfig.json"), token));
            JsonElement settings = config.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
            if (major.Length == 0)
            {
                Assert.IsFalse(settings.TryGetProperty("Ankus.Testing.PostgresMajor", out _));
                Assert.IsFalse(settings.TryGetProperty("Ankus.Testing.PgConfigPath", out _));
            }
            else
            {
                Assert.AreEqual(major, settings.GetProperty("Ankus.Testing.PostgresMajor").ToString());
                Assert.AreEqual(Path.GetFullPath(path, directory), settings.GetProperty("Ankus.Testing.PgConfigPath").GetString());
            }
        }
    }

    /// <summary>
    /// Metadata-only commands honor evaluated version defaults without requiring a registered server.
    /// </summary>
    /// <param name="command">The metadata-only command.</param>
    [TestMethod]
    [DataRow("get")]
    [DataRow("regress")]
    public async Task OfflineCommandsUseEvaluatedPostgresMajor(string command)
    {
        string root = CreateDirectory();
        string project = Path.Join(root, "Metadata.csproj");
        await File.WriteAllTextAsync(project, """
            <Project>
              <PropertyGroup>
                <AnkusPostgresMajor>13</AnkusPostgresMajor>
                <AnkusPostgresMajor Condition="'$(Configuration)' == 'Shipping'">17</AnkusPostgresMajor>
                <AnkusExtensionName>selection_pg$(AnkusPostgresMajor)</AnkusExtensionName>
              </PropertyGroup>
            </Project>
            """, context.CancellationToken);
        string[] arguments = command == "get" ? ["get", "extname"] : ["regress", "--dry-run"];
        ProcessResult result = await InvokeAsync([.. arguments, "--home", root, "--project", project, "-c", "Shipping"], context.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.Contains("selection_pg17", result.StandardOutput);
        Assert.DoesNotContain("selection_pg18", result.StandardOutput);
        Assert.IsFalse(File.Exists(Path.Join(root, "config.json")));
        Assert.IsFalse(Directory.Exists(Path.Join(root, "bin")));
    }

    /// <summary>
    /// Metadata-only commands resolve a forwarded relative pg_config path from the selected project.
    /// </summary>
    /// <param name="command">The metadata-only command.</param>
    [TestMethod]
    [DataRow("get")]
    [DataRow("regress")]
    public async Task OfflineCommandsResolveForwardedPgConfigFromProject(string command)
    {
        string root = CreateDirectory();
        string project = Path.Join(root, "Metadata.csproj");
        await File.WriteAllTextAsync(project, """
            <Project>
              <PropertyGroup>
                <AnkusExtensionName>relative_pg_config</AnkusExtensionName>
              </PropertyGroup>
            </Project>
            """, context.CancellationToken);
        string selectedDirectory = Directory.CreateDirectory(Path.Join(root, "selected-postgres")).FullName;
        string selectedPgConfig = CopyRunnablePgConfig(selectedDirectory);

        string relative = Path.GetRelativePath(root, selectedPgConfig);
        Assert.IsFalse(Path.IsPathFullyQualified(relative));
        string[] arguments = command == "get" ? ["get", "extname"] : ["regress", "--dry-run"];

        ProcessResult result = await InvokeAsync([.. arguments, "--project", project,
            "--property", "AnkusPgConfigPath=" + relative], context.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.Contains("relative_pg_config", result.StandardOutput);
        Assert.IsFalse(Directory.Exists(Path.Join(root, "bin")));
    }

    /// <summary>
    /// Metadata-only commands infer the PostgreSQL major from a project-selected pg_config path.
    /// </summary>
    /// <param name="command">The metadata-only command.</param>
    [TestMethod]
    [DataRow("get")]
    [DataRow("regress")]
    public async Task OfflineCommandsInferMajorFromProjectPgConfig(string command)
    {
        string root = CreateDirectory();
        string selectedDirectory = Directory.CreateDirectory(Path.Join(root, "selected-postgres")).FullName;
        string selectedPgConfig = CopyRunnablePgConfig(selectedDirectory);

        string project = Path.Join(root, "Metadata.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project>
              <PropertyGroup>
                <AnkusPgConfigPath>{{Path.GetRelativePath(root, selectedPgConfig)}}</AnkusPgConfigPath>
                <AnkusExtensionName>selection_pg$(AnkusPostgresMajor)</AnkusExtensionName>
              </PropertyGroup>
            </Project>
            """, context.CancellationToken);
        string[] arguments = command == "get" ? ["get", "extname"] : ["regress", "--dry-run"];

        ProcessResult result = await InvokeAsync([.. arguments, "--project", project], context.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.Contains("selection_pg" + MajorText(), result.StandardOutput);
        Assert.IsFalse(Directory.Exists(Path.Join(root, "bin")));
    }

    /// <summary>
    /// Copies pg_config with the adjacent runtime libraries required by Windows installations.
    /// </summary>
    /// <param name="destinationDirectory">The isolated directory that receives the executable.</param>
    /// <returns>The copied pg_config path.</returns>
    private static string CopyRunnablePgConfig(string destinationDirectory)
    {
        string selectedPgConfig = Path.Join(destinationDirectory, Path.GetFileName(s_installation.PgConfigPath));
        File.Copy(s_installation.PgConfigPath, selectedPgConfig);
        if (OperatingSystem.IsWindows())
        {
            string sourceDirectory = Path.GetDirectoryName(s_installation.PgConfigPath)!;
            foreach (string dependency in Directory.EnumerateFiles(sourceDirectory, "*.dll"))
            {
                File.Copy(dependency, Path.Join(destinationDirectory, Path.GetFileName(dependency)));
            }
        }
        else
        {
            File.SetUnixFileMode(selectedPgConfig, File.GetUnixFileMode(s_installation.PgConfigPath));
        }

        return selectedPgConfig;
    }

    /// <summary>
    /// Reserves and restores an incremental packed consumer for one selection case.
    /// </summary>
    /// <param name="pool">The reusable project pool shared by version-selection cases.</param>
    /// <param name="token">Cancels scaffolding and writes.</param>
    /// <returns>A lease that releases the project for the next case.</returns>
    private static async Task<SelectionProjectLease> AcquireSelectionProjectAsync(
        SelectionProjectPool pool,
        CancellationToken token)
    {
        await pool.Gate.WaitAsync(token);
        SelectionProjectState state = pool.Projects.TryDequeue(out SelectionProjectState? available)
            ? available
            : new();
        try
        {
            if (state.Directory is null)
            {
                state.Directory = await CreateSelectionProjectAsync(token);
                state.Properties = await File.ReadAllTextAsync(Path.Join(state.Directory, "Directory.Build.props"), token);
                state.Extension = await File.ReadAllTextAsync(
                    Path.Join(state.Directory, "src", "TestCommandProbe", "TestCommandProbe.csproj"), token);
            }
            else
            {
                await File.WriteAllTextAsync(Path.Join(state.Directory, "Directory.Build.props"), state.Properties, token);
                await File.WriteAllTextAsync(
                    Path.Join(state.Directory, "src", "TestCommandProbe", "TestCommandProbe.csproj"), state.Extension, token);
                string solution = Path.Join(state.Directory, "Selection.sln");
                if (File.Exists(solution))
                {
                    File.Delete(solution);
                }

                foreach (string report in Directory.EnumerateFiles(state.Directory, "*.trx", SearchOption.AllDirectories).ToArray())
                {
                    File.Delete(report);
                }
            }

            return new SelectionProjectLease(pool, state);
        }
        catch
        {
            pool.Gate.Release();
            throw;
        }
    }

    /// <summary>
    /// Creates a packed consumer whose fixture and native SQL both observe the selected server major.
    /// </summary>
    /// <param name="token">Cancels scaffolding and writes.</param>
    /// <returns>The generated solution directory with symbolic-link ancestors resolved.</returns>
    private static async Task<string> CreateSelectionProjectAsync(CancellationToken token)
    {
        string output = Path.Join(CreateSharedDirectory(), "selection project");
        (await InvokeAsync(["new", "TestCommandProbe", "-o", output], token)).EnsureSuccess(s_tool, ["new"]);
        string hostDirectory = Path.Join(output, "tests", "TestCommandProbe.Tests");
        await File.WriteAllTextAsync(Path.Join(hostDirectory, "SelectionTests.cs"), $$"""
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            using Npgsql;
            namespace TestCommandProbe.Tests;
            public sealed partial class BackendTests
            {
                [TestMethod]
                public async Task SelectedVersionReachesPostgres()
                {
                    Assert.AreEqual({{s_installation.Version.Major.ToString(CultureInfo.InvariantCulture)}}, s_extension!.Cluster.Installation.Version.Major);
                    await using NpgsqlConnection connection = await s_extension.Cluster.OpenConnectionAsync(context.CancellationToken);
                    await using var command = new NpgsqlCommand("SELECT current_setting('server_version_num')::integer / 10000, add(40, 2)", connection);
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
                    Assert.IsTrue(await reader.ReadAsync(context.CancellationToken));
                    Assert.AreEqual({{s_installation.Version.Major.ToString(CultureInfo.InvariantCulture)}}, reader.GetInt32(0));
                    Assert.AreEqual(42, reader.GetInt32(1));
                }
            }
            """, token);
        return IntegrationEnvironment.PhysicalDirectory(new DirectoryInfo(output));
    }

    private sealed class SelectionProjectPool
    {
        private static readonly int s_capacity = IntegrationEnvironment.PackageTestConcurrency;

        /// <summary>
        /// Limits simultaneous consumers while preserving incremental outputs between cases.
        /// </summary>
        internal SemaphoreSlim Gate { get; } = new(s_capacity, s_capacity);

        /// <summary>
        /// Holds idle projects that have no running compiler or PostgreSQL backend.
        /// </summary>
        internal ConcurrentQueue<SelectionProjectState> Projects { get; } = new();
    }

    private sealed class SelectionProjectState
    {
        /// <summary>
        /// Gets or sets the owned consumer directory.
        /// </summary>
        internal string? Directory
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the original shared properties restored before reuse.
        /// </summary>
        internal string Properties
        {
            get;
            set;
        } = string.Empty;

        /// <summary>
        /// Gets or sets the original extension project restored before reuse.
        /// </summary>
        internal string Extension
        {
            get;
            set;
        } = string.Empty;
    }

    private sealed class SelectionProjectLease(SelectionProjectPool pool, SelectionProjectState state) : IDisposable
    {
        /// <summary>
        /// Gets the project exclusively reserved by this case.
        /// </summary>
        internal string Directory => state.Directory!;

        /// <summary>
        /// Returns the stopped consumer to the pool.
        /// </summary>
        public void Dispose()
        {
            pool.Projects.Enqueue(state);
            pool.Gate.Release();
        }
    }

    /// <summary>
    /// Isolates a consumer from the repository validation host's own explicit installation selection.
    /// </summary>
    /// <returns>Child-process environment overrides without changing the current process.</returns>
    private static Dictionary<string, string?> SelectionEnvironment()
        => new(s_environment)
        {
            ["ANKUS_TEST_PG_CONFIG"] = null,
            ["ANKUS_TEST_POSTGRES_MAJOR"] = null,
            ["ANKUS_TEST_CONFIGURATION"] = null,
            ["AnkusPostgresMajor"] = null,
            ["AnkusPgConfigPath"] = null,
        };
}
