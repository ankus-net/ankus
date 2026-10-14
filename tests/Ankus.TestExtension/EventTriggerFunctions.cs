[assembly: PgSql("event-support", """
    CREATE SCHEMA event_values;
    CREATE TABLE event_values.audit(
        position bigint GENERATED ALWAYS AS IDENTITY, event text, kind text, tag text, phase text, depth integer,
        class_id oid, object_id oid, sub_id integer, command_tag text, object_type text, schema_name text,
        identity text, in_extension boolean, original boolean, normal boolean, is_temporary boolean,
        object_name text, address_names text[], address_arguments text[], rewrite_oid oid, rewrite_reason integer, detail text);
    """, Requires = ["sql-first"])]

namespace Ankus.TestExtension;

/// <summary>
/// Makes event metadata, snapshots, guarded errors, and nested callback scope observable in PostgreSQL.
/// </summary>
[PgSchema("event_values", Create = false)]
public static class EventTriggerFunctions
{
    private static readonly List<string> s_calls = [];
    private static PgEventTriggerContext? s_retainedContext;
    private static PgEventTriggerContext? s_parent;
    private static IReadOnlyList<PgDdlCommand>? s_commands;
    private static IReadOnlyList<PgDroppedObject>? s_dropped;
    private static PgTableRewrite? s_rewrite;
    private static IReadOnlyList<PgDdlCommand>? s_retainedCommands;
    private static IReadOnlyList<PgDroppedObject>? s_retainedDropped;
    private static PgTableRewrite? s_retainedRewrite;
    private static uint s_eventEnumOid;
    private static int s_depth;

    /// <summary>
    /// Records an event and applies the action selected by the session's custom test setting.
    /// </summary>
    /// <param name="context">The owned current event metadata.</param>
    [PgEventTrigger]
    [PgFunction(Requires = ["event-support"])]
    public static void EventAction(PgEventTriggerContext context)
    {
        s_depth++;
        s_calls.Add(context.Event + ":" + context.CommandTag);
        try
        {
            string mode = Spi.ExecuteScalar<string?>("SELECT current_setting('ankus.event_mode',true)") ?? "audit";
            Audit(context, "callback", Spi.ExecuteScalar<bool>("SELECT to_regclass('event_values.subject') IS NOT NULL").ToString());
            Capture(context, "snapshot");
            if (mode == context.Event + "_error" || mode == "error")
            {
                throw new PgException("P7701", "event rejected command", "owned event detail", "retry valid DDL");
            }

            switch (mode)
            {
                case "managed_error":
                    throw new InvalidOperationException("managed event failure");
                case "wait":
                    Spi.Execute("SELECT pg_sleep(30)");
                    break;
                case "retain":
                    s_retainedContext = context;
                    s_retainedCommands = s_commands;
                    s_retainedDropped = s_dropped;
                    s_retainedRewrite = s_rewrite;
                    break;
                case "recover":
                    try
                    {
                        Spi.Execute("SELECT 1/0");
                        Audit(context, "recovery", "unexpected success");
                    }
                    catch (PgException exception)
                    {
                        Audit(context, "recovery", exception.SqlState + ":" + Spi.ExecuteScalar<int>("SELECT 42"));
                    }

                    break;
                case "wrong_kind":
                    Audit(context, "guards", WrongKind(context));
                    break;
                case "worker":
                    string outcome = Task.Run(() => GuardedSnapshot(context)).GetAwaiter().GetResult();
                    Audit(context, "guards", outcome);
                    Capture(context, "after worker");
                    break;
                case "repeat":
                    Spi.Connect(session => session.Query("SELECT repeat('overwrite',100000)"));
                    Capture(context, "repeated");
                    break;
                case "owners":
                    ObserveOwners(context);
                    break;
                case "enum_scope":
                case "enum_scope_error":
                    s_eventEnumOid = PgEnums.GetTypeOid<EnumMood>();
                    if (mode == "enum_scope_error")
                    {
                        throw new PgException("P7713", "event enum scope failure");
                    }

                    break;
                case "nested":
                case "nested_error":
                    if (s_depth == 1)
                    {
                        PgEventTriggerContext? previous = s_parent;
                        s_parent = context;
                        try
                        {
                            Spi.Execute("CREATE TABLE event_values.inner_table(id integer)");
                        }
                        catch (PgException exception)
                        {
                            Audit(context, "nested error", exception.SqlState);
                        }
                        finally
                        {
                            s_parent = previous;
                        }

                        Capture(context, "restored");
                    }
                    else
                    {
                        Audit(context, "parent guard", GuardedSnapshot(s_parent!));
                        if (mode == "nested_error")
                        {
                            throw new PgException("P7711", "inner event failure");
                        }
                    }

                    break;
                case "row_dml":
                    PgEventTriggerContext? parent = s_parent;
                    s_parent = context;
                    try
                    {
                        Spi.Execute("INSERT INTO event_values.rows VALUES(7)");
                    }
                    finally
                    {
                        s_parent = parent;
                    }

                    Capture(context, "after row");
                    break;
                case "row_isolation":
                case "row_isolation_error":
                    try
                    {
                        Spi.ExecuteScalar<long>("SELECT count(*) FROM parent_rows");
                        Audit(context, "row scope", "unexpected inherited transition");
                    }
                    catch (PgException exception)
                    {
                        Audit(context, "row scope", exception.SqlState + ":" + Spi.ExecuteScalar<int>("SELECT 42"));
                    }

                    if (mode == "row_isolation_error")
                    {
                        throw new PgException("P7712", "nested event scope failure");
                    }

                    break;
                case "emoji":
                    Audit(context, "encoding", "😀");
                    break;
            }
        }
        finally
        {
            s_depth--;
        }
    }

    /// <summary>
    /// Records a separately identifiable handler for PostgreSQL's alphabetical event-trigger ordering.
    /// </summary>
    /// <param name="context">The current event.</param>
    [PgEventTrigger]
    [PgFunction(Requires = ["event-support"])]
    public static void EventFirst(PgEventTriggerContext context)
        => Audit(context, "first", Spi.ExecuteScalar<long>("SELECT count(*) FROM event_values.audit WHERE phase='second'").ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Observes effects from the earlier event handler.
    /// </summary>
    /// <param name="context">The current event.</param>
    [PgEventTrigger]
    [PgFunction(Requires = ["event-support"])]
    public static void EventSecond(PgEventTriggerContext context)
        => Audit(context, "second", Spi.ExecuteScalar<long>("SELECT count(*) FROM event_values.audit WHERE phase='first'").ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Exercises a row callback nested inside an event handler's SPI statement.
    /// </summary>
    /// <param name="context">The row trigger context.</param>
    /// <returns>The incoming NEW row.</returns>
    [PgTrigger]
    [PgFunction(Requires = ["event-support"])]
    public static PgHeapTuple? EventAuditRow(PgTriggerContext context)
    {
        PgEventTriggerContext parent = s_parent ?? throw new InvalidOperationException("Missing parent event.");
        Audit(parent, "row", context.Operation + ":" + context.New!.Get<int>("id") + ":" + parent.GetDdlCommands().Count);
        return context.New;
    }

    /// <summary>
    /// Enters DDL event callbacks from a row trigger while retaining the original transition relation.
    /// </summary>
    /// <param name="context">The statement trigger with its transition alias.</param>
    /// <returns>Null because AFTER ignores the return value.</returns>
    [PgTrigger]
    [PgFunction(Requires = ["event-support"])]
    public static PgHeapTuple? EventRowsWithDdl(PgTriggerContext context)
    {
        string query = "SELECT count(*) FROM " + Spi.QuoteIdentifier(context.NewTransitionTableName!);
        long before = Spi.ExecuteScalar<long>(query);
        string outcome = "ok";
        try
        {
            Spi.Execute("CREATE TABLE event_values.from_row(id integer)");
        }
        catch (PgException exception)
        {
            outcome = exception.SqlState;
        }

        long after = Spi.ExecuteScalar<long>(query);
        Spi.Execute("INSERT INTO event_values.audit(phase,detail) VALUES('outer row',$1)",
            SpiParameter.Create(before + ":" + after + ":" + outcome));
        return null;
    }

    /// <summary>
    /// Clears backend-local observations before a potentially rolled-back DDL operation.
    /// </summary>
    [PgFunction(Requires = ["event-support"])]
    public static void EventReset()
    {
        s_calls.Clear();
        s_retainedContext = null;
        s_commands = null;
        s_dropped = null;
        s_rewrite = null;
        s_retainedCommands = null;
        s_retainedDropped = null;
        s_retainedRewrite = null;
    }

    /// <summary>
    /// Returns callback attempts even when their transactional SQL effects were rolled back.
    /// </summary>
    /// <returns>The ordered event and command-tag pairs.</returns>
    [PgFunction(Requires = ["event-support"])]
    public static string EventCalls() => string.Join('|', s_calls);

    /// <summary>
    /// Resolves an unqualified enum before, inside, and after a nested event callback.
    /// </summary>
    /// <param name="fail">Whether the event callback should fail after resolving its enum.</param>
    /// <returns>The three type identities and the guarded DDL outcome.</returns>
    [PgFunction(Requires = ["event-support", "enum.mood"])]
    public static string EventEnumScope(bool fail)
    {
        Spi.Execute("SELECT set_config('ankus.event_mode',$1,true)", SpiParameter.Create(fail ? "enum_scope_error" : "enum_scope"));
        s_eventEnumOid = 0;
        uint before = PgEnums.GetTypeOid<EnumMood>();
        string outcome = "ok";
        try
        {
            Spi.Execute("CREATE TABLE event_values.enum_scope_target(id integer)");
        }
        catch (PgException exception)
        {
            outcome = exception.SqlState;
        }

        uint after = PgEnums.GetTypeOid<EnumMood>();
        return $"{before}:{s_eventEnumOid}:{after}:{outcome}";
    }

    /// <summary>
    /// Reads retained snapshots after callback exit and verifies that fresh helper access is rejected.
    /// </summary>
    /// <returns>The original owned values and stale-access diagnostic.</returns>
    [PgFunction(Requires = ["event-support"])]
    public static string EventRetained()
    {
        PgEventTriggerContext context = s_retainedContext ?? throw new InvalidOperationException("No retained event context.");
        Spi.Query("SELECT repeat('overwrite',100000)");
        GarbageCollection.Collect();
        string snapshot = context.Kind switch
        {
            PgEventTriggerKind.DdlCommandEnd => string.Join('|', s_retainedCommands!.Select(static command =>
                command.CommandTag + ":" + command.ObjectType + ":" + command.SchemaName + ":" + command.ObjectIdentity)),
            PgEventTriggerKind.SqlDrop => string.Join('|', s_retainedDropped!.Where(static item => item.Original).Select(static item =>
                item.ObjectType + ":" + item.ObjectIdentity + ":" + string.Join(',', item.AddressNames!) + ":" + string.Join(',', item.AddressArguments!))),
            PgEventTriggerKind.TableRewrite => s_retainedRewrite!.TableOid + ":" + (int)s_retainedRewrite.Reason,
            _ => "",
        };
        return context.Event + ":" + context.CommandTag + ":" + GuardedSnapshot(context) + ":" + snapshot;
    }

    private static string GuardedSnapshot(PgEventTriggerContext context)
    {
        try
        {
            switch (context.Kind)
            {
                case PgEventTriggerKind.DdlCommandEnd:
                    context.GetDdlCommands();
                    break;
                case PgEventTriggerKind.SqlDrop:
                    context.GetDroppedObjects();
                    break;
                case PgEventTriggerKind.TableRewrite:
                    context.GetTableRewrite();
                    break;
                default:
                    context.GetDdlCommands();
                    break;
            }

            return "unexpected success";
        }
        catch (InvalidOperationException)
        {
            return "InvalidOperationException";
        }
    }

    private static string WrongKind(PgEventTriggerContext context)
    {
        var outcomes = new List<string>();
        if (context.Kind != PgEventTriggerKind.DdlCommandEnd)
        {
            try
            {
                context.GetDdlCommands();
                outcomes.Add("unexpected ddl");
            }
            catch (InvalidOperationException)
            {
                outcomes.Add("ddl protected");
            }
        }

        if (context.Kind != PgEventTriggerKind.SqlDrop)
        {
            try
            {
                context.GetDroppedObjects();
                outcomes.Add("unexpected drop");
            }
            catch (InvalidOperationException)
            {
                outcomes.Add("drop protected");
            }
        }

        if (context.Kind != PgEventTriggerKind.TableRewrite)
        {
            try
            {
                context.GetTableRewrite();
                outcomes.Add("unexpected rewrite");
            }
            catch (InvalidOperationException)
            {
                outcomes.Add("rewrite protected");
            }
        }

        return string.Join('|', outcomes);
    }

    private static void ObserveOwners(PgEventTriggerContext context)
    {
        string function = context.Kind == PgEventTriggerKind.DdlCommandEnd ? "pg_event_trigger_ddl_commands" : "pg_event_trigger_dropped_objects";
        string sql = "SELECT object_identity FROM pg_catalog." + function + "() ORDER BY object_identity";
        SpiResult sessionRows = Spi.Connect(session =>
        {
            SpiResult first = session.Query(sql);
            Audit(context, "owner session repeat", IdentityText(session.Query(sql)));
            return first;
        });
        Spi.Query("SELECT repeat('overwrite',100000)");
        Audit(context, "owner session", IdentityText(sessionRows));
        using (SpiPreparedStatement plan = Spi.Connect(session => session.Prepare(sql).Keep()))
        {
            Audit(context, "owner kept plan", IdentityText(plan.Query()));
        }

        SpiCursor cursor;
        using (SpiPreparedStatement plan = Spi.Prepare(sql))
        {
            cursor = plan.OpenCursor();
        }

        using (cursor)
        {
            SpiResult first = cursor.Fetch(1);
            Spi.Query("SELECT repeat('churn',100000)");
            SpiResult second = cursor.Fetch(100);
            string result = IdentityText(first);
            if (second.Count != 0)
            {
                result += "|" + IdentityText(second);
            }

            Audit(context, "owner disposed plan cursor", result);
        }
    }

    private static string IdentityText(SpiResult result)
        => string.Join('|', result.Select(static row => row.Get<string?>(0) ?? "<NULL>"));

    private static void Capture(PgEventTriggerContext context, string phase)
    {
        switch (context.Kind)
        {
            case PgEventTriggerKind.DdlCommandEnd:
                s_commands = context.GetDdlCommands();
                foreach (PgDdlCommand command in s_commands)
                {
                    Spi.Execute("""
                        INSERT INTO event_values.audit(event,kind,tag,phase,depth,class_id,object_id,sub_id,command_tag,
                            object_type,schema_name,identity,in_extension) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
                        """, SpiParameter.Create(context.Event), SpiParameter.Create(context.Kind.ToString()), SpiParameter.Create(context.CommandTag),
                        SpiParameter.Create(phase), SpiParameter.Create(s_depth), SpiParameter.Create(command.ClassId),
                        SpiParameter.Create(command.ObjectId), SpiParameter.Create(command.ObjectSubId), SpiParameter.Create(command.CommandTag),
                        SpiParameter.Create(command.ObjectType), SpiParameter.Create(command.SchemaName), SpiParameter.Create(command.ObjectIdentity),
                        SpiParameter.Create(command.InExtension));
                }

                Audit(context, phase + " count", s_commands.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case PgEventTriggerKind.SqlDrop:
                s_dropped = context.GetDroppedObjects();
                foreach (PgDroppedObject item in s_dropped)
                {
                    Spi.Execute("""
                        INSERT INTO event_values.audit(event,kind,tag,phase,depth,class_id,object_id,sub_id,object_type,schema_name,
                            identity,original,normal,is_temporary,object_name,address_names,address_arguments)
                        VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17)
                        """, SpiParameter.Create(context.Event), SpiParameter.Create(context.Kind.ToString()), SpiParameter.Create(context.CommandTag),
                        SpiParameter.Create(phase), SpiParameter.Create(s_depth), SpiParameter.Create(item.ClassId), SpiParameter.Create(item.ObjectId),
                        SpiParameter.Create(item.ObjectSubId), SpiParameter.Create(item.ObjectType), SpiParameter.Create(item.SchemaName),
                        SpiParameter.Create(item.ObjectIdentity), SpiParameter.Create(item.Original), SpiParameter.Create(item.Normal),
                        SpiParameter.Create(item.IsTemporary), SpiParameter.Create(item.ObjectName),
                        SpiParameter.Create(item.AddressNames?.ToArray()), SpiParameter.Create(item.AddressArguments?.ToArray()));
                }

                break;
            case PgEventTriggerKind.TableRewrite:
                s_rewrite = context.GetTableRewrite();
                Spi.Execute("""
                    INSERT INTO event_values.audit(event,kind,tag,phase,depth,rewrite_oid,rewrite_reason)
                    VALUES($1,$2,$3,$4,$5,$6,$7)
                    """, SpiParameter.Create(context.Event), SpiParameter.Create(context.Kind.ToString()), SpiParameter.Create(context.CommandTag),
                    SpiParameter.Create(phase), SpiParameter.Create(s_depth), SpiParameter.Create(s_rewrite.TableOid), SpiParameter.Create((int)s_rewrite.Reason));
                break;
        }
    }

    private static void Audit(PgEventTriggerContext context, string phase, string? detail)
        => Spi.Execute("INSERT INTO event_values.audit(event,kind,tag,phase,depth,detail) VALUES($1,$2,$3,$4,$5,$6)",
            SpiParameter.Create(context.Event), SpiParameter.Create(context.Kind.ToString()), SpiParameter.Create(context.CommandTag),
            SpiParameter.Create(phase), SpiParameter.Create(s_depth), SpiParameter.Create(detail));
}
