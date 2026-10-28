using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Separates per-worker signature and export analysis from cached rendering and current global export selection.
/// </summary>
internal static class BackgroundWorkerPipeline
{
    /// <summary>
    /// Registers normalized worker analysis and independently tracked rendering.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The detached diagnostics, invocation contracts and cached emission.</returns>
    internal static IncrementalValueProvider<EquatableArray<WorkerOutput>> Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<WorkerAnalysis> analysis = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgBackgroundWorkerAttribute", static (node, _) => node is MethodDeclarationSyntax or LocalFunctionStatementSyntax or LambdaExpressionSyntax,
            static (attribute, token) => Analyze(attribute, token)).WithTrackingName("WorkerAnalysis");
        IncrementalValuesProvider<BackgroundWorkerDeclaration?> models = analysis.Select(static (value, _) => value.Model)
            .WithTrackingName("WorkerModel");
        IncrementalValuesProvider<BackgroundWorkerEmission?> emission = models.Select(static (value, _) => value is null ? null :
            PgBackgroundWorkerEmitter.Emit(value)).WithTrackingName("WorkerEmission");
        return analysis.Collect().Combine(emission.Collect()).Select(static (value, _) =>
            new EquatableArray<WorkerOutput>(value.Left.Select((item, index) => new WorkerOutput(item, value.Right[index])).Distinct()));
    }

    /// <summary>
    /// Selects current unique exports without retaining compiler symbols or revalidating signatures.
    /// </summary>
    /// <param name="outputs">The independently analyzed worker inventory.</param>
    /// <param name="compilation">The current diagnostic source trees.</param>
    /// <param name="context">The production diagnostic receiver.</param>
    /// <returns>The valid workers with unique current native exports.</returns>
    internal static List<WorkerOutput> Select(EquatableArray<WorkerOutput> outputs, GeneratorSourceResolver compilation, GeneratorDiagnostics context)
    {
        var selected = new List<WorkerOutput>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (WorkerOutput worker in outputs.OrderBy(static value => value.Analysis.SortName, StringComparer.Ordinal))
        {
            if (worker.Analysis.Model is not { } model)
            {
                foreach (GeneratorProblem problem in worker.Analysis.Problems)
                {
                    problem.Report(compilation, context);
                }

                continue;
            }

            if (!names.Add(model.EntryPoint))
            {
                BackgroundWorkerDeclaration.ReportDuplicate(worker.Analysis.Location?.Resolve(compilation), worker.Analysis.Name, model.EntryPoint, context);
                continue;
            }

            selected.Add(worker);
        }

        return selected;
    }

    /// <summary>
    /// Validates the canonical partial definition and detaches current diagnostic coordinates before returning.
    /// </summary>
    private static WorkerAnalysis Analyze(GeneratorAttributeSyntaxContext attribute, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = (IMethodSymbol)attribute.TargetSymbol;
        IMethodSymbol method = candidate.PartialDefinitionPart ?? candidate;
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            problems.Add(new(descriptor, GeneratorLocation.Create(location, attribute.SemanticModel.Compilation), new(arguments))), cancellationToken);
        return new(method.Name, method.ToDisplayString(), BackgroundWorkerDeclaration.Create(method, diagnostics), new(problems),
            GeneratorLocation.Create(method.Locations.FirstOrDefault(), attribute.SemanticModel.Compilation));
    }

    /// <summary>
    /// Retains semantic invocation values separately from ordering and current diagnostic coordinates.
    /// </summary>
    /// <param name="Name">The original managed method name used in diagnostic arguments.</param>
    /// <param name="SortName">The original deterministic declaration ordering key.</param>
    /// <param name="Model">The detached invocation and native identities, or null after a validation error.</param>
    /// <param name="Problems">The original diagnostic contracts with detached source coordinates.</param>
    /// <param name="Location">The canonical definition's current source coordinates.</param>
    internal sealed record WorkerAnalysis(string Name, string SortName, BackgroundWorkerDeclaration? Model,
        EquatableArray<GeneratorProblem> Problems, GeneratorLocation? Location);

    /// <summary>
    /// Supplies one normalized worker analysis and independently rendered immutable artifacts.
    /// </summary>
    /// <param name="Analysis">The detached model, diagnostics and ordering key.</param>
    /// <param name="Emission">The independently cached entry, or null after a validation error.</param>
    internal sealed record WorkerOutput(WorkerAnalysis Analysis, BackgroundWorkerEmission? Emission);
}
