using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Detaches configuration analysis from compiler state and caches declaration rendering per property.
/// </summary>
internal static class GucPipeline
{
    /// <summary>
    /// Registers configuration discovery, validation and independent rendering.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The detached contracts, current diagnostic metadata and reusable source fragments.</returns>
    internal static IncrementalValueProvider<EquatableArray<Output>> Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Analysis> analysis = Discover(context, "Ankus.PgGucBoolAttribute").Collect()
            .Combine(Discover(context, "Ankus.PgGucIntAttribute").Collect())
            .Combine(Discover(context, "Ankus.PgGucRealAttribute").Collect())
            .Combine(Discover(context, "Ankus.PgGucStringAttribute").Collect())
            .Combine(Discover(context, "Ankus.PgGucEnumAttribute").Collect())
            .SelectMany(static (value, _) => value.Left.Left.Left.Left.AddRange(value.Left.Left.Left.Right)
                .AddRange(value.Left.Left.Right).AddRange(value.Left.Right).AddRange(value.Right))
            .WithTrackingName("GucAnalysis");
        IncrementalValuesProvider<GucModel?> models = analysis.Select(static (value, _) => value.Model).WithTrackingName("GucModel");
        IncrementalValuesProvider<GucEmission?> emission = models.Select(static (value, _) => value is null ? null : GucEmission.Create(value))
            .WithTrackingName("GucEmission");
        return analysis.Collect().Combine(emission.Collect()).Select(static (value, _) =>
            new EquatableArray<Output>(value.Left.Select((item, index) => new Output(item, value.Right[index]))));
    }

    /// <summary>
    /// Detects calls that define configuration parameters at run time, which require the native registry.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>Whether any source calls a <c>PgGucRegistry.Define</c> method.</returns>
    internal static IncrementalValueProvider<bool> RegisterRuntimeUse(IncrementalGeneratorInitializationContext context)
        => context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax invocation && IsRuntimeDefinitionName(invocation.Expression),
                static (syntax, cancellationToken) => syntax.SemanticModel.GetSymbolInfo(syntax.Node, cancellationToken).Symbol is IMethodSymbol
                {
                    ContainingType: { Name: "PgGucRegistry", ContainingNamespace: { Name: "Ankus", ContainingNamespace.IsGlobalNamespace: true } },
                })
            .Where(static used => used)
            .Collect()
            .Select(static (uses, _) => !uses.IsEmpty)
            .WithTrackingName("GucRuntimeUse");

    /// <summary>
    /// Accepts qualified, aliased and statically imported definition calls before semantic confirmation.
    /// </summary>
    private static bool IsRuntimeDefinitionName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText.StartsWith("Define", StringComparison.Ordinal),
        SimpleNameSyntax name => name.Identifier.ValueText.StartsWith("Define", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>
    /// Validates one setting while compiler objects remain confined to this transient operation.
    /// </summary>
    private static Analysis? Analyze(GeneratorAttributeSyntaxContext syntax, CancellationToken cancellationToken)
    {
        var property = syntax.TargetSymbol as IPropertySymbol;
        if (property is null || property.GetAttributes().Any(NativeCallbackDeclaration.IsAttribute))
        {
            return null;
        }

        Compilation compilation = syntax.SemanticModel.Compilation;
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            problems.Add(new(descriptor, GeneratorLocation.Create(location, compilation), new(arguments))), cancellationToken);
        GucDeclaration? declaration = GucDeclaration.Create(property, compilation, diagnostics);
        return new(DeclarationIdentity.Create(property), property.Name, declaration is null ? null : GucModel.Create(declaration),
            new(problems), GeneratorLocation.Create(DatumMappingDiagnostics.Argument(syntax.Attributes[0], 0, cancellationToken), compilation));
    }

    /// <summary>
    /// Uses Roslyn's semantic attribute index for one supported configuration kind.
    /// </summary>
    /// <param name="context">The registration context.</param>
    /// <param name="metadataName">The exact public setting attribute.</param>
    /// <returns>Detached analyses for matching property and indexer declarations.</returns>
    private static IncrementalValuesProvider<Analysis> Discover(IncrementalGeneratorInitializationContext context, string metadataName)
        => context.SyntaxProvider.ForAttributeWithMetadataName(metadataName,
            static (node, _) => node is PropertyDeclarationSyntax or IndexerDeclarationSyntax,
            static (syntax, token) => Analyze(syntax, token))
            .Where(static value => value is not null).Select(static (value, _) => value!);

    /// <summary>
    /// Resolves current diagnostic locations and selects deterministic registration order without caching native values.
    /// </summary>
    /// <param name="outputs">The currently discovered setting declarations.</param>
    /// <param name="compilation">The current compilation owning diagnostic coordinates.</param>
    /// <param name="context">The diagnostic output destination.</param>
    /// <returns>The validated, distinct current settings in PostgreSQL name order.</returns>
    internal static List<Output> Select(EquatableArray<Output> outputs, GeneratorSourceResolver compilation, GeneratorDiagnostics context)
    {
        var result = new List<Output>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<DeclarationIdentity>();
        foreach (Output output in outputs)
        {
            Analysis analysis = output.Analysis;
            if (!identities.Add(analysis.Identity))
            {
                continue;
            }

            foreach (GeneratorProblem problem in analysis.Problems)
            {
                problem.Report(compilation, context);
            }

            if (analysis.Model is not { } model)
            {
                continue;
            }

            if (!names.Add(GucDeclaration.Fold(model.Name)))
            {
                GeneratorDiagnostics diagnostics = context;
                diagnostics.Report(GucDeclarationDiagnostics.DuplicateName, analysis.Location?.Resolve(compilation), model.Name);
                continue;
            }

            result.Add(output);
        }

        result.Sort(static (left, right) => string.CompareOrdinal(GucDeclaration.Fold(left.Analysis.Model!.Name), GucDeclaration.Fold(right.Analysis.Model!.Name)));
        return result;
    }

    /// <summary>
    /// Retains source ownership and detached validation failures separately from rendering contracts.
    /// </summary>
    /// <param name="Identity">The property identity for canonical declaration selection.</param>
    /// <param name="Name">The managed property name for current duplicate diagnostics.</param>
    /// <param name="Model">The validated immutable setting contract, or null on failure.</param>
    /// <param name="Problems">The detached validation problems.</param>
    /// <param name="Location">The current declaration coordinates.</param>
    internal sealed record Analysis(DeclarationIdentity Identity, string Name, GucModel? Model, EquatableArray<GeneratorProblem> Problems,
        GeneratorLocation? Location);

    /// <summary>
    /// Supplies one setting's current metadata and independently cached source fragments.
    /// </summary>
    /// <param name="Analysis">The current declaration and diagnostics.</param>
    /// <param name="Emission">The cached source, or null after invalid validation.</param>
    internal sealed record Output(Analysis Analysis, GucEmission? Emission);

    /// <summary>
    /// Composes detached native callbacks and configuration declarations without retaining property symbols.
    /// </summary>
    /// <param name="Callbacks">The detached native callback declarations and cached fragments.</param>
    /// <param name="Settings">The detached configuration declarations.</param>
    /// <param name="RuntimeSettings">Whether source code defines configuration parameters at run time.</param>
    internal sealed record PropertyInputs(EquatableArray<NativeCallbackPipeline.Output> Callbacks, EquatableArray<Output> Settings,
        bool RuntimeSettings);
}
