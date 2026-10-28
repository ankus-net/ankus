using System;

namespace Ankus;

/// <summary>
/// Owns one command's descriptive metadata from pg_event_trigger_ddl_commands, independently of the callback lifetime.
/// The opaque pg_ddl_command value and parse-tree pointers are not exposed.
/// </summary>
public sealed class PgDdlCommand
{
    /// <summary>
    /// Copies an explicitly projected, materialized metadata row.
    /// </summary>
    internal PgDdlCommand(SpiRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ClassId = row.Get<uint?>(0);
        ObjectId = row.Get<uint?>(1);
        ObjectSubId = row.Get<int?>(2);
        CommandTag = row.Get<string?>(3) ?? throw new InvalidOperationException("A DDL command requires a command tag.");
        ObjectType = row.Get<string?>(4) ?? throw new InvalidOperationException("A DDL command requires an object type.");
        SchemaName = row.Get<string?>(5);
        ObjectIdentity = row.Get<string?>(6);
        InExtension = row.Get<bool>(7);
    }

    /// <summary>
    /// Gets the object's catalog OID, or null for commands without one object address, such as GRANT.
    /// </summary>
    public uint? ClassId { get; }

    /// <summary>
    /// Gets the object's OID, or null when the collected command has no single object address.
    /// </summary>
    public uint? ObjectId { get; }

    /// <summary>
    /// Gets the subobject number, zero for a whole object, or null when no object address is available.
    /// </summary>
    public int? ObjectSubId { get; }

    /// <summary>
    /// Gets PostgreSQL's command tag for this collected command.
    /// </summary>
    public string CommandTag { get; }

    /// <summary>
    /// Gets PostgreSQL's description of the object type.
    /// </summary>
    public string ObjectType { get; }

    /// <summary>
    /// Gets the schema name, or null for schema-less objects and commands without a single object address.
    /// </summary>
    public string? SchemaName { get; }

    /// <summary>
    /// Gets the quoted, qualified object identity, or null when the command has no single object address.
    /// </summary>
    public string? ObjectIdentity { get; }

    /// <summary>
    /// Gets whether the command was collected while an extension script was running.
    /// </summary>
    public bool InExtension { get; }
}
