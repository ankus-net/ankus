using System.Globalization;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Real libraries retain project or authored module identity across incremental publications and selected server ABIs.
    /// </summary>
    /// <param name="mode">Whether the library uses defaults, an override, or only a module declaration.</param>
    [TestMethod]
    [DataRow("default")]
    [DataRow("custom")]
    [DataRow("module-only")]
    public async Task NativeModuleIdentityReachesPostgres(string mode)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "Identity.csproj");
        string output = Path.Join(directory, "published");
        for (int revision = 1; revision <= 2; revision++)
        {
            string version = "2.3.4-preview." + revision.ToString(CultureInfo.InvariantCulture);
            string name = mode == "default" ? "Ankus.Native.Metadata" : "Module é \"a\" \\path " + revision.ToString(CultureInfo.InvariantCulture);
            string moduleVersion = mode == "default" ? version : "custom-😀-" + revision.ToString(CultureInfo.InvariantCulture);
            XDocument definition = XDocument.Load(s_project);
            definition.Root!.Add(new XElement("PropertyGroup",
                new XElement("AssemblyName", "Ankus.Native.Metadata"), new XElement("Version", version),
                new XElement("AnkusExtensionName", "ankus_module_probe"), new XElement("AnkusExtensionVersion", "7.8.9")));
            definition.Save(project);
            string identity = mode == "default" ? "" : $$""""
                [assembly: Ankus.PgModule(Name = """{{name}}""", Version = """{{moduleVersion}}""")]

                """";
            await File.WriteAllTextAsync(Path.Join(directory, "Functions.cs"), identity + (mode == "module-only" ? "" : """
                public static class Functions
                {
                    [Ankus.PgFunction] public static int ModuleAnswer() => 42;
                }
                """), token);
            ProcessResult published = await InvokeAsync(["publish", "--home", s_home, "--pg", MajorText(), "--project", project,
                "--output", output], token);
            Assert.AreEqual(0, published.ExitCode, published.StandardOutput + published.StandardError);
            PublishedExtension manifest = PublishedExtension.Read(output);
            Assert.AreEqual("ankus_module_probe--7.8.9.sql", manifest.Sql);
            await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token);
            await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
            await using var command = new NpgsqlCommand("CREATE EXTENSION ankus_module_probe; LOAD '" + manifest.Library +
                "'; SELECT extversion FROM pg_extension WHERE extname = 'ankus_module_probe'", connection);
            Assert.AreEqual("7.8.9", await command.ExecuteScalarAsync(token));
            if (mode != "module-only")
            {
                command.CommandText = "SELECT module_answer()";
                Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
            }

            if (s_installation.Version.Major >= 18)
            {
                command.CommandText = "SELECT module_name, version FROM pg_get_loaded_modules() WHERE file_name = @library";
                command.Parameters.AddWithValue("library", manifest.Library);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(name, reader.GetString(0));
                Assert.AreEqual(moduleVersion, reader.GetString(1));
                Assert.IsFalse(await reader.ReadAsync(token));
            }
        }
    }
}
