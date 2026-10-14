using System.Runtime.InteropServices;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Packaging builds the requested configuration, retains custom names, and produces a loadable installation tree.
    /// </summary>
    /// <param name="configuration">The configuration, or null to exercise the Release default.</param>
    /// <param name="version">The independently expected configuration-specific version.</param>
    [TestMethod]
    [DataRow(null, "2.3.4")]
    [DataRow("Debug", "2.3.5-debug")]
    [DataRow("Profilé Candidate", "2.3.6-candidate")]
    public async Task PackageBuildsConfiguredExtensionAndLoadsInPostgres(string? configuration, string version)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "PackageProbe.csproj");
        XDocument document = XDocument.Load(s_project);
        document.Root!.Add(new XElement("PropertyGroup",
            new XElement("AssemblyName", "Packaged.Custom.Library"),
            new XElement("AnkusExtensionName", "ankus_package_probe"),
            new XElement("AnkusExtensionVersion", "2.3.4"),
            new XElement("AnkusExtensionVersion", new XAttribute("Condition", "'$(Configuration)' == 'Debug'"), "2.3.5-debug"),
            new XElement("AnkusExtensionVersion", new XAttribute("Condition", "'$(Configuration)' == 'Profilé Candidate'"), "2.3.6-candidate")));
        document.Save(project);
        File.Copy(Path.Join(Path.GetDirectoryName(s_project)!, "Hello.cs"), Path.Join(directory, "Hello.cs"));
        string publication = Path.Join(directory, "bin", "ankus", s_installation.Label,
            RuntimeInformation.RuntimeIdentifier, configuration ?? "Release");
        string output = configuration is null ? Path.Join(publication, "ankus_package_probe-" + s_installation.Label) : CreateDirectory();
        string[] selection = configuration is null ? [] : ["--configuration", configuration, "--output", output];
        ProcessResult result = await InvokeAsync(["package", "--home", s_home, "--pg", MajorText(),
            "--project", project, .. selection], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.Contains("Packaged ", result.StandardOutput);
        PublishedExtension manifest = PublishedExtension.Read(publication);
        Assert.AreEqual("ankus_package_probe.control", manifest.Control);
        Assert.AreEqual("ankus_package_probe--" + version + ".sql", manifest.Sql);
        Assert.StartsWith("Packaged.Custom.Library.", manifest.Library);
        Assert.HasCount(3, Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        await AssertPackagedExtensionAsync(output, manifest, "ankus_package_probe", version, token);
    }

    /// <summary>
    /// An existing publication packages without a project, updates only its payload, and loads after moving the package root.
    /// </summary>
    /// <param name="explicitOutput">Whether to select an output root instead of the default child directory.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PackageExistingPublicationPreservesPayloadAndLoadsAfterMove(bool explicitOutput)
    {
        CancellationToken token = context.CancellationToken;
        PublishedExtension manifest = PublishedExtension.Read(s_published);
        string source = CreateDirectory();
        manifest.Write(source);
        Directory.CreateDirectory(Path.Join(source, "extension"));
        string[] artifacts = [manifest.Library, Path.Join("extension", manifest.Control), Path.Join("extension", manifest.Sql)];
        var originals = new Dictionary<string, byte[]>();
        foreach (string artifact in artifacts)
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.Join(s_published, artifact), token);
            originals.Add(artifact, bytes);
            await File.WriteAllBytesAsync(Path.Join(source, artifact), bytes, token);
        }

        await File.WriteAllTextAsync(Path.Join(source, "unrelated.txt"), "source-only", token);
        string output = explicitOutput ? CreateDirectory() : Path.Join(source, "ankus_tool_probe-" + s_installation.Label);
        Directory.CreateDirectory(output);
        string marker = Path.Join(output, "keep.txt");
        await File.WriteAllTextAsync(marker, "existing-package-file", token);
        string[] paths =
        [
            Path.Join(s_installation.LibraryDirectory, manifest.Library),
            Path.Join(s_installation.SharedDirectory, "extension", manifest.Control),
            Path.Join(s_installation.SharedDirectory, "extension", manifest.Sql),
        ];
        Dictionary<string, byte[]?> installationFiles = paths.ToDictionary(static path => path,
            static path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        string[] selection = explicitOutput ? ["-o", output] : [];
        string[] arguments = ["package", "--home", s_home, "--pg", MajorText(), "--from", source, .. selection];
        ProcessResult first = await InvokeAsync(arguments, token);
        Assert.AreEqual(0, first.ExitCode, first.StandardError);
        string installedSql = Path.Join(PackageSharedDirectory(output), "extension", manifest.Sql);
        await File.WriteAllTextAsync(installedSql, "stale package content", token);
        ProcessResult repeated = await InvokeAsync(arguments, token);
        Assert.AreEqual(0, repeated.ExitCode, repeated.StandardError);
        Assert.HasCount(4, Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        Assert.AreEqual("existing-package-file", await File.ReadAllTextAsync(marker, token));
        string[] packaged =
        [
            Path.Join(PackageLibraryDirectory(output), manifest.Library),
            Path.Join(PackageSharedDirectory(output), "extension", manifest.Control),
            installedSql,
        ];
        for (int index = 0; index < artifacts.Length; index++)
        {
            Assert.AreSequenceEqual(originals[artifacts[index]], await File.ReadAllBytesAsync(packaged[index], token));
            Assert.AreSequenceEqual(originals[artifacts[index]], await File.ReadAllBytesAsync(Path.Join(source, artifacts[index]), token));
        }

        foreach ((string path, byte[]? expected) in installationFiles)
        {
            if (expected is null)
            {
                Assert.IsFalse(File.Exists(path));
            }
            else
            {
                Assert.AreSequenceEqual(expected, await File.ReadAllBytesAsync(path, token));
            }
        }

        string relocated = output + " moved";
        Directory.Move(output, relocated);
        await AssertPackagedExtensionAsync(relocated, manifest, "ankus_tool_probe", "0.1.0", token);
    }

    /// <summary>
    /// Conflicting source selections fail before trying to resolve an unconfigured PostgreSQL installation.
    /// </summary>
    [TestMethod]
    public async Task PackageRejectsAmbiguousSources()
    {
        string output = Path.Join(CreateDirectory(), "package");
        ProcessResult result = await InvokeAsync(["package", "--home", CreateDirectory(), "--from", s_published,
            "--project", "missing.csproj", "--output", output], context.CancellationToken);
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("Use either --from or --project.", result.StandardError);
        Assert.IsFalse(Directory.Exists(output));
    }

    /// <summary>
    /// A compiler failure reports a nonzero result and preserves the existing package tree byte for byte.
    /// </summary>
    [TestMethod]
    public async Task FailedPackageBuildPreservesExistingTree()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "BrokenPackage.csproj");
        XDocument.Load(s_project).Save(project);
        await File.WriteAllTextAsync(Path.Join(directory, "Broken.cs"), "#error package-build-failure", token);
        string output = CreateDirectory();
        string marker = Path.Join(output, "existing.control");
        const string Original = "preserve the previous package";
        await File.WriteAllTextAsync(marker, Original, token);
        ProcessResult result = await InvokeAsync(["package", "--home", s_home, "--pg", MajorText(),
            "--project", project, "--output", output], token);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("package-build-failure", result.StandardOutput + result.StandardError);
        Assert.AreEqual(marker, Assert.ContainsSingle(Directory.GetFiles(output, "*", SearchOption.AllDirectories)));
        Assert.AreEqual(Original, await File.ReadAllTextAsync(marker, token));
    }

    private async Task AssertPackagedExtensionAsync(string output, PublishedExtension manifest,
        string name, string version, CancellationToken token, string? libraryDirectory = null, string? sharedDirectory = null)
    {
        string libraries = libraryDirectory ?? PackageLibraryDirectory(output);
        string shared = sharedDirectory ?? PackageSharedDirectory(output);
        PostgresInstallation installation = await ReserveCaseInstallationAsync(token);
        List<string> configuration = ["dynamic_library_path = '" + EscapeSetting(libraries) + "'"];
        if (installation.Version.Major >= 18)
        {
            configuration.Add("extension_control_path = '" + EscapeSetting(shared) + "'");
        }
        else
        {
            // Before PostgreSQL18, copy the actual packaged SQL into this test's owned installation.
            string[] artifacts = [manifest.Control, manifest.Sql];
            foreach (string artifact in artifacts)
            {
                File.Copy(Path.Join(shared, "extension", artifact),
                    Path.Join(installation.SharedDirectory, "extension", artifact), overwrite: true);
            }
        }

        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
        {
            Installation = installation,
            DataDirectoryBase = Path.Join(s_root, "package-pgdata"),
            LogDirectory = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs"),
            PostgreSqlConfiguration = configuration,
        }, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("CREATE EXTENSION \"" + name + "\"; SELECT public.add(19, 23)", connection);
        Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
        command.CommandText = "SELECT extversion FROM pg_extension WHERE extname = @name";
        command.Parameters.AddWithValue("name", name);
        Assert.AreEqual(version, await command.ExecuteScalarAsync(token));
    }

    private static string PackageLibraryDirectory(string output)
        => OperatingSystem.IsWindows() ? Path.Join(output, "lib") : StagedPath(output, s_installation.LibraryDirectory);

    private static string PackageSharedDirectory(string output)
        => OperatingSystem.IsWindows() ? Path.Join(output, "share") : StagedPath(output, s_installation.SharedDirectory);
}
