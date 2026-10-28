using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Separates callback semantic validation, comparable contracts and reusable managed dispatch rendering.
/// </summary>
internal static class NativeCallbackPipeline
{
    /// <summary>
    /// Registers current declaration diagnostics and independently cached exact transport rendering.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The current callback inventory and independently cached property fragments.</returns>
    internal static IncrementalValueProvider<EquatableArray<Output>> Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Analysis> analysis = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgNativeCallbackAttribute",
            static (node, _) => node is PropertyDeclarationSyntax or IndexerDeclarationSyntax,
            static (attributeContext, token) => Analyze(attributeContext, token)).WithTrackingName("NativeCallbackAnalysis");
        IncrementalValuesProvider<NativeCallbackModel?> models = analysis.Select(static (value, _) => value.Model)
            .WithTrackingName("NativeCallbackModel");
        IncrementalValuesProvider<string?> emission = models.Select(static (value, _) => value is null ? null : PgNativeCallbackEmitter.Emit(value))
            .WithTrackingName("NativeCallbackEmission");
        return analysis.Collect().Combine(emission.Collect()).Select(static (value, _) =>
            new EquatableArray<Output>(value.Left.Select((item, index) => new Output(item, value.Right[index]))));
    }

    /// <summary>
    /// Confines compiler objects to current semantic validation and detaches diagnostic coordinates.
    /// </summary>
    private static Analysis Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        var property = (IPropertySymbol)context.TargetSymbol;
        Compilation compilation = context.SemanticModel.Compilation;
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            problems.Add(new(descriptor, GeneratorLocation.Create(location, compilation), new(arguments))), cancellationToken);
        NativeCallbackDeclaration? declaration = NativeCallbackDeclaration.Create(property, diagnostics);
        return new(DeclarationIdentity.Create(property), declaration is null ? null : NativeCallbackModel.Create(declaration), new(problems));
    }

    /// <summary>
    /// Reports diagnostics against the current compilation and selects each canonical valid callback once.
    /// </summary>
    /// <param name="outputs">The current property inventory.</param>
    /// <param name="compilation">The compilation owning current diagnostic locations.</param>
    /// <param name="context">The diagnostic production destination.</param>
    /// <returns>The valid canonical callback outputs in discovery order.</returns>
    internal static List<Output> Select(EquatableArray<Output> outputs, GeneratorSourceResolver compilation, GeneratorDiagnostics context)
    {
        var result = new List<Output>();
        var identities = new HashSet<DeclarationIdentity>();
        foreach (Output output in outputs)
        {
            if (!identities.Add(output.Analysis.Identity))
            {
                continue;
            }

            foreach (GeneratorProblem problem in output.Analysis.Problems)
            {
                problem.Report(compilation, context);
            }

            if (output.Analysis.Model is not null)
            {
                result.Add(output);
            }
        }

        return result;
    }

    /// <summary>
    /// Retains declaration identity and current diagnostics separately from the rendering model.
    /// </summary>
    /// <param name="Identity">The canonical managed property identity.</param>
    /// <param name="Model">The complete value contract, or null on validation failure.</param>
    /// <param name="Problems">The detached current validation failures.</param>
    internal sealed record Analysis(DeclarationIdentity Identity, NativeCallbackModel? Model, EquatableArray<GeneratorProblem> Problems);

    /// <summary>
    /// Supplies current analysis and an independently cached managed property and dispatcher fragment.
    /// </summary>
    /// <param name="Analysis">The current validated or rejected declaration.</param>
    /// <param name="Emission">The reusable source fragment, or null for invalid declarations.</param>
    internal sealed record Output(Analysis Analysis, string? Emission);

}
