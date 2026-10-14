using Ankus.PgConfig;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes the ported pgrx custom_libname sample, whose library name differs from its project and extension names.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
[TestClass]
public sealed class CustomLibraryNameExampleTests(TestContext context)
{
    /// <summary>
    /// AnkusLibraryName names the published library, the control's module_pathname and the loaded file, while the
    /// assembly keeps its project name.
    /// </summary>
    [TestMethod]
    public async Task CustomLibraryNameLoadsSeparatelyNamedLibrary()
    {
        int major = (await IntegrationEnvironment.GetInstallationAsync(context.CancellationToken)).Version.Major;
        string library = "other_name" + LibrarySuffix(major);
        string output = IntegrationEnvironment.NativeOutputDirectory;
        Assert.IsTrue(File.Exists(Path.Join(output, library)));
        Assert.IsFalse(File.Exists(Path.Join(output, "Ankus.Examples.CustomLibraryName" + LibrarySuffix(major))));
        IReadOnlyDictionary<string, string> control = ExtensionControlFile.Read(Path.Join(output, "extension", "ankus_custom_libname.control"));
        Assert.AreEqual(library, control["module_pathname"]);
        ExtensionSchema schema = ExtensionSchema.Read(Path.Join(output, library));
        Assert.AreEqual("ankus_custom_libname", schema.Name);
        Assert.AreEqual(library, schema.Artifacts.Library);
        Assert.Contains("'MODULE_PATHNAME'", schema.Sql);

        await PostgresFixture.Cluster.RunInTransactionAsync(nameof(CustomLibraryNameLoadsSeparatelyNamedLibrary), async (connection, transaction, token) =>
        {
            await ExecuteAsync(connection, transaction, "CREATE EXTENSION ankus_custom_libname", token);
            Assert.AreEqual("Hello, custom_libname", await ScalarAsync<string>(connection, transaction, "SELECT hello_custom_libname()", token));
            Assert.AreEqual(library, await ScalarAsync<string>(connection, transaction,
                "SELECT probin FROM pg_proc WHERE oid = 'hello_custom_libname()'::regprocedure", token));
            if (major >= 18)
            {
                // The module identity keeps the assembly name, as pgrx's keeps the Cargo package name.
                Assert.AreEqual("Ankus.Examples.CustomLibraryName|" + control["default_version"], await ScalarAsync<string>(connection, transaction,
                    $"SELECT module_name || '|' || version FROM pg_get_loaded_modules() WHERE file_name = '{library}'", token));
            }
        }, context.CancellationToken);
    }

    /// <summary>
    /// Returns PostgreSQL's dynamic library suffix, which Ankus also uses for published libraries.
    /// </summary>
    /// <param name="major">The PostgreSQL major.</param>
    /// <returns>The platform suffix.</returns>
    internal static string LibrarySuffix(int major)
        => OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() && major >= 16 ? ".dylib" : ".so";

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
