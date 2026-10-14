using System.Runtime.InteropServices;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Invalid configurations fail during parsing for every command before project, installation or server mutation.
    /// </summary>
    /// <param name="configuration">The invalid path-component partition.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("../outside")]
    [DataRow(@"..\outside")]
    [DataRow("/absolute")]
    [DataRow("name:stream")]
    [DataRow("name*")]
    [DataRow("line\nbreak")]
    [DataRow("trailing.")]
    [DataRow("trailing ")]
    public async Task InvalidBuildConfigurationsDoNotMutateState(string configuration)
    {
        string home = CreateDirectory();
        string project = Path.Join(home, "missing.csproj");
        foreach (string operation in new[] { "build", "publish", "install", "package", "schema", "run", "connect", "get", "regress", "test" })
        {
            string[] command = operation == "get" ? [operation, "comment"] : [operation];
            string[] projectArguments = operation == "test" ? [] : ["--project", project];
            ProcessResult result = await InvokeAsync([.. command, "--home", home, .. projectArguments,
                "--configuration", configuration], context.CancellationToken);
            Assert.AreEqual(1, result.ExitCode, operation + ": " + result.StandardOutput + result.StandardError);
            Assert.Contains("Configuration must be a nonempty directory name", result.StandardError);
            Assert.IsEmpty(Directory.GetFileSystemEntries(home));
        }
    }

    /// <summary>
    /// Build, publish and staged installation retain a custom configuration's paths, imported identity and native behavior.
    /// </summary>
    [TestMethod]
    public async Task CustomBuildCommandsPreserveConfiguration()
    {
        CancellationToken token = context.CancellationToken;
        const string Configuration = "Shipping+Checked";
        string directory = CreateDirectory();
        string project = Path.Join(directory, "Configured.csproj");
        XDocument definition = XDocument.Load(s_project);
        definition.Root!.Add(new XElement("Import", new XAttribute("Project", "shipping.props")));
        definition.Save(project);
        new XDocument(new XElement("Project", new XElement("PropertyGroup",
            new XAttribute("Condition", "'$(Configuration)' == 'Shipping+Checked'"),
            new XElement("AssemblyName", "Ankus.Configured.Library"),
            new XElement("AnkusExtensionName", "ankus_configured"),
            new XElement("AnkusExtensionVersion", "3.2.1"),
            new XElement("DefineConstants", "$(DefineConstants);CONFIGURED_BUILD")))).Save(Path.Join(directory, "shipping.props"));
        await File.WriteAllTextAsync(Path.Join(directory, "Functions.cs"), """
            using Ankus;
            public static class Functions
            {
                [PgFunction]
                public static int ConfigurationValue()
                {
            #if CONFIGURED_BUILD
                    return 42;
            #else
                    return -1;
            #endif
                }
            }
            """, token);
        string[] options = ["--home", s_home, "--pg", MajorText(), "--project", project, "-c", Configuration];
        ProcessResult build = await InvokeAsync(["build", .. options], token);
        Assert.AreEqual(0, build.ExitCode, build.StandardOutput + build.StandardError);
        string publication = Path.Join(directory, "bin", "ankus", s_postgresKey, RuntimeInformation.RuntimeIdentifier, Configuration);
        PublishedExtension original = PublishedExtension.Read(publication);
        Assert.AreEqual("ankus_configured.control", original.Control);
        Assert.AreEqual("ankus_configured--3.2.1.sql", original.Sql);
        Assert.StartsWith("Ankus.Configured.Library.", original.Library);
        Assert.IsFalse(Directory.Exists(Path.Join(directory, "bin", "ankus", s_postgresKey, RuntimeInformation.RuntimeIdentifier, "Release")));

        string output = CreateDirectory();
        ProcessResult publish = await InvokeAsync(["publish", .. options, "--output", output], token);
        Assert.AreEqual(0, publish.ExitCode, publish.StandardOutput + publish.StandardError);
        PublishedExtension manifest = PublishedExtension.Read(output);
        Assert.AreEqual(original.Library, manifest.Library);
        Assert.AreEqual(original.Sql, manifest.Sql);
        await using (PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token))
        {
            await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
            await ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_configured");
            Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, "SELECT configuration_value()"));
            Assert.AreEqual("3.2.1", await SqlPackageScalarAsync<string>(connection,
                "SELECT extversion FROM pg_extension WHERE extname='ankus_configured'"));
        }

        string stage = CreateDirectory();
        ProcessResult install = await InvokeAsync(["install", .. options, "--destdir", stage], token);
        Assert.AreEqual(0, install.ExitCode, install.StandardOutput + install.StandardError);
        string extension = Path.Join(StagedPath(stage, s_installation.SharedDirectory), "extension");
        Assert.AreEqual(await File.ReadAllTextAsync(Path.Join(publication, "extension", original.Sql), token),
            await File.ReadAllTextAsync(Path.Join(extension, original.Sql), token));
        Assert.AreEqual(await File.ReadAllTextAsync(Path.Join(publication, "extension", original.Control), token),
            await File.ReadAllTextAsync(Path.Join(extension, original.Control), token));
        Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Join(publication, original.Library), token),
            await File.ReadAllBytesAsync(Path.Join(StagedPath(stage, s_installation.LibraryDirectory), original.Library), token));
        Assert.HasCount(3, Directory.GetFiles(stage, "*", SearchOption.AllDirectories));
    }
}
