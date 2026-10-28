using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Separates finite closed mapping discovery, registration rendering and current provider composition.
/// </summary>
internal static class DatumPipeline
{
    /// <summary>
    /// Registers semantic discovery and independently cached lazy registration values.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <param name="methods">The selected attributed method inventory.</param>
    /// <param name="local">The locally attributed scalar roots.</param>
    /// <param name="ranges">The locally attributed scalar range bounds.</param>
    /// <param name="aggregates">The selected aggregate container identities.</param>
    /// <param name="derived">The locally declared derived-operator roots.</param>
    /// <param name="includeTests">Whether backend test signatures participate in native publication.</param>
    /// <returns>The detached validated mappings, derived semantics and cached registration statements.</returns>
    internal static IncrementalValueProvider<Output> Register(IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<ImmutableArray<IMethodSymbol>> methods,
        IncrementalValueProvider<ImmutableArray<INamedTypeSymbol>> local,
        IncrementalValueProvider<ImmutableArray<INamedTypeSymbol>> ranges,
        IncrementalValueProvider<EquatableArray<AggregatePipeline.Output>> aggregates,
        IncrementalValueProvider<ImmutableArray<INamedTypeSymbol>> derived,
        IncrementalValueProvider<bool> includeTests)
    {
        IncrementalValueProvider<Inputs> inputs = context.CompilationProvider.Combine(methods)
            .Select(static (value, _) => new Inputs(value.Left, value.Right, [], [], new([]), [], false));
        inputs = inputs.Combine(local).Select(static (value, _) => value.Left with { Local = value.Right });
        inputs = inputs.Combine(ranges).Select(static (value, _) => value.Left with { Ranges = value.Right });
        inputs = inputs.Combine(aggregates).Select(static (value, _) => value.Left with { Aggregates = value.Right });
        inputs = inputs.Combine(derived).Select(static (value, _) => value.Left with { Derived = value.Right });
        inputs = inputs.Combine(includeTests).Select(static (value, _) => value.Left with { IncludeTests = value.Right });
        IncrementalValueProvider<Analysis> analysis = inputs.Select(static (value, token) => Analyze(value, token))
            .WithTrackingName("DatumAnalysis");
        IncrementalValuesProvider<DatumRegistrationModel> registrations = analysis.SelectMany(static (value, _) =>
            value.Models.OrderBy(static model => model.RangeBound is not null).Select(static model => model.Registration).ToImmutableArray())
            .WithTrackingName("DatumRegistrationModel");
        IncrementalValuesProvider<string> source = registrations.Select(static (value, _) => value.Emit())
            .WithTrackingName("DatumRegistrationEmission");
        IncrementalValuesProvider<HelperInput> helperModels = analysis.SelectMany(static (value, _) => value.Derived.SelectMany(model =>
            DerivedHelperModel.Create(model).Select(helper => new HelperInput(new(model.Identity, model.Managed, helper.Role), helper))).ToImmutableArray())
            .WithTrackingName("DerivedHelperModel");
        IncrementalValuesProvider<HelperOutput> helpers = helperModels.Select(static (value, _) => new HelperOutput(value.Slot, value.Model.Emit()))
            .WithTrackingName("DerivedHelperEmission");
        IncrementalValuesProvider<SqlInput> sqlModels = analysis.SelectMany(static (value, _) => value.Derived.Select(model =>
            new SqlInput(model.Identity, model.Managed, DerivedSqlModel.Create(model))).ToImmutableArray())
            .WithTrackingName("DerivedSqlModel");
        IncrementalValuesProvider<SqlOutput> sql = sqlModels.Select(static (value, _) => new SqlOutput(value.Type, value.Managed,
            value.Model is null ? null : DerivedSqlEmission.Create(value.Model))).WithTrackingName("DerivedSqlEmission");
        return analysis.Combine(source.Collect()).Combine(helpers.Collect()).Combine(sql.Collect()).Select(static (value, _) =>
            new Output(value.Left.Left.Left, new(value.Left.Left.Right), new(value.Left.Right), new(value.Right)))
            .WithTrackingName("DatumOutputs");
    }

    /// <summary>
    /// Resolves all compiler symbols within this invocation and freezes independently validated closed roots.
    /// </summary>
    private static Analysis Analyze(Inputs input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Compilation compilation = input.Compilation;
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) => problems.Add(new(descriptor,
            GeneratorLocation.Create(location, compilation), new(arguments))), cancellationToken);
        ImmutableArray<IMethodSymbol> methods = [.. input.Methods.Where(method =>
            (input.IncludeTests || !PgTestDeclaration.IsTest(method)) && !method.GetAttributes().Any(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "Ankus.PgBenchmarkAttribute"))];
        ImmutableArray<INamedTypeSymbol> aggregates = [.. input.Aggregates.Select(value =>
            compilation.Assembly.GetTypeByMetadataName(value.Analysis.MetadataName)).OfType<INamedTypeSymbol>()];
        List<DatumTypeDeclaration>? declarations = DatumTypeDeclaration.Discover(compilation, input.Local, input.Ranges,
            methods, aggregates, [.. compilation.Assembly.GetAttributes().Where(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "Ankus.PgSqlTypeProviderAttribute")], diagnostics);
        bool declared = !input.Local.IsEmpty || !input.Ranges.IsEmpty || !input.Derived.IsEmpty;
        if (declarations is null)
        {
            return new(declared, false, new([]), new([]), new(problems));
        }

        IEnumerable<INamedTypeSymbol> derived = input.Derived.Where(static type =>
            !DatumTypeDeclaration.IsMapped(type) || DatumTypeDeclaration.IsClosed(type))
            .Concat(declarations.Select(static declaration => declaration.Type).Where(static type =>
                type.GetAttributes().Any(DerivedOperatorDeclaration.IsAttribute)))
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).OrderBy(static type => type.ToDisplayString(), StringComparer.Ordinal);
        return new(declared, true, new(declarations.Select(declaration => declaration.Freeze(compilation))),
            new(derived.Select(type => DerivedOperatorModel.Create(type, compilation, cancellationToken))), new(problems));
    }

    /// <summary>
    /// Holds transient compiler inputs only before the immutable semantic boundary.
    /// </summary>
    /// <param name="Compilation">The current compiler state.</param>
    /// <param name="Methods">The attributed method inventory.</param>
    /// <param name="Local">The local scalar roots.</param>
    /// <param name="Ranges">The local scalar range bounds.</param>
    /// <param name="Aggregates">The selected aggregate containers.</param>
    /// <param name="Derived">The local derived-operator roots.</param>
    /// <param name="IncludeTests">Whether backend tests participate in native publication.</param>
    private sealed record Inputs(Compilation Compilation, ImmutableArray<IMethodSymbol> Methods,
        ImmutableArray<INamedTypeSymbol> Local, ImmutableArray<INamedTypeSymbol> Ranges,
        EquatableArray<AggregatePipeline.Output> Aggregates,
        ImmutableArray<INamedTypeSymbol> Derived, bool IncludeTests);

    /// <summary>
    /// Carries semantic values and current diagnostic coordinates without retaining symbols or syntax trees.
    /// </summary>
    /// <param name="Declared">Whether local mapping or derived declarations require extension generation.</param>
    /// <param name="Valid">Whether complete finite mapping preflight succeeded.</param>
    /// <param name="Models">The validated closed scalar and range contracts.</param>
    /// <param name="Derived">The selected derived-operator semantics.</param>
    /// <param name="Problems">The original validation diagnostics and current coordinates.</param>
    internal sealed record Analysis(bool Declared, bool Valid, EquatableArray<DatumTypeModel> Models,
        EquatableArray<DerivedOperatorModel> Derived, EquatableArray<GeneratorProblem> Problems);

    /// <summary>
    /// Supplies current semantic metadata and cached registrations to extension composition.
    /// </summary>
    /// <param name="Analysis">The complete detached semantic result.</param>
    /// <param name="Registrations">The scalar-first lazy registration statements.</param>
    /// <param name="Helpers">The independently cached derived helper boundaries.</param>
    /// <param name="Sql">The independently cached derived catalog declarations.</param>
    internal sealed record Output(Analysis Analysis, EquatableArray<string> Registrations, EquatableArray<HelperOutput> Helpers,
        EquatableArray<SqlOutput> Sql);

    /// <summary>
    /// Associates one exact managed root with its minimal catalog rendering contract.
    /// </summary>
    /// <param name="Type">The structural declaring type identity.</param>
    /// <param name="Managed">The managed spelling preserving tuple labels and native integer distinctions.</param>
    /// <param name="Model">The SQL-only contract, absent when no catalog identity is available.</param>
    internal sealed record SqlInput(DeclarationIdentity Type, string Managed, DerivedSqlModel? Model);

    /// <summary>
    /// Keeps cached catalog fragments associated with their current declaring root.
    /// </summary>
    /// <param name="Type">The structural declaring type identity.</param>
    /// <param name="Managed">The exact managed declaring type spelling.</param>
    /// <param name="Emission">The cached catalog grammar, absent when its identity is unavailable.</param>
    internal sealed record SqlOutput(DeclarationIdentity Type, string Managed, DerivedSqlEmission? Emission);

    /// <summary>
    /// Identifies a selected value-semantic helper independently of declaration coordinates.
    /// </summary>
    /// <param name="Type">The exact declaring managed identity.</param>
    /// <param name="Managed">The managed spelling preserving tuple labels and native integer distinctions.</param>
    /// <param name="Role">The comparison or hashing operation.</param>
    internal sealed record HelperSlot(DeclarationIdentity Type, string Managed, string Role);

    /// <summary>
    /// Joins one stable role identity with the minimal conversion and invocation contract.
    /// </summary>
    /// <param name="Slot">The declaring type and operation.</param>
    /// <param name="Model">The detached helper contract.</param>
    internal sealed record HelperInput(HelperSlot Slot, DerivedHelperModel Model);

    /// <summary>
    /// Keeps a cached helper fragment associated with its selected role during current graph composition.
    /// </summary>
    /// <param name="Slot">The declaring type and operation.</param>
    /// <param name="Emission">The immutable helper boundary.</param>
    internal sealed record HelperOutput(HelperSlot Slot, DerivedHelperEmission Emission);
}
