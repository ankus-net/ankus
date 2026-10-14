using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks owned event metadata, immutable snapshots, and strict invocation lifetime at the guarded helper boundary.
/// </summary>
[TestClass]
public sealed class PgEventTriggerTests
{
    [ThreadStatic]
    private static int s_queryCount;

    [ThreadStatic]
    private static string? s_command;

    [ThreadStatic]
    private static SpiResult? s_result;

    [ThreadStatic]
    private static int s_releaseCount;

    /// <summary>
    /// Sets the result the static native SPI callback returns, or null for none.
    /// </summary>
    private static void Respond(SpiResult? result) => s_result = result;

    /// <summary>
    /// Clears the query and release counts the static native callbacks record.
    /// </summary>
    private static void ResetCounts()
    {
        s_queryCount = 0;
        s_releaseCount = 0;
    }

    /// <summary>
    /// Every event kind retains the exact native event and command text after the borrowed buffers are released.
    /// </summary>
    [TestMethod]
    [DataRow("ddl_command_start", "CREATE TABLE", PgEventTriggerKind.DdlCommandStart)]
    [DataRow("ddl_command_end", "ALTER TABLE", PgEventTriggerKind.DdlCommandEnd)]
    [DataRow("sql_drop", "DROP TABLE", PgEventTriggerKind.SqlDrop)]
    [DataRow("table_rewrite", "ALTER TABLE", PgEventTriggerKind.TableRewrite)]
    [DataRow("login", "LOGIN", PgEventTriggerKind.Login)]
    public void EventsRetainExactKindAndCommandTag(string eventName, string commandTag, PgEventTriggerKind kind)
    {
        PgEventTriggerContext context = Enter(eventName, commandTag);
        try
        {
            Assert.AreEqual(eventName, context.Event);
            Assert.AreEqual(commandTag, context.CommandTag);
            Assert.AreEqual(kind, context.Kind);
            Assert.IsNull(context.Parent);
        }
        finally
        {
            NativeEventTrigger.Exit(context);
        }

        Assert.AreEqual(eventName, context.Event);
        Assert.AreEqual(commandTag, context.CommandTag);
        Assert.AreEqual(kind, context.Kind);
    }

    /// <summary>
    /// The entry protocol rejects missing and extra slots before attempting to inspect their payloads.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    public void EntryRequiresExactlyTwoSlots(int length)
    {
        var values = new NativeValue[length];
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeEventTrigger.Enter(values));
    }

    /// <summary>
    /// Unknown event names, empty fields, embedded zero bytes, and invalid UTF-8 cannot create an invocation scope.
    /// </summary>
    [TestMethod]
    [DataRow(0, "unknown")]
    [DataRow(0, "DDL_COMMAND_END")]
    [DataRow(0, "")]
    [DataRow(1, "")]
    [DataRow(0, "ddl_command_end\0")]
    [DataRow(1, "CREATE\0TABLE")]
    public void InvalidMetadataCannotEnterScope(int slot, string text)
    {
        NativeValue[] values = [NativeValue.FromString("ddl_command_end"), NativeValue.FromString("CREATE TABLE")];
        values[slot].Release();
        values[slot] = NativeValue.FromBytes(Encoding.UTF8.GetBytes(text));
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeEventTrigger.Enter(values));
        }
        finally
        {
            Release(values);
        }
    }

    /// <summary>
    /// Invalid UTF-8 in either slot fails through the strict decoder instead of introducing replacement characters.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void InvalidUtf8IsRejected(int slot)
    {
        NativeValue[] values = [NativeValue.FromString("ddl_command_end"), NativeValue.FromString("CREATE TABLE")];
        values[slot].Release();
        values[slot] = NativeValue.FromBytes([0xc3, 0x28]);
        try
        {
            Assert.ThrowsExactly<DecoderFallbackException>(() => NativeEventTrigger.Enter(values));
        }
        finally
        {
            Release(values);
        }
    }

    /// <summary>
    /// Each scalar text header field is checked independently, with otherwise valid event metadata.
    /// </summary>
    [TestMethod]
    [DataRow(0, "null")]
    [DataRow(1, "null")]
    [DataRow(0, "integer")]
    [DataRow(1, "integer")]
    [DataRow(0, "auxiliary1")]
    [DataRow(1, "auxiliary2")]
    [DataRow(0, "infinity")]
    [DataRow(1, "negative-length")]
    [DataRow(0, "missing-data")]
    public void ConflictingTextHeadersAreRejected(int slot, string defect)
    {
        NativeValue[] values = [NativeValue.FromString("ddl_command_end"), NativeValue.FromString("CREATE TABLE")];
        switch (defect)
        {
            case "null":
                values[slot].IsNull = 1;
                break;
            case "integer":
                values[slot].Integral = 1;
                break;
            case "auxiliary1":
                Auxiliary1(ref values[slot]) = 1;
                break;
            case "auxiliary2":
                Auxiliary2(ref values[slot]) = 1;
                break;
            case "infinity":
                Infinity(ref values[slot]) = 1;
                break;
            case "negative-length":
                Length(ref values[slot]) = -1;
                break;
            case "missing-data":
                values[slot].Release();
                Length(ref values[slot]) = 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }

        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeEventTrigger.Enter(values));
        }
        finally
        {
            Release(values);
        }
    }

    /// <summary>
    /// Phase guards run before native dispatch, and accepted phases invoke only their explicit catalog metadata projection.
    /// </summary>
    [TestMethod]
    [DataRow("ddl_command_start", -1)]
    [DataRow("ddl_command_end", 0)]
    [DataRow("sql_drop", 1)]
    [DataRow("table_rewrite", 2)]
    [DataRow("login", -1)]
    public void HelpersRequireTheirExactActivePhase(string eventName, int allowedHelper)
    {
        using var backend = new BackendScope();
        PgEventTriggerContext context = Enter(eventName);
        ResetCounts();
        try
        {
            for (int helper = 0; helper < 3; helper++)
            {
                if (helper == allowedHelper)
                {
                    PgException error = Assert.ThrowsExactly<PgException>(() => InvokeHelper(context, helper));
                    Assert.AreEqual("P0001", error.SqlState);
                    Assert.AreEqual("metadata query dispatched", error.Message);
                }
                else
                {
                    Assert.ThrowsExactly<InvalidOperationException>(() => InvokeHelper(context, helper));
                }
            }

            Assert.AreEqual(allowedHelper < 0 ? 0 : 1, s_queryCount);
            if (allowedHelper >= 0)
            {
                string expected = allowedHelper switch
                {
                    0 => "SELECT classid, objid, objsubid, command_tag, object_type, schema_name, object_identity, in_extension FROM pg_catalog.pg_event_trigger_ddl_commands()",
                    1 => "SELECT classid, objid, objsubid, original, normal, is_temporary, object_type, schema_name, object_name, object_identity, address_names, address_args FROM pg_catalog.pg_event_trigger_dropped_objects()",
                    _ => "SELECT pg_catalog.pg_event_trigger_table_rewrite_oid() AS table_oid, pg_catalog.pg_event_trigger_table_rewrite_reason() AS reason",
                };

                Assert.AreEqual(expected, s_command);
            }
        }
        finally
        {
            NativeEventTrigger.Exit(context);
        }

        Assert.ThrowsExactly<InvalidOperationException>(context.GetDdlCommands);
        Assert.ThrowsExactly<InvalidOperationException>(context.GetDroppedObjects);
        Assert.ThrowsExactly<InvalidOperationException>(context.GetTableRewrite);
        Assert.AreEqual(allowedHelper < 0 ? 0 : 1, s_queryCount);
    }

    /// <summary>
    /// Public collection helpers materialize ordered immutable snapshots before the guarded result allocations are released.
    /// </summary>
    [TestMethod]
    [DataRow("ddl_command_end", 0)]
    [DataRow("sql_drop", 1)]
    public void HelpersReturnOwnedImmutableOrderedSnapshots(string eventName, int helper)
    {
        using var backend = new BackendScope();
        SpiRow first = helper == 0
            ? DdlRow([1259U, 8100U, 0, "CREATE TABLE", "table", "é schema", "\"é schema\".first", true])
            : DroppedRow([1259U, 8100U, 0, true, false, false, "table", "é schema", "first", "\"é schema\".first",
                new PgArray<string>(["é schema", "first"]), new PgArray<string>([])]);
        SpiRow second = helper == 0
            ? DdlRow([null, null, null, "GRANT", "TABLE", null, null, false])
            : DroppedRow([1247U, 8101U, 0, false, true, false, "type", "é schema", "second", "\"é schema\".second", null, null]);
        Respond(new SpiResult(ResultColumns(first), [first, second], 2));
        ResetCounts();
        PgEventTriggerContext context = Enter(eventName);
        object snapshots;
        try
        {
            snapshots = InvokeHelper(context, helper);
            Assert.AreEqual(1, s_releaseCount);
        }
        finally
        {
            NativeEventTrigger.Exit(context);
            Respond(null);
        }

        IList list = Assert.IsInstanceOfType<IList>(snapshots);
        Assert.HasCount(2, list);
        Assert.ThrowsExactly<NotSupportedException>(() => list[0] = list[1]);
        if (helper == 0)
        {
            IReadOnlyList<PgDdlCommand> commands = Assert.IsInstanceOfType<IReadOnlyList<PgDdlCommand>>(snapshots);
            Assert.AreEqual(8100U, commands[0].ObjectId);
            Assert.AreEqual("\"é schema\".first", commands[0].ObjectIdentity);
            Assert.IsTrue(commands[0].InExtension);
            Assert.IsNull(commands[1].ObjectId);
            Assert.AreEqual("GRANT", commands[1].CommandTag);
            Assert.IsFalse(commands[1].InExtension);
        }
        else
        {
            IReadOnlyList<PgDroppedObject> dropped = Assert.IsInstanceOfType<IReadOnlyList<PgDroppedObject>>(snapshots);
            Assert.AreEqual(8100U, dropped[0].ObjectId);
            Assert.AreEqual("\"é schema\".first", dropped[0].ObjectIdentity);
            Assert.IsNotNull(dropped[0].AddressNames);
            Assert.AreSequenceEqual(["é schema", "first"], dropped[0].AddressNames!);
            Assert.IsNotNull(dropped[0].AddressArguments);
            Assert.IsEmpty(dropped[0].AddressArguments!);
            Assert.AreEqual(8101U, dropped[1].ObjectId);
            Assert.IsNull(dropped[1].AddressNames);
            Assert.IsNull(dropped[1].AddressArguments);
        }
    }

    /// <summary>
    /// Empty collection helpers preserve empty results, while a rewrite helper requires exactly one materialized row.
    /// </summary>
    [TestMethod]
    [DataRow("ddl_command_end", 0)]
    [DataRow("sql_drop", 1)]
    [DataRow("table_rewrite", 2)]
    public void HelpersDistinguishEmptyCollectionsFromMissingRewriteMetadata(string eventName, int helper)
    {
        using var backend = new BackendScope();
        Respond(new SpiResult([], [], 0));
        ResetCounts();
        PgEventTriggerContext context = Enter(eventName);
        try
        {
            if (helper == 2)
            {
                Assert.ThrowsExactly<InvalidOperationException>(context.GetTableRewrite);
            }
            else
            {
                IList list = Assert.IsInstanceOfType<IList>(InvokeHelper(context, helper));
                Assert.IsEmpty(list);
            }

            Assert.AreEqual(1, s_releaseCount);
        }
        finally
        {
            NativeEventTrigger.Exit(context);
            Respond(null);
        }
    }

    /// <summary>
    /// A rewrite helper copies its two scalars before native release and rejects an unexpected second row.
    /// </summary>
    [TestMethod]
    public void RewriteHelperOwnsItsSingleResultAndRejectsExtraRows()
    {
        using var backend = new BackendScope();
        var row = new SpiRow([8100U, 5], [new("table_oid", 26), new("reason", 23)]);
        Respond(new SpiResult(ResultColumns(row), [row], 1));
        ResetCounts();
        PgEventTriggerContext context = Enter("table_rewrite", "ALTER TABLE");
        PgTableRewrite rewrite;
        try
        {
            rewrite = context.GetTableRewrite();
            Assert.AreEqual(1, s_releaseCount);
            Respond(new SpiResult(ResultColumns(row), [row, row], 2));
            Assert.ThrowsExactly<InvalidOperationException>(context.GetTableRewrite);
            Assert.AreEqual(2, s_releaseCount);
        }
        finally
        {
            NativeEventTrigger.Exit(context);
            Respond(null);
        }

        Assert.AreEqual(8100U, rewrite.TableOid);
        Assert.AreEqual(PgTableRewriteReason.AlterPersistence | PgTableRewriteReason.ColumnRewrite, rewrite.Reason);
    }

    /// <summary>
    /// Nested scope exit restores its parent, releases the retained parent link, and refuses out-of-order or repeated exit.
    /// </summary>
    [TestMethod]
    public void NestedScopesRestoreParentsAndReleaseTheirLinks()
    {
        using var backend = new BackendScope();
        PgEventTriggerContext parent = Enter("ddl_command_end");
        try
        {
            PgEventTriggerContext child = Enter("sql_drop", "DROP TABLE");
            try
            {
                Assert.AreSame(parent, child.Parent);
                Assert.ThrowsExactly<InvalidOperationException>(parent.GetDdlCommands);
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeEventTrigger.Exit(parent));
                Assert.AreSame(parent, child.Parent);
                Assert.ThrowsExactly<PgException>(child.GetDroppedObjects);
            }
            finally
            {
                NativeEventTrigger.Exit(child);
            }

            Assert.IsNull(child.Parent);
            Assert.AreEqual("sql_drop", child.Event);
            Assert.ThrowsExactly<InvalidOperationException>(child.GetDroppedObjects);
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeEventTrigger.Exit(child));
            Assert.ThrowsExactly<PgException>(parent.GetDdlCommands);
            Assert.ThrowsExactly<InvalidOperationException>(() => Enter("unknown"));
            Assert.ThrowsExactly<PgException>(parent.GetDdlCommands);
        }
        finally
        {
            NativeEventTrigger.Exit(parent);
        }

        Assert.IsNull(parent.Parent);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeEventTrigger.Exit(parent));
        PgEventTriggerContext subsequent = Enter("login", "LOGIN");
        try
        {
            Assert.IsNull(subsequent.Parent);
            Assert.AreEqual(PgEventTriggerKind.Login, subsequent.Kind);
        }
        finally
        {
            NativeEventTrigger.Exit(subsequent);
        }
    }

    /// <summary>
    /// A child handler's managed exception unwinds its context in finally and restores the parent's metadata helper access.
    /// </summary>
    [TestMethod]
    public void ExceptionalChildExitRestoresParentScope()
    {
        using var backend = new BackendScope();
        PgEventTriggerContext parent = Enter("ddl_command_end");
        PgEventTriggerContext? child = null;
        try
        {
            Assert.ThrowsExactly<ArgumentException>(() => ThrowFromChild(out child));
            Assert.IsNotNull(child);
            Assert.IsNull(child.Parent);
            Assert.ThrowsExactly<PgException>(parent.GetDdlCommands);
        }
        finally
        {
            NativeEventTrigger.Exit(parent);
        }
    }

    /// <summary>
    /// Worker threads can read copied metadata but cannot query or exit another thread's context, even with a backend binding.
    /// </summary>
    [TestMethod]
    public void WorkerThreadCannotUseOrExitTheOwningContext()
    {
        using var backend = new BackendScope();
        PgEventTriggerContext context = Enter("ddl_command_end", "CREATE TABLE é");
        try
        {
            DedicatedThread.Run(() =>
            {
                using var workerBackend = new BackendScope();
                ResetCounts();
                Assert.AreEqual("ddl_command_end", context.Event);
                Assert.AreEqual("CREATE TABLE é", context.CommandTag);
                Assert.AreEqual(PgEventTriggerKind.DdlCommandEnd, context.Kind);
                Assert.ThrowsExactly<InvalidOperationException>(() => context.GetDdlCommands());
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeEventTrigger.Exit(context));
                Assert.AreEqual(0, s_queryCount);
                PgEventTriggerContext own = Enter("sql_drop");
                try
                {
                    Assert.IsNull(own.Parent);
                    Assert.ThrowsExactly<PgException>(() => own.GetDroppedObjects());
                    Assert.AreEqual(1, s_queryCount);
                }
                finally
                {
                    NativeEventTrigger.Exit(own);
                }
            });

            Assert.ThrowsExactly<PgException>(context.GetDdlCommands);
        }
        finally
        {
            NativeEventTrigger.Exit(context);
        }
    }

    /// <summary>
    /// DDL snapshots pin every projected field and remain detached from later SPI row replacements.
    /// </summary>
    [TestMethod]
    public void DdlCommandsOwnExactAddressAndDescriptionMetadata()
    {
        SpiRow row = DdlRow([uint.MaxValue, 8100U, 3, "ALTER TABLE", "table column", "é space", "\"é space\".table.column", true]);
        var command = new PgDdlCommand(row);
        row.Set(0, 1U);
        row.Set(3, "changed");
        row.Set(5, "changed");
        row.Set(7, false);
        Assert.AreEqual(uint.MaxValue, command.ClassId);
        Assert.AreEqual(8100U, command.ObjectId);
        Assert.AreEqual(3, command.ObjectSubId);
        Assert.AreEqual("ALTER TABLE", command.CommandTag);
        Assert.AreEqual("table column", command.ObjectType);
        Assert.AreEqual("é space", command.SchemaName);
        Assert.AreEqual("\"é space\".table.column", command.ObjectIdentity);
        Assert.IsTrue(command.InExtension);
    }

    /// <summary>
    /// GRANT and default-privilege commands preserve missing addresses as null instead of zero or empty strings.
    /// </summary>
    [TestMethod]
    [DataRow("GRANT")]
    [DataRow("REVOKE")]
    [DataRow("ALTER DEFAULT PRIVILEGES")]
    public void DdlCommandsPreserveNullableObjectAddresses(string tag)
    {
        var command = new PgDdlCommand(DdlRow([null, null, null, tag, "TABLE", null, null, false]));
        Assert.IsNull(command.ClassId);
        Assert.IsNull(command.ObjectId);
        Assert.IsNull(command.ObjectSubId);
        Assert.AreEqual(tag, command.CommandTag);
        Assert.AreEqual("TABLE", command.ObjectType);
        Assert.IsNull(command.SchemaName);
        Assert.IsNull(command.ObjectIdentity);
        Assert.IsFalse(command.InExtension);
    }

    /// <summary>
    /// A dropped-object snapshot owns every scalar and vector, including temporary aliases and immutable empty argument lists.
    /// </summary>
    [TestMethod]
    public void DroppedObjectsCopyAddressesAndKeepTemporaryMetadata()
    {
        string[] names = ["pg_temp", "é table"];
        string[] arguments = ["integer", "é schema.type"];
        SpiRow row = DroppedRow([1259U, uint.MaxValue, 2, true, false, true, "table column", "pg_temp", "é table",
            "pg_temp.\"é table\".column", names, arguments]);
        var dropped = new PgDroppedObject(row);
        names[0] = "changed";
        arguments[1] = "changed";
        row.Set(6, "changed");
        Assert.AreEqual(1259U, dropped.ClassId);
        Assert.AreEqual(uint.MaxValue, dropped.ObjectId);
        Assert.AreEqual(2, dropped.ObjectSubId);
        Assert.IsTrue(dropped.Original);
        Assert.IsFalse(dropped.Normal);
        Assert.IsTrue(dropped.IsTemporary);
        Assert.AreEqual("table column", dropped.ObjectType);
        Assert.AreEqual("pg_temp", dropped.SchemaName);
        Assert.AreEqual("é table", dropped.ObjectName);
        Assert.AreEqual("pg_temp.\"é table\".column", dropped.ObjectIdentity);
        Assert.IsNotNull(dropped.AddressNames);
        Assert.IsNotNull(dropped.AddressArguments);
        Assert.AreSequenceEqual(["pg_temp", "é table"], dropped.AddressNames);
        Assert.AreSequenceEqual(["integer", "é schema.type"], dropped.AddressArguments);
        IList<string> immutableNames = Assert.IsInstanceOfType<IList<string>>(dropped.AddressNames);
        IList<string> immutableArguments = Assert.IsInstanceOfType<IList<string>>(dropped.AddressArguments);
        Assert.ThrowsExactly<NotSupportedException>(() => immutableNames[0] = "replacement");
        Assert.ThrowsExactly<NotSupportedException>(() => immutableArguments[0] = "replacement");
        Assert.AreEqual("pg_temp", dropped.AddressNames[0]);
        Assert.AreEqual("integer", dropped.AddressArguments[0]);
    }

    /// <summary>
    /// Missing identity and address vectors remain distinct from empty vectors, including for temporary objects.
    /// </summary>
    [TestMethod]
    public void DroppedObjectsDistinguishNullFromEmptyAddresses()
    {
        var missing = new PgDroppedObject(DroppedRow([1259U, 8100U, 0, false, true, false, "table", null, null, null, null, null]));
        Assert.IsNull(missing.SchemaName);
        Assert.IsNull(missing.ObjectName);
        Assert.IsNull(missing.ObjectIdentity);
        Assert.IsNull(missing.AddressNames);
        Assert.IsNull(missing.AddressArguments);
        Assert.IsFalse(missing.Original);
        Assert.IsTrue(missing.Normal);
        Assert.IsFalse(missing.IsTemporary);
        string[] temporaryNames = ["pg_temp", "table"];
        var temporary = new PgDroppedObject(DroppedRow([1259U, 8101U, 0, true, false, true, "table", "pg_temp", "table",
            "pg_temp.table", temporaryNames, Array.Empty<string>()]));
        Assert.IsTrue(temporary.IsTemporary);
        Assert.IsNotNull(temporary.AddressNames);
        Assert.AreSequenceEqual(["pg_temp", "table"], temporary.AddressNames);
        Assert.IsNotNull(temporary.AddressArguments);
        Assert.IsEmpty(temporary.AddressArguments);
        var empty = new PgDroppedObject(DroppedRow([1259U, 8102U, 0, false, false, false, "table", null, "", "",
            Array.Empty<string>(), Array.Empty<string>()]));
        Assert.AreEqual("", empty.ObjectName);
        Assert.AreEqual("", empty.ObjectIdentity);
        Assert.IsNotNull(empty.AddressNames);
        Assert.IsEmpty(empty.AddressNames);
        Assert.IsNotNull(empty.AddressArguments);
        Assert.IsEmpty(empty.AddressArguments);
    }

    /// <summary>
    /// Address vectors reject SQL NULL elements and shapes that cannot be copied to ordinary ordered names without losing information.
    /// </summary>
    [TestMethod]
    [DataRow(10, "null-element")]
    [DataRow(11, "null-element")]
    [DataRow(10, "rank")]
    [DataRow(11, "lower-bound")]
    public void DroppedAddressesRejectNullElementsAndLossyShapes(int slot, string defect)
    {
        object?[] values = [1259U, 8100U, 0, true, false, false, "table", null, null, null, null, null];
        values[slot] = defect switch
        {
            "null-element" => new string?[] { null },
            "rank" => new PgArray<string>(["name"], [1, 1]),
            "lower-bound" => new PgArray<string>(["name"], [1], [0]),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => new PgDroppedObject(DroppedRow(values)));
    }

    /// <summary>
    /// All defined rewrite bits, combinations, and future positive bits survive the owned snapshot mapping.
    /// </summary>
    [TestMethod]
    [DataRow(0, PgTableRewriteReason.None)]
    [DataRow(1, PgTableRewriteReason.AlterPersistence)]
    [DataRow(2, PgTableRewriteReason.DefaultValue)]
    [DataRow(4, PgTableRewriteReason.ColumnRewrite)]
    [DataRow(8, PgTableRewriteReason.AccessMethod)]
    [DataRow(15, PgTableRewriteReason.AlterPersistence | PgTableRewriteReason.DefaultValue | PgTableRewriteReason.ColumnRewrite | PgTableRewriteReason.AccessMethod)]
    [DataRow(17, (PgTableRewriteReason)17)]
    [DataRow(int.MaxValue, (PgTableRewriteReason)int.MaxValue)]
    public void RewriteReasonsPreserveKnownAndFutureBits(int bits, PgTableRewriteReason expected)
    {
        var row = new SpiRow([uint.MaxValue, bits], [new("table_oid", 26), new("reason", 23)]);
        var rewrite = new PgTableRewrite(row);
        row.Set(0, 1U);
        row.Set(1, -1);
        Assert.AreEqual(uint.MaxValue, rewrite.TableOid);
        Assert.AreEqual(expected, rewrite.Reason);
    }

    /// <summary>
    /// Invalid rewrite identities and negative reason masks cannot masquerade as valid backend metadata.
    /// </summary>
    [TestMethod]
    [DataRow(0U, 1)]
    [DataRow(8100U, -1)]
    public void RewriteMetadataRejectsInvalidValues(uint oid, int reason)
    {
        var row = new SpiRow([oid, reason], [new("table_oid", 26), new("reason", 23)]);
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgTableRewrite(row));
    }

    /// <summary>
    /// Required snapshot fields reject SQL NULL instead of exposing invalid nonnullable public properties.
    /// </summary>
    [TestMethod]
    [DataRow("ddl", 3)]
    [DataRow("ddl", 4)]
    [DataRow("ddl", 7)]
    [DataRow("dropped", 0)]
    [DataRow("dropped", 1)]
    [DataRow("dropped", 2)]
    [DataRow("dropped", 3)]
    [DataRow("dropped", 4)]
    [DataRow("dropped", 5)]
    [DataRow("dropped", 6)]
    [DataRow("rewrite", 0)]
    [DataRow("rewrite", 1)]
    public void RequiredSnapshotFieldsRejectNull(string kind, int ordinal)
    {
        object?[] values = kind switch
        {
            "ddl" => [1259U, 8100U, 0, "CREATE TABLE", "table", null, null, false],
            "dropped" => [1259U, 8100U, 0, true, false, false, "table", null, null, null, null, null],
            _ => [8100U, 1],
        };

        values[ordinal] = null;
        Assert.ThrowsExactly<InvalidOperationException>(() => kind switch
        {
            "ddl" => new PgDdlCommand(DdlRow(values)),
            "dropped" => new PgDroppedObject(DroppedRow(values)),
            _ => new PgTableRewrite(new SpiRow(values, [new("table_oid", 26), new("reason", 23)])),
        });
    }

    /// <summary>
    /// Creates a context and immediately releases both borrowed text buffers to expose accidental lifetime coupling.
    /// </summary>
    private static PgEventTriggerContext Enter(string eventName, string commandTag = "CREATE TABLE")
    {
        NativeValue[] values = [NativeValue.FromString(eventName), NativeValue.FromString(commandTag)];
        try
        {
            return NativeEventTrigger.Enter(values);
        }
        finally
        {
            Release(values);
        }
    }

    /// <summary>
    /// Simulates the generated callback's finally boundary when a child handler throws.
    /// </summary>
    private static void ThrowFromChild(out PgEventTriggerContext? child)
    {
        child = Enter("sql_drop");
        try
        {
            throw new ArgumentException("child callback failed");
        }
        finally
        {
            NativeEventTrigger.Exit(child);
        }
    }

    /// <summary>
    /// Releases the exact native buffers owned by a metadata fixture.
    /// </summary>
    private static void Release(NativeValue[] values)
    {
        for (int index = 0; index < values.Length; index++)
        {
            values[index].Release();
        }
    }

    /// <summary>
    /// Calls one public event metadata helper without changing its phase or ownership requirements.
    /// </summary>
    private static object InvokeHelper(PgEventTriggerContext context, int helper) => helper switch
    {
        0 => context.GetDdlCommands(),
        1 => context.GetDroppedObjects(),
        2 => context.GetTableRewrite(),
        _ => throw new ArgumentOutOfRangeException(nameof(helper)),
    };

    /// <summary>
    /// Creates an independent DDL projection with the same PostgreSQL column identities as the public helper.
    /// </summary>
    private static SpiRow DdlRow(object?[] values) => new(values,
        [new("classid", 26), new("objid", 26), new("objsubid", 23), new("command_tag", 25),
            new("object_type", 25), new("schema_name", 25), new("object_identity", 25), new("in_extension", 16)]);

    /// <summary>
    /// Creates an independent dropped-object projection with nullable text and text-array metadata columns.
    /// </summary>
    private static SpiRow DroppedRow(object?[] values) => new(values,
        [new("classid", 26), new("objid", 26), new("objsubid", 23), new("original", 16), new("normal", 16),
            new("is_temporary", 16), new("object_type", 25), new("schema_name", 25), new("object_name", 25),
            new("object_identity", 25), new("address_names", 1009), new("address_args", 1009)]);

    /// <summary>
    /// Copies projected column identities for the ordinal-based native result fixture.
    /// </summary>
    private static SpiColumn[] ResultColumns(SpiRow row)
        => [.. Enumerable.Range(0, row.Count).Select(index => new SpiColumn("value", row.GetTypeOid(index)))];

    /// <summary>
    /// Captures a guarded metadata query and returns owned sentinel diagnostics so tests distinguish dispatch from rejection.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int CaptureQuery(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
    {
        s_queryCount++;
        s_command = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(request->_command, request->_commandLength));
        if (s_result is not null)
        {
            try
            {
                PopulateResult(s_result, result);
                return 0;
            }
            // codeql[cs/catch-of-all-exceptions] This stands in for native code, so every exception goes back as an error instead of unwinding through it.
            catch (Exception exception)
            {
                NativeError.Write(exception, error);
                return 1;
            }
        }

        NativeError.Write(new PgException("P0001", "metadata query dispatched"), error);
        return 1;
    }

    /// <summary>
    /// Allocates a controlled native SPI projection through the same value encoders used at the guarded boundary.
    /// </summary>
    private static unsafe void PopulateResult(SpiResult source, NativeSpiResult* result)
    {
        result->_release = &ReleaseResult;
        result->_columnCount = source.Columns.Count;
        result->_rowCount = source.Count;
        result->_rowsAffected = source.RowsAffected;
        result->_columns = (NativeSpiColumn*)NativeMemory.AllocZeroed((nuint)source.Columns.Count, (nuint)sizeof(NativeSpiColumn));
        result->_values = (NativeValue*)NativeMemory.AllocZeroed((nuint)(source.Count * source.Columns.Count), (nuint)sizeof(NativeValue));
        for (int column = 0; column < source.Columns.Count; column++)
        {
            result->_columns[column]._name = NativeValue.FromString(source.Columns[column].Name);
            result->_columns[column]._typeOid = source.Columns[column].TypeOid;
            result->_columns[column]._baseTypeOid = source.Columns[column].TypeOid;
        }

        for (int row = 0; row < source.Count; row++)
        {
            for (int column = 0; column < source.Columns.Count; column++)
            {
                result->_values[row * source.Columns.Count + column] = SpiType.ToNative(source[row].Get<object?>(column));
            }
        }
    }

    /// <summary>
    /// Releases every result allocation and records that helper snapshots have crossed the native lifetime boundary.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ReleaseResult(NativeSpiResult* result)
    {
        s_releaseCount++;
        if (result->_columns != null)
        {
            for (int column = 0; column < result->_columnCount; column++)
            {
                result->_columns[column]._name.Release();
            }

            NativeMemory.Free(result->_columns);
        }

        if (result->_values != null)
        {
            for (int index = 0; index < result->_rowCount * result->_columnCount; index++)
            {
                result->_values[index].Release();
            }

            NativeMemory.Free(result->_values);
        }

        *result = default;
    }

    /// <summary>
    /// Accesses the first discriminator to construct malformed native text headers.
    /// </summary>
    private static ref int Auxiliary1(ref NativeValue value) => ref value._auxiliary1;

    /// <summary>
    /// Accesses the second discriminator to construct malformed native text headers.
    /// </summary>
    private static ref int Auxiliary2(ref NativeValue value) => ref value._auxiliary2;

    /// <summary>
    /// Accesses the temporal discriminator to construct malformed native text headers.
    /// </summary>
    private static ref int Infinity(ref NativeValue value) => ref value._temporalInfinity;

    /// <summary>
    /// Accesses the byte length to construct malformed native text headers without dereferencing them.
    /// </summary>
    private static ref int Length(ref NativeValue value) => ref value._length;

    /// <summary>
    /// Binds the controlled native callback on this thread and restores any enclosing binding after each test scope.
    /// </summary>
    private sealed unsafe class BackendScope : IDisposable
    {
        private readonly nint _previous = NativeBackend.Enter((nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&CaptureQuery);

        /// <summary>
        /// Restores the preceding backend callback binding.
        /// </summary>
        public void Dispose() => NativeBackend.Exit(_previous);
    }
}
