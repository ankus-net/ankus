using System.Runtime.InteropServices;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Every project command rejects malformed property names before resolving projects or changing state.
    /// </summary>
    /// <param name="property">The malformed assignment.</param>
    [TestMethod]
    [DataRow("missing-value")]
    [DataRow("=value")]
    [DataRow(" spaced=value")]
    [DataRow("name;Other=value")]
    [DataRow("prefix:name=value")]
    public async Task ProjectCommandsRejectMalformedGlobalProperties(string property)
    {
        string directory = CreateDirectory();
        foreach (string operation in new[] { "build", "publish", "install", "package", "schema", "run", "connect", "get", "regress", "bench" })
        {
            string[] command = operation == "get" ? [operation, "extname"] : [operation];
            ProcessResult result = await InvokeAsync([.. command, "--project", Path.Join(directory, "missing.csproj"),
                "--property", property], context.CancellationToken);
            Assert.AreEqual(1, result.ExitCode, operation + ": " + result.StandardError);
            Assert.Contains("MSBuild properties must contain name=value", result.StandardError);
            Assert.IsEmpty(Directory.GetFileSystemEntries(directory));
        }
    }

    /// <summary>
    /// A prebuilt publication cannot pretend to apply new build properties.
    /// </summary>
    /// <param name="operation">The publication-consuming command.</param>
    [TestMethod]
    [DataRow("install")]
    [DataRow("package")]
    [DataRow("schema")]
    [DataRow("get")]
    public async Task PublicationCommandsRejectGlobalProperties(string operation)
    {
        string directory = CreateDirectory();
        string[] command = operation == "get" ? [operation, "extname"] : [operation];
        ProcessResult result = await InvokeAsync([.. command, "--from", "missing-publication", "--property", "Probe=value",
            "--home", directory], context.CancellationToken);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("Use either --from", result.StandardError);
        Assert.IsEmpty(Directory.GetFileSystemEntries(directory));
    }

    /// <summary>
    /// Contradictory explicit configurations and targets fail before publication.
    /// </summary>
    /// <param name="property">The contradictory canonical setting.</param>
    /// <param name="error">The expected contract error.</param>
    [TestMethod]
    [DataRow("Configuration=Debug", "Select the same configuration")]
    [DataRow("Configuration=../outside", "Configuration must be a nonempty directory name")]
    [DataRow("AnkusPostgresMajor=12", "AnkusPostgresMajor must select PostgreSQL 13–19")]
    [DataRow("AnkusPostgresMajor=20", "AnkusPostgresMajor must select PostgreSQL 13–19")]
    [DataRow("AnkusPostgresMajor=18.x", "AnkusPostgresMajor must select PostgreSQL 13–19")]
    [DataRow("RuntimeIdentifier=unavailable-target", "host RuntimeIdentifier")]
    [DataRow("SelfContained=false", "SelfContained=true")]
    public async Task ProjectCommandsRejectContradictoryGlobalProperties(string property, string error)
    {
        string directory = CreateDirectory();
        ProcessResult result = await InvokeAsync(["publish", "--project", Path.Join(directory, "missing.csproj"),
            "--configuration", "Release", "--property", property, "--output", Path.Join(directory, "publication")],
            context.CancellationToken);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains(error, result.StandardError);
        Assert.IsEmpty(Directory.GetFileSystemEntries(directory));
    }

    /// <summary>
    /// Explicit PostgreSQL selectors cannot disagree with forwarded global properties.
    /// </summary>
    /// <param name="selection">The conflicting selector.</param>
    [TestMethod]
    [DataRow("major")]
    [DataRow("path")]
    public async Task ProjectCommandsRejectConflictingPostgresProperties(string selection)
    {
        string directory = CreateDirectory();
        string[] options = selection == "major"
            ? ["--pg", DifferentMajor().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--property", "AnkusPostgresMajor=" + MajorText()]
            : ["--pg-config", s_installation.PgConfigPath, "--property", "AnkusPgConfigPath=" + Path.Join(directory, "missing-pg-config")];
        ProcessResult rejected = await InvokeAsync(["publish", "--project", Path.Join(directory, "missing.csproj"),
            .. options, "--output", Path.Join(directory, "publication")], context.CancellationToken);
        Assert.AreNotEqual(0, rejected.ExitCode);
        Assert.Contains(selection == "major" ? "Select the same PostgreSQL major" : "Select the same pg_config", rejected.StandardError);
        Assert.IsEmpty(Directory.GetFileSystemEntries(directory));
    }

    /// <summary>
    /// Literal global properties consistently select the server, configuration, native code, metadata and installation files.
    /// </summary>
    [TestMethod]
    public async Task ProjectGlobalPropertiesReachNativePublicationAndQueries()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "PropertyProbe.csproj");
        XDocument definition = XDocument.Load(s_project);
        definition.Root!.Add(new XElement("Import", new XAttribute("Project", "selection.props")));
        definition.Save(project);
        await File.WriteAllTextAsync(Path.Join(directory, "selection.props"), """
            <Project>
              <PropertyGroup>
                <AnkusPostgresMajor>$(ServerMajor)</AnkusPostgresMajor>
                <AnkusPgConfigPath>$(ServerConfig)</AnkusPgConfigPath>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Shipping+Checked' and '$(Probe)' == 'enabled' and '$(ResponseProbe)' == 'enabled'">
                <AnkusExtensionName>ankus_global_properties</AnkusExtensionName>
                <AnkusExtensionVersion>3.2.1</AnkusExtensionVersion>
                <DefineConstants>$(DefineConstants);PROPERTY_WITNESS</DefineConstants>
              </PropertyGroup>
            </Project>
            """, token);
        await File.WriteAllTextAsync(Path.Join(directory, "Functions.cs"), """
            using Ankus;
            public static class Functions
            {
                [PgFunction]
                public static int PropertyValue()
                {
            #if PROPERTY_WITNESS
                    return 42;
            #else
                    return -1;
            #endif
                }
            }
            """, token);
        await File.WriteAllTextAsync(Path.Join(directory, "Directory.Build.rsp"),
            "-property:ResponseProbe=enabled" + Environment.NewLine, token);
        string configurationDirectory = Directory.CreateDirectory(Path.Join(directory, "configuration with spaces")).FullName;
        string restoreConfiguration = Path.Join(configurationDirectory, "NuGet.Config");
        File.Copy(Path.Join(s_root, "NuGet.Config"), restoreConfiguration);
        new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"))))
            .Save(Path.Join(directory, "NuGet.Config"));
        string packageDirectory = Path.Join(directory, "packages with spaces");
        string intermediateDirectory = Path.Join(directory, "intermediate with spaces") + Path.DirectorySeparatorChar;
        string home = Path.Join(directory, "unregistered home");
        string[] options = ["--home", home, "--project", project, "--property", "Configuration=Shipping+Checked",
            "-p", "Probe=disabled", "--property", "pRoBe=enabled", "--property", "ServerMajor=" + MajorText(),
            "--property", "ServerConfig=" + s_installation.PgConfigPath,
            "--property", "RestoreConfigFile=" + restoreConfiguration,
            "--property", "RestorePackagesPath=" + packageDirectory,
            "--property", "BaseIntermediateOutputPath=" + intermediateDirectory];
        ProcessResult name = await InvokeAsync(["get", "extname", .. options], token);
        Assert.AreEqual(0, name.ExitCode, name.StandardError);
        Assert.AreEqual("ankus_global_properties" + Environment.NewLine, name.StandardOutput);
        ProcessResult version = await InvokeAsync(["get", "default_version", .. options], token);
        Assert.AreEqual(0, version.ExitCode, version.StandardOutput + version.StandardError);
        Assert.AreEqual("3.2.1" + Environment.NewLine, version.StandardOutput);
        ProcessResult build = await InvokeAsync(["build", .. options], token);
        Assert.AreEqual(0, build.ExitCode, build.StandardOutput + build.StandardError);
        Assert.IsTrue(Directory.Exists(Path.Join(packageDirectory, "ankus.runtime", s_version)));
        Assert.IsNotEmpty(Directory.GetFiles(intermediateDirectory, "*", SearchOption.AllDirectories));
        Assert.IsFalse(Directory.Exists(Path.Join(directory, "obj")));
        string defaultOutput = Path.Join(directory, "bin", "ankus", s_postgresKey,
            RuntimeInformation.RuntimeIdentifier, "Shipping+Checked");
        PublishedExtension original = PublishedExtension.Read(defaultOutput);
        Assert.AreEqual("ankus_global_properties.control", original.Control);
        Assert.AreEqual("ankus_global_properties--3.2.1.sql", original.Sql);
        string output = CreateDirectory();
        ProcessResult publish = await InvokeAsync(["publish", .. options, "--output", output], token);
        Assert.AreEqual(0, publish.ExitCode, publish.StandardOutput + publish.StandardError);
        PublishedExtension publication = PublishedExtension.Read(output);
        Assert.AreEqual(original.Control, publication.Control);
        Assert.AreEqual(original.Sql, publication.Sql);
        await using (PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token))
        {
            await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
            await ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_global_properties");
            Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, "SELECT property_value()"));
            Assert.AreEqual("3.2.1", await SqlPackageScalarAsync<string>(connection,
                "SELECT extversion FROM pg_extension WHERE extname='ankus_global_properties'"));
        }

        ProcessResult schema = await InvokeAsync(["schema", .. options], token);
        Assert.AreEqual(0, schema.ExitCode, schema.StandardError);
        Assert.AreEqual(await File.ReadAllTextAsync(Path.Join(defaultOutput, "extension", original.Sql), token), schema.StandardOutput);
        ProcessResult skippedSchema = await InvokeAsync(["schema", .. options, "--skip-build"], token);
        Assert.AreEqual(0, skippedSchema.ExitCode, skippedSchema.StandardError);
        Assert.AreEqual(schema.StandardOutput, skippedSchema.StandardOutput);
        // From the project directory without --project, a relative intermediate path names the same directory, as
        // pgrx's third schema invocation uses a relative Cargo configuration from the extension directory.
        string[] relative = [.. options.Where((_, index) => index is not (2 or 3))];
        relative[^1] = "BaseIntermediateOutputPath=" + Path.GetFileName(Path.TrimEndingDirectorySeparator(intermediateDirectory)) +
            Path.DirectorySeparatorChar;
        ProcessResult projectDirectorySchema = await PackageProcessRunner.RunAsync(s_tool, ["schema", .. relative, "--skip-build"],
            s_environment, token, workingDirectory: directory);
        Assert.AreEqual(0, projectDirectorySchema.ExitCode, projectDirectorySchema.StandardOutput + projectDirectorySchema.StandardError);
        Assert.AreEqual(schema.StandardOutput, projectDirectorySchema.StandardOutput);
        Assert.IsFalse(Directory.Exists(Path.Join(directory, "obj")));
        string stage = CreateDirectory();
        ProcessResult install = await InvokeAsync(["install", .. options, "--destdir", stage], token);
        Assert.AreEqual(0, install.ExitCode, install.StandardOutput + install.StandardError);
        Assert.AreEqual(await File.ReadAllTextAsync(Path.Join(defaultOutput, "extension", original.Control), token),
            await File.ReadAllTextAsync(Path.Join(StagedPath(stage, s_installation.SharedDirectory), "extension", original.Control), token));
        string package = CreateDirectory();
        ProcessResult packaged = await InvokeAsync(["package", .. options, "--output", package], token);
        Assert.AreEqual(0, packaged.ExitCode, packaged.StandardOutput + packaged.StandardError);
        Assert.HasCount(3, Directory.GetFiles(package, "*", SearchOption.AllDirectories));
        Assert.IsFalse(Directory.Exists(home));
    }
}
