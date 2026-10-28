using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Detaches operator and cast validation from source coordinates and caches their typed SQL rendering.
/// </summary>
internal static class OperatorCastPipeline
{
    /// <summary>
    /// Registers semantic catalog analysis and independent per-declaration rendering.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The detached declarations, cached fragments and current graph metadata.</returns>
    internal static IncrementalValueProvider<EquatableArray<Output>> Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<EquatableArray<Analysis>> operators = ForAttribute(context, "Ankus.PgOperatorAttribute");
        IncrementalValuesProvider<EquatableArray<Analysis>> casts = ForAttribute(context, "Ankus.PgCastAttribute");
        IncrementalValuesProvider<Analysis> analysis = operators.Collect().Combine(casts.Collect())
            .SelectMany(static (value, _) => value.Left.Concat(value.Right).SelectMany(static declarations => declarations).Distinct())
            .WithTrackingName("OperatorCastAnalysis");
        IncrementalValuesProvider<OperatorCastModel?> models = analysis.Select(static (value, _) => value.Model)
            .WithTrackingName("OperatorCastModel");
        IncrementalValuesProvider<OperatorCastEmission?> emission = models.Select(static (value, _) =>
            value is null ? null : OperatorCastEmission.Create(value)).WithTrackingName("OperatorCastEmission");
        return analysis.Collect().Combine(emission.Collect()).Select(static (value, _) =>
            new EquatableArray<Output>(value.Left.Select((item, index) => new Output(item, value.Right[index]))));
    }

    /// <summary>
    /// Discovers either attached role using the same complete authored attribute order.
    /// </summary>
    private static IncrementalValuesProvider<EquatableArray<Analysis>> ForAttribute(IncrementalGeneratorInitializationContext context, string name)
        => context.SyntaxProvider.ForAttributeWithMetadataName(name, static (node, _) => node is MethodDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax,
            static (attribute, token) => Analyze(attribute, token));

    /// <summary>
    /// Reads attached catalog contracts only for a valid ordinary backing function.
    /// </summary>
    private static EquatableArray<Analysis> Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        FunctionPipeline.FunctionAnalysis? function = FunctionPipeline.Analyze(context, cancellationToken);
        if (function?.Model is null)
        {
            return new([]);
        }

        var method = (IMethodSymbol)context.TargetSymbol;
        return new(method.GetAttributes().Where(static attribute => attribute.AttributeClass?.ToDisplayString() is
            "Ankus.PgOperatorAttribute" or "Ankus.PgCastAttribute").Select(attribute =>
                OperatorCastDeclaration.Analyze(method, function, attribute, context.SemanticModel.Compilation, cancellationToken)));
    }

    /// <summary>
    /// Separates catalog rendering contracts from current declaration identity, dependencies and diagnostics.
    /// </summary>
    /// <param name="Identity">The backing method's assembly-qualified managed identity.</param>
    /// <param name="Display">The backing method's deterministic display and graph key.</param>
    /// <param name="Kind">The operator or cast declaration kind.</param>
    /// <param name="Signature">The catalog signature used to detect duplicates and select declarations.</param>
    /// <param name="Names">The authored selection names in declaration order.</param>
    /// <param name="BooleanOperator">Whether this declaration participates in the boolean-operator inventory.</param>
    /// <param name="Model">The validated catalog contract, or null after validation failed.</param>
    /// <param name="Options">The detached graph dependency options.</param>
    /// <param name="Problems">The detached semantic validation problems.</param>
    /// <param name="Location">The current backing declaration coordinates.</param>
    internal sealed record Analysis(DeclarationIdentity Identity, string Display, string Kind, string Signature, EquatableArray<string> Names,
        bool BooleanOperator, OperatorCastModel? Model, SqlDeclarationOptions Options, EquatableArray<GeneratorProblem> Problems,
        GeneratorLocation? Location);

    /// <summary>
    /// Supplies one attached declaration's current metadata and cached SQL fragments.
    /// </summary>
    /// <param name="Analysis">The current detached metadata and diagnostics.</param>
    /// <param name="Emission">The independently rendered SQL, or null after validation failed.</param>
    internal sealed record Output(Analysis Analysis, OperatorCastEmission? Emission);
}
