using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

internal sealed partial class SqlGraph
{
    private readonly Dictionary<DeclarationIdentity, List<SqlEntity>> _declarations = [];
    private readonly List<(SqlEntity Source, SqlEntity Target)> _inheritedRequirements = [];

    /// <summary>
    /// Keeps aggregate prerequisites ahead of every helper, including helpers shared by multiple aggregates.
    /// </summary>
    internal void InheritRequirements(SqlEntity source, SqlEntity target) => _inheritedRequirements.Add((source, target));

    /// <summary>
    /// Associates an exact managed declaration with a SQL node, including shared schema aliases.
    /// </summary>
    internal void Register(ISymbol declaration, SqlEntity entity)
        => Register(DeclarationIdentity.Create(declaration), declaration.ToDisplayString(), entity);

    /// <summary>
    /// Associates detached semantic identity and provenance with one installation node.
    /// </summary>
    /// <param name="declaration">The exact assembly-qualified declaration identity.</param>
    /// <param name="display">The managed name used for source provenance.</param>
    /// <param name="entity">The installation node.</param>
    internal void Register(DeclarationIdentity declaration, string display, SqlEntity entity)
    {
        entity.ManagedSources.Add(display);
        if (!_declarations.TryGetValue(declaration, out List<SqlEntity>? entities))
        {
            entities = [];
            _declarations.Add(declaration, entities);
        }

        if (!entities.Contains(entity))
        {
            entities.Add(entity);
        }
    }

    /// <summary>
    /// Identifies explicit dependencies whose destination is a C# declaration. Other dependencies name a string ID.
    /// </summary>
    internal static bool IsReference(AttributeData attribute)
        => attribute.AttributeClass?.ToDisplayString() is "Ankus.PgRequiresAttribute" or "Ankus.PgBeforeAttribute" or "Ankus.PgSupportFunctionAttribute";

    /// <summary>
    /// Resolves typed edges after all declarations exist and before ordering or encoding the graph.
    /// </summary>
    internal void ResolveReferences(IEnumerable<SqlReferenceModel> references, GeneratorSourceResolver compilation)
    {
        foreach (SqlReferenceModel reference in references)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            if (reference.Error is not null)
            {
                ReferenceError(reference, reference.Error, compilation);
            }
            else if (reference.Kind == "PgSupportFunctionAttribute")
            {
                BindPlannerSupport(reference, compilation);
            }
            else
            {
                BindDependency(reference, compilation);
            }
        }

        foreach ((SqlEntity source, SqlEntity target) in _inheritedRequirements)
        {
            target.Inherit(source.Requires);
            target.RequiredDeclarations.UnionWith(source.RequiredDeclarations.Where(required => required != target));
        }
    }

    /// <summary>
    /// Orders one source declaration relative to its compiler-selected target.
    /// </summary>
    private void BindDependency(SqlReferenceModel reference, GeneratorSourceResolver compilation)
    {
        SqlEntity? source = Source(reference, compilation);
        if (source is null)
        {
            return;
        }

        if (reference.TargetError is not null)
        {
            ReferenceError(reference, reference.TargetError, compilation);
            return;
        }

        SqlEntity? target = Target(reference, compilation);
        if (target is null)
        {
            return;
        }

        if (reference.Kind == "PgBeforeAttribute")
        {
            target.DeclaredDependencies.Add(source);
            RecordEdge(target, source, Resolve(reference.Location));
        }
        else
        {
            source.RequiredDeclarations.Add(target);
            RecordEdge(source, target, Resolve(reference.Location));
        }
    }

    /// <summary>
    /// Applies one ordinary support function to every SQL function generated from the attributed method.
    /// </summary>
    /// <remarks>
    /// An inherited aggregate helper can generate one SQL function for each aggregate that uses it; each one calls the same method.
    /// </remarks>
    private void BindPlannerSupport(SqlReferenceModel reference, GeneratorSourceResolver compilation)
    {
        _declarations.TryGetValue(reference.Source.Identity, out List<SqlEntity>? entities);
        SqlEntity[] sources = [.. (entities ?? []).Where(static entity => entity.Function is not null)];
        if (sources.Length == 0)
        {
            ReferenceError(reference, new(SqlReferenceProblemKind.SupportSource), compilation);
            return;
        }

        if (reference.TargetError is not null)
        {
            ReferenceError(reference, reference.TargetError, compilation);
            return;
        }

        SqlReferenceModel.Declaration declaration = reference.Target!;
        if (!_declarations.TryGetValue(declaration.Identity, out List<SqlEntity>? targets))
        {
            ReferenceError(reference, new(SqlReferenceProblemKind.MissingSqlObject, declaration.Display, reference.TargetLocation), compilation);
            return;
        }

        if (reference.ExternalSupport)
        {
            ReferenceError(reference, new(SqlReferenceProblemKind.SupportConflict), compilation);
            return;
        }

        SqlEntity[] functions = [.. targets.Where(static entity => entity.Function is not null)];
        SqlEntity[] ordinary = [.. functions.Where(static entity => !entity.Function!.RequiresAggregateContext)];
        if (ordinary.Length == 0 && functions.Length != 0)
        {
            ReferenceError(reference, new(SqlReferenceProblemKind.SupportAggregate, Location: reference.TargetLocation), compilation);
            return;
        }

        if (ordinary.Length != 1 || ordinary[0].Function is not { IsPlannerSupport: true } support)
        {
            ReferenceError(reference, new(SqlReferenceProblemKind.SupportSignature, Location: reference.TargetLocation), compilation);
            return;
        }

        foreach (SqlEntity source in sources)
        {
            source.RequiredDeclarations.Add(ordinary[0]);
            RecordEdge(source, ordinary[0], Resolve(reference.Location));
            source.Function!.SetPlannerSupport(support.Declaration.TemplateName);
        }
    }

    private SqlEntity? Source(SqlReferenceModel reference, GeneratorSourceResolver compilation)
    {
        SqlReferenceModel.Declaration declaration = reference.Source;
        string? id = reference.DeclarationId;
        _declarations.TryGetValue(declaration.Identity, out List<SqlEntity>? entities);
        if (!declaration.Assembly && entities is null)
        {
            return ReferenceError(reference, new(SqlReferenceProblemKind.SourceMissingSqlObject, declaration.Display), compilation);
        }

        if (id is not null)
        {
            if (!ValidName(id))
            {
                return ReferenceError(reference, new(SqlReferenceProblemKind.DeclarationId, Location: reference.DeclarationIdLocation), compilation);
            }

            SqlEntity[] matches = [.. (declaration.Assembly ? _entities : entities!).Where(entity => entity.Names.Contains(id))];
            return matches.Length == 1 ? matches[0] : ReferenceError(reference,
                new(SqlReferenceProblemKind.DeclarationSelection, id, reference.DeclarationIdLocation), compilation);
        }

        if (declaration.Assembly)
        {
            return ReferenceError(reference, new(SqlReferenceProblemKind.AssemblyDeclaration), compilation);
        }

        SqlEntity[] primary = Primary(declaration, entities!);
        return primary.Length == 1 ? primary[0] : ReferenceError(reference,
            new(SqlReferenceProblemKind.SourceAmbiguousSqlObject, declaration.Display), compilation);
    }

    private SqlEntity? Target(SqlReferenceModel reference, GeneratorSourceResolver compilation)
    {
        SqlReferenceModel.Declaration declaration = reference.Target!;
        if (!_declarations.TryGetValue(declaration.Identity, out List<SqlEntity>? entities))
        {
            return ReferenceError(reference, new(SqlReferenceProblemKind.MissingSqlObject, declaration.Display, reference.TargetLocation), compilation);
        }

        SqlEntity[] primary = Primary(declaration, entities);
        return primary.Length == 1 ? primary[0] : ReferenceError(reference,
            new(SqlReferenceProblemKind.AmbiguousSqlObject, declaration.Display, reference.TargetLocation), compilation);
    }

    /// <summary>
    /// Prefers a method's function or a type's schema, type, enum or aggregate over attached declarations.
    /// </summary>
    private static SqlEntity[] Primary(SqlReferenceModel.Declaration declaration, List<SqlEntity> entities)
    {
        SqlEntity[] primary = [.. entities.Where(entity => declaration.Method
            ? entity.Kind == "function" : entity.Kind is "schema" or "type" or "enum" or "aggregate")];
        return primary.Length == 0 ? [.. entities] : primary;
    }

    private SqlEntity? ReferenceError(SqlReferenceModel reference, SqlReferenceProblem problem, GeneratorSourceResolver compilation)
    {
        _invalid = true;
        DiagnosticDescriptor descriptor = SqlReferenceDiagnostics.Descriptor(problem.Kind);
        Location? location = (problem.Location ?? reference.Location)?.Resolve(compilation);
        if (problem.Identity is null)
        {
            _context.Report(descriptor, location);
        }
        else
        {
            _context.Report(descriptor, location, problem.Identity);
        }

        return null;
    }
}
