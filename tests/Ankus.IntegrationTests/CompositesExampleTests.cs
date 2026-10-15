using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes pgrx's composite_type example through the published composites sample.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
[DoNotParallelize]
public sealed class CompositesExampleTests(TestContext context)
{
    /// <summary>
    /// Mirrors each pgrx test and checks the field values of created, copied, nested and operator results.
    /// </summary>
    [TestMethod]
    public Task CompositesSampleCreatesCopiesAndNestsDogs()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CompositesSampleCreatesCopiesAndNestsDogs), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, "CREATE EXTENSION ankus_composites", token);
            Assert.AreEqual("Nami:0", await Scalar<string>(connection, transaction,
                "SELECT (d).name || ':' || (d).scritches FROM (SELECT create_dog('Nami', 0) AS d) AS dogs", token));
            Assert.AreEqual("dog", await Scalar<string>(connection, transaction, "SELECT pg_typeof(create_dog('Nami', 0))::text", token));
            Assert.AreEqual("Nami:1", await Scalar<string>(connection, transaction,
                "SELECT (d).name || ':' || (d).scritches FROM (SELECT scritch_dog(ROW('Nami', 1)::Dog) AS d) AS dogs", token));
            Assert.AreEqual("Nami:Sally", await Scalar<string>(connection, transaction, """
                SELECT ((f).dog).name || ':' || ((f).cat).name
                FROM (SELECT make_friendship(ROW('Nami', 0)::Dog, ROW('Sally', 0)::Cat) AS f) AS friendships
                """, token));
            Assert.AreEqual(1, await Scalar<int>(connection, transaction, "SELECT (ROW('Nami', 0)::Dog + 1).scritches;", token));

            Assert.AreEqual("(Nami,7)", await Scalar<string>(connection, transaction, "SELECT scritch_dog(ROW('Nami', 7)::Dog)::text", token));
            Assert.IsTrue(await Scalar<bool>(connection, transaction, """
                SELECT (scritch_dog(ROW('Nami', NULL)::Dog)).scritches IS NULL
                    AND (scritch_dog(ROW(NULL, 3)::Dog)).name IS NULL
                    AND scritch_dog(NULL) IS NULL AND create_dog(NULL, 1) IS NULL AND create_dog('Nami', NULL) IS NULL
                    AND make_friendship(NULL, ROW('Sally', 0)::Cat) IS NULL
                """, token));
            Assert.AreEqual("(\"(Sally,5)\",\"(Nami,-3)\")", await Scalar<string>(connection, transaction,
                "SELECT make_friendship(create_dog('Nami', -3), ROW('Sally', 5)::Cat)::text", token));
            Assert.AreEqual("catanddogfriendship", await Scalar<string>(connection, transaction,
                "SELECT pg_typeof(make_friendship(ROW('Nami', 0)::Dog, ROW('Sally', 0)::Cat))::text", token));
            Assert.AreEqual("(Nami,2)", await Scalar<string>(connection, transaction, "SELECT (ROW('Nami', NULL)::Dog + 2)::text", token));
            Assert.AreEqual("(Nami,-2147483648)", await Scalar<string>(connection, transaction,
                "SELECT (ROW('Nami', -1)::Dog + -2147483647)::text", token));
            Assert.AreEqual("Nami:4,Nami:5,Nami:6", await Scalar<string>(connection, transaction,
                "SELECT string_agg(name || ':' || scritches, ',' ORDER BY scritches) FROM scritch_repeatedly(ROW('Nami', 3)::Dog, 3)", token));
            Assert.AreEqual(0L, await Scalar<long>(connection, transaction,
                "SELECT count(*) FROM scritch_repeatedly(ROW('Nami', 3)::Dog, 0)", token));
            Assert.AreEqual("{NULL,\"(Bo,4)\"}", await Scalar<string>(connection, transaction,
                "SELECT echo_dogs(ARRAY[NULL, ROW('Bo', 4)::Dog])::text", token));
            Assert.AreEqual("dog[]", await Scalar<string>(connection, transaction,
                "SELECT pg_typeof(echo_dogs(ARRAY[]::Dog[]))::text", token));
            Assert.AreEqual("record:", await Scalar<string>(connection, transaction,
                "SELECT name || ':' || coalesce(scritches::text, '') FROM make_record('record', NULL) AS r(name text, scritches integer)", token));
            Assert.AreEqual(connection.ProcessID, await Scalar<int>(connection, transaction, "SELECT pg_backend_pid()", token));
        }, context.CancellationToken);

    /// <summary>
    /// Sums dog scritches with a composite aggregate input, skipping NULL dogs and treating NULL counts as zero.
    /// </summary>
    [TestMethod]
    public Task CompositesSampleSumsScritches()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CompositesSampleSumsScritches), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, "CREATE EXTENSION ankus_composites", token);
            Assert.AreEqual(42, await Scalar<int>(connection, transaction, """
                SELECT sum_scritches(dogs) FROM (
                    VALUES
                        (ROW('Nami', 0)::Dog),
                        (ROW('Brandy', 42)::Dog)
                ) AS dogs(dogs);
                """, token));
            Assert.AreEqual(49, await Scalar<int>(connection, transaction, """
                SELECT sum_scritches(d) FROM (VALUES (ROW('Nami', 7)::Dog), (NULL::Dog), (ROW('Brandy', NULL)::Dog),
                    (ROW('Sally', 42)::Dog)) AS dogs(d)
                """, token));
            Assert.AreEqual(0, await Scalar<int>(connection, transaction,
                "SELECT sum_scritches(d) FROM (SELECT NULL::Dog WHERE false) AS dogs(d)", token));
            Assert.AreSequenceEqual<string>(["a:3", "b:-5"], await Scalar<string[]>(connection, transaction, """
                SELECT array_agg(owner || ':' || total ORDER BY owner) FROM (
                    SELECT owner, sum_scritches(d) AS total
                    FROM (VALUES ('a', ROW('x', 1)::Dog), ('a', ROW('y', 2)::Dog), ('b', ROW('z', -5)::Dog)) AS dogs(owner, d)
                    GROUP BY owner) AS totals
                """, token));
            Assert.AreEqual("value", await Scalar<string>(connection, transaction, """
                SELECT array_to_string(proargnames, ',') FROM pg_proc WHERE oid = 'sum_scritches(dog)'::regprocedure
                """, token));
            Assert.AreEqual("0:integer", await Scalar<string>(connection, transaction, """
                SELECT a.agginitval || ':' || a.aggtranstype::regtype::text FROM pg_aggregate a
                WHERE a.aggfnoid = 'sum_scritches(dog)'::regprocedure
                """, token));
        }, context.CancellationToken);

    /// <summary>
    /// Reports checked overflow as a managed error and recovers in the same backend.
    /// </summary>
    /// <param name="sql">An overflowing operator, set or aggregate expression.</param>
    [TestMethod]
    [DataRow("SELECT ROW('Nami', 2147483647)::Dog + 1")]
    [DataRow("SELECT ROW('Nami', NULL)::Dog + 2147483647 + 1")]
    [DataRow("SELECT count(*) FROM scritch_repeatedly(ROW('Nami', 2147483646)::Dog, 2)")]
    [DataRow("SELECT sum_scritches(d) FROM (VALUES (ROW('a', 2147483647)::Dog), (ROW('b', 1)::Dog)) AS dogs(d)")]
    public Task CompositesSampleRejectsOverflowAndRecovers(string sql)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CompositesSampleRejectsOverflowAndRecovers), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, "CREATE EXTENSION ankus_composites", token);
            await transaction.SaveAsync("overflow", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, transaction, sql, token));
            Assert.AreEqual("38000", error.SqlState);
            Assert.AreEqual("Arithmetic operation resulted in an overflow.", error.MessageText);
            await transaction.RollbackAsync("overflow", token);
            Assert.AreEqual(2147483647, await Scalar<int>(connection, transaction, "SELECT (ROW('Nami', 2147483646)::Dog + 1).scritches", token));
            Assert.AreEqual(connection.ProcessID, await Scalar<int>(connection, transaction, "SELECT pg_backend_pid()", token));
        }, context.CancellationToken);

    /// <summary>
    /// Creates the composite types first, orders the friendship type before its function and resolves types outside search_path.
    /// </summary>
    [TestMethod]
    public Task CompositesSampleOrdersTypesAndResolvesInstalledSchema()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CompositesSampleOrdersTypesAndResolvesInstalledSchema), async (connection, transaction, token) =>
        {
            string script = await File.ReadAllTextAsync(IntegrationEnvironment.ExtensionScript("ankus_composites"), token);
            int dog = script.IndexOf("CREATE TYPE Dog AS", StringComparison.Ordinal);
            int friendship = script.IndexOf("CREATE TYPE CatAndDogFriendship AS", StringComparison.Ordinal);
            int makeFriendship = script.IndexOf("CREATE FUNCTION \"make_friendship\"", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, dog);
            Assert.IsLessThan(friendship, dog);
            Assert.IsLessThan(makeFriendship, friendship);
            Assert.IsLessThan(script.IndexOf("CREATE FUNCTION", StringComparison.Ordinal), dog);

            await Execute(connection, transaction, """
                CREATE SCHEMA composites_hidden;
                CREATE EXTENSION ankus_composites WITH SCHEMA composites_hidden;
                SET LOCAL search_path = pg_catalog;
                """, token);
            Assert.AreEqual("composites_hidden.dog:Nami:3", await Scalar<string>(connection, transaction, """
                SELECT pg_typeof(d)::text || ':' || (d).name || ':' || (d).scritches
                FROM (SELECT composites_hidden.create_dog('Nami', 3) AS d) AS dogs
                """, token));
            Assert.AreEqual("composites_hidden.catanddogfriendship", await Scalar<string>(connection, transaction, """
                SELECT pg_typeof(composites_hidden.make_friendship(ROW('Nami', 0)::composites_hidden.dog,
                    ROW('Sally', 1)::composites_hidden.cat))::text
                """, token));
            Assert.AreSequenceEqual<string>(["cat", "catanddogfriendship", "dog"], await Scalar<string[]>(connection, transaction, """
                SELECT array_agg(t.typname::text ORDER BY t.typname COLLATE "C")
                FROM pg_depend d JOIN pg_type t ON d.classid = 'pg_type'::regclass AND t.oid = d.objid
                    JOIN pg_extension e ON e.oid = d.refobjid
                WHERE d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e' AND e.extname = 'ankus_composites'
                    AND t.typtype = 'c'
                """, token));
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
