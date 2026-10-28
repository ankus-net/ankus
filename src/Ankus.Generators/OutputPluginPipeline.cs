using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Separates per-method output plugin validation from cached rendering and current extension-wide selection.
/// </summary>
internal static class OutputPluginPipeline
{
    /// <summary>
    /// Registers normalized initializer analysis and independently tracked rendering.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The detached diagnostics, invocation contracts and cached emission.</returns>
    internal static IncrementalValueProvider<EquatableArray<Output>> Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Analysis> analysis = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgOutputPluginAttribute", static (node, _) => node is MethodDeclarationSyntax or LocalFunctionStatementSyntax or LambdaExpressionSyntax,
            static (attribute, token) => Analyze(attribute, token)).WithTrackingName("OutputPluginAnalysis");
        IncrementalValuesProvider<OutputPluginDeclaration?> models = analysis.Select(static (value, _) => value.Model)
            .WithTrackingName("OutputPluginModel");
        IncrementalValuesProvider<OutputPluginEmission?> emission = models.Select(static (value, _) => value is null ? null :
            PgOutputPluginEmitter.Emit(value)).WithTrackingName("OutputPluginEmission");
        return analysis.Collect().Combine(emission.Collect()).Select(static (value, _) =>
            new EquatableArray<Output>(value.Left.Select((item, index) => new Output(item, value.Right[index])).Distinct()));
    }

    /// <summary>
    /// Reports current diagnostics and selects the extension's single valid initializer.
    /// </summary>
    /// <param name="outputs">The independently analyzed initializer inventory.</param>
    /// <param name="compilation">The current diagnostic source trees.</param>
    /// <param name="context">The production diagnostic receiver.</param>
    /// <returns>The only valid initializer, or null when none is declared, one is invalid or several are declared.</returns>
    internal static OutputPluginEmission? Select(EquatableArray<Output> outputs, GeneratorSourceResolver compilation, GeneratorDiagnostics context)
    {
        Output? selected = null;
        bool valid = true;
        foreach (Output output in outputs.OrderBy(static value => value.Analysis.SortName, StringComparer.Ordinal))
        {
            foreach (GeneratorProblem problem in output.Analysis.Problems)
            {
                problem.Report(compilation, context);
            }

            if (output.Analysis.Model is null)
            {
                valid = false;
            }
            else if (selected is null)
            {
                selected = output;
            }
            else
            {
                context.Report(OutputPluginDiagnostics.Duplicate, output.Analysis.Location?.Resolve(compilation), output.Analysis.Name,
                    selected.Analysis.SortName);
                valid = false;
            }
        }

        return valid ? selected?.Emission : null;
    }

    /// <summary>
    /// Validates the canonical partial definition and detaches current diagnostic coordinates before returning.
    /// </summary>
    private static Analysis Analyze(GeneratorAttributeSyntaxContext attribute, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = (IMethodSymbol)attribute.TargetSymbol;
        IMethodSymbol method = candidate.PartialDefinitionPart ?? candidate;
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            problems.Add(new(descriptor, GeneratorLocation.Create(location, attribute.SemanticModel.Compilation), new(arguments))), cancellationToken);
        return new(method.Name, method.ToDisplayString(), OutputPluginDeclaration.Create(method, diagnostics), new(problems),
            GeneratorLocation.Create(method.Locations.FirstOrDefault(), attribute.SemanticModel.Compilation));
    }

    /// <summary>
    /// Retains semantic invocation values separately from ordering and current diagnostic coordinates.
    /// </summary>
    /// <param name="Name">The original managed method name used in diagnostic arguments.</param>
    /// <param name="SortName">The deterministic declaration ordering key and display name.</param>
    /// <param name="Model">The detached invocation contract, or null after a validation error.</param>
    /// <param name="Problems">The original diagnostic contracts with detached source coordinates.</param>
    /// <param name="Location">The canonical definition's current source coordinates.</param>
    internal sealed record Analysis(string Name, string SortName, OutputPluginDeclaration? Model,
        EquatableArray<GeneratorProblem> Problems, GeneratorLocation? Location);

    /// <summary>
    /// Supplies one normalized initializer analysis and its independently rendered artifacts.
    /// </summary>
    /// <param name="Analysis">The detached model, diagnostics and ordering key.</param>
    /// <param name="Emission">The independently cached export, or null after a validation error.</param>
    internal sealed record Output(Analysis Analysis, OutputPluginEmission? Emission);
}
