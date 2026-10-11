using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Separates aggregate semantic validation, independent helper boundaries and typed SQL rendering from current graph composition.
/// </summary>
internal static class AggregatePipeline
{
    /// <summary>
    /// Registers independently cached aggregate and support rendering stages without retaining compiler state.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The validated contracts, current coordinates and cached rendering fragments.</returns>
    internal static IncrementalValueProvider<EquatableArray<Output>> Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Analysis> analysis = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgAggregateAttribute", static (node, _) => node is TypeDeclarationSyntax,
            static (attribute, token) => Analyze(attribute, token)).WithTrackingName("AggregateAnalysis");
        IncrementalValuesProvider<AggregateModel?> models = analysis.Select(static (value, _) => value.Model)
            .WithTrackingName("AggregateModel");
        IncrementalValuesProvider<AggregateSqlModel?> sqlModels = models.Select(static (value, _) =>
            value is null ? null : AggregateSqlModel.Create(value)).WithTrackingName("AggregateSqlModel");
        IncrementalValuesProvider<AggregateSqlEmission?> sql = sqlModels.Select(static (value, _) =>
            value is null ? null : AggregateSqlEmission.Create(value)).WithTrackingName("AggregateSqlEmission");
        IncrementalValuesProvider<BoundaryInput> boundaryModels = analysis.SelectMany(static (value, _) =>
            value.Helpers.Select(helper => new BoundaryInput(new(value.Identity, helper.Model.Role),
                AggregateHelperBoundaryModel.Create(helper.Model, helper.Callback))).ToImmutableArray())
            .WithTrackingName("AggregateHelperBoundaryModel");
        IncrementalValuesProvider<BoundaryOutput> boundaries = boundaryModels.Select(static (value, _) =>
            new BoundaryOutput(value.Slot, PgAggregateEmitter.CreateHelperBoundary(value.Model)))
            .WithTrackingName("AggregateHelperBoundaryEmission");
        IncrementalValuesProvider<HelperSqlInput> helperSqlModels = analysis.SelectMany(static (value, _) =>
            value.Helpers.Select(helper => new HelperSqlInput(new(value.Identity, helper.Model.Role),
                AggregateHelperSqlModel.Create(helper.Model, helper.Callback))).ToImmutableArray())
            .WithTrackingName("AggregateHelperSqlModel");
        IncrementalValuesProvider<HelperSqlOutput> helperSql = helperSqlModels.Select(static (value, _) =>
            new HelperSqlOutput(value.Slot, value.Model, value.Model.Emit())).WithTrackingName("AggregateHelperSqlEmission");
        return analysis.Collect().Combine(sql.Collect()).Combine(boundaries.Collect()).Combine(helperSql.Collect())
            .Select(static (value, _) => Compose(value.Left.Left.Left, value.Left.Left.Right, value.Left.Right, value.Right))
            .WithTrackingName("AggregateOutputs");
    }

    /// <summary>
    /// Analyzes one type using compiler symbols only during this invocation and retains original diagnostic contracts.
    /// </summary>
    private static Analysis Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var type = (INamedTypeSymbol)context.TargetSymbol;
        Compilation compilation = context.SemanticModel.Compilation;
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) => problems.Add(new(descriptor,
            GeneratorLocation.Create(location, compilation), new(arguments))), cancellationToken);
        AggregateDeclaration? declaration = AggregateDeclaration.Create(type, compilation, diagnostics);
        var helpers = new EquatableArray<HelperAnalysis>(declaration is null ? [] : declaration.Helpers.Values.Select(helper =>
            new HelperAnalysis(helper.Freeze(), DeclarationIdentity.Create(helper.Method), helper.Method.ToDisplayString(),
                helper.Method.Name, helper.Method.ContainingType.ToDisplayString(),
                SqlDeclarationOptions.Read(helper.Method.GetAttributes().FirstOrDefault(static attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "Ankus.PgFunctionAttribute"), compilation, cancellationToken),
                GeneratorLocation.Create(helper.Method.Locations.FirstOrDefault(), compilation),
                PgFunctionGenerator.GetCallbackName(PgFunctionGenerator.SymbolScope(type.ContainingAssembly) + ":" + helper.Invocation.Identity,
                    "aggregate_" + SqlText.SnakeCase(helper.Role)))));
        return new(DeclarationIdentity.Create(type), MetadataName(type), type.ToDisplayString(), type.Name,
            declaration?.Freeze(), helpers, new(AggregateDeclaration.SelectedMethods(type).SelectMany(static method =>
                new[] { DeclarationIdentity.Create(method), DeclarationIdentity.Create(method.OriginalDefinition) }).Distinct()),
            SqlDeclarationOptions.Read(context.Attributes[0], compilation, cancellationToken)!, GeneratorLocation.Create(type.Locations.FirstOrDefault(), compilation), new(problems));
    }

    /// <summary>
    /// Joins cached helper fragments by aggregate and role while keeping current source metadata outside rendering inputs.
    /// </summary>
    private static EquatableArray<Output> Compose(ImmutableArray<Analysis> analyses, ImmutableArray<AggregateSqlEmission?> sql,
        ImmutableArray<BoundaryOutput> boundaries, ImmutableArray<HelperSqlOutput> helpers)
    {
        ILookup<Slot, BoundaryOutput> boundaryLookup = boundaries.ToLookup(static value => value.Slot);
        ILookup<Slot, HelperSqlOutput> sqlLookup = helpers.ToLookup(static value => value.Slot);
        return new(analyses.Select((analysis, index) => new Output(analysis, sql[index], new(analysis.Helpers.Select(helper =>
        {
            var slot = new Slot(analysis.Identity, helper.Model.Role);
            HelperSqlOutput definition = sqlLookup[slot].First();
            return new HelperOutput(helper, boundaryLookup[slot].First().Emission, definition.Model, definition.Emission);
        })))));
    }

    /// <summary>
    /// Forms an unescaped metadata lookup name for current local datum-dependency discovery.
    /// </summary>
    private static string MetadataName(INamedTypeSymbol type)
    {
        var types = new Stack<string>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            types.Push(current.MetadataName);
        }

        var namespaces = new Stack<string>();
        for (INamespaceSymbol? current = type.ContainingNamespace; current is not null && !current.IsGlobalNamespace; current = current.ContainingNamespace)
        {
            namespaces.Push(current.MetadataName);
        }

        return (namespaces.Count == 0 ? string.Empty : string.Join(".", namespaces) + ".") + string.Join("+", types);
    }

    /// <summary>
    /// Retains aggregate semantics and current graph/diagnostic metadata independently of compiler objects.
    /// </summary>
    /// <param name="Identity">The exact assembly-qualified managed identity.</param>
    /// <param name="MetadataName">The current local metadata lookup name.</param>
    /// <param name="Display">The graph selection and provenance name.</param>
    /// <param name="Name">The managed diagnostic name.</param>
    /// <param name="Model">The validated aggregate contract, or null after failure.</param>
    /// <param name="Helpers">The validated support contracts and current graph metadata.</param>
    /// <param name="Selected">Support method and generic definition identities excluded from ordinary discovery even when validation fails.</param>
    /// <param name="Options">Authored aggregate dependencies and SQL replacement policy.</param>
    /// <param name="Location">Current aggregate declaration coordinates.</param>
    /// <param name="Problems">Original validation failures with detached current coordinates.</param>
    internal sealed record Analysis(DeclarationIdentity Identity, string MetadataName, string Display, string Name,
        AggregateModel? Model, EquatableArray<HelperAnalysis> Helpers, EquatableArray<DeclarationIdentity> Selected,
        SqlDeclarationOptions Options, GeneratorLocation? Location, EquatableArray<GeneratorProblem> Problems);

    /// <summary>
    /// Keeps support function semantics separate from graph options and current source attribution.
    /// </summary>
    /// <param name="Model">The complete validated support contract.</param>
    /// <param name="Identity">The exact managed support method identity.</param>
    /// <param name="Display">The method graph and provenance name.</param>
    /// <param name="Name">The unqualified managed selection name.</param>
    /// <param name="Container">The managed declaration container name.</param>
    /// <param name="Options">Optional authored support SQL and dependency policy.</param>
    /// <param name="Location">Current support declaration coordinates.</param>
    /// <param name="Callback">The assembly-specific managed entry identity.</param>
    internal sealed record HelperAnalysis(AggregateHelperModel Model, DeclarationIdentity Identity, string Display,
        string Name, string Container, SqlDeclarationOptions? Options, GeneratorLocation? Location, string Callback);

    /// <summary>
    /// Identifies a rendered support role without conflating different aggregate containers.
    /// </summary>
    /// <param name="Aggregate">The exact aggregate declaration identity.</param>
    /// <param name="Role">The PostgreSQL support role.</param>
    internal sealed record Slot(DeclarationIdentity Aggregate, string Role);

    /// <summary>
    /// Carries minimal keyed conversion and invocation inputs to cached boundary rendering.
    /// </summary>
    /// <param name="Slot">The support role identity.</param>
    /// <param name="Model">The immutable managed/native boundary inputs.</param>
    internal sealed record BoundaryInput(Slot Slot, AggregateHelperBoundaryModel Model);

    /// <summary>
    /// Carries independently rendered managed/native helper fragments.
    /// </summary>
    /// <param name="Slot">The support role identity.</param>
    /// <param name="Emission">The cached managed/native/export fragments.</param>
    internal sealed record BoundaryOutput(Slot Slot, PgAggregateEmitter.HelperEmission Emission);

    /// <summary>
    /// Carries minimal keyed SQL support inputs to cached rendering.
    /// </summary>
    /// <param name="Slot">The support role identity.</param>
    /// <param name="Model">The immutable support SQL inputs.</param>
    internal sealed record HelperSqlInput(Slot Slot, AggregateHelperSqlModel Model);

    /// <summary>
    /// Carries independently rendered support SQL and its planner-support signature policy.
    /// </summary>
    /// <param name="Slot">The support role identity.</param>
    /// <param name="Model">The SQL signature and execution policy.</param>
    /// <param name="Emission">The escaped typed SQL fragments.</param>
    internal sealed record HelperSqlOutput(Slot Slot, AggregateHelperSqlModel Model, FunctionSqlEmission Emission);

    /// <summary>
    /// Supplies one current support declaration and its independently cached boundary and SQL rendering.
    /// </summary>
    /// <param name="Analysis">The validated support contract and current graph coordinates.</param>
    /// <param name="Boundary">The managed/native/export fragments.</param>
    /// <param name="SqlModel">The planner-support SQL signature policy.</param>
    /// <param name="Sql">The escaped typed support SQL fragments.</param>
    internal sealed record HelperOutput(HelperAnalysis Analysis, PgAggregateEmitter.HelperEmission Boundary,
        AggregateHelperSqlModel SqlModel, FunctionSqlEmission Sql);

    /// <summary>
    /// Supplies one current aggregate declaration and its cached aggregate and support rendering.
    /// </summary>
    /// <param name="Analysis">The detached semantic validation and current coordinates.</param>
    /// <param name="Sql">The aggregate DDL fragments, absent after validation fails.</param>
    /// <param name="Helpers">The complete independently rendered support functions.</param>
    internal sealed record Output(Analysis Analysis, AggregateSqlEmission? Sql, EquatableArray<HelperOutput> Helpers);
}
