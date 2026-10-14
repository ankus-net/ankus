using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies relocated installations preserve the selected server and leave source files untouched.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
public sealed class PostgresTestInstallationTests(TestContext context)
{
    /// <summary>
    /// Test staging remaps custom paths, retains exact declared payloads and refuses incomplete replacements.
    /// </summary>
    [TestMethod]
    public async Task ScriptDirectoriesStayInsideOwnedTestInstallation()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation source = await IntegrationEnvironment.GetInstallationAsync(token);
        string root = Directory.CreateTempSubdirectory("ankus-test-script-layout-").FullName;
        try
        {
            string outside = Path.Join(root, "author files");
            Directory.CreateDirectory(outside);
            string marker = Path.Join(outside, "retain.txt");
            await File.WriteAllTextAsync(marker, "author-owned", token);
            string stagingParent = root;
            if (!OperatingSystem.IsWindows())
            {
                string physical = Path.Join(root, "physical staging parent");
                Directory.CreateDirectory(physical);
                stagingParent = Path.Join(root, "staging alias");
                Directory.CreateSymbolicLink(stagingParent, physical);
            }

            await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(source, Path.Join(stagingParent, "server"), token);
            string ownedRoot = IntegrationEnvironment.PhysicalDirectory(new DirectoryInfo(owner.RootDirectory)) + Path.DirectorySeparatorChar;
            Assert.StartsWith(ownedRoot, IntegrationEnvironment.PhysicalDirectory(new DirectoryInfo(owner.Installation.SharedDirectory)));
            string?[] directories = [null, "", "../../escape", outside];
            for (int index = 0; index < directories.Length; index++)
            {
                string? directory = directories[index];
                string name = "test_script_layout_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string publication = Path.Join(root, name);
                string extension = Path.Join(publication, "extension");
                Directory.CreateDirectory(extension);
                var manifest = new PublishedExtension(source.Version.Major, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                    "Unused.so", name + ".control", name + "--1.sql", [name + "--1--2.sql"], [name + "--1.control"], directory);
                var settings = new Dictionary<string, string> { ["default_version"] = "1", ["comment"] = "primary" };
                if (directory is not null)
                {
                    settings.Add("directory", directory);
                }

                string originalControl = ExtensionControlFile.Format(settings);
                await File.WriteAllTextAsync(Path.Join(extension, manifest.Control), originalControl, token);
                await File.WriteAllTextAsync(Path.Join(extension, manifest.Sql), "SELECT 'café 🐘';\r\n", token);
                await File.WriteAllTextAsync(Path.Join(extension, manifest.UpgradeScripts[0]), "SELECT 43;\n", token);
                string secondary = Path.Join(extension, manifest.VersionControlFiles[0]);
                await File.WriteAllTextAsync(secondary, "comment='version comment'\r\n", token);
                await File.WriteAllTextAsync(Path.Join(extension, name + "--unlisted.sql"), "unlisted", token);
                manifest.Write(publication);
                owner.InstallExtensionFiles(publication);
                string controls = Path.Join(owner.Installation.SharedDirectory, "extension");
                IReadOnlyDictionary<string, string> staged = ExtensionControlFile.Read(Path.Join(controls, manifest.Control));
                string target = controls;
                if (directory is not null)
                {
                    Assert.AreNotEqual(directory, staged["directory"]);
                    target = Path.GetFullPath(Path.Join(owner.Installation.SharedDirectory, staged["directory"]));
                    Assert.StartsWith(ownedRoot, IntegrationEnvironment.PhysicalDirectory(new DirectoryInfo(target)));
                }
                else
                {
                    Assert.IsFalse(staged.ContainsKey("directory"));
                    Assert.AreEqual(originalControl, await File.ReadAllTextAsync(Path.Join(controls, manifest.Control), token));
                }

                Assert.AreEqual("primary", staged["comment"]);
                Assert.AreEqual("1", staged["default_version"]);
                foreach (string file in new[] { manifest.Sql, manifest.UpgradeScripts[0], manifest.VersionControlFiles[0] })
                {
                    Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Join(extension, file), token),
                        await File.ReadAllBytesAsync(Path.Join(target, file), token));
                }

                Assert.IsFalse(File.Exists(Path.Join(target, name + "--unlisted.sql")));
                Assert.AreEqual(originalControl, await File.ReadAllTextAsync(Path.Join(extension, manifest.Control), token));
                await File.WriteAllTextAsync(Path.Join(extension, manifest.Sql), "changed", token);
                File.Delete(secondary);
                FileNotFoundException missing = Assert.ThrowsExactly<FileNotFoundException>(() => owner.InstallExtensionFiles(publication));
                Assert.AreEqual(secondary, missing.FileName);
                Assert.AreEqual("SELECT 'café 🐘';\r\n", await File.ReadAllTextAsync(Path.Join(target, manifest.Sql), token));
                Assert.AreEqual("version comment", ExtensionControlFile.Read(Path.Join(target, manifest.VersionControlFiles[0]))["comment"]);
            }

            Assert.AreEqual(marker, Assert.ContainsSingle(Directory.GetFiles(outside, "*", SearchOption.AllDirectories)));
            Assert.AreEqual("author-owned", await File.ReadAllTextAsync(marker, token));
            await owner.DisposeAsync();
            Assert.IsFalse(Directory.Exists(owner.RootDirectory));
            Assert.ThrowsExactly<ObjectDisposedException>(() => owner.InstallExtensionFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A Unix directory alias such as Homebrew's opt path relocates through the reported binary directory.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task DirectoryAliasStagesRunnableServerAndPreservesSource()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation source = await IntegrationEnvironment.GetInstallationAsync(token);
        byte[] original = await File.ReadAllBytesAsync(source.PgConfigPath, token);
        string root = Directory.CreateTempSubdirectory("ankus-relocation-").FullName;
        try
        {
            string alias = Path.Join(root, "linked-bin");
            Directory.CreateSymbolicLink(alias, source.BinDirectory);
            PostgresInstallation linked = await PostgresInstallation.CreateAsync(Path.Join(alias, "pg_config"), token);
            Assert.AreEqual(source.BinDirectory, linked.BinDirectory);
            Assert.AreNotEqual(Path.GetDirectoryName(linked.PgConfigPath), linked.BinDirectory);
            string stage = Path.Join(root, "stage");
            await using (PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(linked, stage, token))
            {
                Assert.AreEqual(source.Version, owner.Installation.Version);
                Assert.StartsWith(stage + Path.DirectorySeparatorChar, owner.Installation.PgConfigPath);
                Assert.IsTrue(File.Exists(owner.Installation.PgConfigPath));
                await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
                {
                    Installation = owner.Installation,
                    DataDirectoryBase = Path.Join(root, "data"),
                    LogDirectory = Path.Join(root, "logs"),
                }, token);
                await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
                await using var command = new NpgsqlCommand("SELECT 19 + 23", connection);
                Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
                command.CommandText = "SHOW server_version_num";
                string version = Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token));
                Assert.AreEqual(source.Version.Major, int.Parse(version, System.Globalization.CultureInfo.InvariantCulture) / 10000);
            }

            Assert.IsFalse(Directory.Exists(stage));
            Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(source.PgConfigPath, token));
            PostgresInstallation retained = await PostgresInstallation.CreateAsync(source.PgConfigPath, token);
            Assert.AreEqual(source.Version, retained.Version);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
