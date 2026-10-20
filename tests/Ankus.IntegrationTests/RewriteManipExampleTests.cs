using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes the ported pgrx rewrite_manip sample through its published Native AOT extension.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
[TestClass]
public sealed class RewriteManipExampleTests(TestContext context)
{
    /// <summary>
    /// Mirrors every pgrx test and README query, plus boundary indexes, in the extension's fixed schema.
    /// </summary>
    /// <param name="sql">The schema-qualified call.</param>
    /// <param name="expected">The independently derived result.</param>
    [TestMethod]
    [DataRow("SELECT rewrite_manip.demo_change_var_nodes(1, 5)", 5)]
    [DataRow("SELECT rewrite_manip.demo_change_var_nodes(2, 5)", 5)]
    [DataRow("SELECT rewrite_manip.demo_change_var_nodes(5, 5)", 5)]
    [DataRow("SELECT rewrite_manip.demo_change_var_nodes(0, 7)", 7)]
    [DataRow("SELECT rewrite_manip.demo_change_var_nodes(2147483647, 1)", 1)]
    [DataRow("SELECT rewrite_manip.demo_offset_var_nodes(3)", 4)]
    [DataRow("SELECT rewrite_manip.demo_offset_var_nodes(0)", 1)]
    [DataRow("SELECT rewrite_manip.demo_offset_var_nodes(-1)", 0)]
    [DataRow("SELECT rewrite_manip.demo_offset_var_nodes(2147483646)", 2147483647)]
    [DataRow("SELECT rewrite_manip.demo_increment_var_sublevels_up(0, 2)", 2)]
    [DataRow("SELECT rewrite_manip.demo_increment_var_sublevels_up(3, -1)", 2)]
    [DataRow("SELECT rewrite_manip.demo_increment_var_sublevels_up(7, 0)", 7)]
    [DataRow("SELECT rewrite_manip.demo_increment_var_sublevels_up(0, -1)", -1)]
    [DataRow("SELECT rewrite_manip.demo_increment_var_sublevels_up(-1, 0)", -1)]
    public Task RewriteManipSampleChangesVarNodes(string sql, int expected) => RunInstalledAsync(async (connection, transaction, token) =>
    {
        Assert.AreEqual(expected, await ScalarAsync<int>(connection, transaction, sql, token));
        Assert.AreEqual(expected, await ScalarAsync<int>(connection, transaction, sql, token));
    });

    /// <summary>
    /// Mirrors pgrx's matching and nonmatching range-table checks, including PostgreSQL's level and zero-index rules.
    /// </summary>
    /// <param name="varno">The referenced range-table index.</param>
    /// <param name="check">The index searched for.</param>
    /// <param name="expected">Whether the reference is found.</param>
    [TestMethod]
    [DataRow(3, 3, true)]
    [DataRow(3, 5, false)]
    [DataRow(1, 1, true)]
    [DataRow(0, 0, true)]
    [DataRow(2147483647, 2147483647, true)]
    [DataRow(2147483647, 1, false)]
    public Task RewriteManipSampleFindsRangeTableEntries(int varno, int check, bool expected) => RunInstalledAsync(async (connection, transaction, token) =>
        Assert.AreEqual(expected, await ScalarAsync<bool>(connection, transaction,
            $"SELECT rewrite_manip.demo_range_table_entry_used({varno}, {check})", token)));

    /// <summary>
    /// The functions are installed in the control file's schema with pgrx's names, argument names and strict integer signatures.
    /// </summary>
    [TestMethod]
    public Task RewriteManipSampleInstallsItsSignatures() => RunInstalledAsync(async (connection, transaction, token) =>
    {
        Assert.AreEqual(
            "demo_change_var_nodes(old_varno integer, new_varno integer)->integer;" +
            "demo_increment_var_sublevels_up(initial_level integer, delta integer)->integer;" +
            "demo_offset_var_nodes(\"offset\" integer)->integer;" +
            "demo_range_table_entry_used(varno integer, check_rt_index integer)->boolean",
            await ScalarAsync<string>(connection, transaction, """
                SELECT string_agg(p.proname || '(' || pg_get_function_arguments(p.oid) || ')->' || pg_get_function_result(p.oid), ';'
                    ORDER BY p.proname)
                FROM pg_proc p
                JOIN pg_depend d ON d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e'
                JOIN pg_extension e ON e.oid = d.refobjid AND e.extname = 'ankus_rewrite_manip'
                WHERE p.pronamespace = 'rewrite_manip'::regnamespace AND p.proisstrict
                """, token));
        Assert.IsTrue(await ScalarAsync<bool>(connection, transaction,
            "SELECT rewrite_manip.demo_change_var_nodes(NULL, 5) IS NULL AND rewrite_manip.demo_range_table_entry_used(3, NULL) IS NULL", token));
    });

    /// <summary>
    /// PostgreSQL 16 and later reject a negative range-table index in the nulling-relation set; the error crosses the
    /// native guard with its SQLSTATE and the same backend recovers. Earlier versions have no such set and report false.
    /// </summary>
    [TestMethod]
    public Task RewriteManipSampleReportsNativeErrorsAndRecovers() => RunInstalledAsync(async (connection, transaction, token) =>
    {
        await ErrorTrap.InstallAsync(connection, transaction, token);
        int version = await ScalarAsync<int>(connection, transaction, "SELECT current_setting('server_version_num')::integer", token);
        const string Negative = "SELECT rewrite_manip.demo_range_table_entry_used(3, -1)";
        if (version >= 160000)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Assert.AreEqual("XX000: negative bitmapset member not allowed",
                    await ErrorTrap.RunAsync(connection, transaction, Negative, token));
                Assert.AreEqual(4, await ScalarAsync<int>(connection, transaction, "SELECT rewrite_manip.demo_offset_var_nodes(3)", token));
            }
        }
        else
        {
            Assert.IsFalse(await ScalarAsync<bool>(connection, transaction, Negative, token));
        }

        Assert.IsTrue(await ScalarAsync<bool>(connection, transaction, "SELECT rewrite_manip.demo_range_table_entry_used(3, 3)", token));
        Assert.AreEqual(connection.ProcessID, await ScalarAsync<int>(connection, transaction, "SELECT pg_backend_pid()", token));
    });

    /// <summary>
    /// Installs the sample in a rolled-back transaction of the shared cluster.
    /// </summary>
    private Task RunInstalledAsync(Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> test)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(RewriteManipExampleTests), async (connection, transaction, token) =>
        {
            await using (var install = new NpgsqlCommand("CREATE EXTENSION ankus_rewrite_manip", connection, transaction))
            {
                await install.ExecuteNonQueryAsync(token);
            }

            await test(connection, transaction, token);
        }, context.CancellationToken);

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }
}
