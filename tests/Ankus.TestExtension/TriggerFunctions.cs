[assembly: PgSql("trigger-support", """
    CREATE SCHEMA trigger_values;
    CREATE TYPE trigger_values.foreign_row AS (id integer, value integer, note text);
    CREATE DOMAIN trigger_values.positive AS integer CHECK (VALUE > 0);
    CREATE TABLE trigger_values.events (
        position bigint GENERATED ALWAYS AS IDENTITY,
        trigger_name text, operation text, timing text, level text, event integer,
        relation_oid oid, trigger_oid oid, row_type_oid oid, table_schema text, table_name text,
        arguments text[], old_row text, new_row text, old_transition text, new_transition text, detail text);
    """, Requires = ["sql-first"])]

namespace Ankus.TestExtension;

/// <summary>
/// Exposes trigger callbacks with independently observable row, metadata, ownership, and SPI effects.
/// </summary>
[PgSchema("trigger_values", Create = false)]
public static class TriggerFunctions
{
    private static PgTriggerContext? s_context;
    private static SpiPreparedStatement? s_transitionPlan;
    private static SpiCursor? s_transitionCursor;
    private static string? s_transitionCursorName;
    private static int s_cleanupStarted;
    private static int s_cleanupDisposed;
    private static readonly List<string> s_cleanupRows = [];

    /// <summary>
    /// Audits the received context and applies the action declared by the first trigger argument.
    /// </summary>
    /// <param name="context">The owned trigger invocation.</param>
    /// <returns>The replacement row or the trigger skip signal.</returns>
    [PgTrigger]
    [PgFunction(Requires = ["trigger-support"])]
    public static PgHeapTuple? TriggerAction(PgTriggerContext context)
    {
        Audit(context);
        PgHeapTuple? row = context.New ?? context.Old;
        string action = context.Arguments.Count == 0 ? "observe" : context.Arguments[0];
        switch (action)
        {
            case "observe":
                return row;
            case "skip":
                return null;
            case "all_null":
                return context.Descriptor.CreateTuple();
            case "old":
                return context.Old;
            case "foreign":
                return PgTupleDescriptor.Load("trigger_values.foreign_row").CreateTuple();
            case "retain":
                s_context = context;
                return row;
            case "error":
                throw new PgException("P0001", "trigger rejected row", "owned trigger detail", "retry a valid row");
            case "managed_error":
                throw new InvalidOperationException("managed trigger failure");
            case "wait":
                Spi.Execute("SELECT pg_sleep(30)");
                return row;
            case "recover":
                string recovery = Spi.Connect(session =>
                {
                    try
                    {
                        session.Execute("SELECT 1 / 0");
                        return "unexpected success";
                    }
                    catch (PgException exception)
                    {
                        return exception.SqlState + ":" + session.ExecuteScalar<int>("SELECT 42");
                    }
                });
                Audit(context, recovery);
                return row;
            case "relation":
                using (PgRelation relation = PgRelation.Open(context.RelationOid))
                {
                    Audit(context, relation.NamespaceName + "." + relation.Name + ":" + (relation.Oid == context.RelationOid));
                }

                return row;
            case "nested":
                Spi.Execute("INSERT INTO trigger_values.child VALUES (90,900,'inner')");
                Audit(context, "after nested:" + Spi.ExecuteScalar<int>("SELECT 42"));
                return row;
            case "instead":
            case "instead_skip":
                WriteView(context);
                return action == "instead_skip" ? null : row;
            case "skip_even":
                if (row!.Get<int>("id") % 2 == 0)
                {
                    return null;
                }

                goto case "replace";
            case "replace":
                PgHeapTuple replacement = row!.Clone();
                replacement.Set("value", replacement.Get<int?>("value") + 10);
                replacement.Set("note", "changed");
                return replacement;
            case "null_note":
                PgHeapTuple nullable = row!.Clone();
                nullable.Set<string?>("note", null);
                return nullable;
            case "negative":
                PgHeapTuple negative = row!.Clone();
                negative.Set("value", -1);
                return negative;
            case "long_note":
                PgHeapTuple longNote = row!.Clone();
                longNote.Set("note", "too long");
                return longNote;
            case "emoji":
                PgHeapTuple emoji = row!.Clone();
                emoji.Set("note", "😀");
                return emoji;
            default:
                throw new ArgumentException("Unknown trigger action.", nameof(context));
        }
    }

    /// <summary>
    /// Reads unavailable generated fields through both accessors and records the descriptor's physical slots.
    /// </summary>
    /// <param name="context">The generated-column trigger invocation.</param>
    /// <returns>The row with its ordinary base value incremented before storage.</returns>
    [PgTrigger]
    [PgFunction(Requires = ["trigger-support"])]
    public static PgHeapTuple? TriggerGenerated(PgTriggerContext context)
    {
        PgHeapTuple row = context.New ?? context.Old!;
        var fields = new List<string>();
        for (int index = 0; index < row.Count; index++)
        {
            PgTupleAttributeInfo attribute = row.Descriptor.Attributes[index];
            if (attribute.IsDropped)
            {
                try
                {
                    row.Set<int?>(index, null);
                    fields.Add("dropped:unexpected");
                }
                catch (InvalidOperationException)
                {
                    fields.Add("dropped:protected");
                }
            }
            else if (attribute.IsUnavailable)
            {
                string read;
                try
                {
                    row.Get<int?>(index);
                    read = "unexpected";
                }
                catch (InvalidOperationException)
                {
                    read = "protected";
                }

                try
                {
                    row.Set(index, 0);
                    fields.Add(attribute.Name + ":" + read + ":unexpected");
                }
                catch (InvalidOperationException)
                {
                    fields.Add(attribute.Name + ":" + read + ":protected");
                }
            }
            else if (attribute.Name is "stored" or "virtual")
            {
                fields.Add(attribute.Name + ":" + row.Get<int?>(index));
            }
        }

        Audit(context, string.Join('|', fields));
        if (context.Timing == PgTriggerTiming.Before && context.New is not null)
        {
            try
            {
                Spi.ExecuteScalar<PgHeapTuple>("SELECT $1", SpiParameter.Create(row));
                Audit(context, "unexpected composite conversion");
            }
            catch (PgException exception)
            {
                Audit(context, "ordinary:" + exception.SqlState);
            }

            row.Set("value", row.Get<int>("value") + 10);
        }

        return row;
    }

    /// <summary>
    /// Queries transition relations through independent connections, sessions, plans, and cursor owners.
    /// </summary>
    /// <param name="context">The invocation containing transition aliases.</param>
    /// <returns>Null because AFTER ignores the row return.</returns>
    [PgTrigger]
    [PgFunction(Requires = ["trigger-support"])]
    public static PgHeapTuple? TriggerTransitions(PgTriggerContext context)
    {
        string mode = context.Arguments.Count == 0 ? "query" : context.Arguments[0];
        string? before = ReadTransition(context.OldTransitionTableName, mode);
        string? after = ReadTransition(context.NewTransitionTableName, mode);
        Audit(context, mode, before, after);
        if (mode == "nested")
        {
            Spi.Execute("INSERT INTO trigger_values.child VALUES (90,900,'inner')");
            Audit(context, "restored", ReadTransition(context.OldTransitionTableName, "query"),
                ReadTransition(context.NewTransitionTableName, "query"));
        }

        return null;
    }

    /// <summary>
    /// Returns a real zero-column row, or skips it, without conflating the two native pointer states.
    /// </summary>
    /// <param name="context">The empty-table invocation.</param>
    /// <returns>The empty row or null skip signal.</returns>
    [PgTrigger]
    [PgFunction(Requires = ["trigger-support"])]
    public static PgHeapTuple? TriggerEmpty(PgTriggerContext context)
    {
        if (context.New is null || context.New.Count != 0 || context.Descriptor.Attributes.Count != 0)
        {
            throw new InvalidOperationException("Expected an owned zero-column NEW tuple.");
        }

        return context.Arguments.Count == 0 ? context.Descriptor.CreateTuple() : null;
    }

    /// <summary>
    /// Leaves two suspended ProjectSet portals for automatic callback-exit cleanup.
    /// </summary>
    /// <param name="context">The invocation whose transition tables must remain valid during disposal.</param>
    /// <returns>Null because this is an AFTER statement trigger.</returns>
    [PgTrigger]
    [PgFunction(Requires = ["trigger-support"])]
    public static PgHeapTuple? TriggerCleanup(PgTriggerContext context)
    {
        s_cleanupStarted = 0;
        s_cleanupDisposed = 0;
        s_cleanupRows.Clear();
        string name = context.NewTransitionTableName!;
        for (int index = 0; index < 2; index++)
        {
            using SpiCursor cursor = Spi.OpenCursor(
                "SELECT id,trigger_values.trigger_cleanup_rows($1,$2) FROM " + Spi.QuoteIdentifier(name),
                SpiParameter.Create(name), SpiParameter.Create(index == 1 && context.Arguments.Count != 0));
            if (cursor.Fetch(1).Count != 1)
            {
                throw new InvalidOperationException("Expected a suspended ProjectSet row.");
            }

            cursor.Detach();
        }

        return null;
    }

    /// <summary>
    /// Queries the active transition environment while an unfinished iterator is disposed by trigger cleanup.
    /// </summary>
    /// <param name="name">The transition alias.</param>
    /// <param name="fail">Whether disposal throws after observing that alias.</param>
    /// <returns>A deliberately unconsumed sequence.</returns>
    [PgFunction(Requires = ["trigger-support"], SetMode = PgSetMode.ValuePerCall)]
    public static IEnumerable<int> TriggerCleanupRows(string name, bool fail) => new CleanupSequence(name, fail);

    /// <summary>
    /// Reports managed iterator cleanup after successful or aborted trigger-exit processing.
    /// </summary>
    /// <returns>The exact started, disposed, and observed transition counts.</returns>
    [PgFunction(Requires = ["trigger-support"])]
    public static string TriggerCleanupStatus()
        => s_cleanupStarted + ":" + s_cleanupDisposed + ":" + string.Join('|', s_cleanupRows);

    /// <summary>
    /// Reads owned context and row data after callback exit, memory churn, and a collection.
    /// </summary>
    /// <returns>The original name, argument, descriptor, and row values.</returns>
    [PgFunction(Requires = ["trigger-support"])]
    public static string TriggerRetained()
    {
        PgTriggerContext context = s_context ?? throw new InvalidOperationException("No retained trigger context.");
        Spi.Query("SELECT repeat('overwrite',100000)");
        GC.Collect();
        return context.Name + "|" + context.TableSchema + "." + context.TableName + "|" +
            string.Join(',', context.Arguments) + "|" + RowText(context.Old) + "|" + RowText(context.New);
    }

    /// <summary>
    /// Attempts to execute a transition plan outside its callback and proves guarded SPI recovery.
    /// </summary>
    /// <returns>The PostgreSQL error and subsequent result.</returns>
    [PgFunction(Requires = ["trigger-support"])]
    public static string TriggerPlanOutside()
    {
        try
        {
            s_transitionPlan!.Query();
            return "unexpected success";
        }
        catch (PgException exception)
        {
            return exception.SqlState + ":" + Spi.ExecuteScalar<int>("SELECT 42");
        }
    }

    /// <summary>
    /// Attempts to fetch through a saved transition cursor after callback cleanup.
    /// </summary>
    /// <param name="detached">Whether the callback detached its cursor.</param>
    /// <returns>The PostgreSQL or managed expiration error and successful follow-up query.</returns>
    [PgFunction(Requires = ["trigger-support"])]
    public static string TriggerCursorOutside(bool detached)
    {
        try
        {
            if (detached)
            {
                using SpiCursor cursor = Spi.FindCursor(s_transitionCursorName!);
                cursor.Fetch(1);
            }
            else
            {
                s_transitionCursor!.Fetch(1);
            }

            return "unexpected success";
        }
        catch (PgException exception)
        {
            return exception.SqlState + ":" + Spi.ExecuteScalar<int>("SELECT 42");
        }
    }

    private static string? ReadTransition(string? name, string mode)
    {
        if (name is null)
        {
            return null;
        }

        string sql = "SELECT id,value,note FROM " + Spi.QuoteIdentifier(name) + " ORDER BY id";
        switch (mode)
        {
            case "session":
                return Spi.Connect(session =>
                {
                    string first = ResultText(session.Query(sql));
                    string second = ResultText(session.Query(sql));
                    return first + "/" + second;
                });
            case "plan":
                using (SpiPreparedStatement plan = Spi.Prepare(sql))
                {
                    return ResultText(plan.Query());
                }
            case "session_plan":
                return Spi.Connect(session =>
                {
                    using SpiPreparedStatement plan = session.Prepare(sql);
                    return ResultText(plan.Query());
                });
            case "cache":
                if (s_transitionPlan is null || s_transitionPlan.CommandText != sql)
                {
                    s_transitionPlan?.Dispose();
                    s_transitionPlan = Spi.Connect(session => session.Prepare(sql).Keep());
                }

                return ResultText(s_transitionPlan.Query());
            case "cursor":
            case "plan_cursor":
            case "escape":
            case "detach":
                SpiCursor cursor;
                if (mode == "plan_cursor")
                {
                    using SpiPreparedStatement plan = Spi.Prepare(sql);
                    cursor = plan.OpenCursor();
                }
                else
                {
                    cursor = Spi.OpenCursor(sql);
                }

                string result = ResultText(cursor.Fetch(1));
                Spi.Query("SELECT repeat('churn',10000)");
                string remainder = mode is "escape" or "detach" ? "" : ResultText(cursor.Fetch(100));
                if (remainder.Length != 0)
                {
                    result = result.Length == 0 ? remainder : result + "|" + remainder;
                }

                if (mode is "escape" or "detach")
                {
                    s_transitionCursor?.Dispose();
                    s_transitionCursor = cursor;
                    s_transitionCursorName = mode == "detach" ? cursor.Detach() : cursor.Name;
                }
                else
                {
                    cursor.Dispose();
                }

                return result;
            default:
                return ResultText(Spi.Query(sql));
        }
    }

    private static void WriteView(PgTriggerContext context)
    {
        switch (context.Operation)
        {
            case PgTriggerOperation.Insert:
                Spi.Execute("INSERT INTO trigger_values.rows VALUES ($1,$2,$3)",
                    SpiParameter.Create(context.New!.Get<int?>("id")), SpiParameter.Create(context.New.Get<int?>("value")),
                    SpiParameter.Create(context.New.Get<string?>("note")));
                break;
            case PgTriggerOperation.Update:
                Spi.Execute("UPDATE trigger_values.rows SET value=$1,note=$2 WHERE id=$3",
                    SpiParameter.Create(context.New!.Get<int?>("value")), SpiParameter.Create(context.New.Get<string?>("note")),
                    SpiParameter.Create(context.Old!.Get<int?>("id")));
                break;
            case PgTriggerOperation.Delete:
                Spi.Execute("DELETE FROM trigger_values.rows WHERE id=$1", SpiParameter.Create(context.Old!.Get<int?>("id")));
                break;
            default:
                throw new InvalidOperationException("A view cannot receive a row TRUNCATE trigger.");
        }
    }

    /// <summary>
    /// Records the trigger's UPDATE OF columns, the columns an UPDATE assigns and its constraint deferral.
    /// </summary>
    /// <param name="context">The owned trigger invocation.</param>
    /// <returns>The unchanged row.</returns>
    [PgTrigger]
    [PgFunction(Requires = ["trigger-support"])]
    public static PgHeapTuple? TriggerColumns(PgTriggerContext context)
    {
        Audit(context, string.Join(',', context.UpdateOfColumns) + "|" +
            (context.UpdatedColumns is null ? "null" : string.Join(',', context.UpdatedColumns)) + "|" +
            (context.ConstraintOid != 0) + "|" + context.IsDeferrable + "|" + context.IsInitiallyDeferred);
        return context.New ?? context.Old;
    }

    private static void Audit(PgTriggerContext context, string? detail = null, string? oldTransitionRows = null,
        string? newTransitionRows = null)
        => Spi.Execute("""
            INSERT INTO trigger_values.events(trigger_name,operation,timing,level,event,relation_oid,trigger_oid,row_type_oid,
                table_schema,table_name,arguments,old_row,new_row,old_transition,new_transition,detail)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)
            """, SpiParameter.Create(context.Name), SpiParameter.Create(context.Operation.ToString()),
            SpiParameter.Create(context.Timing.ToString()), SpiParameter.Create(context.Level.ToString()),
            SpiParameter.Create((int)context.Event), SpiParameter.Create(context.RelationOid), SpiParameter.Create(context.TriggerOid),
            SpiParameter.Create(context.Descriptor.TypeOid), SpiParameter.Create(context.TableSchema), SpiParameter.Create(context.TableName),
            SpiParameter.Create(context.Arguments.ToArray()), SpiParameter.Create(oldTransitionRows ?? RowText(context.Old)),
            SpiParameter.Create(newTransitionRows ?? RowText(context.New)), SpiParameter.Create(context.OldTransitionTableName),
            SpiParameter.Create(context.NewTransitionTableName), SpiParameter.Create(detail));

    private static string? RowText(PgHeapTuple? row)
        => row is null ? null : row.Get<int?>("id") + ":" + row.Get<int?>("value") + ":" + row.Get<string?>("note");

    private static string ResultText(SpiResult result)
        => string.Join('|', result.Select(static row => row.Get<int?>(0) + ":" + row.Get<int?>(1) + ":" + row.Get<string?>(2)));
    /// <summary>
    /// Allocates a counted enumerator whose disposal deliberately needs the live transition environment.
    /// </summary>
    private sealed class CleanupSequence(string name, bool fail) : IEnumerable<int>
    {
        /// <inheritdoc />
        public IEnumerator<int> GetEnumerator()
        {
            s_cleanupStarted++;
            return new CleanupEnumerator(name, fail);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Observes cleanup exactly once and can throw from Dispose before older cursor owners are released.
    /// </summary>
    private sealed class CleanupEnumerator(string name, bool fail) : IEnumerator<int>
    {
        private int _current;
        private bool _disposed;

        /// <inheritdoc />
        public int Current => _current;

        object System.Collections.IEnumerator.Current => Current;

        /// <inheritdoc />
        public bool MoveNext() => ++_current <= 3;

        /// <inheritdoc />
        public void Reset() => throw new NotSupportedException();

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                s_cleanupRows.Add("duplicate disposal");
                return;
            }

            _disposed = true;
            s_cleanupDisposed++;
            try
            {
                long count = Spi.ExecuteScalar<long>("SELECT count(*) FROM " + Spi.QuoteIdentifier(name));
                s_cleanupRows.Add("rows:" + count);
            }
            catch (InvalidOperationException)
            {
                s_cleanupRows.Add("cleanup restricted");
            }

            if (fail)
            {
                throw new PgException("P7107", "trigger iterator Dispose failure");
            }
        }
    }

}
