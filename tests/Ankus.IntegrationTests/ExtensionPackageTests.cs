using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies published control and SQL files participate in PostgreSQL's extension ownership and relocation lifecycle.
/// These tests serialize catalog changes to the assembly's shared extension and roll them back afterward.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
[DoNotParallelize]
public sealed class ExtensionPackageTests(TestContext context)
{
    /// <summary>
    /// Verifies CREATE EXTENSION records the version, ownership, and generated function execution properties.
    /// </summary>
    /// <param name="signature">The installed SQL function signature.</param>
    [TestMethod]
    [DataRow("add(integer,integer)")]
    [DataRow("greet(text)")]
    public Task InstalledExtensionOwnsGeneratedFunction(string signature)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(InstalledExtensionOwnsGeneratedFunction),
            async (connection, transaction, token) =>
            {
                const string sql = """
                    SELECT e.extversion, e.extrelocatable, p.oid::regprocedure::text,
                           p.proisstrict, l.lanname, p.provolatile::text, p.proparallel::text
                    FROM pg_extension e
                    JOIN pg_depend d ON d.refobjid = e.oid AND d.refclassid = 'pg_extension'::regclass
                    JOIN pg_proc p ON p.oid = d.objid AND d.classid = 'pg_proc'::regclass
                    JOIN pg_language l ON l.oid = p.prolang
                    WHERE e.extname = 'ankus_hello' AND d.deptype = 'e' AND p.oid = $1::regprocedure
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("public." + signature);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);

                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(IntegrationEnvironment.ExtensionVersion("ankus_hello"), reader.GetString(0));
                Assert.IsTrue(reader.GetBoolean(1));
                Assert.AreEqual(signature, reader.GetString(2));
                Assert.IsTrue(reader.GetBoolean(3));
                Assert.AreEqual("c", reader.GetString(4));
                Assert.AreEqual("v", reader.GetString(5));
                Assert.AreEqual("u", reader.GetString(6));
                Assert.IsFalse(await reader.ReadAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies relocatable installation files let PostgreSQL move the function into a different schema.
    /// </summary>
    [TestMethod]
    public Task RelocatingExtensionMovesGeneratedFunction()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(RelocatingExtensionMovesGeneratedFunction),
            async (connection, transaction, token) =>
            {
                const string sql = """
                    CREATE SCHEMA relocated;
                    ALTER EXTENSION ankus_hello SET SCHEMA relocated;
                    SELECT relocated.add(40, 2), to_regprocedure('public.add(integer,integer)') IS NULL,
                           extnamespace::regnamespace::text
                    FROM pg_extension WHERE extname = 'ankus_hello'
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);

                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(42, reader.GetInt32(0));
                Assert.IsTrue(reader.GetBoolean(1));
                Assert.AreEqual("relocated", reader.GetString(2));
                Assert.IsFalse(await reader.ReadAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies uninstall removes owned functions and the published package can reinstall into a requested schema.
    /// </summary>
    [TestMethod]
    public Task DroppingAndReinstallingExtensionRestoresFunction()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(DroppingAndReinstallingExtensionRestoresFunction),
            async (connection, transaction, token) =>
            {
                await using (var drop = new NpgsqlCommand(
                    "DROP EXTENSION ankus_hello; SELECT to_regprocedure('public.add(integer,integer)') IS NULL",
                    connection, transaction))
                {
                    Assert.IsTrue(Assert.IsInstanceOfType<bool>(await drop.ExecuteScalarAsync(token)));
                }

                string sql = $"""
                    CREATE SCHEMA reinstalled;
                    CREATE EXTENSION ankus_hello WITH SCHEMA reinstalled VERSION '{IntegrationEnvironment.ExtensionVersion("ankus_hello")}';
                    SELECT reinstalled.add(-20, 5)
                    """;
                await using var install = new NpgsqlCommand(sql, connection, transaction);
                Assert.AreEqual(-15, Assert.IsInstanceOfType<int>(await install.ExecuteScalarAsync(token)));
            }, context.CancellationToken);
}
