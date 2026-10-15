using System.Collections.ObjectModel;

namespace Ankus;

/// <summary>
/// Holds what stays the same for every row a trigger's call site sends: the trigger, its relation and its arguments.
/// Every context the site creates shares one instance.
/// </summary>
/// <param name="relationOid">The triggering relation's catalog OID.</param>
/// <param name="triggerOid">The trigger's catalog OID.</param>
/// <param name="name">The trigger's name.</param>
/// <param name="tableName">The relation's name.</param>
/// <param name="tableSchema">The relation's schema.</param>
/// <param name="oldTransitionTableName">The OLD transition table's name, or null.</param>
/// <param name="newTransitionTableName">The NEW transition table's name, or null.</param>
/// <param name="arguments">The trigger's arguments.</param>
/// <param name="descriptor">The relation's row descriptor.</param>
/// <param name="updateOfColumns">The ordinals of the trigger's <c>UPDATE OF</c> columns.</param>
internal sealed class PgTriggerDefinition(uint relationOid, uint triggerOid, string name, string tableName, string tableSchema,
    string? oldTransitionTableName, string? newTransitionTableName, ReadOnlyCollection<string> arguments,
    PgTupleDescriptor descriptor, ReadOnlyCollection<int> updateOfColumns)
{
    /// <summary>
    /// Gets the triggering relation's catalog OID.
    /// </summary>
    internal uint RelationOid => relationOid;

    /// <summary>
    /// Gets the trigger's catalog OID.
    /// </summary>
    internal uint TriggerOid => triggerOid;

    /// <summary>
    /// Gets the trigger's name.
    /// </summary>
    internal string Name => name;

    /// <summary>
    /// Gets the relation's name.
    /// </summary>
    internal string TableName => tableName;

    /// <summary>
    /// Gets the relation's schema.
    /// </summary>
    internal string TableSchema => tableSchema;

    /// <summary>
    /// Gets the OLD transition table's name, or null.
    /// </summary>
    internal string? OldTransitionTableName => oldTransitionTableName;

    /// <summary>
    /// Gets the NEW transition table's name, or null.
    /// </summary>
    internal string? NewTransitionTableName => newTransitionTableName;

    /// <summary>
    /// Gets the trigger's arguments.
    /// </summary>
    internal ReadOnlyCollection<string> Arguments => arguments;

    /// <summary>
    /// Gets the relation's row descriptor.
    /// </summary>
    internal PgTupleDescriptor Descriptor => descriptor;

    /// <summary>
    /// Gets the ordinals of the trigger's <c>UPDATE OF</c> columns.
    /// </summary>
    internal ReadOnlyCollection<int> UpdateOfColumns => updateOfColumns;
}
