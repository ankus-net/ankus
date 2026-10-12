using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies composite transport against PostgreSQL values, catalogs, and transaction recovery.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
[DoNotParallelize]
public sealed class CompositeDatumTests(TestContext context)
{
    /// <summary>
    /// Preserves named, nested, and anonymous records through each detached ownership path.
    /// </summary>
    /// <param name="mode">The direct, query, plan, session, cursor, session-plan cursor, retained-plan, or edited-row path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public Task NamedAndNestedTuplesPreserveExactValuesAcrossOwners(int mode)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(NamedAndNestedTuplesPreserveExactValuesAcrossOwners),
            async (connection, transaction, token) =>
            {
                (string function, string values, int count)[] cases =
                [
                    ("tuple_dog", """
                        (ROW('hé😀,()\',-2147483648)::tuple_values.dog),(ROW('',2147483647)::tuple_values.dog),
                        (ROW(NULL,9)::tuple_values.dog),(ROW('NULL',NULL)::tuple_values.dog),
                        (ROW(NULL,NULL)::tuple_values.dog),(NULL::tuple_values.dog)
                        """, 6),
                    ("tuple_pack", """
                        (ROW(ROW('leader',4)::tuple_values.dog,ARRAY[NULL,ROW('pup',2)::tuple_values.dog], 'pack')::tuple_values.pack),
                        (ROW(NULL,ARRAY[]::tuple_values.dog[],'empty')::tuple_values.pack),
                        (ROW(ROW(NULL,NULL)::tuple_values.dog,ARRAY[NULL,NULL]::tuple_values.dog[],NULL)::tuple_values.pack),
                        (ROW(NULL,NULL,NULL)::tuple_values.pack),(NULL::tuple_values.pack)
                        """, 5),
                ];
                foreach ((string function, string values, int count) in cases)
                {
                    await using var command = new NpgsqlCommand($"""
                        WITH inputs(value) AS (VALUES {values})
                        SELECT record_send(value), record_send(tuple_values.{function}(value,$1)) FROM inputs
                        """, connection, transaction);
                    command.Parameters.AddWithValue(mode);
                    await AssertBinaryRowsAsync(command, count, token);
                }
            }, context.CancellationToken);

    /// <summary>
    /// Preserves every scalar family through tuple cells, including exact infinities, numeric scale, and nested arrays.
    /// </summary>
    /// <param name="mode">The ownership path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public Task PrimitiveTupleCellsRetainTheirBinaryRepresentation(int mode)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(PrimitiveTupleCellsRetainTheirBinaryRepresentation),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    WITH inputs(value) AS (VALUES
                        (ROW(true,-32768,'9223372036854775807','4294967295','-0','NaN',
                            '123456789012345678901.234500','infinity','24:00:00','-infinity','1 mon -2 days 3 microseconds',
                            '00112233-4455-6677-8899-aabbccddeeff','{ "n": 1.2300 }','{"z":1,"a":2}',
                            '2001:db8::1234/64','192.0.2.0/24','(1.25,-0)','[1,9)',decode('00ff1000','hex'),
                            ARRAY['hé😀',NULL,''],ARRAY[1,NULL,-7])::tuple_values.all_types),
                        (ROW(NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL)::tuple_values.all_types),
                        (NULL::tuple_values.all_types))
                    SELECT record_send(value), record_send(tuple_values.tuple_primitives(value,$1)) FROM inputs
                    """, connection, transaction);
                command.Parameters.AddWithValue(mode);
                await AssertBinaryRowsAsync(command, 3, token);
            }, context.CancellationToken);

    /// <summary>
    /// Preserves registered anonymous record identity across SPI owners, including zero and one field.
    /// </summary>
    /// <param name="mode">The ownership path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public Task AnonymousRecordsKeepShapeAndValues(int mode)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(AnonymousRecordsKeepShapeAndValues),
            async (connection, transaction, token) =>
            {
                string[] rows =
                [
                    "ROW()", "ROW(NULL::text)", "ROW('anonymous'::text,7,NULL::bytea)", "NULL::record",
                    "ROW('Low'::datatype.enum_mood,ARRAY[NULL,'café','High']::datatype.enum_mood[])",
                ];
                foreach (string row in rows)
                {
                    await using var command = new NpgsqlCommand($"SELECT record_send({row}), record_send(tuple_values.tuple_record({row},$1))", connection, transaction);
                    command.Parameters.AddWithValue(mode);
                    await AssertBinaryRowsAsync(command, 1, token);
                }
            }, context.CancellationToken);

    /// <summary>
    /// Preserves explicit composite array identity even when no element supplies a tuple descriptor.
    /// </summary>
    /// <param name="mode">The ownership path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public Task CompositeArraysKeepNullElementsDimensionsAndIdentity(int mode)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CompositeArraysKeepNullElementsDimensionsAndIdentity),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    WITH inputs(value) AS (VALUES
                        (ARRAY[ROW('Ada',3),ROW(NULL,NULL)]::tuple_values.dog[]),
                        (ARRAY[NULL,ROW('Bo',4)]::tuple_values.dog[]),
                        (ARRAY[NULL,NULL]::tuple_values.dog[]),
                        (ARRAY[]::tuple_values.dog[]),
                        (array_fill(NULL::tuple_values.dog,ARRAY[2,3],ARRAY[-2,4])),
                        (NULL::tuple_values.dog[]))
                    SELECT array_send(value), array_send(tuple_values.tuple_array(value,$1)) FROM inputs
                    """, connection, transaction);
                command.Parameters.AddWithValue(mode);
                await AssertBinaryRowsAsync(command, 6, token);
            }, context.CancellationToken);

    /// <summary>
    /// Retains domain identity separately from the physical base tuple for scalar and array values.
    /// </summary>
    /// <param name="mode">The ownership path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public Task CompositeDomainsRetainDeclaredIdentityAcrossOwners(int mode)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CompositeDomainsRetainDeclaredIdentityAcrossOwners),
            async (connection, transaction, token) =>
            {
                await using (var command = new NpgsqlCommand("""
                    WITH inputs(value) AS (VALUES (ROW('domain',7)::tuple_values.dog_domain),
                        (ROW(NULL,NULL)::tuple_values.dog_domain),(NULL::tuple_values.dog_domain))
                    SELECT record_send(value),record_send(tuple_values.tuple_domain_dog(value,$1)) FROM inputs
                    """, connection, transaction))
                {
                    command.Parameters.AddWithValue(mode);
                    await AssertBinaryRowsAsync(command, 3, token);
                }

                await using var arrays = new NpgsqlCommand("""
                    WITH inputs(value) AS (VALUES
                        (ARRAY[NULL,ROW('domain',7)::tuple_values.dog_domain]::tuple_values.dog_domain[]),
                        (ARRAY[NULL,NULL]::tuple_values.dog_domain[]),
                        (ARRAY[]::tuple_values.dog_domain[]),
                        (array_fill(NULL::tuple_values.dog_domain,ARRAY[2,2],ARRAY[-1,3])),
                        (NULL::tuple_values.dog_domain[]))
                    SELECT array_send(value),array_send(tuple_values.tuple_domain_array(value,$1)) FROM inputs
                    """, connection, transaction);
                arrays.Parameters.AddWithValue(mode);
                await AssertBinaryRowsAsync(arrays, 5, token);
            }, context.CancellationToken);

    /// <summary>
    /// Empty named tuples are nonnull values even though their PostgreSQL row predicates are vacuous.
    /// </summary>
    [TestMethod]
    public Task ZeroAttributeNamedAndAnonymousTuplesRemainNonnull()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ZeroAttributeNamedAndAnonymousTuplesRemainNonnull),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT record_send(ROW()::tuple_values.empty_row),record_send(tuple_values.tuple_empty())
                    UNION ALL SELECT record_send(ROW()),record_send(tuple_values.tuple_record(ROW(),0))
                    """, connection, transaction);
                await AssertBinaryRowsAsync(command, 2, token);
            }, context.CancellationToken);

    /// <summary>
    /// Matches the caller's record descriptor and rejects incompatible arity or field types with backend recovery.
    /// </summary>
    /// <param name="columns">The incompatible caller-supplied record descriptor.</param>
    [TestMethod]
    [DataRow("\"Name\" text, age bigint")]
    [DataRow("\"Name\" text")]
    [DataRow("\"Name\" integer, age integer")]
    [DataRow("\"Name\" text, age integer, extra text")]
    public Task AnonymousRecordsRejectCallerDescriptorMismatch(string columns)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(AnonymousRecordsRejectCallerDescriptorMismatch),
            async (connection, transaction, token) =>
            {
                await transaction.SaveAsync("record_shape", token);
                await using (var invalid = new NpgsqlCommand($"SELECT * FROM tuple_values.tuple_anonymous('Ada',3) AS row({columns})", connection, transaction))
                {
                    PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => invalid.ExecuteScalarAsync(token));
                    Assert.AreEqual("42804", error.SqlState);
                }

                await transaction.RollbackAsync("record_shape", token);
                await using var valid = new NpgsqlCommand("SELECT * FROM tuple_values.tuple_anonymous('Ada',3) AS row(\"Name\" text,age integer)", connection, transaction);
                await using NpgsqlDataReader reader = await valid.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("Ada", reader.GetString(0));
                Assert.AreEqual(3, reader.GetInt32(1));
                Assert.IsFalse(await reader.ReadAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Independently constructed tuple and array values carry the requested SQL type and shape.
    /// </summary>
    [TestMethod]
    public Task DescriptorConstructionPreservesAllNullRowsAndArrayIdentity()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(DescriptorConstructionPreservesAllNullRowsAndArrayIdentity),
            async (connection, transaction, token) =>
            {
                await using (var command = new NpgsqlCommand("""
                    SELECT record_send(ROW('Ada',3)::tuple_values.dog),record_send(tuple_values.tuple_create('Ada',3))
                    UNION ALL SELECT record_send(ROW(NULL,NULL)::tuple_values.dog),record_send(tuple_values.tuple_create(NULL,NULL))
                    UNION ALL SELECT record_send(ROW('Ada'::text,3)),record_send(tuple_values.tuple_anonymous('Ada',3))
                    UNION ALL SELECT record_send(ROW(NULL::text,NULL::integer)),record_send(tuple_values.tuple_anonymous(NULL,NULL))
                    """, connection, transaction))
                {
                    await AssertBinaryRowsAsync(command, 4, token);
                }

                string[] arrays =
                [
                    "ARRAY[]::tuple_values.dog[]",
                    "ARRAY[NULL,ROW('Ada',3)]::tuple_values.dog[]",
                    "ARRAY[NULL,NULL]::tuple_values.dog[]",
                    "'[-1:0][3:4]={{\"(Ada,3)\",NULL},{\"(,)\",\"(Bo,4)\"}}'::tuple_values.dog[]",
                ];
                for (int scenario = 0; scenario < arrays.Length; scenario++)
                {
                    await using var command = new NpgsqlCommand($"SELECT array_send({arrays[scenario]}),array_send(tuple_values.tuple_array_create($1))", connection, transaction);
                    command.Parameters.AddWithValue(scenario);
                    await AssertBinaryRowsAsync(command, 1, token);
                }
            }, context.CancellationToken);

    /// <summary>
    /// Uses annotated array identity when ordinary vector or shaped-array storage only declares record elements.
    /// </summary>
    /// <param name="shaped">Whether to use shaped PgArray storage instead of a CLR vector.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task OrdinaryCompositeArraysUseAnnotatedIdentityWithoutInferringFromElements(bool shaped)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(OrdinaryCompositeArraysUseAnnotatedIdentityWithoutInferringFromElements),
            async (connection, transaction, token) =>
            {
                string function = shaped ? "tuple_plain_array_create" : "tuple_vector_create";
                (int scenario, string sql)[] cases =
                [
                    (0, "ARRAY[]::tuple_values.dog[]"),
                    (1, shaped ? "'[-2:-1]={NULL,NULL}'::tuple_values.dog[]" : "ARRAY[NULL,NULL]::tuple_values.dog[]"),
                    (2, shaped ? "'[-2:-1]={NULL,\"(Ada,3)\"}'::tuple_values.dog[]" : "ARRAY[NULL,ROW('Ada',3)]::tuple_values.dog[]"),
                    (4, "NULL::tuple_values.dog[]"),
                ];
                foreach ((int scenario, string sql) in cases)
                {
                    await using var command = new NpgsqlCommand($"SELECT array_send({sql}),array_send(tuple_values.{function}($1))", connection, transaction);
                    command.Parameters.AddWithValue(scenario);
                    await AssertBinaryRowsAsync(command, 1, token);
                }

                await transaction.SaveAsync("array_identity", token);
                await using (var invalid = new NpgsqlCommand($"SELECT tuple_values.{function}(3)", connection, transaction))
                {
                    PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => invalid.ExecuteScalarAsync(token));
                    Assert.AreEqual("42804", error.SqlState);
                }

                await transaction.RollbackAsync("array_identity", token);
                await using var recovery = new NpgsqlCommand($"SELECT cardinality(tuple_values.{function}(2))", connection, transaction);
                Assert.AreEqual(2, await recovery.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Converts named composite array input into ordinary nullable managed vector elements.
    /// </summary>
    [TestMethod]
    public Task CompositeVectorInputsRetainNullRowsAndValues()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CompositeVectorInputsRetainNullRowsAndValues),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    WITH inputs(value) AS (VALUES (ARRAY[NULL,ROW('Ada',3),ROW(NULL,NULL)]::tuple_values.dog[]),
                        (ARRAY[]::tuple_values.dog[]),(NULL::tuple_values.dog[]))
                    SELECT array_send(value),array_send(tuple_values.tuple_vector(value)) FROM inputs
                    """, connection, transaction);
                await AssertBinaryRowsAsync(command, 3, token);
            }, context.CancellationToken);

    /// <summary>
    /// Streams 10,000 composites built from one descriptor to the client and through SPI, as pgrx's
    /// <c>test_tuple_desc_clone</c> does.
    /// </summary>
    [TestMethod]
    public Task LargeCompositeSetsShareOneDescriptor()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(LargeCompositeSetsShareOneDescriptor),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT count(*), count(DISTINCT (d).name), sum((d).age), bool_and((d).name = 'dog ' || (d).age)
                      FROM tuple_values.tuple_many_dogs(10000) d
                    """, connection, transaction);
                await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
                {
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.AreEqual(10000L, reader.GetInt64(0));
                    Assert.AreEqual(10000L, reader.GetInt64(1));
                    Assert.AreEqual(50005000L, reader.GetInt64(2));
                    Assert.IsTrue(reader.GetBoolean(3));
                }

                command.CommandText = "SELECT tuple_values.tuple_many_dogs_through_spi(10000)";
                Assert.AreEqual("10000|10000|50005000", await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Executes pgrx's composite default and variadic cases, an installed composite-to-composite cast, and a composite
    /// whose array-of-composite field is set in code.
    /// </summary>
    [TestMethod]
    public Task CompositeDefaultsVariadicsAndCastsExecute()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CompositeDefaultsVariadicsAndCastsExecute),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT tuple_values.tuple_default_name(),
                           tuple_values.tuple_default_name(ROW('Brandy', 1)::tuple_values.dog),
                           tuple_values.tuple_variadic_names(ROW('Nami', 1)::tuple_values.dog, ROW('Brandy', 1)::tuple_values.dog),
                           tuple_values.tuple_variadic_names(VARIADIC ARRAY[NULL, ROW(NULL, 2)]::tuple_values.dog[]),
                           tuple_values.tuple_default_variadic_names(),
                           tuple_values.tuple_default_variadic_names(ROW('Brandy', 1)::tuple_values.dog),
                           (ROW('Nami', 3)::tuple_values.dog)::tuple_values.other_dog::text,
                           pg_typeof((ROW('Nami', 3)::tuple_values.dog)::tuple_values.other_dog) = 'tuple_values.other_dog'::regtype,
                           (SELECT castcontext::text || castmethod::text FROM pg_cast
                             WHERE castsource = 'tuple_values.dog'::regtype AND casttarget = 'tuple_values.other_dog'::regtype),
                           (NULL::tuple_values.dog)::tuple_values.other_dog IS NULL,
                           tuple_values.tuple_pack_build()::text = ROW(ROW('Nami', 3)::tuple_values.dog,
                               ARRAY[ROW('Brandy', 1), NULL, ROW('Ada', 2)]::tuple_values.dog[], 'built')::tuple_values.pack::text,
                           tuple_values.tuple_array_default_names(),
                           tuple_values.tuple_array_default_names(ARRAY[NULL, ROW('Ada', 2)]::tuple_values.dog[]),
                           tuple_values.tuple_nullable_array_default() || '|' ||
                               tuple_values.tuple_nullable_array_default(ARRAY[NULL, ROW('Ada', 2)]::tuple_values.dog[]),
                           (SELECT string_agg(id || '=' || pack::text, ';' ORDER BY id) FROM tuple_values.tuple_array_table()),
                           (SELECT pg_typeof(pack)::text FROM tuple_values.tuple_array_table() LIMIT 1)
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("Nami", reader.GetString(0));
                Assert.AreEqual("Brandy", reader.GetString(1));
                Assert.AreSequenceEqual(["Nami", "Brandy"], reader.GetFieldValue<string?[]>(2));
                Assert.AreSequenceEqual([null, null], reader.GetFieldValue<string?[]>(3));
                Assert.AreSequenceEqual(["Nami"], reader.GetFieldValue<string?[]>(4));
                Assert.AreSequenceEqual(["Brandy"], reader.GetFieldValue<string?[]>(5));
                Assert.AreEqual("(Nami,)", reader.GetString(6));
                Assert.IsTrue(reader.GetBoolean(7));
                Assert.AreEqual("ef", reader.GetString(8));
                Assert.IsTrue(reader.GetBoolean(9));
                Assert.IsTrue(reader.GetBoolean(10));
                Assert.AreSequenceEqual(["Nami"], reader.GetFieldValue<string?[]>(11));
                Assert.AreSequenceEqual([null, "Ada"], reader.GetFieldValue<string?[]>(12));
                Assert.AreEqual("null|2:1", reader.GetString(13));
                Assert.AreEqual("1={\"(Nami,3)\",NULL};2={}", reader.GetString(14));
                Assert.AreEqual("tuple_values.dog[]", reader.GetString(15));
            }, context.CancellationToken);

    /// <summary>
    /// Matches owned metadata to independent catalog values, including typmods, domains, collations, and physical holes.
    /// </summary>
    /// <param name="byOid">Whether descriptor lookup uses a name or catalog OID.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task DescriptorMetadataMatchesCatalogs(bool byOid)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(DescriptorMetadataMatchesCatalogs),
            async (connection, transaction, token) =>
            {
                string[] names = ["dog", "pack", "checked_row", "metadata_row", "dropped", "all_types", "relation_row"];
                foreach (string name in names)
                {
                    await using var command = new NpgsqlCommand("""
                        WITH expected AS (
                            SELECT a.attnum-1 AS ordinal,a.attname::text AS name,a.atttypid AS type_oid,
                                COALESCE(NULLIF(t.typbasetype,0),a.atttypid) AS base_type_oid,
                                a.atttypmod AS type_modifier,a.attcollation AS collation_oid,a.attisdropped AS is_dropped,
                                a.attnotnull AS is_not_null,COALESCE(t.typtype='c',false) AS is_composite
                            FROM pg_attribute a JOIN pg_type parent ON parent.typrelid=a.attrelid
                            LEFT JOIN pg_type t ON t.oid=a.atttypid
                            WHERE parent.oid=$1::regtype AND a.attnum>0),
                        actual AS (SELECT * FROM tuple_values.tuple_attributes($1,$2))
                        SELECT (SELECT count(*) FROM expected), (SELECT count(*) FROM actual),
                            (SELECT count(*) FROM ((SELECT * FROM expected EXCEPT ALL SELECT * FROM actual)
                                UNION ALL (SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)) differences)
                        """, connection, transaction);
                    command.Parameters.AddWithValue($"tuple_values.{name}");
                    command.Parameters.AddWithValue(byOid);
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.IsGreaterThan(0L, reader.GetInt64(0), name);
                    Assert.AreEqual(reader.GetInt64(0), reader.GetInt64(1), name);
                    Assert.AreEqual(0L, reader.GetInt64(2), name);
                    Assert.IsFalse(await reader.ReadAsync(token));
                }
            }, context.CancellationToken);

    /// <summary>
    /// Distinguishes named identity from anonymous record identity and a registered type modifier.
    /// </summary>
    [TestMethod]
    public Task TupleHeadersRetainNamedAndAnonymousIdentity()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TupleHeadersRetainNamedAndAnonymousIdentity),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT 'tuple_values.dog'::regtype::oid::bigint,
                        tuple_values.tuple_identity(ROW('Ada',3)::tuple_values.dog),
                        tuple_values.tuple_identity(tuple_values.tuple_anonymous('Ada',3))
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreSequenceEqual([reader.GetInt64(0), -1, 2], reader.GetFieldValue<long[]>(1));
                long[] anonymous = reader.GetFieldValue<long[]>(2);
                Assert.HasCount(3, anonymous);
                Assert.AreEqual(2249L, anonymous[0]);
                Assert.IsGreaterThanOrEqualTo(0L, anonymous[1]);
                Assert.AreEqual(2L, anonymous[2]);
            }, context.CancellationToken);

    /// <summary>
    /// Checks clone slot independence with both zero-based and named cell access.
    /// </summary>
    [TestMethod]
    public Task TupleCloneReplacesOnlyItsOwnSlots()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TupleCloneReplacesOnlyItsOwnSlots),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT tuple_values.tuple_clone(ROW('Ada',3)::tuple_values.dog)", connection, transaction);
                string[] actual = Assert.IsInstanceOfType<string[]>(await command.ExecuteScalarAsync(token));
                Assert.AreSequenceEqual(["Ada", "3", "Changed", "99"], actual);
            }, context.CancellationToken);

    /// <summary>
    /// Rejects incompatible reads, replacements, names, ordinals, and composite identities before mutation.
    /// </summary>
    /// <param name="scenario">The rejected operation.</param>
    /// <param name="exception">The precise managed exception type.</param>
    [TestMethod]
    [DataRow(0, "InvalidCastException")]
    [DataRow(1, "InvalidCastException")]
    [DataRow(2, "ArgumentException")]
    [DataRow(3, "ArgumentOutOfRangeException")]
    [DataRow(4, "ArgumentOutOfRangeException")]
    [DataRow(5, "InvalidCastException")]
    [DataRow(6, "InvalidCastException")]
    [DataRow(7, "ArgumentException")]
    [DataRow(8, "none")]
    [DataRow(9, "InvalidCastException")]
    public Task InvalidTupleOperationsLeaveExistingCellsIntact(int scenario, string exception)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(InvalidTupleOperationsLeaveExistingCellsIntact),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT tuple_values.tuple_access_failure(ROW('Ada',3)::tuple_values.dog,$1)", connection, transaction);
                command.Parameters.AddWithValue(scenario);
                // As in pgrx, a typed read of a NULL cell yields null without checking the requested type (scenario 8).
                Assert.AreEqual(exception == "none" ? "no error" : $"{exception}:Ada:3", await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Loading a descriptor for a type that does not exist fails with PostgreSQL's undefined-object error, as pgrx's
    /// missing-type test requires, and the same backend then loads an existing type.
    /// </summary>
    [TestMethod]
    public Task MissingTypeDescriptorFailsAndRecovers()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(MissingTypeDescriptorFailsAndRecovers),
            async (connection, transaction, token) =>
            {
                await transaction.SaveAsync("missing_type", token);
                await using var command = new NpgsqlCommand("SELECT tuple_values.tuple_load_by_name('definitely_not_existing')", connection, transaction);
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
                Assert.AreEqual(PostgresErrorCodes.UndefinedObject, error.SqlState);
                Assert.AreEqual("type \"definitely_not_existing\" does not exist", error.MessageText);
                await transaction.RollbackAsync("missing_type", token);
                command.CommandText = "SELECT tuple_values.tuple_load_by_name('tuple_values.dog')";
                Assert.AreEqual(2, await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Retains dropped physical slots while live SQL attributes remain addressable by physical ordinal.
    /// </summary>
    [TestMethod]
    public Task DroppedAttributesRemainNullAndUnwritable()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(DroppedAttributesRemainNullAndUnwritable),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT tuple_values.tuple_dropped(ROW(17,'last')::tuple_values.dropped)", connection, transaction);
                Assert.AreEqual("3:17:True:last:ArgumentException:InvalidOperationException", await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Owns toasted nested text and arrays after SPI disconnection, later allocations, and managed collection.
    /// </summary>
    [TestMethod]
    public Task RetainedNestedTuplesSurviveSpiAndGarbageCollection()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(RetainedNestedTuplesSurviveSpiAndGarbageCollection),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT record_send(ROW(ROW(repeat('hé😀',20000),8)::tuple_values.dog,
                        ARRAY[NULL,ROW('nested',9)::tuple_values.dog],repeat('tag',20000))::tuple_values.pack),
                        record_send(tuple_values.tuple_retained())
                    """, connection, transaction);
                await AssertBinaryRowsAsync(command, 1, token);
            }, context.CancellationToken);

    /// <summary>
    /// Detaches relation-backed TOAST data before deleting the row and closing its SPI owner.
    /// </summary>
    [TestMethod]
    public Task StoredCompositeValuesSurviveSourceDeletion()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(StoredCompositeValuesSurviveSourceDeletion),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    CREATE TEMP TABLE tuple_toast(value tuple_values.pack);
                    ALTER TABLE tuple_toast ALTER COLUMN value SET STORAGE EXTERNAL;
                    INSERT INTO tuple_toast SELECT ROW(ROW(repeat('hé😀',20000),8)::tuple_values.dog,
                        ARRAY[NULL,ROW(repeat('nested',10000),9)::tuple_values.dog],repeat('tag',20000))::tuple_values.pack;
                    SELECT record_send(value) FROM tuple_toast
                    """, connection, transaction);
                byte[] expected = Assert.IsInstanceOfType<byte[]>(await command.ExecuteScalarAsync(token));
                command.CommandText = "SELECT record_send(tuple_values.tuple_stored())";
                byte[] actual = Assert.IsInstanceOfType<byte[]>(await command.ExecuteScalarAsync(token));
                Assert.AreSequenceEqual(expected, actual);
                command.CommandText = "SELECT count(*) FROM tuple_toast";
                Assert.AreEqual(0L, await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Table constraints do not apply to standalone values of a table's composite row type.
    /// </summary>
    [TestMethod]
    public Task TableNotNullMetadataDoesNotBecomeACompositeConstraint()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TableNotNullMetadataDoesNotBecomeACompositeConstraint),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT record_send(ROW(NULL,NULL)::tuple_values.relation_row),record_send(tuple_values.tuple_relation_row())
                    """, connection, transaction);
                await AssertBinaryRowsAsync(command, 1, token);
            }, context.CancellationToken);

    /// <summary>
    /// Rejects stale output identity or layout and recovers on the same backend after savepoint rollback.
    /// </summary>
    /// <param name="scenario">The incompatible catalog change.</param>
    /// <param name="sqlState">The required native diagnostic.</param>
    [TestMethod]
    [DataRow(0, "42804")]
    [DataRow(1, "42804")]
    [DataRow(2, "42804")]
    [DataRow(3, "42704")]
    public Task StaleTupleOutputFailsWithoutPoisoningBackend(int scenario, string sqlState)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(StaleTupleOutputFailsWithoutPoisoningBackend),
            async (connection, transaction, token) =>
            {
                await transaction.SaveAsync("stale", token);
                await using (var command = new NpgsqlCommand("SELECT record_send(tuple_values.tuple_stale($1))", connection, transaction))
                {
                    command.Parameters.AddWithValue(scenario);
                    PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
                    Assert.AreEqual(sqlState, error.SqlState);
                }

                await transaction.RollbackAsync("stale", token);
                await using var recovery = new NpgsqlCommand("""
                    SELECT (tuple_values.tuple_create('recovered',42)).age,
                        count(*) FROM pg_attribute WHERE attrelid='tuple_values.ephemeral'::regclass AND attnum>0
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await recovery.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(42, reader.GetInt32(0));
                Assert.AreEqual(2L, reader.GetInt64(1));
            }, context.CancellationToken);

    /// <summary>
    /// Applies domain checks during output while allowing domain NULL when the domain permits it.
    /// </summary>
    [TestMethod]
    public Task DomainFieldsValidateAndRecoverAfterNativeErrors()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(DomainFieldsValidateAndRecoverAfterNativeErrors),
            async (connection, transaction, token) =>
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await transaction.SaveAsync("domain_check", token);
                    await using (var invalid = new NpgsqlCommand("SELECT tuple_values.tuple_domain(-1)", connection, transaction))
                    {
                        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => invalid.ExecuteScalarAsync(token));
                        Assert.AreEqual("23514", error.SqlState);
                    }

                    await transaction.RollbackAsync("domain_check", token);
                    await using var valid = new NpgsqlCommand("""
                        SELECT record_send(ROW('domain',7)::tuple_values.checked_row),record_send(tuple_values.tuple_domain(7))
                        UNION ALL SELECT record_send(ROW('domain',NULL)::tuple_values.checked_row),record_send(tuple_values.tuple_domain(NULL))
                        """, connection, transaction);
                    await AssertBinaryRowsAsync(valid, 2, token);
                }
            }, context.CancellationToken);

    /// <summary>
    /// Uses declared domain descriptors while enforcing constraints on both direct and array-element output.
    /// </summary>
    [TestMethod]
    public Task DomainDescriptorsRetainBaseIdentityAndValidateConstructedValues()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(DomainDescriptorsRetainBaseIdentityAndValidateConstructedValues),
            async (connection, transaction, token) =>
            {
                await using (var identity = new NpgsqlCommand("""
                    SELECT 'tuple_values.dog_domain'::regtype::oid,'tuple_values.dog'::regtype::oid,
                        tuple_values.tuple_descriptor_identity('tuple_values.dog_domain')
                    """, connection, transaction))
                {
                    await using NpgsqlDataReader reader = await identity.ExecuteReaderAsync(token);
                    Assert.IsTrue(await reader.ReadAsync(token));
                    uint domain = reader.GetFieldValue<uint>(0);
                    uint baseType = reader.GetFieldValue<uint>(1);
                    Assert.AreNotEqual(domain, baseType);
                    Assert.AreSequenceEqual([domain, baseType, domain], reader.GetFieldValue<uint[]>(2));
                }

                await using (var valid = new NpgsqlCommand("""
                    SELECT record_send(ROW('domain dog',7)::tuple_values.dog_domain),record_send(tuple_values.tuple_domain_create(7))
                    UNION ALL SELECT array_send(ARRAY[NULL,ROW('domain dog',7)::tuple_values.dog_domain]::tuple_values.dog_domain[]),
                        array_send(tuple_values.tuple_domain_array_create(7))
                    """, connection, transaction))
                {
                    await AssertBinaryRowsAsync(valid, 2, token);
                }

                string[] functions = ["tuple_domain_create", "tuple_domain_array_create"];
                foreach (string function in functions)
                {
                    await transaction.SaveAsync("composite_domain", token);
                    await using (var invalid = new NpgsqlCommand($"SELECT tuple_values.{function}(-1)", connection, transaction))
                    {
                        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => invalid.ExecuteScalarAsync(token));
                        Assert.AreEqual("23514", error.SqlState);
                    }

                    await transaction.RollbackAsync("composite_domain", token);
                }

                await using var recovery = new NpgsqlCommand("SELECT (tuple_values.tuple_domain_create(5)).age", connection, transaction);
                Assert.AreEqual(5, await recovery.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Validates NOT NULL composite domains at scalar, tuple-cell, and array-element boundaries.
    /// </summary>
    /// <param name="expression">The managed output containing the forbidden domain NULL.</param>
    [TestMethod]
    [DataRow("tuple_values.tuple_required(true)")]
    [DataRow("tuple_values.tuple_required_field()")]
    [DataRow("tuple_values.tuple_required_array()")]
    public Task NotNullCompositeDomainsRejectNullDatumsAndRecover(string expression)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(NotNullCompositeDomainsRejectNullDatumsAndRecover),
            async (connection, transaction, token) =>
            {
                await transaction.SaveAsync("required_domain", token);
                await using (var invalid = new NpgsqlCommand($"SELECT {expression}", connection, transaction))
                {
                    PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => invalid.ExecuteScalarAsync(token));
                    Assert.AreEqual("23502", error.SqlState);
                }

                await transaction.RollbackAsync("required_domain", token);
                await using var valid = new NpgsqlCommand("""
                    SELECT record_send(ROW(NULL,NULL)::tuple_values.required_dog),record_send(tuple_values.tuple_required(false))
                    """, connection, transaction);
                await AssertBinaryRowsAsync(valid, 1, token);
            }, context.CancellationToken);

    /// <summary>
    /// Rejects a caller record descriptor that changes the typmod of a matching attribute OID.
    /// </summary>
    [TestMethod]
    public Task AnonymousCallerDescriptorMustMatchAttributeTypeModifiers()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(AnonymousCallerDescriptorMustMatchAttributeTypeModifiers),
            async (connection, transaction, token) =>
            {
                await transaction.SaveAsync("record_typmod", token);
                await using (var invalid = new NpgsqlCommand("""
                    SELECT * FROM tuple_values.tuple_record(ROW('abc',12.34)::tuple_values.metadata_row,0)
                        AS row(label varchar(2), amount numeric(6,2))
                    """, connection, transaction))
                {
                    PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => invalid.ExecuteScalarAsync(token));
                    Assert.AreEqual("42804", error.SqlState);
                }

                await transaction.RollbackAsync("record_typmod", token);
                await using var valid = new NpgsqlCommand("""
                    SELECT * FROM tuple_values.tuple_record(ROW('abc',12.34)::tuple_values.metadata_row,0)
                        AS row(label varchar(3), amount numeric(6,2))
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await valid.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("abc", reader.GetString(0));
                Assert.AreEqual(12.34m, reader.GetDecimal(1));
                Assert.IsFalse(await reader.ReadAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// A composite type changed between calls in one session converts with its new columns in both directions, so
    /// nothing resolved for the old columns is reused.
    /// </summary>
    [TestMethod]
    public Task ChangedCompositeTypesConvertWithTheirNewColumns()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ChangedCompositeTypesConvertWithTheirNewColumns),
            async (connection, transaction, token) =>
            {
                (string Change, string Row, string Expected)[] steps =
                [
                    ("CREATE TYPE pg_temp.shape_probe AS (a integer, b text)", "ROW(1,'x')", """{"a":1,"b":"x"}"""),
                    ("ALTER TYPE pg_temp.shape_probe RENAME ATTRIBUTE b TO c", "ROW(2,'y')", """{"a":2,"c":"y"}"""),
                    ("ALTER TYPE pg_temp.shape_probe ADD ATTRIBUTE d bigint", "ROW(3,'z',5)", """{"a":3,"c":"z","d":5}"""),
                    ("ALTER TYPE pg_temp.shape_probe ALTER ATTRIBUTE a TYPE bigint", "ROW(4000000000,'w',6)", """{"a":4000000000,"c":"w","d":6}"""),
                    ("ALTER TYPE pg_temp.shape_probe DROP ATTRIBUTE c", "ROW(5,7)", """{"a":5,"d":7}"""),
                ];
                foreach ((string change, string row, string expected) in steps)
                {
                    await using var command = new NpgsqlCommand(change, connection, transaction);
                    await command.ExecuteNonQueryAsync(token);
                    command.CommandText = $"SELECT row_to_json(tuple_values.tuple_record({row}::pg_temp.shape_probe, 0))::text";
                    Assert.AreEqual(expected, await command.ExecuteScalarAsync(token), change);
                }
            }, context.CancellationToken);

    /// <summary>
    /// Applies ordinary assignment coercion for constrained numeric scale and character length.
    /// </summary>
    [TestMethod]
    public Task TupleOutputAppliesAttributeTypeModifiers()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TupleOutputAppliesAttributeTypeModifiers),
            async (connection, transaction, token) =>
            {
                await using (var valid = new NpgsqlCommand("""
                    SELECT record_send(ROW('abc',12.35)::tuple_values.metadata_row),
                        record_send(tuple_values.tuple_typmod('abc',12.345))
                    """, connection, transaction))
                {
                    await AssertBinaryRowsAsync(valid, 1, token);
                }

                (string label, string amount, string sqlState)[] errors = [("abcd", "1", "22001"), ("abc", "10000", "22003")];
                foreach ((string label, string amount, string sqlState) in errors)
                {
                    await transaction.SaveAsync("typmod", token);
                    await using (var invalid = new NpgsqlCommand("SELECT record_send(tuple_values.tuple_typmod($1,$2::numeric))", connection, transaction))
                    {
                        invalid.Parameters.AddWithValue(label);
                        invalid.Parameters.AddWithValue(amount);
                        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => invalid.ExecuteScalarAsync(token));
                        Assert.AreEqual(sqlState, error.SqlState);
                    }

                    await transaction.RollbackAsync("typmod", token);
                }

                await using var recovery = new NpgsqlCommand("SELECT (tuple_values.tuple_typmod('xyz',9.87)).amount", connection, transaction);
                Assert.AreEqual(9.87m, await recovery.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Repeated guarded tuple conversion failures preserve earlier writes and a reusable prepared statement.
    /// </summary>
    [TestMethod]
    public Task GuardedTupleErrorsPreserveSessionAndPreparedPlan()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(GuardedTupleErrorsPreserveSessionAndPreparedPlan),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT tuple_values.tuple_recover()", connection, transaction);
                Assert.AreEqual("20:1:retained:41", await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Uses a named SETOF result without mistaking its attributes for a TABLE of composite cells.
    /// </summary>
    [TestMethod]
    public Task StreamingCompositeSetDistinguishesNullRows()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(StreamingCompositeSetDistinguishesNullRows),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT record_send(value) FROM (SELECT tuple_values.tuple_set() AS value) rows", connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.IsTrue(reader.IsDBNull(0), "The first row is a NULL datum.");
                Assert.IsTrue(await reader.ReadAsync(token));
                byte[] allNull = [0, 0, 0, 2, 0, 0, 0, 25, 255, 255, 255, 255, 0, 0, 0, 23, 255, 255, 255, 255];
                Assert.AreSequenceEqual(allNull, reader.GetFieldValue<byte[]>(0));
                Assert.IsTrue(await reader.ReadAsync(token));
                byte[] expected = [0, 0, 0, 2, 0, 0, 0, 25, 0, 0, 0, 3, 115, 101, 116, 0, 0, 0, 23, 0, 0, 0, 4, 0, 0, 0, 12];
                Assert.AreSequenceEqual(expected, reader.GetFieldValue<byte[]>(0));
                Assert.IsFalse(await reader.ReadAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Materializes named composite rows and independently verifies row cardinality and expanded attributes.
    /// </summary>
    [TestMethod]
    public Task MaterializedCompositeSetUsesNamedTupleDescriptor()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(MaterializedCompositeSetUsesNamedTupleDescriptor),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT name,age FROM tuple_values.tuple_set_materialized()", connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                for (int index = 0; index < 2; index++)
                {
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.IsTrue(reader.IsDBNull(0));
                    Assert.IsTrue(reader.IsDBNull(1));
                }

                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("set", reader.GetString(0));
                Assert.AreEqual(12, reader.GetInt32(1));
                Assert.IsFalse(await reader.ReadAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Supplies a record descriptor to an anonymous composite SETOF result and preserves each row's cells.
    /// </summary>
    /// <param name="materialized">Whether the function requires a materialized result.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task AnonymousCompositeSetsUseCallerDescriptor(bool materialized)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(AnonymousCompositeSetsUseCallerDescriptor),
            async (connection, transaction, token) =>
            {
                string function = materialized ? "tuple_anonymous_set_materialized" : "tuple_anonymous_set";
                await using var command = new NpgsqlCommand($"SELECT * FROM tuple_values.{function}() AS rows(\"Name\" text, age integer)", connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("first", reader.GetString(0));
                Assert.AreEqual(1, reader.GetInt32(1));
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.IsTrue(reader.IsDBNull(0));
                Assert.IsTrue(reader.IsDBNull(1));
                Assert.IsFalse(await reader.ReadAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Rejects a materialized anonymous set in a value-only context lacking a caller record descriptor.
    /// </summary>
    [TestMethod]
    public Task MaterializedAnonymousSetRequiresCompatibleCallerContext()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(MaterializedAnonymousSetRequiresCompatibleCallerContext),
            async (connection, transaction, token) =>
            {
                await transaction.SaveAsync("record_context", token);
                await using (var invalid = new NpgsqlCommand("SELECT tuple_values.tuple_anonymous_set_materialized()", connection, transaction))
                {
                    PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => invalid.ExecuteScalarAsync(token));
                    Assert.AreEqual("0A000", error.SqlState);
                }

                await transaction.RollbackAsync("record_context", token);
                await using var valid = new NpgsqlCommand("SELECT count(*) FROM tuple_values.tuple_anonymous_set_materialized() AS rows(\"Name\" text,age integer)", connection, transaction);
                Assert.AreEqual(2L, await valid.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Distinguishes named composite TABLE cells, all-null composites, and SQL NULL cells.
    /// </summary>
    [TestMethod]
    public Task TableColumnsCarryTheirIndividualCompositeTypes()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TableColumnsCarryTheirIndividualCompositeTypes),
            async (connection, transaction, token) =>
            {
                await using (var command = new NpgsqlCommand("""
                    SELECT id,(dog).name,(dog).age,record_send(other) IS NULL,
                        pg_typeof(dog)::text,pg_typeof(other)::text FROM tuple_values.tuple_table() ORDER BY id
                    """, connection, transaction))
                {
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.AreEqual(1, reader.GetInt32(0));
                    Assert.AreEqual("table", reader.GetString(1));
                    Assert.AreEqual(5, reader.GetInt32(2));
                    Assert.IsFalse(reader.GetBoolean(3));
                    Assert.AreEqual("tuple_values.dog", reader.GetString(4));
                    Assert.AreEqual("tuple_values.other_dog", reader.GetString(5));
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.AreEqual(2, reader.GetInt32(0));
                    Assert.IsTrue(reader.IsDBNull(1));
                    Assert.IsTrue(reader.IsDBNull(2));
                    Assert.IsTrue(reader.GetBoolean(3));
                    Assert.IsFalse(await reader.ReadAsync(token));
                }

                await using var single = new NpgsqlCommand("SELECT record_send(dog) IS NULL,(dog).age FROM (SELECT tuple_values.tuple_single_table() AS dog) rows", connection, transaction);
                await using NpgsqlDataReader rows = await single.ExecuteReaderAsync(token);
                Assert.IsTrue(await rows.ReadAsync(token));
                Assert.IsTrue(rows.GetBoolean(0));
                Assert.IsTrue(rows.IsDBNull(1));
                Assert.IsTrue(await rows.ReadAsync(token));
                Assert.IsFalse(rows.GetBoolean(0));
                Assert.IsTrue(rows.IsDBNull(1));
                Assert.IsTrue(await rows.ReadAsync(token));
                Assert.IsFalse(rows.GetBoolean(0));
                Assert.AreEqual(12, rows.GetInt32(1));
                Assert.IsFalse(await rows.ReadAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Uses named composite bindings in PostgreSQL operators and casts, including field and whole-row NULLs.
    /// </summary>
    [TestMethod]
    public Task CompositeOperatorsAndCastsPreserveFieldsAndNullBehavior()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CompositeOperatorsAndCastsPreserveFieldsAndNullBehavior),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT ROW('Ada',3)::tuple_values.dog OPERATOR(tuple_values.@=) ROW('Ada',3)::tuple_values.dog,
                        ROW('Ada',3)::tuple_values.dog OPERATOR(tuple_values.@=) ROW('Ada',4)::tuple_values.dog,
                        ROW(NULL,NULL)::tuple_values.dog OPERATOR(tuple_values.@=) ROW(NULL,NULL)::tuple_values.dog,
                        ROW('Ada',NULL)::tuple_values.dog OPERATOR(tuple_values.@=) ROW('Ada',3)::tuple_values.dog,
                        NULL::tuple_values.dog OPERATOR(tuple_values.@=) ROW('Ada',3)::tuple_values.dog,
                        (ROW('Ada',3)::tuple_values.dog)::integer,
                        (ROW('Ada',NULL)::tuple_values.dog)::integer,
                        (NULL::tuple_values.dog)::integer
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.IsTrue(reader.GetBoolean(0));
                Assert.IsFalse(reader.GetBoolean(1));
                Assert.IsTrue(reader.GetBoolean(2));
                Assert.IsFalse(reader.GetBoolean(3));
                Assert.IsTrue(reader.IsDBNull(4));
                Assert.AreEqual(3, reader.GetInt32(5));
                Assert.IsTrue(reader.IsDBNull(6));
                Assert.IsTrue(reader.IsDBNull(7));
                Assert.IsFalse(await reader.ReadAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Preserves server-encoded catalog field names beyond sixty-three UTF-8 bytes and recovers from untranslatable record output.
    /// </summary>
    [TestMethod]
    public async Task Latin1CompositeNamesAndCellsPreserveEncodingAndRecover()
    {
        CancellationToken token = context.CancellationToken;
        string database = "tuple_latin1_" + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection administrator = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await using (var create = new NpgsqlCommand(
            $"CREATE DATABASE {database} TEMPLATE template0 ENCODING 'LATIN1' LC_COLLATE 'C' LC_CTYPE 'C'", administrator))
        {
            await create.ExecuteNonQueryAsync(token);
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Database = database };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token);
            int backend = connection.ProcessID;
            await using var command = new NpgsqlCommand("CREATE EXTENSION ankus_test", connection);
            await command.ExecuteNonQueryAsync(token);
            string name = new('é', 63);
            command.CommandText = $"""
                SELECT to_json(tuple_values.tuple_record(source,$1))->>$2
                FROM (SELECT 'café'::text AS "{name}") source
                """;
            command.Parameters.AddWithValue(0);
            command.Parameters.AddWithValue(name);
            Assert.AreEqual("café", await command.ExecuteScalarAsync(token));
            command.Parameters[0].Value = 1;
            Assert.AreEqual("café", await command.ExecuteScalarAsync(token));

            command.Parameters.Clear();
            command.CommandText = "SELECT tuple_values.tuple_untranslatable()";
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual(PostgresErrorCodes.UntranslatableCharacter, error.SqlState);
            command.CommandText = "SELECT to_json(tuple_values.tuple_anonymous('récupéré',7))->>'Name'";
            Assert.AreEqual("récupéré", await command.ExecuteScalarAsync(token));
            Assert.AreEqual(backend, connection.ProcessID);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", administrator);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Compares an independent PostgreSQL binary value with managed output for every returned row.
    /// </summary>
    private static async Task AssertBinaryRowsAsync(NpgsqlCommand command, int expectedCount, CancellationToken token)
    {
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
        int count = 0;
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(0))
            {
                Assert.IsTrue(reader.IsDBNull(1), $"Row {count} must remain SQL NULL.");
            }
            else
            {
                Assert.IsFalse(reader.IsDBNull(1), $"Row {count} must remain a nonnull datum.");
                Assert.AreSequenceEqual(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), $"Row {count}.");
            }

            count++;
        }

        Assert.AreEqual(expectedCount, count);
    }
}
