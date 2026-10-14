using System.Xml.Linq;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Automatic response properties select the same conditional SDK project and extension identity as publication.
    /// </summary>
    [TestMethod]
    public async Task DirectoryBuildResponsePropertiesSelectExtensionProject()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = await CreateControlProjectAsync(directory, token, "response_default");
        XDocument definition = XDocument.Load(project);
        XElement root = definition.Root!;
        root.Attribute("Sdk")!.Remove();
        root.AddFirst(new XElement("Import", new XAttribute("Project", "Sdk.props"),
            new XAttribute("Sdk", "Ankus.Sdk"), new XAttribute("Version", s_version),
            new XAttribute("Condition", "'$(ExtensionFlavor)' == 'active'")));
        root.Add(new XElement("PropertyGroup", new XAttribute("Condition", "'$(ExtensionFlavor)' == 'active'"),
            new XElement("AnkusExtensionName", "response_selected")));
        root.Add(new XElement("Import", new XAttribute("Project", "Sdk.targets"),
            new XAttribute("Sdk", "Ankus.Sdk"), new XAttribute("Version", s_version),
            new XAttribute("Condition", "'$(ExtensionFlavor)' == 'active'")));
        definition.Save(project);
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0")))).Save(
                Path.Join(directory, "Unrelated.csproj"));
        await File.WriteAllTextAsync(Path.Join(directory, "Directory.Build.rsp"), "-property:ExtensionFlavor=active\n", token);

        ProcessResult selected = await InvokeAsync(["get", "extname", "--project", directory], token);
        Assert.AreEqual(0, selected.ExitCode, selected.StandardError);
        Assert.AreEqual("response_selected" + Environment.NewLine, selected.StandardOutput);

        ProcessResult overridden = await InvokeAsync(
            ["get", "extname", "--project", directory, "--property", "ExtensionFlavor=inactive"], token);
        Assert.AreNotEqual(0, overridden.ExitCode);
        Assert.Contains("Specify --project", overridden.StandardError);
    }

    /// <summary>
    /// Installed commands select the evaluated extension SDK across both solution formats and valid SDK import forms.
    /// </summary>
    /// <param name="format">The solution format.</param>
    /// <param name="sdk">The SDK declaration form.</param>
    [TestMethod]
    [DataRow("sln", "attribute")]
    [DataRow("sln", "element")]
    [DataRow("sln", "import")]
    [DataRow("slnx", "attribute")]
    [DataRow("slnx", "element")]
    [DataRow("slnx", "import")]
    public async Task ExtensionIdentitySelectsEvaluatedSolutionProject(string format, string sdk)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string extensionDirectory = Directory.CreateDirectory(Path.Join(directory, "extension")).FullName;
        string project = await CreateControlProjectAsync(extensionDirectory, token, "ankus_resolution_probe");
        XDocument definition = XDocument.Load(project);
        if (sdk != "attribute")
        {
            definition.Root!.Attribute("Sdk")!.Remove();
            if (sdk == "element")
            {
                definition.Root.AddFirst(new XElement("Sdk", new XAttribute("Name", "Ankus.Sdk"), new XAttribute("Version", s_version)));
            }
            else
            {
                new XDocument(new XElement("Project", new XElement("Import", new XAttribute("Project", "Sdk.props"),
                    new XAttribute("Sdk", "Ankus.Sdk"), new XAttribute("Version", s_version)))).Save(Path.Join(extensionDirectory, "Extension.props"));
                definition.Root.AddFirst(new XElement("Import", new XAttribute("Project", "Extension.props")));
                definition.Root.Add(new XElement("Import", new XAttribute("Project", "Sdk.targets"),
                    new XAttribute("Sdk", "Ankus.Sdk"), new XAttribute("Version", s_version)));
            }
        }

        definition.Root!.Add(new XElement("PropertyGroup", new XAttribute("Condition", "'$(ExtensionFlavor)' == 'active' and '$(Configuration)' == 'Shipping'"),
            new XElement("AnkusExtensionName", "ankus_resolution_selected")));
        definition.Save(project);
        await File.WriteAllTextAsync(Path.Join(extensionDirectory, "Functions.cs"), "invalid C# source", token);
        string helper = Path.Join(directory, "Helper.csproj");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"),
                new XElement("AnkusExtensionName", "not_an_extension")),
            new XElement("Import", new XAttribute("Project", "Sdk.props"), new XAttribute("Sdk", "Ankus.Sdk"),
                new XAttribute("Version", s_version), new XAttribute("Condition", "'$(ExtensionFlavor)' == 'inactive'")))).Save(helper);
        string solution = await WriteResolutionSolutionAsync(directory, format, [project, helper], token);
        string home = Path.Join(directory, "unregistered");
        foreach (string selection in new[] { solution, directory })
        {
            ProcessResult result = await InvokeAsync(["get", "extname", "--project", selection, "--home", home,
                "-c", "Shipping", "--property", "ExtensionFlavor=active"], token);
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            Assert.AreEqual("ankus_resolution_selected" + Environment.NewLine, result.StandardOutput);
            Assert.IsEmpty(result.StandardError);
        }

        Assert.IsEmpty(Directory.GetDirectories(directory, "obj", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetDirectories(directory, "bin", SearchOption.AllDirectories));
        Assert.IsFalse(Directory.Exists(home));
    }

    /// <summary>
    /// Installing from a solution directory selects its one extension project and builds it with the solution's
    /// shared build settings, as pgrx's <c>install_from_virtual_workspace_auto_detects_manifest_and_preserves_rustflags</c>
    /// installs from a virtual workspace while keeping its rustflags.
    /// </summary>
    [TestMethod]
    public async Task InstallFromSolutionDirectorySelectsTheExtensionProject()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string extensionDirectory = Directory.CreateDirectory(Path.Join(directory, "extension")).FullName;
        string project = Path.Join(extensionDirectory, "WorkspaceProbe.csproj");
        XDocument definition = XDocument.Load(s_project);
        definition.Root!.Add(new XElement("PropertyGroup", new XElement("AnkusExtensionName", "ankus_workspace_probe")));
        definition.Save(project);
        await File.WriteAllTextAsync(Path.Join(extensionDirectory, "Functions.cs"), """
            #if !WORKSPACE_SETTING
            #error The solution's Directory.Build.props did not reach the extension build.
            #endif
            using Ankus;
            public static class Functions
            {
                [PgFunction]
                public static int WorkspaceValue() => 7;
            }
            """, token);
        await File.WriteAllTextAsync(Path.Join(directory, "Directory.Build.props"), """
            <Project>
              <PropertyGroup>
                <DefineConstants>$(DefineConstants);WORKSPACE_SETTING</DefineConstants>
              </PropertyGroup>
            </Project>
            """, token);
        string helperDirectory = Directory.CreateDirectory(Path.Join(directory, "helper")).FullName;
        string helper = Path.Join(helperDirectory, "Helper.csproj");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0")))).Save(helper);
        await File.WriteAllTextAsync(Path.Join(helperDirectory, "Helper.cs"), "public static class Helper { }", token);
        _ = await WriteResolutionSolutionAsync(directory, "slnx", [project, helper], token);
        string stage = CreateDirectory();
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool,
            ["install", "--home", s_home, "--pg", MajorText(), "--destdir", stage], s_environment, token, workingDirectory: directory);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string extension = Path.Join(StagedPath(stage, s_installation.SharedDirectory), "extension");
        Assert.IsTrue(File.Exists(Path.Join(extension, "ankus_workspace_probe.control")));
        Assert.Contains("workspace_value", await File.ReadAllTextAsync(Path.Join(extension, "ankus_workspace_probe--0.1.0.sql"), token));
        Assert.IsFalse(Directory.Exists(Path.Join(helperDirectory, "bin")));
    }

    /// <summary>
    /// An unrelated project that cannot evaluate does not block selection of the one valid extension project.
    /// </summary>
    /// <param name="format">The solution format.</param>
    [TestMethod]
    [DataRow("sln")]
    [DataRow("slnx")]
    public async Task ExtensionIdentitySkipsUnrelatedUnevaluableSolutionProject(string format)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = await CreateControlProjectAsync(
            Directory.CreateDirectory(Path.Join(directory, "extension")).FullName, token, "ankus_resolution_selected");
        string broken = Path.Join(directory, "Broken.csproj");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("Import", new XAttribute("Project", "missing.props")))).Save(broken);
        string solution = await WriteResolutionSolutionAsync(directory, format, [project, broken], token);
        string home = Path.Join(directory, "unregistered");

        ProcessResult result = await InvokeAsync(
            ["get", "extname", "--project", solution, "--home", home], token);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual("ankus_resolution_selected" + Environment.NewLine, result.StandardOutput);
        Assert.IsEmpty(result.StandardError);
        Assert.IsEmpty(Directory.GetDirectories(directory, "obj", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetDirectories(directory, "bin", SearchOption.AllDirectories));
        Assert.IsFalse(Directory.Exists(home));
    }

    /// <summary>
    /// Every extension command rejects multiple extension projects before compilation, installation or server mutation.
    /// </summary>
    /// <param name="command">The command using the shared resolver.</param>
    [TestMethod]
    [DataRow("build")]
    [DataRow("publish")]
    [DataRow("install")]
    [DataRow("package")]
    [DataRow("run")]
    [DataRow("regress")]
    [DataRow("schema")]
    [DataRow("get")]
    public async Task ExtensionCommandsRejectAmbiguousSolutions(string command)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string first = await CreateControlProjectAsync(Directory.CreateDirectory(Path.Join(directory, "first")).FullName, token);
        string second = await CreateControlProjectAsync(Directory.CreateDirectory(Path.Join(directory, "second")).FullName, token);
        string solution = await WriteResolutionSolutionAsync(directory, "slnx", [first, second], token);
        string home = Path.Join(directory, "unused home");
        string[] identity = command == "get" ? ["extname"] : [];
        ProcessResult result = await InvokeAsync([command, .. identity,
            "--project", solution, "--home", home, "--pg", MajorText(), "--pg-config", s_installation.PgConfigPath], token);

        Assert.AreEqual(1, result.ExitCode);
        Assert.IsEmpty(result.StandardOutput);
        Assert.Contains("Specify --project", result.StandardError);
        Assert.IsEmpty(Directory.GetDirectories(directory, "obj", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetDirectories(directory, "bin", SearchOption.AllDirectories));
        Assert.IsFalse(Directory.Exists(home));
    }

    /// <summary>
    /// Writes a portable solution containing the exact project paths selected by the test.
    /// </summary>
    /// <param name="directory">The solution directory.</param>
    /// <param name="format">The solution format.</param>
    /// <param name="projects">The selected project files.</param>
    /// <param name="token">Cancels file creation.</param>
    /// <returns>The solution path.</returns>
    private static async Task<string> WriteResolutionSolutionAsync(string directory, string format, string[] projects, CancellationToken token)
    {
        string path = Path.Join(directory, "Resolution." + format);
        string[] relative = [.. projects.Select(project => Path.GetRelativePath(directory, project).Replace(Path.DirectorySeparatorChar, '\\'))];
        if (format == "slnx")
        {
            new XDocument(new XElement("Solution", relative.Select(project => new XElement("Project", new XAttribute("Path", project))))).Save(path);
        }
        else
        {
            string[] entries = [.. relative.Select(project =>
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"" + Path.GetFileNameWithoutExtension(project)
                + "\", \"" + project + "\", \"{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}\"\nEndProject")];
            await File.WriteAllTextAsync(path, "Microsoft Visual Studio Solution File, Format Version 12.00\n"
                + string.Join('\n', entries) + "\nGlobal\nEndGlobal\n", token);
        }

        return path;
    }
}
