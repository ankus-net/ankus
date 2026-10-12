using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes generated Native AOT trigger callbacks against PostgreSQL row, statement, and transition semantics.
/// </summary>
/// <param name="context">The per-test cancellation and diagnostic context.</param>
[TestClass]
[DoNotParallelize]
public sealed class TriggerTests(TestContext context)
{
    /// <summary>
    /// Verifies each ordinary operation, timing, and level independently against catalog identity and row images.
    /// </summary>
    /// <param name="operation">The PostgreSQL operation.</param>
    /// <param name="timing">The callback timing.</param>
    /// <param name="level">The callback level.</param>
    /// <param name="eventBits">The native callback event bits, distinct from catalog trigger bits.</param>
    [TestMethod]
    [DataRow("INSERT", "BEFORE", "ROW", 12)]
    [DataRow("INSERT", "AFTER", "ROW", 4)]
    [DataRow("UPDATE", "BEFORE", "ROW", 14)]
    [DataRow("UPDATE", "AFTER", "ROW", 6)]
    [DataRow("DELETE", "BEFORE", "ROW", 13)]
    [DataRow("DELETE", "AFTER", "ROW", 5)]
    [DataRow("INSERT", "BEFORE", "STATEMENT", 8)]
    [DataRow("INSERT", "AFTER", "STATEMENT", 0)]
    [DataRow("UPDATE", "BEFORE", "STATEMENT", 10)]
    [DataRow("UPDATE", "AFTER", "STATEMENT", 2)]
    [DataRow("DELETE", "BEFORE", "STATEMENT", 9)]
    [DataRow("DELETE", "AFTER", "STATEMENT", 1)]
    [DataRow("TRUNCATE", "BEFORE", "STATEMENT", 11)]
    [DataRow("TRUNCATE", "AFTER", "STATEMENT", 3)]
    public Task EventsExposeExactRowsMetadataAndCatalogIdentities(string operation, string timing, string level, int eventBits)
        => Run(nameof(EventsExposeExactRowsMetadataAndCatalogIdentities), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, $"""
                CREATE TRIGGER "déclenché" {timing} {operation} ON trigger_values.rows FOR EACH {level}
                EXECUTE FUNCTION trigger_values.trigger_action('observe','','quote''slash\','café😀');
                """, token);
            int affected = await Execute(connection, transaction, Change(operation), token);
            if (operation != "TRUNCATE")
            {
                Assert.AreEqual(1, affected);
            }

            await using var command = new NpgsqlCommand("""
                SELECT trigger_name,operation,timing,level,event,table_schema,table_name,arguments,old_row,new_row,
                    relation_oid='trigger_values.rows'::regclass,
                    trigger_oid=(SELECT oid FROM pg_trigger WHERE tgrelid='trigger_values.rows'::regclass AND tgname='déclenché'),
                    row_type_oid='trigger_values.rows'::regtype, relation_oid<>row_type_oid,
                    old_transition,new_transition
                FROM trigger_values.events ORDER BY position
                """, connection, transaction);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.AreEqual("déclenché", reader.GetString(0));
            Assert.AreEqual(operation, reader.GetString(1).ToUpperInvariant());
            Assert.AreEqual(timing, reader.GetString(2).ToUpperInvariant());
            Assert.AreEqual(level, reader.GetString(3).ToUpperInvariant());
            Assert.AreEqual(eventBits, reader.GetInt32(4));
            Assert.AreEqual("trigger_values", reader.GetString(5));
            Assert.AreEqual("rows", reader.GetString(6));
            string[] arguments = ["observe", "", "quote'slash\\", "café😀"];
            Assert.AreSequenceEqual(arguments, reader.GetFieldValue<string[]>(7));
            string? oldRow = level == "ROW" && operation is "UPDATE" or "DELETE" ? "1:10:old" : null;
            string? newRow = level == "ROW" ? operation switch
            {
                "INSERT" => "2:20:new",
                "UPDATE" => "1:20:new",
                _ => null,
            } : null;
            Assert.AreEqual(oldRow, reader.IsDBNull(8) ? null : reader.GetString(8));
            Assert.AreEqual(newRow, reader.IsDBNull(9) ? null : reader.GetString(9));
            for (int index = 10; index <= 13; index++)
            {
                Assert.IsTrue(reader.GetBoolean(index), $"Catalog identity column {index}.");
            }

            Assert.IsTrue(reader.IsDBNull(14));
            Assert.IsTrue(reader.IsDBNull(15));
            Assert.IsFalse(await reader.ReadAsync(token));
        });

    /// <summary>
    /// Returns modified NEW values to both storage and RETURNING while preserving the original OLD image.
    /// </summary>
    /// <param name="operation">The insert or update operation.</param>
    [TestMethod]
    [DataRow("INSERT")]
    [DataRow("UPDATE")]
    public Task BeforeRowsReplaceStoredAndReturnedValues(string operation)
        => Run(nameof(BeforeRowsReplaceStoredAndReturnedValues), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, $"""
                CREATE TRIGGER change BEFORE {operation} ON trigger_values.rows FOR EACH ROW
                EXECUTE FUNCTION trigger_values.trigger_action('replace');
                """, token);
            int id = operation == "INSERT" ? 2 : 1;
            Assert.AreEqual($"{id}:30:changed", await Scalar<string>(connection, transaction,
                "WITH changed AS (" + Change(operation) + " RETURNING *) SELECT id||':'||value||':'||note FROM changed", token));
            Assert.AreEqual($"{id}:30:changed", await Scalar<string>(connection, transaction,
                $"SELECT id||':'||value||':'||note FROM trigger_values.rows WHERE id={id}", token));
            Assert.AreEqual(operation == "INSERT" ? "<absent>|2:20:new" : "1:10:old|1:20:new",
                await Scalar<string>(connection, transaction,
                    "SELECT coalesce(old_row,'<absent>')||'|'||new_row FROM trigger_values.events", token));
        });

    /// <summary>
    /// Distinguishes a null skip pointer from nonnull tuple results and PostgreSQL's ignored DELETE and AFTER payloads.
    /// </summary>
    /// <param name="operation">The affected row operation.</param>
    [TestMethod]
    [DataRow("INSERT")]
    [DataRow("UPDATE")]
    [DataRow("DELETE")]
    public Task BeforeNullSkipsRowsAndLaterCallbacks(string operation)
        => Run(nameof(BeforeNullSkipsRowsAndLaterCallbacks), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, $"""
                CREATE TRIGGER a_skip BEFORE {operation} ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('skip');
                CREATE TRIGGER b_later BEFORE {operation} ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action();
                CREATE TRIGGER c_after AFTER {operation} ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action();
                """, token);
            Assert.AreEqual(0, await Execute(connection, transaction, Change(operation), token));
            Assert.AreEqual(0L, await Scalar<long>(connection, transaction,
                "WITH skipped AS (" + Change(operation) + " RETURNING *) SELECT count(*) FROM skipped", token));
            Assert.AreEqual("1:10:old", await StoredRows(connection, transaction, token));
            string[] names = ["a_skip", "a_skip"];
            Assert.AreSequenceEqual(names, await Scalar<string[]>(connection, transaction,
                "SELECT array_agg(trigger_name ORDER BY position) FROM trigger_values.events", token));
        });

    /// <summary>
    /// Accepts an all-null physical row and treats returning OLD on UPDATE as a completed update.
    /// </summary>
    [TestMethod]
    public Task AllNullAndOldReturnsAreNotSkipSignals()
        => Run(nameof(AllNullAndOldReturnsAreNotSkipSignals), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, """
                CREATE TRIGGER all_null BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('all_null');
                CREATE TRIGGER keep_old BEFORE UPDATE ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('old');
                CREATE TRIGGER after_update AFTER UPDATE ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action();
                """, token);
            Assert.AreEqual(1, await Execute(connection, transaction, Change("INSERT"), token));
            Assert.AreEqual(1L, await Scalar<long>(connection, transaction,
                "SELECT count(*) FROM trigger_values.rows WHERE id IS NULL AND value IS NULL AND note IS NULL", token));
            Assert.AreEqual(1, await Execute(connection, transaction, Change("UPDATE"), token));
            Assert.AreEqual("1:10:old", await Scalar<string>(connection, transaction,
                "SELECT id||':'||value||':'||note FROM trigger_values.rows WHERE id=1", token));
            Assert.AreEqual("1:10:old|1:10:old", await Scalar<string>(connection, transaction,
                "SELECT old_row||'|'||new_row FROM trigger_values.events WHERE trigger_name='after_update'", token));
        });

    /// <summary>
    /// Triggers report their UPDATE OF columns, the columns an UPDATE assigns and their constraint deferral.
    /// </summary>
    [TestMethod]
    public Task TriggersReportColumnsAndConstraintDeferral()
        => Run(nameof(TriggersReportColumnsAndConstraintDeferral), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, """
                CREATE TRIGGER columns_of BEFORE UPDATE OF value, note ON trigger_values.rows
                    FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_columns();
                CREATE CONSTRAINT TRIGGER columns_deferred AFTER UPDATE ON trigger_values.rows DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_columns();
                CREATE TRIGGER columns_insert AFTER INSERT ON trigger_values.rows
                    FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_columns();
                UPDATE trigger_values.rows SET note = 'new', id = id;
                INSERT INTO trigger_values.rows VALUES (2, 20, 'other');
                SET CONSTRAINTS ALL IMMEDIATE;
                """, token);
            Assert.AreEqual("columns_of=1,2|0,2|False|False|False;columns_insert=|null|False|False|False;columns_deferred=|0,2|True|True|True",
                await Scalar<string>(connection, transaction,
                    "SELECT string_agg(trigger_name||'='||detail, ';' ORDER BY position) FROM trigger_values.events", token));
        });

    /// <summary>
    /// Discards irrelevant return tuples before serialization and preserves the actual deleted image.
    /// </summary>
    /// <param name="operation">The row operation.</param>
    /// <param name="timing">The timing whose return payload is ignored.</param>
    /// <param name="action">The ignored foreign tuple or null pointer.</param>
    [TestMethod]
    [DataRow("INSERT", "AFTER", "foreign")]
    [DataRow("UPDATE", "AFTER", "foreign")]
    [DataRow("DELETE", "AFTER", "foreign")]
    [DataRow("DELETE", "BEFORE", "foreign")]
    [DataRow("INSERT", "AFTER", "skip")]
    [DataRow("UPDATE", "AFTER", "skip")]
    [DataRow("DELETE", "AFTER", "skip")]
    public Task IgnoredReturnPayloadsDoNotApplyForeignTupleValidation(string operation, string timing, string action)
        => Run(nameof(IgnoredReturnPayloadsDoNotApplyForeignTupleValidation), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, $"""
                CREATE TRIGGER ignored {timing} {operation} ON trigger_values.rows FOR EACH ROW
                EXECUTE FUNCTION trigger_values.trigger_action('{action}');
                """, token);
            string expected = operation switch
            {
                "INSERT" => "2:20:new",
                "UPDATE" => "1:20:new",
                _ => "1:10:old"
            };
            Assert.AreEqual(expected, await Scalar<string>(connection, transaction,
                "WITH changed AS (" + Change(operation) + " RETURNING *) SELECT id||':'||value||':'||note FROM changed", token));
            Assert.AreEqual(operation == "DELETE" ? "" : operation == "INSERT" ? "1:10:old|2:20:new" : "1:20:new",
                await StoredRows(connection, transaction, token));
        });

    /// <summary>
    /// Fires statement callbacks even for empty inputs while keeping row callbacks absent.
    /// </summary>
    /// <param name="operation">The empty statement operation.</param>
    [TestMethod]
    [DataRow("INSERT")]
    [DataRow("UPDATE")]
    [DataRow("DELETE")]
    [DataRow("TRUNCATE")]
    public Task EmptyStatementsStillFireBeforeAndAfter(string operation)
        => Run(nameof(EmptyStatementsStillFireBeforeAndAfter), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, $"""
                DELETE FROM trigger_values.rows;
                CREATE TRIGGER before_statement BEFORE {operation} ON trigger_values.rows FOR EACH STATEMENT EXECUTE FUNCTION trigger_values.trigger_action();
                CREATE TRIGGER after_statement AFTER {operation} ON trigger_values.rows FOR EACH STATEMENT EXECUTE FUNCTION trigger_values.trigger_action('foreign');
                """, token);
            string sql = operation == "INSERT" ? "INSERT INTO trigger_values.rows SELECT 2,20,'new' WHERE false" : Change(operation);
            await Execute(connection, transaction, sql, token);
            string[] expected = ["Before:Statement:<absent>:<absent>", "After:Statement:<absent>:<absent>"];
            Assert.AreSequenceEqual(expected, await Scalar<string[]>(connection, transaction, """
                SELECT array_agg(timing||':'||level||':'||coalesce(old_row,'<absent>')||':'||coalesce(new_row,'<absent>') ORDER BY position)
                FROM trigger_values.events
                """, token));
            Assert.AreEqual("", await StoredRows(connection, transaction, token));
        });

    /// <summary>
    /// Performs explicit view writes and exposes INSTEAD OF row images and return-count semantics.
    /// </summary>
    /// <param name="operation">The view operation.</param>
    /// <param name="skip">Whether to return null after the SPI side effect.</param>
    [TestMethod]
    [DataRow("INSERT", false)]
    [DataRow("UPDATE", false)]
    [DataRow("DELETE", false)]
    [DataRow("INSERT", true)]
    [DataRow("UPDATE", true)]
    [DataRow("DELETE", true)]
    public Task InsteadOfViewWritesHonorReturnedRowAndSkip(string operation, bool skip)
        => Run(nameof(InsteadOfViewWritesHonorReturnedRowAndSkip), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, $"""
                CREATE VIEW trigger_values.editable AS SELECT * FROM trigger_values.rows;
                CREATE TRIGGER view_change INSTEAD OF {operation} ON trigger_values.editable FOR EACH ROW
                EXECUTE FUNCTION trigger_values.trigger_action('{(skip ? "instead_skip" : "instead")}');
                """, token);
            string change = Change(operation).Replace("trigger_values.rows", "trigger_values.editable", StringComparison.Ordinal);
            await using (var command = new NpgsqlCommand(change + " RETURNING id,value,note", connection, transaction))
            {
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.AreEqual(!skip, await reader.ReadAsync(token));
                if (!skip)
                {
                    Assert.AreEqual(operation == "INSERT" ? 2 : 1, reader.GetInt32(0));
                    Assert.AreEqual(operation == "DELETE" ? 10 : 20, reader.GetInt32(1));
                    Assert.AreEqual(operation == "DELETE" ? "old" : "new", reader.GetString(2));
                    Assert.IsFalse(await reader.ReadAsync(token));
                }

                Assert.AreEqual(skip ? 0 : 1, reader.RecordsAffected);
            }

            Assert.AreEqual(operation == "DELETE" ? "" : operation == "INSERT" ? "1:10:old|2:20:new" : "1:20:new",
                await StoredRows(connection, transaction, token));
            Assert.AreEqual("InsteadOf:Row:" + operation.ToLowerInvariant(),
                await Scalar<string>(connection, transaction,
                    "SELECT timing||':'||level||':'||lower(operation) FROM trigger_values.events", token));
        });

    /// <summary>
    /// Exposes PostgreSQL alphabetical callback ordering and sends the edited NEW row to the next trigger.
    /// </summary>
    [TestMethod]
    public Task TriggerOrderingPropagatesNewAndStopsAfterSkip()
        => Run(nameof(TriggerOrderingPropagatesNewAndStopsAfterSkip), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, """
                CREATE TRIGGER d_later BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action();
                CREATE TRIGGER b_observe BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action();
                CREATE TRIGGER c_skip BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('skip');
                CREATE TRIGGER a_replace BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('replace');
                """, token);
            Assert.AreEqual(0, await Execute(connection, transaction, Change("INSERT"), token));
            string[] expected = ["a_replace:2:20:new", "b_observe:2:30:changed", "c_skip:2:30:changed"];
            Assert.AreSequenceEqual(expected, await Scalar<string[]>(connection, transaction,
                "SELECT array_agg(trigger_name||':'||new_row ORDER BY position) FROM trigger_values.events", token));
            Assert.AreEqual("1:10:old", await StoredRows(connection, transaction, token));
        });

    /// <summary>
    /// Preserves thrown diagnostics, rolls back trigger side effects, and recovers on the same backend.
    /// </summary>
    /// <param name="timing">The callback timing.</param>
    /// <param name="level">The callback level.</param>
    /// <param name="action">The failing return or managed action.</param>
    /// <param name="sqlState">The precise expected failure.</param>
    [TestMethod]
    [DataRow("BEFORE", "ROW", "error", "P0001")]
    [DataRow("AFTER", "ROW", "error", "P0001")]
    [DataRow("BEFORE", "STATEMENT", "error", "P0001")]
    [DataRow("AFTER", "STATEMENT", "error", "P0001")]
    [DataRow("BEFORE", "ROW", "managed_error", "38000")]
    [DataRow("BEFORE", "ROW", "foreign", "42804")]
    [DataRow("BEFORE", "STATEMENT", "all_null", "39P01")]
    public Task InvalidReturnsAndErrorsRollBackAndRecover(string timing, string level, string action, string sqlState)
        => Run(nameof(InvalidReturnsAndErrorsRollBackAndRecover), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, $"""
                CREATE TRIGGER rejected {timing} INSERT ON trigger_values.rows FOR EACH {level}
                EXECUTE FUNCTION trigger_values.trigger_action('{action}');
                """, token);
            int backend = connection.ProcessID;
            await transaction.SaveAsync("before_error", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                Execute(connection, transaction, Change("INSERT"), token));
            Assert.AreEqual(sqlState, error.SqlState);
            if (action == "error")
            {
                Assert.AreEqual("trigger rejected row", error.MessageText);
                Assert.AreEqual("owned trigger detail", error.Detail);
                Assert.AreEqual("retry a valid row", error.Hint);
            }

            await transaction.RollbackAsync("before_error", token);
            Assert.AreEqual("1:10:old", await StoredRows(connection, transaction, token));
            Assert.AreEqual(0L, await Scalar<long>(connection, transaction, "SELECT count(*) FROM trigger_values.events", token));
            await Execute(connection, transaction, "DROP TRIGGER rejected ON trigger_values.rows", token);
            Assert.AreEqual(1, await Execute(connection, transaction, Change("INSERT"), token));
            Assert.AreEqual(backend, connection.ProcessID);
        });

    /// <summary>
    /// Keeps earlier SPI effects when a guarded nested error is caught and continues the outer row operation.
    /// </summary>
    [TestMethod]
    public Task CaughtSpiErrorsLeaveTriggerAndBackendUsable()
        => Run(nameof(CaughtSpiErrorsLeaveTriggerAndBackendUsable), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, """
                CREATE TRIGGER recover BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('recover');
                """, token);
            Assert.AreEqual(1, await Execute(connection, transaction, Change("INSERT"), token));
            Assert.AreEqual("22012:42", await Scalar<string>(connection, transaction,
                "SELECT detail FROM trigger_values.events WHERE detail IS NOT NULL", token));
            Assert.AreEqual(2L, await Scalar<long>(connection, transaction, "SELECT count(*) FROM trigger_values.events", token));
            Assert.AreEqual("1:10:old|2:20:new", await StoredRows(connection, transaction, token));
        });

    /// <summary>
    /// Opens the trigger's relation from its OID inside the trigger, as pgrx's <c>before_insert_metadata_safe</c> reads
    /// the relation's name and namespace.
    /// </summary>
    [TestMethod]
    public Task TriggersOpenTheirRelationFromItsOid()
        => Run(nameof(TriggersOpenTheirRelationFromItsOid), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, """
                CREATE TRIGGER relation BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('relation');
                """, token);
            Assert.AreEqual(1, await Execute(connection, transaction, Change("INSERT"), token));
            Assert.AreEqual("trigger_values.rows:True", await Scalar<string>(connection, transaction,
                "SELECT detail FROM trigger_values.events WHERE detail IS NOT NULL", token));
            Assert.AreEqual("1:10:old|2:20:new", await StoredRows(connection, transaction, token));
        });

    /// <summary>
    /// Unwinds a cancelled native SPI call through the managed trigger, rolls back its audit, and runs a new callback.
    /// </summary>
    [TestMethod]
    public Task CancelledTriggerRollsBackAndRestoresBackendContext()
        => Run(nameof(CancelledTriggerRollsBackAndRestoresBackendContext), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, """
                CREATE TRIGGER wait BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('wait');
                SET LOCAL statement_timeout='100ms';
                """, token);
            await transaction.SaveAsync("before_cancel", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, transaction, Change("INSERT"), token));
            Assert.AreEqual("57014", error.SqlState);
            await transaction.RollbackAsync("before_cancel", token);
            await Execute(connection, transaction, "SET LOCAL statement_timeout=0", token);
            Assert.AreEqual("1:10:old", await StoredRows(connection, transaction, token));
            Assert.AreEqual(0L, await Scalar<long>(connection, transaction, "SELECT count(*) FROM trigger_values.events", token));
            await Execute(connection, transaction, """
                DROP TRIGGER wait ON trigger_values.rows;
                CREATE TRIGGER recovered BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('recover');
                """, token);
            Assert.AreEqual(1, await Execute(connection, transaction, Change("INSERT"), token));
            Assert.AreEqual("22012:42", await Scalar<string>(connection, transaction,
                "SELECT detail FROM trigger_values.events WHERE detail IS NOT NULL", token));
        });

    /// <summary>
    /// Resolves target relation metadata independently of the callback function's fixed schema.
    /// </summary>
    [TestMethod]
    public Task RelationSchemaComesFromTargetRatherThanFunction()
        => Run(nameof(RelationSchemaComesFromTargetRatherThanFunction), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, """
                CREATE SCHEMA "données😀";
                CREATE TABLE "données😀"."source table"(id integer,value integer,note text);
                CREATE TRIGGER "different trigger" BEFORE INSERT ON "données😀"."source table" FOR EACH ROW
                    EXECUTE FUNCTION trigger_values.trigger_action();
                INSERT INTO "données😀"."source table" VALUES(7,8,'other owner');
                """, token);
            Assert.AreEqual("different trigger|données😀|source table|0|7:8:other owner",
                await Scalar<string>(connection, transaction, """
                    SELECT trigger_name||'|'||table_schema||'|'||table_name||'|'||cardinality(arguments)||'|'||new_row
                    FROM trigger_values.events
                    """, token));
            Assert.IsTrue(await Scalar<bool>(connection, transaction, """
                SELECT relation_oid='"données😀"."source table"'::regclass AND row_type_oid='"données😀"."source table"'::regtype
                FROM trigger_values.events
                """, token));
        });

    /// <summary>
    /// Enforces relation domain, typmod, and storage constraints after a managed row replacement.
    /// </summary>
    /// <param name="definition">The target relation's field constraints.</param>
    /// <param name="action">The invalid replacement.</param>
    /// <param name="sqlState">The server error.</param>
    [TestMethod]
    [DataRow("value trigger_values.positive,note text", "negative", "23514")]
    [DataRow("value integer CHECK(value>0),note text", "negative", "23514")]
    [DataRow("value integer,note text NOT NULL", "null_note", "23502")]
    [DataRow("value integer,note varchar(3)", "long_note", "22001")]
    public Task ReplacementRowsEnforceActualRelationConstraints(string definition, string action, string sqlState)
        => Run(nameof(ReplacementRowsEnforceActualRelationConstraints), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, $"""
                CREATE TABLE trigger_values.rows(id integer,{definition});
                CREATE TRIGGER rejected BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('{action}');
                """, token);
            await transaction.SaveAsync("invalid", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, transaction, Change("INSERT"), token));
            Assert.AreEqual(sqlState, error.SqlState);
            await transaction.RollbackAsync("invalid", token);
            await Execute(connection, transaction, "DROP TRIGGER rejected ON trigger_values.rows", token);
            Assert.AreEqual(1, await Execute(connection, transaction, Change("INSERT"), token));
        });

    /// <summary>
    /// Applies each column's type modifier to a replacement row as an assignment would: fixed-length character columns
    /// are padded and variable-length ones keep the value.
    /// </summary>
    /// <param name="note">The note column's type.</param>
    /// <param name="bytes">The stored note's length in bytes, including any padding.</param>
    [TestMethod]
    [DataRow("char(10)", 10)]
    [DataRow("varchar(10)", 7)]
    public Task ReplacementRowsApplyTypeModifiers(string note, int bytes)
        => Run(nameof(ReplacementRowsApplyTypeModifiers), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, $"""
                CREATE TABLE trigger_values.rows(id integer,value integer,note {note});
                CREATE TRIGGER replaced BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('replace');
                """, token);
            Assert.AreEqual(1, await Execute(connection, transaction, Change("INSERT"), token));
            Assert.AreEqual(bytes, await Scalar<int>(connection, transaction, "SELECT octet_length(note) FROM trigger_values.rows WHERE id=2", token));
            Assert.AreEqual(30, await Scalar<int>(connection, transaction, "SELECT value FROM trigger_values.rows WHERE id=2", token));
        });

    /// <summary>
    /// Protects undefined generated cells and dropped physical slots while preserving later fields and computed values.
    /// </summary>
    [TestMethod]
    public Task GeneratedAndDroppedFieldsRespectAvailability()
        => Run(nameof(GeneratedAndDroppedFieldsRespectAvailability), async (connection, transaction, token) =>
        {
            bool supportsVirtualColumns = PostgresFixture.Cluster.Installation.Version.Major >= 18;
            string virtualColumn = supportsVirtualColumns
                ? ",virtual integer GENERATED ALWAYS AS(value*3) VIRTUAL"
                : string.Empty;
            await Execute(connection, transaction, $$"""
                CREATE TABLE trigger_values.rows(id integer,obsolete text,value integer,note text,
                    stored integer GENERATED ALWAYS AS(value*2) STORED{{virtualColumn}});
                ALTER TABLE trigger_values.rows DROP COLUMN obsolete;
                CREATE TRIGGER before_row BEFORE INSERT OR UPDATE ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_generated();
                CREATE TRIGGER after_row AFTER INSERT OR UPDATE ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_generated();
                INSERT INTO trigger_values.rows(id,value,note) VALUES(1,10,'note');
                """, token);
            string projection = supportsVirtualColumns
                ? "value||':'||stored||':'||virtual||':'||note"
                : "value||':'||stored||':'||note";
            Assert.AreEqual(supportsVirtualColumns ? "20:40:60:note" : "20:40:note",
                await Scalar<string>(connection, transaction, $"SELECT {projection} FROM trigger_values.rows", token));
            string virtualProtection = supportsVirtualColumns ? "|virtual:protected:protected" : string.Empty;
            string[] expected =
            [
                "Before:dropped:protected|stored:protected:protected" + virtualProtection,
                "Before:ordinary:55000",
                "After:dropped:protected|stored:40" + virtualProtection,
            ];
            Assert.AreSequenceEqual(expected, await Scalar<string[]>(connection, transaction,
                "SELECT array_agg(timing||':'||detail ORDER BY position) FROM trigger_values.events", token));
            await Execute(connection, transaction, "TRUNCATE trigger_values.events; UPDATE trigger_values.rows SET value=30", token);
            projection = supportsVirtualColumns ? "value||':'||stored||':'||virtual" : "value||':'||stored";
            Assert.AreEqual(supportsVirtualColumns ? "40:80:120" : "40:80",
                await Scalar<string>(connection, transaction, $"SELECT {projection} FROM trigger_values.rows", token));
            expected[2] = "After:dropped:protected|stored:80" + virtualProtection;
            Assert.AreSequenceEqual(expected, await Scalar<string[]>(connection, transaction,
                "SELECT array_agg(timing||':'||detail ORDER BY position) FROM trigger_values.events", token));
        });

    /// <summary>
    /// Owns toast data, row metadata, and arguments after callback memory and SPI contexts have ended.
    /// </summary>
    [TestMethod]
    public Task RetainedContextSurvivesToastSpiAndGarbageCollection()
        => Run(nameof(RetainedContextSurvivesToastSpiAndGarbageCollection), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, """
                UPDATE trigger_values.rows SET note=repeat('café😀',12000);
                CREATE TRIGGER retained BEFORE UPDATE ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('retain','owned');
                UPDATE trigger_values.rows SET value=20,note=repeat('new😀',13000);
                """, token);
            string expected = "retained|trigger_values.rows|retain,owned|1:10:" + string.Concat(Enumerable.Repeat("café😀", 12000)) +
                "|1:20:" + string.Concat(Enumerable.Repeat("new😀", 13000));
            Assert.AreEqual(expected, await Scalar<string>(connection, transaction, "SELECT trigger_values.trigger_retained()", token));
        });

    /// <summary>
    /// Reads the exact final transition rows through each SPI ownership path, including skipped and edited rows.
    /// </summary>
    /// <param name="mode">The SPI query, session, plan, or cursor path.</param>
    [TestMethod]
    [DataRow("query")]
    [DataRow("session")]
    [DataRow("plan")]
    [DataRow("session_plan")]
    [DataRow("cursor")]
    [DataRow("plan_cursor")]
    [DataRow("cache")]
    public Task TransitionInsertRowsMatchActualWritesAcrossOwners(string mode)
        => Run(nameof(TransitionInsertRowsMatchActualWritesAcrossOwners), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, $"""
                DELETE FROM trigger_values.rows;
                CREATE TRIGGER before_row BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('skip_even');
                CREATE TRIGGER transition AFTER INSERT ON trigger_values.rows REFERENCING NEW TABLE AS "new rows" FOR EACH STATEMENT
                EXECUTE FUNCTION trigger_values.trigger_transitions('{mode}');
                """, token);
            Assert.AreEqual(2, await Execute(connection, transaction,
                "INSERT INTO trigger_values.rows VALUES(1,10,'one'),(2,20,'two'),(3,30,'three')", token));
            string expected = "1:20:changed|3:40:changed";
            Assert.AreEqual(expected, await StoredRows(connection, transaction, token));
            Assert.AreEqual(mode == "session" ? expected + "/" + expected : expected,
                await Scalar<string>(connection, transaction, "SELECT new_row FROM trigger_values.events WHERE trigger_name='transition'", token));
            Assert.AreEqual("<absent>|new rows", await Scalar<string>(connection, transaction,
                "SELECT coalesce(old_transition,'<absent>')||'|'||new_transition FROM trigger_values.events WHERE trigger_name='transition'", token));
            await Execute(connection, transaction, "TRUNCATE trigger_values.events", token);
            Assert.AreEqual(1, await Execute(connection, transaction, "INSERT INTO trigger_values.rows VALUES(5,50,'five')", token));
            Assert.AreEqual(mode == "session" ? "5:60:changed/5:60:changed" : "5:60:changed",
                await Scalar<string>(connection, transaction, "SELECT new_row FROM trigger_values.events WHERE trigger_name='transition'", token));
        });

    /// <summary>
    /// Keeps OLD and NEW transition aliases independent and preserves empty relation identity.
    /// </summary>
    /// <param name="operation">The update or delete operation.</param>
    /// <param name="rowLevel">Whether every row callback observes the full transition set.</param>
    [TestMethod]
    [DataRow("UPDATE", false)]
    [DataRow("DELETE", false)]
    [DataRow("UPDATE", true)]
    [DataRow("DELETE", true)]
    public Task TransitionOldAndNewSetsHaveExactImages(string operation, bool rowLevel)
        => Run(nameof(TransitionOldAndNewSetsHaveExactImages), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            string referencing = operation == "UPDATE" ? "OLD TABLE AS old_rows NEW TABLE AS new_rows" : "OLD TABLE AS old_rows";
            await Execute(connection, transaction, $"""
                INSERT INTO trigger_values.rows VALUES(2,20,'two');
                CREATE TRIGGER transition AFTER {operation} ON trigger_values.rows REFERENCING {referencing}
                FOR EACH {(rowLevel ? "ROW" : "STATEMENT")} EXECUTE FUNCTION trigger_values.trigger_transitions('query');
                """, token);
            string sql = operation == "UPDATE" ? "UPDATE trigger_values.rows SET value=value+1" : "DELETE FROM trigger_values.rows";
            Assert.AreEqual(2, await Execute(connection, transaction, sql, token));
            string[] expected = [.. Enumerable.Repeat("1:10:old|2:20:two/" +
                (operation == "UPDATE" ? "1:11:old|2:21:two" : "<absent>"), rowLevel ? 2 : 1)];
            Assert.AreSequenceEqual(expected, await Scalar<string[]>(connection, transaction,
                "SELECT array_agg(old_row||'/'||coalesce(new_row,'<absent>') ORDER BY position) FROM trigger_values.events", token));
            await Execute(connection, transaction, "TRUNCATE trigger_values.events", token);
            await Execute(connection, transaction, sql + " WHERE false", token);
            Assert.AreEqual(rowLevel ? 0L : 1L, await Scalar<long>(connection, transaction, "SELECT count(*) FROM trigger_values.events", token));
            if (!rowLevel)
            {
                Assert.AreEqual("old_rows:", await Scalar<string>(connection, transaction,
                    "SELECT old_transition||':'||old_row FROM trigger_values.events", token));
            }
        });

    /// <summary>
    /// Rejects transition cursor access after callback exit even when managed ownership was detached.
    /// </summary>
    /// <param name="detached">Whether the portal was detached inside the callback.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task EscapedTransitionCursorsExpireAndBackendRecovers(bool detached)
        => Run(nameof(EscapedTransitionCursorsExpireAndBackendRecovers), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, $"""
                CREATE TRIGGER transition AFTER INSERT ON trigger_values.rows REFERENCING NEW TABLE AS transient_rows
                FOR EACH STATEMENT EXECUTE FUNCTION trigger_values.trigger_transitions('{(detached ? "detach" : "escape")}');
                """, token);
            Assert.AreEqual(3, await Execute(connection, transaction,
                "INSERT INTO trigger_values.rows VALUES(2,20,'new'),(3,30,'three'),(4,40,'four')", token));
            Assert.AreEqual("34000:42", await Scalar<string>(connection, transaction,
                "SELECT trigger_values.trigger_cursor_outside(" + (detached ? "true" : "false") + ")", token));
            Assert.AreEqual(0L, await Scalar<long>(connection, transaction,
                "SELECT count(*) FROM pg_cursors WHERE statement LIKE 'SELECT id,value,note FROM %transient_rows%'", token));
        });

    /// <summary>
    /// Rebinds retained plans to each invocation and rejects execution without a live transition environment.
    /// </summary>
    [TestMethod]
    public Task RetainedTransitionPlanRebindsAndRejectsOutsideUse()
        => Run(nameof(RetainedTransitionPlanRebindsAndRejectsOutsideUse), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, """
                CREATE TRIGGER transition AFTER INSERT ON trigger_values.rows REFERENCING NEW TABLE AS cached_rows
                FOR EACH STATEMENT EXECUTE FUNCTION trigger_values.trigger_transitions('cache');
                INSERT INTO trigger_values.rows VALUES(2,20,'second');
                INSERT INTO trigger_values.rows VALUES(3,30,'third');
                """, token);
            string[] expected = ["2:20:second", "3:30:third"];
            Assert.AreSequenceEqual(expected, await Scalar<string[]>(connection, transaction,
                "SELECT array_agg(new_row ORDER BY position) FROM trigger_values.events", token));
            Assert.AreEqual("XX000:42", await Scalar<string>(connection, transaction, "SELECT trigger_values.trigger_plan_outside()", token));
            Assert.AreEqual(1, await Execute(connection, transaction, "INSERT INTO trigger_values.rows VALUES(4,40,'fourth')", token));
            Assert.AreEqual("4:40:fourth", await Scalar<string>(connection, transaction,
                "SELECT new_row FROM trigger_values.events ORDER BY position DESC LIMIT 1", token));
        });

    /// <summary>
    /// Restores an outer transition environment after an inner callback uses identical alias names.
    /// </summary>
    [TestMethod]
    public Task NestedTriggersRestoreParentContextAndTransitionRelations()
        => Run(nameof(NestedTriggersRestoreParentContextAndTransitionRelations), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, """
                CREATE TABLE trigger_values.child(LIKE trigger_values.rows);
                CREATE TRIGGER child AFTER INSERT ON trigger_values.child REFERENCING NEW TABLE AS same_rows FOR EACH STATEMENT
                    EXECUTE FUNCTION trigger_values.trigger_transitions('query');
                CREATE TRIGGER parent AFTER INSERT ON trigger_values.rows REFERENCING NEW TABLE AS same_rows FOR EACH STATEMENT
                    EXECUTE FUNCTION trigger_values.trigger_transitions('nested');
                """, token);
            Assert.AreEqual(1, await Execute(connection, transaction, Change("INSERT"), token));
            string[] expected = ["parent:nested:2:20:new", "child:query:90:900:inner", "parent:restored:2:20:new"];
            Assert.AreSequenceEqual(expected, await Scalar<string[]>(connection, transaction,
                "SELECT array_agg(trigger_name||':'||detail||':'||new_row ORDER BY position) FROM trigger_values.events", token));
        });

    /// <summary>
    /// Keeps transition environments alive while multiple suspended cursor iterators are disposed, including a thrown cleanup error.
    /// </summary>
    /// <param name="fail">Whether one cursor's iterator throws during disposal.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task SuspendedCursorCleanupKeepsTransitionEnvironmentUntilAllOwnersEnd(bool fail)
        => Run(nameof(SuspendedCursorCleanupKeepsTransitionEnvironmentUntilAllOwnersEnd), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, $"""
                CREATE TRIGGER cleanup AFTER INSERT ON trigger_values.rows REFERENCING NEW TABLE AS cleanup_rows FOR EACH STATEMENT
                    EXECUTE FUNCTION trigger_values.trigger_cleanup({(fail ? "'fail'" : "")});
                """, token);
            await transaction.SaveAsync("before_cleanup", token);
            const string sql = "INSERT INTO trigger_values.rows VALUES(2,20,'second'),(3,30,'third')";
            if (fail)
            {
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, transaction, sql, token));
                Assert.AreEqual("P7107", error.SqlState);
                Assert.AreEqual("trigger iterator Dispose failure", error.MessageText);
                await transaction.RollbackAsync("before_cleanup", token);
                Assert.AreEqual("1:10:old", await StoredRows(connection, transaction, token));
                string status = await Scalar<string>(connection, transaction, "SELECT trigger_values.trigger_cleanup_status()", token);
                Assert.StartsWith("2:2:", status);
                Assert.Contains("rows:2", status);
                Assert.DoesNotContain("rows:0", status);
            }
            else
            {
                Assert.AreEqual(2, await Execute(connection, transaction, sql, token));
                Assert.AreEqual("2:2:rows:2|rows:2", await Scalar<string>(connection, transaction,
                    "SELECT trigger_values.trigger_cleanup_status()", token));
                Assert.AreEqual("1:10:old|2:20:second|3:30:third", await StoredRows(connection, transaction, token));
            }

            Assert.AreEqual(0L, await Scalar<long>(connection, transaction,
                "SELECT count(*) FROM pg_cursors WHERE statement LIKE 'SELECT id,trigger_values.trigger_cleanup_rows%'", token));
            await Execute(connection, transaction, "DROP TRIGGER cleanup ON trigger_values.rows", token);
            Assert.AreEqual(1, await Execute(connection, transaction, "INSERT INTO trigger_values.rows VALUES(4,40,'recovered')", token));
        });

    /// <summary>
    /// Distinguishes a nonnull zero-column tuple from a trigger skip pointer.
    /// </summary>
    /// <param name="skip">Whether to return the native skip pointer.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ZeroColumnRowsKeepPhysicalTupleIdentity(bool skip)
        => Run(nameof(ZeroColumnRowsKeepPhysicalTupleIdentity), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, $"""
                CREATE TABLE trigger_values.empty();
                CREATE TRIGGER empty_row BEFORE INSERT ON trigger_values.empty FOR EACH ROW
                    EXECUTE FUNCTION trigger_values.trigger_empty({(skip ? "'skip'" : "")});
                """, token);
            Assert.AreEqual(skip ? 0 : 1, await Execute(connection, transaction, "INSERT INTO trigger_values.empty DEFAULT VALUES", token));
            Assert.AreEqual(skip ? 0L : 1L, await Scalar<long>(connection, transaction, "SELECT count(*) FROM trigger_values.empty", token));
            if (!skip)
            {
                Assert.AreEqual("00000000", await Scalar<string>(connection, transaction,
                    "SELECT encode(record_send(row),'hex') FROM trigger_values.empty row", token));
            }
        });

    /// <summary>
    /// Distinguishes UPDATE OF and WHEN filtering and classifies ON CONFLICT statement callbacks correctly.
    /// </summary>
    [TestMethod]
    public Task NativeTriggerFilteringAndConflictOrderingArePreserved()
        => Run(nameof(NativeTriggerFilteringAndConflictOrderingArePreserved), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, """
                ALTER TABLE trigger_values.rows ADD UNIQUE(id);
                CREATE TRIGGER column_update BEFORE UPDATE OF value ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action();
                CREATE TRIGGER never BEFORE UPDATE ON trigger_values.rows FOR EACH ROW WHEN(false) EXECUTE FUNCTION trigger_values.trigger_action('error');
                UPDATE trigger_values.rows SET value=value;
                """, token);
            Assert.AreEqual("column_update", await Scalar<string>(connection, transaction,
                "SELECT trigger_name FROM trigger_values.events", token));
            await Execute(connection, transaction, """
                TRUNCATE trigger_values.events;
                DROP TRIGGER column_update ON trigger_values.rows;
                CREATE TRIGGER before_insert BEFORE INSERT ON trigger_values.rows FOR EACH STATEMENT EXECUTE FUNCTION trigger_values.trigger_action();
                CREATE TRIGGER before_update BEFORE UPDATE ON trigger_values.rows FOR EACH STATEMENT EXECUTE FUNCTION trigger_values.trigger_action();
                CREATE TRIGGER after_insert AFTER INSERT ON trigger_values.rows FOR EACH STATEMENT EXECUTE FUNCTION trigger_values.trigger_action();
                CREATE TRIGGER after_update AFTER UPDATE ON trigger_values.rows FOR EACH STATEMENT EXECUTE FUNCTION trigger_values.trigger_action();
                INSERT INTO trigger_values.rows VALUES(1,99,'conflict') ON CONFLICT(id) DO UPDATE SET value=excluded.value;
                """, token);
            string[] expected = ["Before:Insert", "Before:Update", "After:Update", "After:Insert"];
            Assert.AreSequenceEqual(expected, await Scalar<string[]>(connection, transaction,
                "SELECT array_agg(timing||':'||operation ORDER BY position) FROM trigger_values.events", token));
            Assert.AreEqual("1:99:old", await StoredRows(connection, transaction, token));
        });

    /// <summary>
    /// Executes deferred callbacks after the originating statement and reports the actual partition row relation.
    /// </summary>
    [TestMethod]
    public Task DeferredAndPartitionTriggersRetainActualRelationIdentity()
        => Run(nameof(DeferredAndPartitionTriggersRetainActualRelationIdentity), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, """
                CREATE TABLE trigger_values.rows(id integer,value integer,note text) PARTITION BY RANGE(id);
                CREATE TABLE trigger_values.part PARTITION OF trigger_values.rows FOR VALUES FROM(0) TO(10);
                CREATE CONSTRAINT TRIGGER deferred AFTER INSERT ON trigger_values.part DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action();
                CREATE TRIGGER parent BEFORE INSERT ON trigger_values.rows FOR EACH STATEMENT EXECUTE FUNCTION trigger_values.trigger_action();
                INSERT INTO trigger_values.rows VALUES(1,10,'partition');
                """, token);
            Assert.AreEqual("parent", await Scalar<string>(connection, transaction, "SELECT trigger_name FROM trigger_values.events", token));
            await Execute(connection, transaction, "SET CONSTRAINTS ALL IMMEDIATE", token);
            string[] expected = ["parent:rows:Statement:<absent>", "deferred:part:Row:1:10:partition"];
            Assert.AreSequenceEqual(expected, await Scalar<string[]>(connection, transaction,
                "SELECT array_agg(trigger_name||':'||table_name||':'||level||':'||coalesce(new_row,'<absent>') ORDER BY position) FROM trigger_values.events", token));
            Assert.IsTrue(await Scalar<bool>(connection, transaction,
                "SELECT relation_oid='trigger_values.part'::regclass AND row_type_oid='trigger_values.part'::regtype FROM trigger_values.events WHERE trigger_name='deferred'", token));
        });

    /// <summary>
    /// Executes a deferred trigger during COMMIT when ordinary statement portal context is unavailable.
    /// </summary>
    [TestMethod]
    public async Task DeferredTriggerCanUseSpiDuringCommit()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        string triggerName = "commit_" + Guid.NewGuid().ToString("N");
        try
        {
            await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token))
            {
                await Execute(connection, transaction, $"""
                    CREATE TEMP TABLE commit_rows(id integer,value integer,note text) ON COMMIT DROP;
                    CREATE CONSTRAINT TRIGGER {triggerName} AFTER INSERT ON commit_rows DEFERRABLE INITIALLY DEFERRED
                        FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('recover');
                    INSERT INTO commit_rows VALUES(1,2,'committed');
                    """, token);
                Assert.AreEqual(0L, await Scalar<long>(connection, transaction,
                    $"SELECT count(*) FROM trigger_values.events WHERE trigger_name='{triggerName}'", token));
                await transaction.CommitAsync(token);
            }

            await using var command = new NpgsqlCommand($"""
                SELECT array_agg(new_row||':'||coalesce(detail,'initial') ORDER BY position)
                FROM trigger_values.events WHERE trigger_name='{triggerName}'
                """, connection);
            string[] expected = ["1:2:committed:initial", "1:2:committed:22012:42"];
            Assert.AreSequenceEqual(expected, Assert.IsInstanceOfType<string[]>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "SELECT to_regclass('pg_temp.commit_rows') IS NULL";
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DELETE FROM trigger_values.events WHERE trigger_name='{triggerName}'", connection);
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Reaches the native CALLED_AS_TRIGGER guard through a scalar alias and recovers before valid trigger DML.
    /// </summary>
    [TestMethod]
    public Task OrdinaryInvocationIsRejectedBeforeManagedDispatch()
        => Run(nameof(OrdinaryInvocationIsRejectedBeforeManagedDispatch), async (connection, transaction, token) =>
        {
            await SetupRows(connection, transaction, token);
            await Execute(connection, transaction, """
                DO $body$ DECLARE p pg_proc; BEGIN
                    SELECT * INTO p FROM pg_proc WHERE oid='trigger_values.trigger_action()'::regprocedure;
                    EXECUTE format('CREATE FUNCTION trigger_values.invalid_call() RETURNS integer AS %L,%L LANGUAGE c',p.probin,p.prosrc);
                END $body$;
                """, token);
            await transaction.SaveAsync("invalid", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                Scalar<int>(connection, transaction, "SELECT trigger_values.invalid_call()", token));
            Assert.AreEqual("39P01", error.SqlState);
            await transaction.RollbackAsync("invalid", token);
            Assert.AreEqual(0L, await Scalar<long>(connection, transaction, "SELECT count(*) FROM trigger_values.events", token));
            await Execute(connection, transaction, """
                CREATE TRIGGER valid BEFORE INSERT ON trigger_values.rows FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action();
                """, token);
            Assert.AreEqual(1, await Execute(connection, transaction, Change("INSERT"), token));
        });

    /// <summary>
    /// Uses expanded LATIN1 metadata and transition identifiers and preserves errors from unrepresentable row output.
    /// </summary>
    [TestMethod]
    public async Task Latin1TriggerNamesArgumentsAndTransitionsUseServerEncoding()
    {
        CancellationToken token = context.CancellationToken;
        string database = "trigger_latin1_" + Guid.NewGuid().ToString("N");
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
            string name = new('é', 63);
            await using var command = new NpgsqlCommand($"""
                CREATE EXTENSION ankus_test;
                CREATE TABLE trigger_values."{name}"(id integer,value integer,note text);
                CREATE TRIGGER "{name}" AFTER INSERT ON trigger_values."{name}" REFERENCING NEW TABLE AS "{name}"
                    FOR EACH STATEMENT EXECUTE FUNCTION trigger_values.trigger_transitions('query','café');
                INSERT INTO trigger_values."{name}" VALUES(1,2,'café');
                """, connection);
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = "SELECT trigger_name||'|'||table_name||'|'||new_transition||'|'||arguments[2]||'|'||new_row FROM trigger_values.events";
            Assert.AreEqual(name + "|" + name + "|" + name + "|café|1:2:café", await command.ExecuteScalarAsync(token));
            command.CommandText = $"""
                CREATE TRIGGER output BEFORE INSERT ON trigger_values."{name}" FOR EACH ROW EXECUTE FUNCTION trigger_values.trigger_action('emoji');
                """;
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = $"INSERT INTO trigger_values.\"{name}\" VALUES(2,3,'input')";
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteNonQueryAsync(token));
            Assert.AreEqual("22P05", error.SqlState);
            command.CommandText = $"DROP TRIGGER output ON trigger_values.\"{name}\"; INSERT INTO trigger_values.\"{name}\" VALUES(3,4,'récupéré')";
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = $"SELECT note FROM trigger_values.\"{name}\" WHERE id=3";
            Assert.AreEqual("récupéré", await command.ExecuteScalarAsync(token));
            Assert.AreEqual(backend, connection.ProcessID);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", administrator);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private Task Run(string name, Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> action)
        => PostgresFixture.Cluster.RunInTransactionAsync(name, action, context.CancellationToken);

    private static Task<int> SetupRows(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        => Execute(connection, transaction, """
            CREATE TABLE trigger_values.rows(id integer,value integer,note text);
            INSERT INTO trigger_values.rows VALUES(1,10,'old');
            """, token);

    private static string Change(string operation) => operation switch
    {
        "INSERT" => "INSERT INTO trigger_values.rows VALUES(2,20,'new')",
        "UPDATE" => "UPDATE trigger_values.rows SET value=20,note='new' WHERE id=1",
        "DELETE" => "DELETE FROM trigger_values.rows WHERE id=1",
        "TRUNCATE" => "TRUNCATE trigger_values.rows",
        _ => throw new ArgumentException("Unexpected test operation.", nameof(operation)),
    };

    private static Task<string> StoredRows(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        => Scalar<string>(connection, transaction,
            "SELECT coalesce(string_agg(id||':'||value||':'||note,'|' ORDER BY id),'') FROM trigger_values.rows", token);

    private static async Task<int> Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }
}
