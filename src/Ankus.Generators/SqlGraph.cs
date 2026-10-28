using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Validates and topologically orders installation SQL with stable output and explicit cycle diagnostics.
/// </summary>
internal sealed partial class SqlGraph
{
    private readonly List<SqlEntity> _entities = [];
    private readonly List<SqlEntity> _ordered = [];
    private readonly List<(SqlEntity Entry, HashSet<SqlEntity> Members)> _replacements = [];

    /// <summary>
    /// Retains the authored value that declared each explicit edge, for cycle diagnostics.
    /// </summary>
    private readonly Dictionary<(SqlEntity Dependent, SqlEntity Prerequisite), Location> _edges = [];
    private readonly GeneratorDiagnostics _context;
    private readonly GeneratorSourceResolver _sources;

    /// <summary>
    /// Retains the compiler-visible root for portable source attribution.
    /// </summary>
    private readonly string _projectDirectory;
    private bool _invalid;

    /// <summary>
    /// Creates a graph whose validation errors are reported to the current generator run.
    /// </summary>
    /// <param name="context">The diagnostic sink and cancellation token.</param>
    /// <param name="projectDirectory">The compiler-visible root for portable source comments.</param>
    /// <param name="sources">The detached source attribution for authored values and SQL provenance.</param>
    internal SqlGraph(GeneratorDiagnostics context, string projectDirectory, GeneratorSourceResolver sources)
    {
        _context = context;
        _projectDirectory = projectDirectory;
        _sources = sources;
    }

    /// <summary>
    /// Registers an installation node.
    /// </summary>
    /// <param name="entity">The declaration node.</param>
    /// <param name="declaration">The managed declaration represented by this SQL node, when present.</param>
    internal void Add(SqlEntity entity, ISymbol? declaration = null)
    {
        _entities.Add(entity);
        if (declaration is not null)
        {
            Register(declaration, entity);
        }
    }

    /// <summary>
    /// Replaces a declaration and its attached SQL with one fragment ordered after every external prerequisite.
    /// Original nodes and internal edges remain available for identifiers and cycle validation.
    /// </summary>
    /// <param name="entry">The node that will emit the complete replacement.</param>
    /// <param name="sql">The replacement text.</param>
    /// <param name="related">Attached nodes whose SQL is included in the replacement.</param>
    internal void Replace(SqlEntity entry, string sql, IReadOnlyList<SqlEntity> related)
    {
        entry.Sql = sql;
        var members = new HashSet<SqlEntity>(related) { entry };
        foreach (SqlEntity entity in related)
        {
            entity.Sql = string.Empty;
        }

        _replacements.Add((entry, members));
    }

    /// <summary>
    /// Applies immutable dependency options after semantic declaration analysis.
    /// </summary>
    /// <param name="entity">The destination node.</param>
    /// <param name="options">The detached authored options.</param>
    /// <param name="name">An explicit, already validated SQL name, or null to use the optional Id.</param>
    /// <param name="nameLocation">The authored explicit SQL name.</param>
    internal void ConfigureOptions(SqlEntity entity, SqlDeclarationOptions options, string? name = null, Location? nameLocation = null)
    {
        if (name is null && options.Id is not null)
        {
            name = options.Id;
            nameLocation = Resolve(options.Locations.Id);
            if (TextError(name, SqlGraphDiagnostics.s_blankId, SqlGraphDiagnostics.s_idZero, SqlGraphDiagnostics.s_idUnicode) is { } error)
            {
                Error(nameLocation ?? entity.Location, error);
                name = null;
            }
        }

        if (name is not null && entity.Names.Add(name))
        {
            entity.NameLocations.Add(name, nameLocation ?? entity.Location);
        }

        ReadNames(options.Requires, entity.Requires);
        ReadNames(options.Before, entity.Before);

        void ReadNames(EquatableArray<SqlDependencyName> values, Dictionary<string, Location?> destination)
        {
            foreach (SqlDependencyName value in values)
            {
                Location? location = Resolve(value.Location) ?? entity.Location;
                DiagnosticDescriptor? error = value.NullList ? SqlGraphDiagnostics.s_nullList : TextError(value.Value,
                    SqlGraphDiagnostics.s_blankReference, SqlGraphDiagnostics.s_referenceZero, SqlGraphDiagnostics.s_referenceUnicode);
                if (error is not null)
                {
                    Error(location, error);
                }
                else if (!destination.ContainsKey(value.Value!))
                {
                    destination.Add(value.Value!, location);
                }
            }
        }
    }

    /// <summary>
    /// Reattaches an authored value to the current composition's source attribution.
    /// </summary>
    /// <param name="location">The detached coordinates, when the value was authored in source.</param>
    /// <returns>The transient diagnostic location, or null when the value has no source coordinates.</returns>
    internal Location? Resolve(GeneratorLocation? location) => location?.Resolve(_sources);

    /// <summary>
    /// Reports a fixed independently correctable declaration contract and prevents partial installation output.
    /// </summary>
    /// <param name="location">The exact offending argument.</param>
    /// <param name="descriptor">The fixed validation contract.</param>
    /// <param name="arguments">The exact authored identity substitutions.</param>
    internal void Error(Location? location, DiagnosticDescriptor descriptor, params string[] arguments)
    {
        _invalid = true;
        _context.Report(descriptor, location, arguments);
    }

    /// <summary>
    /// Selects the first failed text contract for an authored identifier or SQL replacement.
    /// </summary>
    /// <param name="value">The authored text.</param>
    /// <param name="blank">The null or whitespace contract, or null when blank text is valid.</param>
    /// <param name="zero">The zero-character contract.</param>
    /// <param name="unicode">The well-formed Unicode contract.</param>
    /// <returns>The failed contract, or null for valid text.</returns>
    internal static DiagnosticDescriptor? TextError(string? value, DiagnosticDescriptor? blank, DiagnosticDescriptor zero, DiagnosticDescriptor unicode)
    {
        if (value is null || string.IsNullOrWhiteSpace(value))
        {
            return blank;
        }

        return value.Contains('\0') ? zero : !SqlText.IsText(value) ? unicode : null;
    }

    /// <summary>
    /// Resolves references and freezes deterministic installation order without rendering sources.
    /// </summary>
    /// <returns>The detached rendering contract, or null when the graph is invalid.</returns>
    internal InstallationGraphModel? Freeze()
    {
        _ordered.Clear();
        var names = new Dictionary<string, SqlEntity>(StringComparer.Ordinal);
        foreach (SqlEntity entity in _entities)
        {
            foreach (string name in entity.Names.OrderBy(static value => value, StringComparer.Ordinal))
            {
                if (names.ContainsKey(name))
                {
                    Error(entity.NameLocations[name], SqlGraphDiagnostics.s_duplicateId, SqlGraphDiagnostics.Quote(name));
                }
                else
                {
                    names.Add(name, entity);
                }
            }
        }

        var missing = new HashSet<(string Name, Location? Location)>();
        Dictionary<SqlEntity, HashSet<SqlEntity>> explicitDependencies = _entities.ToDictionary(
            static entity => entity, static entity => new HashSet<SqlEntity>(entity.DeclaredDependencies.Concat(entity.RequiredDeclarations)));
        foreach (SqlEntity entity in _entities)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            entity.Dependencies.UnionWith(entity.DeclaredDependencies);
            entity.Dependencies.UnionWith(entity.RequiredDeclarations);
            Link(entity.Requires, before: false);
            Link(entity.Before, before: true);

            void Link(Dictionary<string, Location?> references, bool before)
            {
                foreach (KeyValuePair<string, Location?> reference in references.OrderBy(static value => value.Key, StringComparer.Ordinal))
                {
                    string name = reference.Key;
                    if (!names.TryGetValue(name, out SqlEntity? dependency))
                    {
                        if (missing.Add((name, reference.Value)))
                        {
                            Error(reference.Value, SqlGraphDiagnostics.s_missingDependency, SqlGraphDiagnostics.Quote(name));
                        }
                    }
                    else if (before)
                    {
                        dependency.Dependencies.Add(entity);
                        explicitDependencies[dependency].Add(entity);
                        RecordEdge(dependency, entity, reference.Value);
                    }
                    else
                    {
                        entity.Dependencies.Add(dependency);
                        explicitDependencies[entity].Add(dependency);
                        RecordEdge(entity, dependency, reference.Value);
                    }
                }
            }
        }

        AddBoundary(1);
        AddBoundary(2);
        if (_invalid)
        {
            return null;
        }

        foreach (SqlEntity entity in _entities)
        {
            foreach (SqlEntity provider in entity.TypeDependencies.Where(provider => !ExplicitlyFollows(provider, entity)))
            {
                entity.Dependencies.Add(provider);
            }
        }

        foreach ((SqlEntity entry, HashSet<SqlEntity> members) in _replacements)
        {
            foreach (SqlEntity member in members)
            {
                if (member == entry)
                {
                    continue;
                }

                entry.Dependencies.UnionWith(member.Dependencies.Where(dependency => !members.Contains(dependency)));
            }
        }

        var remaining = new Dictionary<SqlEntity, int>();
        var dependents = new Dictionary<SqlEntity, List<SqlEntity>>();
        var ready = new SortedSet<SqlEntity>(Comparer<SqlEntity>.Create(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Key, right.Key)));
        foreach (SqlEntity entity in _entities)
        {
            remaining.Add(entity, entity.Dependencies.Count);
            dependents.Add(entity, []);
            if (entity.Dependencies.Count == 0)
            {
                ready.Add(entity);
            }
        }

        foreach (SqlEntity entity in _entities)
        {
            foreach (SqlEntity dependency in entity.Dependencies)
            {
                dependents[dependency].Add(entity);
            }
        }

        int emitted = 0;
        while (ready.Count != 0)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            SqlEntity entity = ready.Min!;
            ready.Remove(entity);
            _ordered.Add(entity);

            emitted++;
            foreach (SqlEntity dependent in dependents[entity])
            {
                remaining[dependent]--;
                if (remaining[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (emitted != _entities.Count)
        {
            ReportCycle(remaining);
            return null;
        }

        return new(new EquatableArray<InstallationGraphModel.Node>(_ordered.Select(entity => InstallationGraphModel.Node.Create(entity, _projectDirectory, _sources))));

        bool ExplicitlyFollows(SqlEntity provider, SqlEntity consumer)
        {
            var pending = new Stack<SqlEntity>();
            var visited = new HashSet<SqlEntity>();
            pending.Push(provider);
            while (pending.Count != 0)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                SqlEntity current = pending.Pop();
                if (!visited.Add(current))
                {
                    continue;
                }

                foreach (SqlEntity dependency in explicitDependencies[current])
                {
                    if (dependency == consumer)
                    {
                        return true;
                    }

                    pending.Push(dependency);
                }
            }

            return false;
        }

        void AddBoundary(int order)
        {
            SqlEntity[] boundaries = [.. _entities.Where(entity => entity.Order == order)];
            foreach (SqlEntity extra in boundaries.Skip(1))
            {
                Error(extra.OrderLocation ?? extra.Location, order == 1 ? SqlGraphDiagnostics.s_bootstrap : SqlGraphDiagnostics.s_finalize);
            }

            foreach (SqlEntity boundary in boundaries)
            {
                foreach (SqlEntity entity in _entities)
                {
                    if (entity == boundary)
                    {
                        continue;
                    }

                    if (order == 1)
                    {
                        entity.Dependencies.Add(boundary);
                    }
                    else
                    {
                        boundary.Dependencies.Add(entity);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Records the authored value that ordered one node after another, for cycle diagnostics.
    /// </summary>
    /// <param name="dependent">The node that must follow.</param>
    /// <param name="prerequisite">The node that must precede.</param>
    /// <param name="location">The authored dependency value, when present.</param>
    private void RecordEdge(SqlEntity dependent, SqlEntity prerequisite, Location? location)
    {
        if (location is not null && !_edges.ContainsKey((dependent, prerequisite)))
        {
            _edges.Add((dependent, prerequisite), location);
        }
    }

    /// <summary>
    /// Reports one deterministic cycle among blocked nodes, at an authored edge when one participates.
    /// </summary>
    /// <remarks>
    /// Every blocked node has a blocked prerequisite, so following the smallest blocked prerequisite must revisit a node.
    /// The reported cycle starts at its smallest internal key; its first explicit edge identifies the authored value to change.
    /// Consecutive nodes with the same authored name, such as a family and its generated members, are named once.
    /// </remarks>
    /// <param name="remaining">The unsatisfied prerequisite count of every node after ordering stopped.</param>
    private void ReportCycle(Dictionary<SqlEntity, int> remaining)
    {
        var path = new List<SqlEntity>();
        SqlEntity current = _entities.Where(entity => remaining[entity] != 0).OrderBy(static entity => entity.Key, StringComparer.Ordinal).First();
        int repeated = path.IndexOf(current);
        while (repeated < 0)
        {
            path.Add(current);
            current = current.Dependencies.Where(dependency => remaining[dependency] != 0)
                .OrderBy(static dependency => dependency.Key, StringComparer.Ordinal).First();
            repeated = path.IndexOf(current);
        }

        SqlEntity[] members = [.. path.Skip(repeated)];
        SqlEntity first = members.OrderBy(static entity => entity.Key, StringComparer.Ordinal).First();
        int start = Array.IndexOf(members, first);
        SqlEntity[] cycle = [.. members.Skip(start), .. members.Take(start), first];
        Location? location = null;
        for (int index = 0; index < cycle.Length - 1 && location is null; index++)
        {
            _edges.TryGetValue((cycle[index], cycle[index + 1]), out location);
        }

        List<string> names = [.. cycle.Take(cycle.Length - 1)
            .Where((member, index) => index == 0 || cycle[index - 1].DiagnosticName != member.DiagnosticName)
            .Select(static member => member.DiagnosticName)];

        if (names.Count > 1 && names[names.Count - 1] == names[0])
        {
            names.RemoveAt(names.Count - 1);
        }

        names.Add(names[0]);
        Error(location ?? first.Location, SqlGraphDiagnostics.s_cycle, string.Join(" -> ", names.Select(SqlGraphDiagnostics.Quote)));
    }

    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && SqlText.IsText(name!);
}
