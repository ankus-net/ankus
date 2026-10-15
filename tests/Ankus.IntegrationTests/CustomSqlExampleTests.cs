using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes pgrx's custom_sql example through its independently published extension.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
[DoNotParallelize]
public sealed class CustomSqlExampleTests(TestContext context)
{
    /// <summary>
    /// Mirrors pgrx's ordering test and checks the generated schemas, enum and type that the SQL blocks require.
    /// </summary>
    [TestMethod]
    public Task CustomSqlSampleRunsBlocksInDependencyOrder()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CustomSqlSampleRunsBlocksInDependencyOrder), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, "CREATE EXTENSION ankus_custom_sql", token);
            Assert.AreSequenceEqual<string>(["bootstrap", "single_raw", "single", "multiple_raw", "multiple", "finalizer"],
                await Scalar<string[]>(connection, transaction, "SELECT array_agg(message ORDER BY ctid) FROM extension_sql", token));
            Assert.AreSequenceEqual<string>(["Brandy", "Nami"],
                await Scalar<string[]>(connection, transaction, "SELECT enum_range(NULL::dogs.dog)::text[]", token));
            Assert.AreEqual("""{"last_chomp":"Nami"}""",
                await Scalar<string>(connection, transaction, """SELECT '{"last_chomp":"Nami"}'::home.ball::text""", token));
            Assert.AreSequenceEqual<string>(["dogs.dog", "home.ball", "public.extension_sql"], await Scalar<string[]>(connection, transaction, """
                SELECT array_agg(name ORDER BY name COLLATE "C") FROM (
                    SELECT n.nspname || '.' || t.typname AS name FROM pg_depend d
                        JOIN pg_type t ON d.classid = 'pg_type'::regclass AND t.oid = d.objid
                        JOIN pg_namespace n ON n.oid = t.typnamespace
                    WHERE d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e' AND t.typcategory <> 'A' AND t.typrelid = 0
                        AND d.refobjid = (SELECT oid FROM pg_extension WHERE extname = 'ankus_custom_sql')
                    UNION ALL
                    SELECT n.nspname || '.' || c.relname FROM pg_depend d
                        JOIN pg_class c ON d.classid = 'pg_class'::regclass AND c.oid = d.objid
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e'
                        AND d.refobjid = (SELECT oid FROM pg_extension WHERE extname = 'ankus_custom_sql')) AS members
                """, token));
            Assert.AreSequenceEqual<string>(["dogs", "home"], await Scalar<string[]>(connection, transaction, """
                SELECT array_agg(n.nspname::text ORDER BY n.nspname COLLATE "C") FROM pg_depend d
                    JOIN pg_namespace n ON d.classid = 'pg_namespace'::regclass AND n.oid = d.objid
                WHERE d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e'
                    AND d.refobjid = (SELECT oid FROM pg_extension WHERE extname = 'ankus_custom_sql')
                """, token));
            Assert.IsTrue(await Scalar<bool>(connection, transaction, """
                SELECT NOT extrelocatable AND NOT (SELECT superuser FROM pg_available_extension_versions WHERE name = 'ankus_custom_sql')
                FROM pg_extension WHERE extname = 'ankus_custom_sql'
                """, token));
        }, context.CancellationToken);

    /// <summary>
    /// Places bootstrap, required, file and final blocks around the generated declarations in the installation script.
    /// </summary>
    [TestMethod]
    public async Task CustomSqlSampleOrdersInstallationScript()
    {
        string script = await File.ReadAllTextAsync(IntegrationEnvironment.ExtensionScript("ankus_custom_sql"), context.CancellationToken);
        string[] markers =
        [
            "INSERT INTO extension_sql VALUES ('bootstrap');",
            "CREATE SCHEMA IF NOT EXISTS \"dogs\";",
            "INSERT INTO extension_sql VALUES ('single_raw');",
            "INSERT INTO extension_sql VALUES ('single');",
            "INSERT INTO extension_sql VALUES ('multiple_raw');",
            "INSERT INTO extension_sql VALUES ('multiple');",
            "INSERT INTO extension_sql VALUES ('finalizer');",
        ];
        int previous = -1;
        foreach (string marker in markers)
        {
            int position = script.IndexOf(marker, StringComparison.Ordinal);
            Assert.IsGreaterThan(previous, position, marker);
            Assert.AreEqual(position, script.LastIndexOf(marker, StringComparison.Ordinal), marker);
            previous = position;
        }

        int multipleRaw = script.IndexOf("('multiple_raw')", StringComparison.Ordinal);
        int finalizer = script.IndexOf("('finalizer')", StringComparison.Ordinal);
        string[] declarations =
        [
            "CREATE SCHEMA IF NOT EXISTS \"home\";",
            "CREATE TYPE \"dogs\".\"dog\" AS ENUM",
            "CREATE TYPE \"home\".\"ball\" (",
        ];
        foreach (string declaration in declarations)
        {
            int position = script.IndexOf(declaration, StringComparison.Ordinal);
            Assert.IsGreaterThan(script.IndexOf("('bootstrap')", StringComparison.Ordinal), position, declaration);
            Assert.IsLessThan(multipleRaw, position, declaration);
            Assert.IsLessThan(finalizer, position, declaration);
        }

        Assert.Contains("-- SQL file: sql/single.sql", script);
        Assert.Contains("-- SQL file: sql/multiple.sql", script);
        Assert.Contains("-- SQL file: sql/finalizer.sql", script);
    }

    /// <summary>
    /// Rejects an undeclared enum label in the type's JSON text, recovers, and reruns the script after removal.
    /// </summary>
    [TestMethod]
    public Task CustomSqlSampleRecoversAndReinstalls()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CustomSqlSampleRecoversAndReinstalls), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, "CREATE EXTENSION ankus_custom_sql", token);
            await transaction.SaveAsync("unknown_dog", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                Execute(connection, transaction, """SELECT '{"last_chomp":"Rex"}'::home.ball""", token));
            Assert.AreEqual("22P02", error.SqlState);
            Assert.AreEqual("Invalid JSON custom-type value.", error.MessageText);
            await transaction.RollbackAsync("unknown_dog", token);
            Assert.AreEqual("""{"last_chomp":"Brandy"}""",
                await Scalar<string>(connection, transaction, """SELECT '{"last_chomp":"Brandy"}'::home.ball::text""", token));

            await Execute(connection, transaction, "DROP EXTENSION ankus_custom_sql", token);
            Assert.IsTrue(await Scalar<bool>(connection, transaction, """
                SELECT to_regclass('extension_sql') IS NULL AND to_regnamespace('dogs') IS NULL AND to_regnamespace('home') IS NULL
                """, token));
            await Execute(connection, transaction, "CREATE EXTENSION ankus_custom_sql", token);
            Assert.AreSequenceEqual<string>(["bootstrap", "single_raw", "single", "multiple_raw", "multiple", "finalizer"],
                await Scalar<string[]>(connection, transaction, "SELECT array_agg(message ORDER BY ctid) FROM extension_sql", token));
            Assert.AreEqual(connection.ProcessID, await Scalar<int>(connection, transaction, "SELECT pg_backend_pid()", token));
        }, context.CancellationToken);

    /// <summary>
    /// Returns one typed observation.
    /// </summary>
    private static async Task<T> Scalar<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// Completes DDL or an error-producing operation before the next assertion.
    /// </summary>
    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(token);
    }
}
