using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Represents one installation step and its explicit and automatically resolved prerequisites.
/// </summary>
internal sealed class SqlEntity
{
    private string _sql = string.Empty;
    private string? _sqlTemplate = string.Empty;
    /// <summary>
    /// Creates an installation node with a stable internal key and diagnostic location.
    /// </summary>
    /// <param name="key">The unique internal sort key.</param>
    /// <param name="sql">The emitted SQL.</param>
    /// <param name="location">The source declaration location.</param>
    internal SqlEntity(string key, string sql, Location? location)
    {
        Key = key;
        Sql = sql;
        Location = location;
    }

    /// <summary>
    /// Creates a function node whose SQL is rendered after planner support references are resolved.
    /// </summary>
    /// <param name="key">The unique internal sort key.</param>
    /// <param name="function">The validated function definition.</param>
    /// <param name="location">The source declaration location.</param>
    internal SqlEntity(string key, SqlFunction function, Location? location) : this(key, string.Empty, location)
    {
        Function = function;
        _sqlTemplate = null;
    }

    /// <summary>
    /// Gets the original generated function contract, including when its SQL is disabled or replaced.
    /// </summary>
    internal SqlFunction? Function { get; }

    /// <summary>
    /// Gets the stable key used for deterministic ordering of independent nodes.
    /// </summary>
    internal string Key { get; }

    /// <summary>
    /// Gets or sets the complete installation SQL for this node.
    /// </summary>
    internal string Sql
    {
        get => _sqlTemplate is null ? Function!.Emit().Replace("\0", string.Empty) : _sql;
        set
        {
            _sqlTemplate = value;
            _sql = value.Replace("\0", string.Empty);
        }
    }

    /// <summary>
    /// Gets typed schema markers separately from ordinary installation SQL.
    /// </summary>
    internal string SqlTemplate => _sqlTemplate ?? Function!.Emit();

    /// <summary>
    /// Gets the source location used for graph diagnostics.
    /// </summary>
    internal Location? Location { get; }

    /// <summary>
    /// Gets the exact managed declarations that contribute to this installation node.
    /// </summary>
    internal HashSet<string> ManagedSources { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the authored SQL file path when the fragment comes from an additional file.
    /// </summary>
    internal string? SourceFile
    {
        get;
        set;
    }

    /// <summary>
    /// Gets dependency aliases exported by this node.
    /// </summary>
    internal HashSet<string> Names { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the authored value that declared each dependency alias, for duplicate-identifier diagnostics.
    /// </summary>
    internal Dictionary<string, Location?> NameLocations { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets explicit identifiers that must precede this node and the authored entry that first named each one.
    /// </summary>
    internal Dictionary<string, Location?> Requires { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets explicit identifiers that must follow this node and the authored entry that first named each one.
    /// </summary>
    internal Dictionary<string, Location?> Before { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets additional SQL and managed names for selecting this declaration without declaring dependency aliases.
    /// </summary>
    internal HashSet<string> SelectionNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets generated object identities for PostgreSQL extension attachment.
    /// </summary>
    internal HashSet<string> Attachments { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the declaration kind carried into native schema metadata.
    /// </summary>
    internal string Kind
    {
        get;
        set;
    } = "sql";

    /// <summary>
    /// Gets or sets the declaration family whose members must be selected together.
    /// </summary>
    internal SqlEntity? Owner
    {
        get;
        set;
    }

    /// <summary>
    /// Gets resolved prerequisite nodes, including generated schema dependencies.
    /// </summary>
    internal HashSet<SqlEntity> Dependencies { get; } = [];

    /// <summary>
    /// Gets compiler-resolved explicit prerequisites, which retain explicit shell-type ordering semantics.
    /// </summary>
    internal HashSet<SqlEntity> DeclaredDependencies { get; } = [];

    /// <summary>
    /// Gets typed Requires edges inherited by aggregate support functions independently of incoming Before edges.
    /// </summary>
    internal HashSet<SqlEntity> RequiredDeclarations { get; } = [];

    /// <summary>
    /// Gets inferred custom SQL type prerequisites that may follow an explicitly ordered shell-type consumer.
    /// </summary>
    internal HashSet<SqlEntity> TypeDependencies { get; } = [];

    /// <summary>
    /// Gets or sets the normal, bootstrap, or final positioning discriminator.
    /// </summary>
    internal int Order
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the authored positioning value, for boundary diagnostics.
    /// </summary>
    internal Location? OrderLocation
    {
        get;
        set;
    }

    /// <summary>
    /// Gets a human-readable name for diagnostics.
    /// </summary>
    internal string DisplayName => Names.OrderBy(static name => name, StringComparer.Ordinal).FirstOrDefault() ?? Key;

    /// <summary>
    /// Gets an authored dependency identifier or managed declaration name for fixed diagnostic substitutions.
    /// </summary>
    /// <remarks>
    /// Generated members of a declaration family, such as index-family support functions, use their family's name.
    /// </remarks>
    internal string DiagnosticName => (Names.Count != 0 ? Names : ManagedSources).OrderBy(static name => name, StringComparer.Ordinal)
        .FirstOrDefault() ?? Owner?.DiagnosticName ?? Key;

    /// <summary>
    /// Adds inherited prerequisites that this node does not itself declare, retaining their authored entries.
    /// </summary>
    /// <param name="requirements">The source node's explicit prerequisites.</param>
    internal void Inherit(Dictionary<string, Location?> requirements)
    {
        foreach (KeyValuePair<string, Location?> requirement in requirements.Where(requirement => !Names.Contains(requirement.Key) && !Requires.ContainsKey(requirement.Key)))
        {
            Requires.Add(requirement.Key, requirement.Value);
        }
    }
}
