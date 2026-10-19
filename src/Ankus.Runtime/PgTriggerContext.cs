namespace Ankus;

/// <summary>
/// Owns trigger metadata and detached OLD and NEW row values independently of PostgreSQL memory contexts.
/// Transition table names remain owned strings, but the tables can only be queried during the trigger invocation.
/// </summary>
public sealed class PgTriggerContext
{
    private readonly PgTriggerDefinition _definition;

    /// <summary>
    /// Validates the event and takes ownership of converted rows while copying the trigger argument list.
    /// </summary>
    internal PgTriggerContext(uint eventBits, uint relationOid, uint triggerOid, string name, string tableName,
        string tableSchema, string? oldTransitionTableName, string? newTransitionTableName,
        string[] arguments, PgHeapTuple? oldRow, PgHeapTuple? newRow, PgTupleDescriptor descriptor,
        int[]? updateOfColumns = null, int[]? updatedColumns = null, uint constraintOid = 0, int constraintFlags = 0)
        : this(new PgTriggerDefinition(relationOid, triggerOid, name, tableName, tableSchema, oldTransitionTableName,
            newTransitionTableName, Array.AsReadOnly<string>([.. arguments ?? throw new ArgumentNullException(nameof(arguments))]),
            descriptor, Array.AsReadOnly<int>([.. updateOfColumns ?? []])), eventBits, oldRow, newRow, updatedColumns,
            constraintOid, constraintFlags)
    {
    }

    /// <summary>
    /// Validates the event and takes ownership of converted rows, sharing a definition that nothing can change, such as
    /// the one a trigger call site parsed once.
    /// </summary>
    /// <remarks>
    /// A call site passes <paramref name="metadataValidated"/> after checking its definition once with
    /// <see cref="ValidateMetadata"/>; it does not change between its rows.
    /// </remarks>
    internal PgTriggerContext(PgTriggerDefinition definition, uint eventBits, PgHeapTuple? oldRow, PgHeapTuple? newRow,
        int[]? updatedColumns, uint constraintOid, int constraintFlags, bool metadataValidated = false)
    {
        if ((eventBits & ~127U) != 0 || (eventBits & 24U) == 24U ||
            ((eventBits & 4U) != 0 && (eventBits & 3U) == 3U) ||
            ((eventBits & 16U) != 0 && (eventBits & 4U) == 0))
        {
            throw new InvalidOperationException("The native trigger event has an invalid operation, timing, or level.");
        }

        if (definition.RelationOid == 0 || definition.TriggerOid == 0)
        {
            throw new InvalidOperationException("Trigger and relation identities must be valid PostgreSQL OIDs.");
        }

        ArgumentNullException.ThrowIfNull(definition.Arguments, "arguments");
        ArgumentNullException.ThrowIfNull(definition.Descriptor, "descriptor");
        ArgumentNullException.ThrowIfNull(definition.UpdateOfColumns, "updateOfColumns");
        if (!metadataValidated)
        {
            ValidateMetadata(definition.Name, definition.TableName, definition.TableSchema, definition.OldTransitionTableName,
                definition.NewTransitionTableName, definition.Arguments, definition.UpdateOfColumns, definition.Descriptor);
        }

        _definition = definition;
        Event = eventBits;
        bool needsOld = Level == PgTriggerLevel.Row && Operation is PgTriggerOperation.Delete or PgTriggerOperation.Update;
        bool needsNew = Level == PgTriggerLevel.Row && Operation is PgTriggerOperation.Insert or PgTriggerOperation.Update;
        if ((oldRow is not null) != needsOld || (newRow is not null) != needsNew)
        {
            throw new InvalidOperationException("OLD and NEW row presence does not match the trigger operation and level.");
        }

        ValidateRow(oldRow, definition.Descriptor);
        ValidateRow(newRow, definition.Descriptor);
        if (updatedColumns is not null)
        {
            if (Operation != PgTriggerOperation.Update)
            {
                throw new InvalidOperationException("Only an UPDATE trigger reports updated columns.");
            }

            ValidateColumns(updatedColumns, definition.Descriptor);
        }
        else if (Operation == PgTriggerOperation.Update)
        {
            updatedColumns = [];
        }

        if (constraintFlags is < 0 or > 3 or 2)
        {
            throw new InvalidOperationException("A trigger can be initially deferred only when it is deferrable.");
        }

        Old = oldRow;
        New = newRow;
        UpdatedColumns = updatedColumns is null ? null : Array.AsReadOnly<int>([.. updatedColumns]);
        ConstraintOid = constraintOid;
        IsDeferrable = (constraintFlags & 1) != 0;
        IsInitiallyDeferred = (constraintFlags & 2) != 0;
    }

    /// <summary>
    /// Gets the exact name of the trigger that fired.
    /// </summary>
    public string Name => _definition.Name;

    /// <summary>
    /// Gets the exact target table or view name.
    /// </summary>
    public string TableName => _definition.TableName;

    /// <summary>
    /// Gets the exact target relation's schema name.
    /// </summary>
    public string TableSchema => _definition.TableSchema;

    /// <summary>
    /// Gets the target relation's catalog OID, distinct from its row type OID.
    /// </summary>
    public uint RelationOid => _definition.RelationOid;

    /// <summary>
    /// Gets the fired trigger's catalog OID.
    /// </summary>
    public uint TriggerOid => _definition.TriggerOid;

    /// <summary>
    /// Gets PostgreSQL's raw trigger event bits, retaining internal deferrability flags.
    /// </summary>
    public uint Event { get; }

    /// <summary>
    /// Gets the single operation that caused this invocation.
    /// </summary>
    public PgTriggerOperation Operation => (PgTriggerOperation)(Event & 3U);

    /// <summary>
    /// Gets whether this invocation runs before, after, or instead of the operation.
    /// </summary>
    public PgTriggerTiming Timing => (Event & 24U) switch
    {
        8 => PgTriggerTiming.Before,
        16 => PgTriggerTiming.InsteadOf,
        _ => PgTriggerTiming.After,
    };

    /// <summary>
    /// Gets whether this invocation operates on one row or the entire statement.
    /// </summary>
    public PgTriggerLevel Level => (Event & 4U) != 0 ? PgTriggerLevel.Row : PgTriggerLevel.Statement;

    /// <summary>
    /// Gets the owned original row for row-level UPDATE and DELETE invocations, otherwise null.
    /// </summary>
    public PgHeapTuple? Old { get; }

    /// <summary>
    /// Gets the owned proposed row for row-level INSERT and UPDATE invocations, otherwise null.
    /// Undefined generated columns are marked unavailable and never read as SQL NULL.
    /// </summary>
    public PgHeapTuple? New { get; }

    /// <summary>
    /// Gets the immutable target relation descriptor, including physical dropped slots.
    /// </summary>
    public PgTupleDescriptor Descriptor => _definition.Descriptor;

    /// <summary>
    /// Gets the immutable trigger arguments in declaration order, including empty strings.
    /// </summary>
    public IReadOnlyList<string> Arguments => _definition.Arguments;

    /// <summary>
    /// Gets the OLD transition relation name, or null when none was declared.
    /// The relation itself is only available during the trigger callback.
    /// </summary>
    public string? OldTransitionTableName => _definition.OldTransitionTableName;

    /// <summary>
    /// Gets the NEW transition relation name, or null when none was declared.
    /// The relation itself is only available during the trigger callback.
    /// </summary>
    public string? NewTransitionTableName => _definition.NewTransitionTableName;

    /// <summary>
    /// Gets the zero-based ordinals of the columns named by the trigger's <c>UPDATE OF</c> list, or an empty list when the
    /// trigger is not column-specific.
    /// </summary>
    public IReadOnlyList<int> UpdateOfColumns => _definition.UpdateOfColumns;

    /// <summary>
    /// Gets the zero-based ordinals of the columns the triggering <c>UPDATE</c> assigns, or null for other operations.
    /// A column can be assigned its current value; compare <see cref="Old"/> and <see cref="New"/> to find changed values.
    /// </summary>
    public IReadOnlyList<int>? UpdatedColumns { get; }

    /// <summary>
    /// Gets the OID of the constraint a constraint trigger implements, or zero for an ordinary trigger.
    /// </summary>
    public uint ConstraintOid { get; }

    /// <summary>
    /// Gets whether the constraint trigger is deferrable.
    /// </summary>
    public bool IsDeferrable { get; }

    /// <summary>
    /// Gets whether the constraint trigger is initially deferred until transaction commit.
    /// </summary>
    public bool IsInitiallyDeferred { get; }

    /// <summary>
    /// Checks the parts of a trigger's metadata that stay the same for every row its call site sends.
    /// </summary>
    /// <param name="name">The trigger's name.</param>
    /// <param name="tableName">The relation's name.</param>
    /// <param name="tableSchema">The relation's schema.</param>
    /// <param name="oldTransitionTableName">The OLD transition table's name, or null.</param>
    /// <param name="newTransitionTableName">The NEW transition table's name, or null.</param>
    /// <param name="arguments">The trigger's arguments.</param>
    /// <param name="updateOfColumns">The ordinals of the trigger's <c>UPDATE OF</c> columns.</param>
    /// <param name="descriptor">The relation's row descriptor.</param>
    internal static void ValidateMetadata(string name, string tableName, string tableSchema, string? oldTransitionTableName,
        string? newTransitionTableName, IReadOnlyList<string> arguments, IReadOnlyList<int> updateOfColumns,
        PgTupleDescriptor descriptor)
    {
        ValidateName(name);
        ValidateName(tableName);
        ValidateName(tableSchema);
        if (oldTransitionTableName is not null)
        {
            ValidateName(oldTransitionTableName);
        }

        if (newTransitionTableName is not null)
        {
            ValidateName(newTransitionTableName);
        }

        // Indexed loops: enumerating a read-only list allocates.
        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is null || arguments[index].Contains('\0', StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Trigger arguments cannot contain SQL NULL or zero characters.");
            }
        }

        ValidateColumns(updateOfColumns, descriptor);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Trigger metadata requires nonempty names without zero characters.");
        }
    }

    private static void ValidateColumns(IReadOnlyList<int> columns, PgTupleDescriptor descriptor)
    {
        for (int index = 0; index < columns.Count; index++)
        {
            if (columns[index] < 0 || columns[index] >= descriptor.AttributeSpan.Length)
            {
                throw new InvalidOperationException("Trigger column ordinals must identify the target relation's attributes.");
            }
        }
    }

    private static void ValidateRow(PgHeapTuple? row, PgTupleDescriptor descriptor)
    {
        // A row read from a call site's transport shares the site's descriptor.
        if (row is null || ReferenceEquals(row.Descriptor, descriptor))
        {
            return;
        }

        if (row.Descriptor.BaseTypeOid != descriptor.BaseTypeOid ||
            row.Descriptor.TypeModifier != descriptor.TypeModifier || row.Count != descriptor.AttributeSpan.Length)
        {
            throw new InvalidOperationException("The trigger row has a different tuple descriptor from the target relation.");
        }

        for (int index = 0; index < row.Count; index++)
        {
            PgTupleAttributeInfo expected = descriptor.AttributeSpan[index];
            PgTupleAttributeInfo actual = row.Descriptor.AttributeSpan[index];
            if (actual.Name != expected.Name || actual.TypeOid != expected.TypeOid || actual.BaseTypeOid != expected.BaseTypeOid ||
                actual.TypeModifier != expected.TypeModifier || actual.CollationOid != expected.CollationOid ||
                actual.IsDropped != expected.IsDropped || actual.IsNotNull != expected.IsNotNull || actual.IsComposite != expected.IsComposite)
            {
                throw new InvalidOperationException("The trigger row's physical attributes do not match the target relation.");
            }
        }
    }
}
