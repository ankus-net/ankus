using System;
using System.Collections.Generic;
using System.Linq;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Owns an event trigger's event name and command tag. Metadata snapshots remain readable after the callback;
/// methods that query PostgreSQL require this invocation to be the active event context on its backend thread.
/// </summary>
public sealed class PgEventTriggerContext
{
    /// <summary>
    /// Validates copied callback metadata and retains the enclosing event context for stack restoration.
    /// </summary>
    internal PgEventTriggerContext(string eventName, string commandTag, PgEventTriggerContext? parent)
    {
        Kind = eventName switch
        {
            "ddl_command_start" => PgEventTriggerKind.DdlCommandStart,
            "ddl_command_end" => PgEventTriggerKind.DdlCommandEnd,
            "sql_drop" => PgEventTriggerKind.SqlDrop,
            "table_rewrite" => PgEventTriggerKind.TableRewrite,
            "login" => PgEventTriggerKind.Login,
            _ => throw new InvalidOperationException("Unknown PostgreSQL event trigger event name."),
        };

        if (string.IsNullOrEmpty(commandTag) || commandTag.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An event trigger requires a nonempty command tag without zero characters.");
        }

        Event = eventName;
        CommandTag = commandTag;
        Parent = parent;
    }

    /// <summary>
    /// Gets the exact PostgreSQL event name, such as ddl_command_end.
    /// </summary>
    public string Event { get; }

    /// <summary>
    /// Gets the event phase that caused this invocation.
    /// </summary>
    public PgEventTriggerKind Kind { get; }

    /// <summary>
    /// Gets the exact PostgreSQL command tag, including LOGIN for a login event.
    /// </summary>
    public string CommandTag { get; }

    /// <summary>
    /// Gets the enclosing managed event invocation, restored after this callback exits.
    /// </summary>
    internal PgEventTriggerContext? Parent
    {
        get;
        private set;
    }

    /// <summary>
    /// Releases the enclosing invocation link while returning the context to restore on successful exit.
    /// </summary>
    internal PgEventTriggerContext? DetachParent()
    {
        PgEventTriggerContext? parent = Parent;
        Parent = null;
        return parent;
    }

    /// <summary>
    /// Copies collected DDL command metadata during this active ddl_command_end invocation.
    /// The returned immutable snapshots remain valid after later SPI calls and after the callback returns.
    /// </summary>
    /// <returns>The commands in PostgreSQL's result order, possibly empty.</returns>
    public IReadOnlyList<PgDdlCommand> GetDdlCommands()
    {
        NativeEventTrigger.CheckAccess(this, PgEventTriggerKind.DdlCommandEnd);
        SpiResult result = Spi.Query("SELECT classid, objid, objsubid, command_tag, object_type, schema_name, object_identity, in_extension FROM pg_catalog.pg_event_trigger_ddl_commands()",
            readOnly: true, limit: 0);
        return Array.AsReadOnly(result.Select(static row => new PgDdlCommand(row)).ToArray());
    }

    /// <summary>
    /// Copies dropped-object metadata during this active sql_drop invocation.
    /// The returned immutable snapshots do not retain pointers into the deleted catalog entries.
    /// </summary>
    /// <returns>The dropped objects in PostgreSQL's result order, possibly empty.</returns>
    public IReadOnlyList<PgDroppedObject> GetDroppedObjects()
    {
        NativeEventTrigger.CheckAccess(this, PgEventTriggerKind.SqlDrop);
        SpiResult result = Spi.Query("SELECT classid, objid, objsubid, original, normal, is_temporary, object_type, schema_name, object_name, object_identity, address_names, address_args FROM pg_catalog.pg_event_trigger_dropped_objects()",
            readOnly: true, limit: 0);
        return Array.AsReadOnly(result.Select(static row => new PgDroppedObject(row)).ToArray());
    }

    /// <summary>
    /// Copies the target relation OID and combined rewrite reason bits during this active table_rewrite invocation.
    /// </summary>
    /// <returns>The owned table rewrite description.</returns>
    public PgTableRewrite GetTableRewrite()
    {
        NativeEventTrigger.CheckAccess(this, PgEventTriggerKind.TableRewrite);
        SpiResult result = Spi.Query("SELECT pg_catalog.pg_event_trigger_table_rewrite_oid() AS table_oid, pg_catalog.pg_event_trigger_table_rewrite_reason() AS reason",
            readOnly: true, limit: 0);
        if (result.Count != 1)
        {
            throw new InvalidOperationException("The table rewrite metadata query must return exactly one row.");
        }

        return new PgTableRewrite(result[0]);
    }
}
