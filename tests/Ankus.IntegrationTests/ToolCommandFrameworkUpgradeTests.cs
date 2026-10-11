using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Upgrades a generated solution's SDK and central references, then restores and executes its published native extension.
    /// </summary>
    [TestMethod]
    public async Task UpgradeUpdatesScaffoldAndPublishesNativeExtension()
    {
        CancellationToken token = context.CancellationToken;
        string root = Path.Combine(CreateDirectory(), "upgraded solution");
        (await InvokeAsync(["new", "UpgradeProbe", "-o", root], token)).EnsureSuccess(s_tool, ["new"]);
        string global = Path.Combine(root, "global.json");
        string central = Path.Combine(root, "Directory.Packages.props");
        foreach (string path in new[] { global, central })
        {
            string current = await File.ReadAllTextAsync(path, token);
            await File.WriteAllTextAsync(path, current.Replace(s_version, "0.0.0-before", StringComparison.Ordinal), token);
        }

        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        ProcessResult dry = await InvokeAsync(["upgrade", "--solution", Path.Combine(root, "UpgradeProbe.slnx"), "--to", s_version, "--dry-run"], token);
        Assert.AreEqual(0, dry.ExitCode, dry.StandardOutput + dry.StandardError);
        Assert.Contains(s_version, dry.StandardOutput);
        AssertUpgradeFilesUnchanged(root, before);
        ProcessResult upgrade = await InvokeAsync(["upgrade", "--project", root, "--to", s_version], token);
        Assert.AreEqual(0, upgrade.ExitCode, upgrade.StandardOutput + upgrade.StandardError);
        foreach ((string path, byte[] bytes) in before)
        {
            string expected = Encoding.UTF8.GetString(bytes);
            if (path == global || path == central)
            {
                expected = expected.Replace("0.0.0-before", s_version, StringComparison.Ordinal);
            }

            Assert.AreSequenceEqual(Encoding.UTF8.GetBytes(expected), await File.ReadAllBytesAsync(path, token), path);
        }

        DateTime timestamp = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(global, timestamp);
        ProcessResult unchanged = await InvokeAsync(["upgrade", "--project", root, "--to", s_version], token);
        Assert.AreEqual(0, unchanged.ExitCode, unchanged.StandardError);
        Assert.Contains("already current", unchanged.StandardOutput);
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(global));

        string solution = Path.Combine(root, "UpgradeProbe.slnx");
        (await RunDotnetAsync(["restore", solution, "-p:AnkusPostgresMajor=" + MajorText()], token)).EnsureSuccess("dotnet", ["restore"]);
        string assets = Path.Combine(root, "tests", "UpgradeProbe.Tests", "obj", "project.assets.json");
        using (JsonDocument restored = JsonDocument.Parse(await File.ReadAllTextAsync(assets, token)))
        {
            Assert.AreEqual("package", restored.RootElement.GetProperty("libraries").GetProperty("Ankus.Testing/" + s_version).GetProperty("type").GetString());
        }

        string project = Path.Combine(root, "src", "UpgradeProbe", "UpgradeProbe.csproj");
        string output = CreateDirectory();
        (await InvokeAsync(["publish", "--project", project, "--home", s_home, "--pg", MajorText(), "--output", output], token))
            .EnsureSuccess(s_tool, ["publish"]);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("CREATE EXTENSION upgrade_probe; SELECT add(19, 23)", connection);
        Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// Exact edits preserve XML quotes, comments, line endings, encodings and properties shared with unrelated dependencies.
    /// </summary>
    /// <param name="unicode">Whether the source uses UTF-16 with a byte-order mark.</param>
    /// <param name="lineEnding">The source line-ending sequence to preserve.</param>
    [TestMethod]
    [DataRow(false, "\n")]
    [DataRow(true, "\n")]
    [DataRow(false, "\r\n")]
    [DataRow(true, "\r\n")]
    [DataRow(false, "\r")]
    [DataRow(true, "\r")]
    public async Task UpgradePreservesUnrelatedVersionsAndFormatting(bool unicode, string lineEnding)
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = Path.Combine(root, "Probe.csproj");
        string original = "<Project Sdk='Microsoft.NET.Sdk'>\r\n" +
            "  <!-- café -->\r\n  <PropertyGroup><Own>0.1.0</Own><Shared>0.1.0</Shared><Empty /></PropertyGroup>\r\n" +
            "  <ItemGroup><PackageReference Include='Ankus.Runtime' Version='$(Own)' />" +
            "<PackageReference Include='Ankus.Testing' Version='$(Shared)' />" +
            "<PackageReference Include='Other' Version='$(Shared)' />" +
            "<PackageReference Include='Ankus.PgConfig'><Version>$(Empty)</Version></PackageReference>" +
            "<PackageReference Include='Ankus.NativeAot.Runtime' Version='10.0.12-ankus.3' /></ItemGroup>\r\n</Project>\r\n";
        original = original.Replace("\r\n", lineEnding, StringComparison.Ordinal);
        Encoding encoding = unicode ? Encoding.Unicode : new UTF8Encoding(false);
        await File.WriteAllTextAsync(project, original, encoding, token);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string expected = original.Replace("<Own>0.1.0</Own>", "<Own>0.2.0</Own>", StringComparison.Ordinal)
            .Replace("Include='Ankus.Testing' Version='$(Shared)'", "Include='Ankus.Testing' Version='0.2.0'", StringComparison.Ordinal)
            .Replace("<Empty />", "<Empty >0.2.0</Empty>", StringComparison.Ordinal);
        Assert.AreSequenceEqual([.. encoding.GetPreamble(), .. encoding.GetBytes(expected)], await File.ReadAllBytesAsync(project, token));
        Assert.AreEqual("0.2.0", XDocument.Load(project).Descendants("Empty").Single().Value);
        Assert.HasCount(1, Directory.GetFiles(root));
    }

    /// <summary>
    /// Uses NuGet ordering for stable/prerelease updates and retains exact, bounded and floating requirement semantics.
    /// </summary>
    /// <param name="prerelease">Whether prereleases are eligible.</param>
    /// <param name="current">The original package requirement.</param>
    /// <param name="expected">The expected updated requirement.</param>
    [TestMethod]
    [DataRow(false, "0.1.0", "0.2.0")]
    [DataRow(true, "0.1.0", "0.3.0-beta.10")]
    [DataRow(false, "[0.1.0]", "[0.2.0]")]
    [DataRow(false, "[0.0.0, 0.2.0)", "[0.1.0, 0.2.0)")]
    [DataRow(false, "0.*", "0.*")]
    public async Task UpgradeSelectsNuGetVersions(bool prerelease, string current, string expected)
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = await WriteUpgradeProjectAsync(root, current, token);
        await CreateUpgradeFeedAsync(root, "Ankus.Testing", token);
        string[] arguments = ["upgrade", "--project", project, .. prerelease ? new[] { "--include-prereleases" } : []];
        ProcessResult result = await InvokeAsync(arguments, token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual(expected, XDocument.Load(project).Descendants("PackageReference").Single().Attribute("Version")!.Value);
    }

    /// <summary>
    /// A NuGet requirement stays a range for packages while the project SDK resolves to a concrete eligible version.
    /// </summary>
    [TestMethod]
    public async Task UpgradeResolvesSdkRangeToConcreteVersion()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = await WriteUpgradeProjectAsync(root, "0.1.0", token);
        string text = await File.ReadAllTextAsync(project, token);
        await File.WriteAllTextAsync(project, text.Replace("Microsoft.NET.Sdk", "Ankus.Sdk/0.1.0", StringComparison.Ordinal), token);
        await CreateUpgradeFeedAsync(root, "Ankus.Sdk", token);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "[0.2.0, 0.3.0)"], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        XDocument updated = XDocument.Load(project);
        Assert.AreEqual("Ankus.Sdk/0.2.0", updated.Root!.Attribute("Sdk")!.Value);
        Assert.AreEqual("[0.2.0, 0.3.0)", updated.Descendants("PackageReference").Single().Attribute("Version")!.Value);
    }

    /// <summary>
    /// Project selection updates imported conditional versions and SDK elements without modifying a sibling project.
    /// </summary>
    /// <param name="legacy">Whether to select a legacy Visual Studio solution.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpgradeSelectsProjectAndImportedVersions(bool legacy)
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string first = await WriteUpgradeProjectAsync(root, "0.1.0", token);
        string sibling = Path.Combine(root, "Other.csproj");
        File.Copy(first, sibling);
        string imported = Path.Combine(root, "Versions.props");
        await File.WriteAllTextAsync(imported, """
            <Project>
              <PropertyGroup><FrameworkVersion Condition="'$(Configuration)' == 'Debug'">0.1.0</FrameworkVersion><FrameworkVersion Condition="'$(Configuration)' != 'Debug'">0.1.1</FrameworkVersion></PropertyGroup>
              <ItemGroup><PackageVersion Include="Ankus.Testing"><Version>$(FrameworkVersion)</Version></PackageVersion></ItemGroup>
            </Project>
            """, token);
        await File.WriteAllTextAsync(first, """
            <Project>
              <Sdk Name="Ankus.Sdk" Version="0.1.0" />
              <Import Project="$(MSBuildThisFileDirectory)Versions.props" />
              <Import Project="Sdk.targets" Sdk="Ankus.Sdk" Version="0.1.0" />
            </Project>
            """, token);
        string solution = Path.Combine(root, legacy ? "Selection.sln" : "Selection.slnx");
        string solutionText = legacy
            ? "Microsoft Visual Studio Solution File, Format Version 12.00\nProject(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Probe\", \"Probe.csproj\", \"{12345678-1234-1234-1234-123456789ABC}\"\nEndProject\nProject(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Other\", \"Other.csproj\", \"{22345678-1234-1234-1234-123456789ABC}\"\nEndProject\nGlobal\nEndGlobal\n"
            : "<Solution><Project Path=\"Probe.csproj\"/><Project Path=\"Other.csproj\"/></Solution>";
        await File.WriteAllTextAsync(solution, solutionText, token);
        byte[] untouched = await File.ReadAllBytesAsync(sibling, token);
        ProcessResult result = await InvokeAsync(["upgrade", "--solution", solution, "--package", "probe", "--to", "0.2.0"], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreSequenceEqual(untouched, await File.ReadAllBytesAsync(sibling, token));
        Assert.AreSequenceEqual(["0.1.0", "0.1.1"], XDocument.Load(imported).Descendants("FrameworkVersion").Select(static element => element.Value));
        Assert.AreEqual("0.2.0", XDocument.Load(imported).Descendants("Version").Single().Value);
        Assert.AreSequenceEqual(["0.2.0", "0.2.0"], XDocument.Load(first).Descendants().Attributes("Version").Select(static attribute => attribute.Value));
        Assert.AreEqual(solutionText, await File.ReadAllTextAsync(solution, token));
    }

    /// <summary>
    /// MSBuild selects computed imports for every declared configuration and target framework without executing targets.
    /// </summary>
    [TestMethod]
    public async Task UpgradeEvaluatesConditionalImportsAcrossBuilds()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = Path.Combine(root, "Probe.csproj");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <Configurations>Debug;Release;Shipping</Configurations>
                <TargetFrameworks>net10.0;net9.0</TargetFrameworks>
                <ImportFolder>wrong</ImportFolder>
                <ImportFolder Condition="'$(Configuration)' != ''">$(Configuration)</ImportFolder>
              </PropertyGroup>
              <Import Project="$([MSBuild]::NormalizePath('$(MSBuildThisFileDirectory)', 'imports', '$(ImportFolder)', '$(TargetFramework)', 'Versions.props'))" Condition="'$(TargetFramework)' != ''" />
              <Import Project="Inactive.props" Condition="false" />
              <Target Name="ShouldNotRun" BeforeTargets="Build;Restore">
                <WriteLinesToFile File="$(MSBuildThisFileDirectory)unexpected.txt" Lines="unexpected target execution" />
              </Target>
            </Project>
            """, token);
        var selected = new List<string>();
        foreach (string configuration in new[] { "Debug", "Release", "Shipping" })
        {
            foreach (string framework in new[] { "net10.0", "net9.0" })
            {
                string directory = Path.Combine(root, "imports", configuration, framework);
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "Versions.props");
                await File.WriteAllTextAsync(file,
                    "<Project><ItemGroup><PackageReference Include='Ankus.Testing' Version='0.1.0'/></ItemGroup></Project>", token);
                selected.Add(file);
            }
        }

        string inactive = Path.Combine(root, "Inactive.props");
        await File.WriteAllTextAsync(inactive,
            "<Project><ItemGroup><PackageReference Include='Ankus.Testing' Version='9.9.9'/></ItemGroup></Project>", token);
        await File.WriteAllTextAsync(Path.Combine(root, "Directory.Build.rsp"), "-target:ShouldNotRun", token);
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        ProcessResult dry = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0", "--dry-run"], token);
        Assert.AreEqual(0, dry.ExitCode, dry.StandardOutput + dry.StandardError);
        AssertUpgradeFilesUnchanged(root, before);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        foreach (string file in selected)
        {
            before[file] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(before[file]).Replace("0.1.0", "0.2.0", StringComparison.Ordinal));
        }

        AssertUpgradeFilesUnchanged(root, before);
    }

    /// <summary>
    /// Shared build and central-package files honor disabled imports and explicit replacement paths.
    /// </summary>
    /// <param name="replace">Whether to replace the conventional files instead of disabling them.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpgradeHonorsSharedImportSelection(bool replace)
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = Path.Combine(root, "Probe.csproj");
        var settings = new StringBuilder();
        var selected = new List<string>();
        foreach ((string kind, string conventional) in new[]
        {
            ("BuildProps", "Directory.Build.props"),
            ("BuildTargets", "Directory.Build.targets"),
            ("PackagesProps", "Directory.Packages.props"),
        })
        {
            string content = "<Project><ItemGroup><PackageReference Include='Ankus.Testing' Version='0.1.0'/></ItemGroup></Project>";
            await File.WriteAllTextAsync(Path.Combine(root, conventional), "Unused file is deliberately not valid XML.", token);
            string replacement = Path.Combine(root, "Selected" + conventional);
            await File.WriteAllTextAsync(replacement, content, token);
            if (replace)
            {
                settings.Append(CultureInfo.InvariantCulture,
                    $"<Directory{kind}Path>$(MSBuildThisFileDirectory)Selected{conventional}</Directory{kind}Path>");
                selected.Add(replacement);
            }
            else
            {
                settings.Append(CultureInfo.InvariantCulture, $"<ImportDirectory{kind}>false</ImportDirectory{kind}>");
            }
        }

        await File.WriteAllTextAsync(project, $$"""
            <Project>
              <PropertyGroup>{{settings}}<TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <ItemGroup><PackageReference Include="Ankus.Runtime" Version="0.1.0" /></ItemGroup>
              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
            </Project>
            """, token);
        selected.Add(project);
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        foreach (string file in selected)
        {
            before[file] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(before[file]).Replace("0.1.0", "0.2.0", StringComparison.Ordinal));
        }

        AssertUpgradeFilesUnchanged(root, before);
    }

    /// <summary>
    /// Computed reassignment, escaped paths and directory globs select the actual authored import files.
    /// </summary>
    /// <param name="selection">The import-path form.</param>
    [TestMethod]
    [DataRow("computed")]
    [DataRow("escaped")]
    [DataRow("glob")]
    public async Task UpgradeResolvesImportPathForms(string selection)
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string directory = Path.Combine(root, selection == "escaped" ? "versions;data" : "versions");
        Directory.CreateDirectory(directory);
        string imported = Path.Combine(directory, "Versions.props");
        string content = "<Project><ItemGroup><PackageReference Include='Ankus.Testing' Version='0.1.0'/></ItemGroup></Project>";
        await File.WriteAllTextAsync(imported, content, token);
        await File.WriteAllTextAsync(Path.Combine(root, "Unused.props"), content, token);
        string definition = selection == "computed"
            ? "<PropertyGroup><Selected>Unused.props</Selected><Selected>$([MSBuild]::NormalizePath('$(MSBuildThisFileDirectory)', 'versions', 'Versions.props'))</Selected></PropertyGroup>"
            : "";
        string import = selection switch
        {
            "computed" => "$(Selected)",
            "escaped" => "versions%3Bdata/Versions.props",
            "glob" => "version*/*.props",
            _ => throw new InvalidOperationException("Unknown import partition."),
        };
        string project = Path.Combine(root, "Probe.csproj");
        await File.WriteAllTextAsync(project, $"<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>{definition}<Import Project='{import}'/></Project>", token);
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        before[imported] = Encoding.UTF8.GetBytes(content.Replace("0.1.0", "0.2.0", StringComparison.Ordinal));
        AssertUpgradeFilesUnchanged(root, before);
    }

    /// <summary>
    /// Imported properties remain safe for unrelated consumers even when a sibling project is outside the selection.
    /// </summary>
    [TestMethod]
    public async Task UpgradePreservesImportedPropertyConsumers()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string shared = Path.Combine(root, "Shared.props");
        await File.WriteAllTextAsync(shared, "<Project><PropertyGroup><SharedVersion>0.1.0</SharedVersion></PropertyGroup></Project>", token);
        string project = Path.Combine(root, "Probe.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk='Microsoft.NET.Sdk'><Import Project='Shared.props'/><ItemGroup><PackageReference Include='Ankus.Testing' Version='$(SharedVersion)'/></ItemGroup></Project>", token);
        string sibling = Path.Combine(root, "Sibling.csproj");
        await File.WriteAllTextAsync(sibling, "<Project Sdk='Microsoft.NET.Sdk'><Import Project='Shared.props'/><ItemGroup><PackageReference Include='Unrelated' Version='$(SharedVersion)'/></ItemGroup></Project>", token);
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        before[project] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(before[project]).Replace("Version='$(SharedVersion)'", "Version='0.2.0'", StringComparison.Ordinal));
        AssertUpgradeFilesUnchanged(root, before);
        foreach ((string file, string version) in new[] { (project, "0.2.0"), (sibling, "0.1.0") })
        {
            ProcessResult evaluated = await RunDotnetAsync(["msbuild", file, "-nologo", "-getItem:PackageReference"], token);
            Assert.AreEqual(0, evaluated.ExitCode, evaluated.StandardOutput + evaluated.StandardError);
            using JsonDocument items = JsonDocument.Parse(evaluated.StandardOutput);
            Assert.AreEqual(version, items.RootElement.GetProperty("Items").GetProperty("PackageReference")[0].GetProperty("Version").GetString());
        }
    }

    /// <summary>
    /// Source overrides use the caller's directory while configured names and source mappings retain their meaning.
    /// </summary>
    /// <param name="named">Whether the override uses its configured name.</param>
    /// <param name="mapped">Whether package-source mapping is enabled.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task UpgradeResolvesSourceOverrides(bool named, bool mapped)
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = await WriteUpgradeProjectAsync(root, "0.1.0", token);
        await CreateUpgradeFeedAsync(root, "Ankus.Testing", token);
        string config = Path.Combine(root, "NuGet.Config");
        if (mapped)
        {
            XDocument settings = XDocument.Load(config);
            settings.Root!.Add(new XElement("packageSourceMapping", new XElement("clear"),
                new XElement("packageSource", new XAttribute("key", "owned"), new XElement("package", new XAttribute("pattern", "Ankus.*")))));
            settings.Save(config);
        }

        string source = named ? "owned" : Path.GetRelativePath(s_root, Path.Combine(root, "feed"));
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--source", source, "--configfile", config], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        before[project] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(before[project]).Replace("0.1.0", "0.2.0", StringComparison.Ordinal));
        AssertUpgradeFilesUnchanged(root, before);
    }

    /// <summary>
    /// A source override reached through a directory alias retains the configured feed's package-source mapping.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task UpgradeResolvesSourceDirectoryAliases()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = await WriteUpgradeProjectAsync(root, "0.1.0", token);
        await CreateUpgradeFeedAsync(root, "Ankus.Testing", token);
        string config = Path.Combine(root, "NuGet.Config");
        XDocument settings = XDocument.Load(config);
        settings.Root!.Add(new XElement("packageSourceMapping", new XElement("clear"),
            new XElement("packageSource", new XAttribute("key", "owned"), new XElement("package", new XAttribute("pattern", "Ankus.*")))));
        settings.Save(config);
        string parent = Path.Combine(CreateDirectory(), "linked parent");
        Directory.CreateSymbolicLink(parent, Path.GetDirectoryName(root)!);
        string target = Path.Combine(parent, Path.GetFileName(root));
        string alias = Path.Combine(CreateDirectory(), "linked source");
        Directory.CreateSymbolicLink(alias, target);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--source", Path.Combine(alias, "feed"), "--configfile", config], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual("0.2.0", XDocument.Load(project).Descendants("PackageReference").Single().Attribute("Version")!.Value);
        Assert.AreEqual(target, new DirectoryInfo(alias).LinkTarget);
        Assert.AreEqual(Path.GetDirectoryName(root), new DirectoryInfo(parent).LinkTarget);
    }

    /// <summary>
    /// Mixed and property-composed identities retain unrelated item versions and become stable after the first upgrade.
    /// </summary>
    /// <param name="kind">The framework item kind.</param>
    /// <param name="expanded">Whether the item contains existing conditional child metadata.</param>
    [TestMethod]
    [DataRow("PackageReference", false)]
    [DataRow("PackageReference", true)]
    [DataRow("PackageVersion", false)]
    [DataRow("PackageVersion", true)]
    [DataRow("GlobalPackageReference", false)]
    [DataRow("GlobalPackageReference", true)]
    public async Task UpgradeTargetsFrameworkItemsInMixedLists(string kind, bool expanded)
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = Path.Combine(root, "Probe.csproj");
        string child = expanded ? $"><Version Condition=\"'$(Configuration)' == 'Release'\">0.1.1</Version><!-- keep metadata comment --></{kind}>" : "/>";
        string original = $$"""
            <Project>
              <PropertyGroup>
                <Configuration Condition="'$(Configuration)' == ''">Debug</Configuration>
                <Prefix>Ankus</Prefix>
                <Suffix Condition="'$(Configuration)' == 'Debug'">Testing</Suffix>
                <Suffix Condition="'$(Configuration)' != 'Debug'">Runtime</Suffix>
                <SharedVersion>0.1.0</SharedVersion>
              </PropertyGroup>
              <ItemGroup><{{kind}} Include="$(Prefix).$(Suffix);Unrelated" Version="$(SharedVersion)" Aliases="keep_alias"{{child}}</ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(project, original, token);
        ProcessResult dry = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0", "--dry-run"], token);
        Assert.AreEqual(0, dry.ExitCode, dry.StandardOutput + dry.StandardError);
        Assert.AreEqual(original, await File.ReadAllTextAsync(project, token));
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        XDocument changed = XDocument.Load(project);
        Assert.AreEqual("0.1.0", changed.Descendants("SharedVersion").Single().Value);
        XElement item = changed.Descendants(kind).Single();
        Assert.AreEqual("$(Prefix).$(Suffix);Unrelated", item.Attribute("Include")!.Value);
        Assert.AreEqual("$(SharedVersion)", item.Attribute("Version")!.Value);
        if (expanded)
        {
            Assert.Contains("<!-- keep metadata comment -->", await File.ReadAllTextAsync(project, token));
        }

        foreach ((string configuration, string framework) in new[] { ("Debug", "Ankus.Testing"), ("Release", "Ankus.Runtime") })
        {
            ProcessResult evaluated = await RunDotnetAsync(["msbuild", project, "-nologo", "-getItem:" + kind, "-p:Configuration=" + configuration], token);
            Assert.AreEqual(0, evaluated.ExitCode, evaluated.StandardOutput + evaluated.StandardError);
            using JsonDocument values = JsonDocument.Parse(evaluated.StandardOutput);
            JsonElement[] items = [.. values.RootElement.GetProperty("Items").GetProperty(kind).EnumerateArray()];
            Assert.AreSequenceEqual([framework, "Unrelated"], items.Select(static value => value.GetProperty("Identity").GetString()));
            Assert.AreEqual("0.2.0", items[0].GetProperty("Version").GetString());
            Assert.AreEqual(expanded && configuration == "Release" ? "0.1.1" : "0.1.0", items[1].GetProperty("Version").GetString());
            Assert.AreSequenceEqual(["keep_alias", "keep_alias"], items.Select(static value => value.GetProperty("Aliases").GetString()));
        }

        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        ProcessResult repeated = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
        Assert.AreEqual(0, repeated.ExitCode, repeated.StandardOutput + repeated.StandardError);
        Assert.Contains("already current", repeated.StandardOutput);
        AssertUpgradeFilesUnchanged(root, before);
    }

    /// <summary>
    /// Identity-specific central versions and per-reference overrides produce the intended NuGet restore graph.
    /// </summary>
    [TestMethod]
    public async Task UpgradeMixedCentralOverridesRestoreSelectedVersions()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        await CreateUpgradeFeedAsync(root, "Ankus.Testing", token);
        await CreateUpgradeFeedAsync(root, "Unrelated", token);
        string central = Path.Combine(root, "Directory.Packages.props");
        await File.WriteAllTextAsync(central, "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include='Ankus.Testing;Unrelated' Version='0.1.0'/></ItemGroup></Project>", token);
        string project = Path.Combine(root, "Probe.csproj");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Shared>0.1.0</Shared></PropertyGroup>
              <ItemGroup><PackageReference Include="Ankus.Testing;Unrelated" VersionOverride="$(Shared)" /></ItemGroup>
            </Project>
            """, token);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual("true", XDocument.Load(central).Descendants("ManagePackageVersionsCentrally").Single().Value);
        Assert.AreEqual("0.1.0", XDocument.Load(project).Descendants("Shared").Single().Value);
        ProcessResult restored = await RunDotnetAsync(["restore", project, "--configfile", Path.Combine(root, "NuGet.Config"), "--packages", Path.Combine(root, "packages")], token);
        Assert.AreEqual(0, restored.ExitCode, restored.StandardOutput + restored.StandardError);
        using JsonDocument assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "obj", "project.assets.json"), token));
        Assert.AreSequenceEqual(["Ankus.Testing/0.2.0", "Unrelated/0.1.0"], assets.RootElement.GetProperty("libraries")
            .EnumerateObject().Select(static package => package.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A symbolic-link import uses MSBuild's logical import directory while retaining the link and unrelated physical sibling.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task UpgradePreservesSymbolicImportContext()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string physical = Path.Combine(root, "physical");
        Directory.CreateDirectory(physical);
        string target = Path.Combine(physical, "Shared.props");
        await File.WriteAllTextAsync(target, "<Project><Import Project='$(MSBuildThisFileDirectory)Versions.props'/></Project>", token);
        string alias = Path.Combine(root, "Shared.props");
        File.CreateSymbolicLink(alias, target);
        string content = "<Project><ItemGroup><PackageReference Include='Ankus.Testing' Version='0.1.0'/></ItemGroup></Project>";
        string imported = Path.Combine(root, "Versions.props");
        await File.WriteAllTextAsync(imported, content, token);
        await File.WriteAllTextAsync(Path.Combine(physical, "Versions.props"), content.Replace("0.1.0", "9.9.9", StringComparison.Ordinal), token);
        string project = Path.Combine(root, "Probe.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Import Project='Shared.props'/></Project>", token);
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        before[imported] = Encoding.UTF8.GetBytes(content.Replace("0.1.0", "0.2.0", StringComparison.Ordinal));
        AssertUpgradeFilesUnchanged(root, before);
        Assert.AreEqual(target, new FileInfo(alias).LinkTarget);
    }

    /// <summary>
    /// Source discovery does not rewrite framework declarations belonging to configured or evaluated package-cache imports.
    /// </summary>
    /// <param name="conditional">Whether the import has an MSBuild condition.</param>
    /// <param name="fallback">Whether NuGet configuration supplies a fallback cache instead of a project property.</param>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task UpgradePreservesPackageOwnedImports(bool conditional, bool fallback)
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string cache = Path.Combine(root, "package-cache");
        Directory.CreateDirectory(cache);
        string imported = Path.Combine(cache, "Dependency.props");
        await File.WriteAllTextAsync(imported, "<Project><ItemGroup><PackageReference Include='Ankus.Runtime' Version='0.1.0'/></ItemGroup></Project>", token);
        string project = Path.Combine(root, "Probe.csproj");
        var document = XDocument.Parse("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetPackageRoot>$(MSBuildThisFileDirectory)package-cache/</NuGetPackageRoot></PropertyGroup>
              <Import Project="$(NuGetPackageRoot)Dependency.props" Condition="Exists('$(NuGetPackageRoot)Dependency.props')" />
              <ItemGroup><PackageReference Include="Ankus.Testing" Version="0.1.0" /></ItemGroup>
            </Project>
            """);
        if (!conditional)
        {
            document.Root!.Element("Import")!.Attribute("Condition")!.Remove();
        }

        if (fallback)
        {
            document.Descendants("NuGetPackageRoot").Single().Remove();
            document.Root!.Element("Import")!.SetAttributeValue("Project", imported);
            document.Root.Element("Import")!.Attribute("Condition")?.SetValue("Exists('" + imported + "')");
            new XDocument(new XElement("configuration", new XElement("fallbackPackageFolders", new XElement("clear"),
                new XElement("add", new XAttribute("key", "owned"), new XAttribute("value", cache)))))
                .Save(Path.Combine(root, "NuGet.Config"));
        }

        await File.WriteAllTextAsync(project, document.ToString(), token);
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        before[project] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(before[project]).Replace("0.1.0", "0.2.0", StringComparison.Ordinal));
        AssertUpgradeFilesUnchanged(root, before);
    }

    /// <summary>
    /// An import-evaluation error rejects the entire solution plan before changing an earlier valid project.
    /// </summary>
    [TestMethod]
    public async Task UpgradeEvaluationFailurePreservesSolution()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        _ = await WriteUpgradeProjectAsync(root, "0.1.0", token);
        string invalid = Path.Combine(root, "Invalid.csproj");
        await File.WriteAllTextAsync(invalid, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Import Project="$([MSBuild]::NormalizePath('$(MSBuildThisFileDirectory)', 'Missing.props'))" />
            </Project>
            """, token);
        string solution = Path.Combine(root, "Probe.slnx");
        await File.WriteAllTextAsync(solution, "<Solution><Project Path='Probe.csproj'/><Project Path='Invalid.csproj'/></Solution>", token);
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        ProcessResult result = await InvokeAsync(["upgrade", "--solution", solution, "--to", "0.2.0"], token);
        Assert.AreEqual(1, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.Contains("MSBuild could not discover imports", result.StandardError);
        Assert.Contains("Missing.props", result.StandardError);
        AssertUpgradeFilesUnchanged(root, before);
    }

    /// <summary>
    /// Feed errors preserve manifests, while package-source mapping excludes an unrelated failing source before any request.
    /// </summary>
    /// <param name="mapped">Whether only the valid feed is mapped to the framework package.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpgradeFeedFailuresAndSourceMappings(bool mapped)
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = await WriteUpgradeProjectAsync(root, "0.1.0", token);
        await CreateUpgradeFeedAsync(root, "Ankus.Testing", token);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string config = Path.Combine(root, "NuGet.Config");
        XDocument configuration = XDocument.Load(config);
        configuration.Root!.Element("packageSources")!.Add(new XElement("add", new XAttribute("key", "broken"),
            new XAttribute("value", $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/v3/index.json"), new XAttribute("allowInsecureConnections", "true")));
        if (mapped)
        {
            configuration.Root.Add(new XElement("packageSourceMapping", new XElement("clear"),
                new XElement("packageSource", new XAttribute("key", "owned"), new XElement("package", new XAttribute("pattern", "Ankus.*")))));
        }

        configuration.Save(config);
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = ServeUpgradeFeedAsync(listener, observed, Task.CompletedTask, stop.Token);
        try
        {
            ProcessResult result = await InvokeAsync(["upgrade", "--project", project], token);
            if (mapped)
            {
                Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
                Assert.IsFalse(observed.Task.IsCompleted);
                Assert.AreEqual("0.2.0", XDocument.Load(project).Descendants("PackageReference").Single().Attribute("Version")!.Value);
                before[project] = await File.ReadAllBytesAsync(project, token);
                AssertUpgradeFilesUnchanged(root, before);
            }
            else
            {
                Assert.AreEqual(1, result.ExitCode, result.StandardOutput + result.StandardError);
                Assert.Contains("discovery for Ankus.Testing was incomplete", result.StandardError);
                Assert.IsTrue(observed.Task.IsCompletedSuccessfully);
                AssertUpgradeFilesUnchanged(root, before);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await server.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Cancellation after an observed package request terminates discovery and leaves the project unchanged.
    /// </summary>
    [TestMethod]
    public async Task UpgradeCanceledDiscoveryPreservesAllFiles()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = await WriteUpgradeProjectAsync(root, "0.1.0", token);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var configuration = new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
            new XElement("add", new XAttribute("key", "waiting"), new XAttribute("allowInsecureConnections", "true"),
                new XAttribute("value", $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/v3/index.json")))));
        configuration.Save(Path.Combine(root, "NuGet.Config"));
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = ServeUpgradeFeedAsync(listener, observed, release.Task, stop.Token);
        Task<ProcessResult> upgrade = InvokeAsync(["upgrade", "--project", project], cancellation.Token);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => upgrade);
            AssertUpgradeFilesUnchanged(root, before);
        }
        finally
        {
            await cancellation.CancelAsync();
            await stop.CancelAsync();
            Task pending = upgrade;
            await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await server.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// A user edit to a source that supplies imports invalidates the plan even when that source needs no version edit.
    /// </summary>
    [TestMethod]
    public async Task UpgradeConcurrentSourceEditPreservesUserChanges()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = Path.Combine(root, "Probe.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk='Microsoft.NET.Sdk'><Import Project='Versions.props'/></Project>", token);
        string imported = Path.Combine(root, "Versions.props");
        await File.WriteAllTextAsync(imported, "<Project><ItemGroup><PackageReference Include='Ankus.Testing' Version='0.1.0'/></ItemGroup></Project>", token);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string source = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/";
        new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
            new XElement("add", new XAttribute("key", "waiting"), new XAttribute("allowInsecureConnections", "true"),
                new XAttribute("value", source + "index.json"))))).Save(Path.Combine(root, "NuGet.Config"));
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = ServeUpgradeFeedAsync(listener, observed, release.Task, stop.Token, source);
        Task<ProcessResult> upgrade = InvokeAsync(["upgrade", "--project", project], stop.Token);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            string edited = "<Project Sdk='Microsoft.NET.Sdk'><!-- concurrent user edit --><Import Project='Versions.props'/></Project>";
            await File.WriteAllTextAsync(project, edited, token);
            before[project] = Encoding.UTF8.GetBytes(edited);
            release.SetResult();
            ProcessResult result = await upgrade;
            Assert.AreEqual(1, result.ExitCode, result.StandardOutput + result.StandardError);
            Assert.Contains("changed during the upgrade", result.StandardError);
            AssertUpgradeFilesUnchanged(root, before);
        }
        finally
        {
            await stop.CancelAsync();
            Task pending = upgrade;
            await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await server.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// <c>--to</c> writes an exact pin or a prerelease as given, as cargo-pgrx's <c>parse_to_accepts_exact_pin</c> and
    /// <c>parse_to_accepts_prerelease</c> accept them. A two-part version is also kept as given; NuGet reads it as a
    /// minimum version, where Cargo's <c>parse_to_accepts_partial_version</c> reads a caret requirement.
    /// </summary>
    /// <param name="target">The requested version.</param>
    [TestMethod]
    [DataRow("[0.2.0]")]
    [DataRow("0.3.0-beta.1")]
    [DataRow("0.2")]
    public async Task UpgradeWritesExactPinsAndPrereleases(string target)
    {
        CancellationToken token = context.CancellationToken;
        string project = await WriteUpgradeProjectAsync(CreateDirectory(), "0.1.0", token);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", target], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual(target, XDocument.Load(project).Descendants("PackageReference").Single().Attribute("Version")!.Value);
    }

    /// <summary>
    /// Replacing a manifest retains its author-selected Unix permissions.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task UpgradePreservesUnixPermissions()
        => await VerifyUpgradeUnixPermissionsAsync(context.CancellationToken);

    private async Task VerifyUpgradeUnixPermissionsAsync(CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Unix permissions require a Unix filesystem.");
        }

        string project = await WriteUpgradeProjectAsync(CreateDirectory(), "0.1.0", token);
        UnixFileMode permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(project, permissions);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.AreEqual(permissions, File.GetUnixFileMode(project));
        Assert.AreEqual("0.2.0", XDocument.Load(project).Descendants("PackageReference").Single().Attribute("Version")!.Value);
    }

    /// <summary>
    /// Invalid version declarations and selections fail before changing any source file.
    /// </summary>
    /// <param name="mode">The invalid declaration or selection.</param>
    [TestMethod]
    [DataRow("range")]
    [DataRow("emptyRange")]
    [DataRow("caret")]
    [DataRow("tilde")]
    [DataRow("cycle")]
    [DataRow("unknown")]
    [DataRow("duplicateSdk")]
    [DataRow("missingProject")]
    [DataRow("unknownPackage")]
    [DataRow("noFramework")]
    [DataRow("identityCycle")]
    [DataRow("identityUnknown")]
    public async Task UpgradeInvalidRequestsPreserveAllFiles(string mode)
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = await WriteUpgradeProjectAsync(root, "0.1.0", token);
        string selection = project;
        string[] target = ["--to", "0.2.0"];
        switch (mode)
        {
            case "range":
                target = ["--to", "not-a-version"];
                break;
            case "emptyRange":
                target = ["--to", ""];
                break;
            case "caret":
                target = ["--to", "^0.2"];
                break;
            case "tilde":
                target = ["--to", "~0.2"];
                break;
            case "identityCycle":
                await File.WriteAllTextAsync(project, "<Project><PropertyGroup><A>$(B)</A><B>$(A)</B></PropertyGroup><ItemGroup><PackageReference Include='$(A)' Version='0.1.0' /></ItemGroup></Project>", token);
                break;
            case "identityUnknown":
                await File.WriteAllTextAsync(project, "<Project><ItemGroup><PackageReference Include='$(AnkusUnknownIdentity)' Version='0.1.0' /></ItemGroup></Project>", token);
                break;
            case "cycle":
                await File.WriteAllTextAsync(project, "<Project><PropertyGroup><A>$(B)</A><B>$(A)</B></PropertyGroup><ItemGroup><PackageReference Include='Ankus.Testing' Version='$(A)' /></ItemGroup></Project>", token);
                break;
            case "unknown":
                await File.WriteAllTextAsync(project, "<Project><ItemGroup><PackageReference Include='Ankus.Testing' Version='$(Unknown)' /></ItemGroup></Project>", token);
                target = [];
                break;
            case "duplicateSdk":
                await File.WriteAllTextAsync(project, "<Project Sdk='Ankus.Sdk'/>", token);
                await File.WriteAllTextAsync(Path.Combine(root, "global.json"), "{\"msbuild-sdks\":{\"Ankus.Sdk\":\"0.1.0\",\"Ankus.Sdk\":\"0.1.1\"}}", token);
                break;
            case "missingProject":
                selection = Path.Combine(root, "Bad.slnx");
                await File.WriteAllTextAsync(selection, "<Solution><Project Path='Probe.csproj'/><Project Path='Missing.csproj'/></Solution>", token);
                break;
            case "unknownPackage":
                selection = Path.Combine(root, "Probe.slnx");
                await File.WriteAllTextAsync(selection, "<Solution><Project Path='Probe.csproj'/></Solution>", token);
                target = ["--to", "0.2.0", "--package", "Missing"];
                break;
            case "noFramework":
                await File.WriteAllTextAsync(project, "<Project Sdk='Microsoft.NET.Sdk'/>", token);
                break;
        }

        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", selection, .. target], token);
        Assert.AreEqual(1, result.ExitCode, result.StandardOutput + result.StandardError);
        string diagnostic = mode switch
        {
            "range" => "'not-a-version' is not a NuGet version or version range",
            "emptyRange" => "'' is not a NuGet version or version range",
            "caret" => "'^0.2' is not a NuGet version or version range",
            "tilde" => "'~0.2' is not a NuGet version or version range",
            "cycle" => "circular reference",
            "unknown" => "not a literal NuGet version or range",
            "duplicateSdk" => "one string version for Ankus.Sdk",
            "missingProject" => "selected solution project was not found",
            "unknownPackage" => "does not identify the requested C# project",
            "noFramework" => "no Ankus package or SDK version declarations",
            "identityCycle" => "package identity property 'A' has a circular reference",
            "identityUnknown" => "package identity property 'AnkusUnknownIdentity' has no source declaration",
            _ => throw new InvalidOperationException("Unknown test partition."),
        };
        Assert.Contains(diagnostic, result.StandardError);
        AssertUpgradeFilesUnchanged(root, before);
    }

    /// <summary>
    /// A native sharing violation on a later replacement rolls back files that were already upgraded.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task UpgradeReplacementFailureRollsBackEarlierFiles()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = await WriteUpgradeProjectAsync(root, "0.1.0", token);
        string original = await File.ReadAllTextAsync(project, token);
        await File.WriteAllTextAsync(project, original.Replace("Microsoft.NET.Sdk", "Ankus.Sdk", StringComparison.Ordinal), token);
        string global = Path.Combine(root, "global.json");
        await File.WriteAllTextAsync(global, "{\"msbuild-sdks\":{\"Ankus.Sdk\":\"0.1.0\"}}", token);
        string central = Path.Combine(root, "Directory.Packages.props");
        await File.WriteAllTextAsync(central, "<Project><ItemGroup><PackageVersion Include='Ankus.Runtime' Version='0.1.0'/></ItemGroup></Project>", token);
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        using var locked = new FileStream(global, FileMode.Open, FileAccess.Read, FileShare.Read);
        ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
        Assert.AreEqual(1, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.Contains(global, result.StandardError);
        AssertUpgradeFilesUnchanged(root, before);
    }

    /// <summary>
    /// Read-only targets fail before any manifest is replaced or temporary file is retained.
    /// </summary>
    [TestMethod]
    public async Task UpgradeReadOnlyManifestPreservesAllFiles()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateDirectory();
        string project = await WriteUpgradeProjectAsync(root, "0.1.0", token);
        string central = Path.Combine(root, "Directory.Packages.props");
        await File.WriteAllTextAsync(central, "<Project><ItemGroup><PackageVersion Include='Ankus.Runtime' Version='0.1.0'/></ItemGroup></Project>", token);
        Dictionary<string, byte[]> before = SnapshotUpgradeFiles(root);
        FileAttributes original = File.GetAttributes(project);
        File.SetAttributes(project, original | FileAttributes.ReadOnly);
        try
        {
            ProcessResult result = await InvokeAsync(["upgrade", "--project", project, "--to", "0.2.0"], token);
            Assert.AreEqual(1, result.ExitCode, result.StandardOutput + result.StandardError);
            Assert.Contains("read-only", result.StandardError);
            AssertUpgradeFilesUnchanged(root, before);
        }
        finally
        {
            File.SetAttributes(project, original);
        }
    }

    private static async Task<string> WriteUpgradeProjectAsync(string root, string version, CancellationToken token)
    {
        string path = Path.Combine(root, "Probe.csproj");
        var document = new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("ItemGroup", new XElement("PackageReference", new XAttribute("Include", "Ankus.Testing"), new XAttribute("Version", version)))));
        await File.WriteAllTextAsync(path, document.ToString(), token);
        return path;
    }

    private static async Task CreateUpgradeFeedAsync(string root, string package, CancellationToken token)
    {
        string feed = Path.Combine(root, "feed");
        Directory.CreateDirectory(feed);
        foreach (string version in new[] { "0.1.0", "0.2.0", "0.3.0-beta.2", "0.3.0-beta.10" })
        {
            await using var file = new FileStream(Path.Combine(feed, package + "." + version + ".nupkg"), FileMode.CreateNew);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            ZipArchiveEntry entry = archive.CreateEntry(package + ".nuspec");
            await using Stream destination = entry.Open();
            string manifest = $"<package><metadata><id>{package}</id><version>{version}</version><authors>Ankus tests</authors><description>Owned version-discovery fixture</description></metadata></package>";
            await destination.WriteAsync(Encoding.UTF8.GetBytes(manifest), token);
        }

        var configuration = new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
            new XElement("add", new XAttribute("key", "owned"), new XAttribute("value", feed)))));
        await File.WriteAllTextAsync(Path.Combine(root, "NuGet.Config"), configuration.ToString(), token);
    }

    private static Dictionary<string, byte[]> SnapshotUpgradeFiles(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(static path => path, File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertUpgradeFilesUnchanged(string root, Dictionary<string, byte[]> expected)
    {
        Assert.AreSequenceEqual(expected.Keys.Order(StringComparer.Ordinal),
            Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach ((string path, byte[] content) in expected)
        {
            Assert.AreSequenceEqual(content, File.ReadAllBytes(path), path);
        }
    }

    private static async Task ServeUpgradeFeedAsync(TcpListener listener, TaskCompletionSource observed, Task response, CancellationToken token, string? source = null)
    {
        while (true)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(token);
            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string request = await reader.ReadLineAsync(token) ?? "";
            while (await reader.ReadLineAsync(token) is string line && line.Length != 0)
            {
            }

            observed.TrySetResult();
            await response.WaitAsync(token);
            string body = source is null ? "{invalid"
                : request.StartsWith("GET /index.json ", StringComparison.Ordinal)
                    ? $$"""{"version":"3.0.0","resources":[{"@id":"{{source}}registrations/","@type":"RegistrationsBaseUrl/3.6.0"}]}"""
                    : """{"count":1,"items":[{"count":1,"lower":"0.2.0","upper":"0.2.0","items":[{"catalogEntry":{"id":"Ankus.Testing","version":"0.2.0","listed":true,"authors":"Ankus tests","description":"Owned concurrency fixture"}}]}]}""";
            byte[] content = Encoding.UTF8.GetBytes(body);
            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {content.Length.ToString(CultureInfo.InvariantCulture)}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(content, token);
        }
    }
}
