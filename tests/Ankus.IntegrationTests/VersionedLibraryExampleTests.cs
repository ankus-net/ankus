using Ankus.PgConfig;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes the ported pgrx versioned_so and versioned_custom_libname_so samples.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
[TestClass]
public sealed class VersionedLibraryExampleTests(TestContext context)
{
    /// <summary>
    /// The library carries the extension version, the control has no module_pathname, and the installation SQL,
    /// embedded schema and catalog name the versioned library directly.
    /// </summary>
    /// <param name="extension">The SQL extension name.</param>
    /// <param name="function">The sample function.</param>
    /// <param name="greeting">The function's result.</param>
    /// <param name="libraryBase">The library name before its version.</param>
    [TestMethod]
    [DataRow("ankus_versioned_so", "hello_versioned_so", "Hello, versioned_so", "Ankus.Examples.VersionedLibrary")]
    [DataRow("ankus_versioned_custom_libname_so", "hello_versioned_custom_libname_so", "Hello, versioned_custom_libname_so",
        "versioned_othername")]
    public async Task VersionedSampleNamesItsVersionedLibrary(string extension, string function, string greeting, string libraryBase)
    {
        int major = (await IntegrationEnvironment.GetInstallationAsync(context.CancellationToken)).Version.Major;
        string output = IntegrationEnvironment.NativeOutputDirectory;
        IReadOnlyDictionary<string, string> control = ExtensionControlFile.Read(Path.Join(output, "extension", extension + ".control"));
        Assert.IsFalse(control.ContainsKey("module_pathname"));
        string version = control["default_version"];
        string module = libraryBase + "-" + version;
        string library = module + CustomLibraryNameExampleTests.LibrarySuffix(major);
        Assert.IsTrue(File.Exists(Path.Join(output, library)));
        Assert.IsFalse(File.Exists(Path.Join(output, libraryBase + CustomLibraryNameExampleTests.LibrarySuffix(major))));
        string installation = await File.ReadAllTextAsync(Path.Join(output, "extension", extension + "--" + version + ".sql"),
            context.CancellationToken);
        Assert.Contains($"AS '{module}', ", installation);
        Assert.DoesNotContain("MODULE_PATHNAME", installation);
        ExtensionSchema schema = ExtensionSchema.Read(Path.Join(output, library));
        Assert.AreEqual(installation, schema.Sql);
        Assert.AreEqual(library, schema.Artifacts.Library);
        Assert.AreEqual(installation, schema.Graph!.Sql);
        Assert.Contains($"AS '{module}', ", schema.Select([function], alterExtension: false).Sql);

        await PostgresFixture.Cluster.RunInTransactionAsync(nameof(VersionedSampleNamesItsVersionedLibrary), async (connection, transaction, token) =>
        {
            await ExecuteAsync(connection, transaction, $"CREATE EXTENSION {extension}", token);
            Assert.AreEqual(greeting, await ScalarAsync<string>(connection, transaction, $"SELECT {function}()", token));
            Assert.AreEqual(module + "|" + version, await ScalarAsync<string>(connection, transaction,
                $"SELECT p.probin || '|' || e.extversion FROM pg_proc p, pg_extension e WHERE p.oid = '{function}()'::regprocedure AND e.extname = '{extension}'",
                token));
            if (major >= 18)
            {
                Assert.AreEqual(1L, await ScalarAsync<long>(connection, transaction,
                    $"SELECT count(*) FROM pg_get_loaded_modules() WHERE file_name = '{library}'", token));
            }
        }, context.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(token);
    }
}
