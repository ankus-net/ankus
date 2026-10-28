using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Separates canonical phase validation and rendering from current global phase selection and diagnostics.
/// </summary>
internal static class LifecyclePipeline
{
    /// <summary>
    /// Registers both phases with independently tracked immutable invocation and rendering boundaries.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The canonical phase inventory with detached diagnostics and cached rendering.</returns>
    internal static IncrementalValueProvider<EquatableArray<LifecycleOutput>> Register(IncrementalGeneratorInitializationContext context)
    {
        return Phase("Ankus.PgInitializeAttribute", "Initialize").Combine(Phase("Ankus.PgModuleLoadAttribute", "ModuleLoad"))
            .Select(static (value, _) => new EquatableArray<LifecycleOutput>(value.Left.Concat(value.Right).Distinct()));

        IncrementalValueProvider<EquatableArray<LifecycleOutput>> Phase(string attributeName, string stage)
        {
            IncrementalValuesProvider<LifecycleAnalysis> analysis = context.SyntaxProvider.ForAttributeWithMetadataName(
                attributeName, static (_, _) => true, static (attribute, token) => Analyze(attribute, token))
                .Where(static value => value is not null).Select(static (value, _) => value!).WithTrackingName(stage + "Analysis");
            IncrementalValuesProvider<LifecycleDeclaration?> models = analysis.Select(static (value, _) => value.Model)
                .WithTrackingName(stage + "Model");
            IncrementalValuesProvider<LifecycleEmission?> emission = models.Select(static (value, _) => value is null ? null :
                LifecycleEmission.Create(value)).WithTrackingName(stage + "Emission");
            return analysis.Collect().Combine(emission.Collect()).Select(static (value, _) =>
                new EquatableArray<LifecycleOutput>(value.Left.Select((item, index) => new LifecycleOutput(item, value.Right[index]))));
        }
    }

    /// <summary>
    /// Selects at most one current callback per phase without retaining compiler symbols or revalidating signatures.
    /// </summary>
    /// <param name="outputs">The canonical current phase inventory.</param>
    /// <param name="compilation">The current source trees for diagnostic coordinates.</param>
    /// <param name="context">The production diagnostic receiver.</param>
    /// <returns>The two validated phase emissions, or no callbacks after any phase validation failure.</returns>
    internal static (LifecycleEmission? Initialize, LifecycleEmission? ModuleLoad) Select(
        EquatableArray<LifecycleOutput> outputs, GeneratorSourceResolver compilation, GeneratorDiagnostics context)
    {
        LifecycleOutput[] ordered = [.. outputs.OrderBy(static value => value.Analysis.SortName, StringComparer.Ordinal)];
        bool valid = true;
        foreach (LifecycleOutput output in ordered)
        {
            valid &= output.Analysis.Model is not null;
            foreach (GeneratorProblem problem in output.Analysis.Problems)
            {
                problem.Report(compilation, context);
            }
        }

        LifecycleOutput[] initialize = [.. ordered.Where(static value => value.Analysis.Initialize)];
        LifecycleOutput[] moduleLoad = [.. ordered.Where(static value => value.Analysis.ModuleLoad)];
        foreach ((LifecycleOutput[] declarations, bool load) in new[] { (initialize, false), (moduleLoad, true) })
        {
            if (declarations.Length > 1)
            {
                LifecycleAnalysis analysis = declarations[1].Analysis;
                InitializeDeclaration.ReportDuplicate(analysis.Location?.Resolve(compilation), analysis.Name, load, context);
                valid = false;
            }
        }

        return valid ? (initialize.SingleOrDefault()?.Emission, moduleLoad.SingleOrDefault()?.Emission) : (null, null);
    }

    /// <summary>
    /// Detaches a canonical definition's diagnostic coordinates and semantic invocation contract.
    /// </summary>
    private static LifecycleAnalysis? Analyze(GeneratorAttributeSyntaxContext attribute, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (attribute.TargetSymbol is not IMethodSymbol candidate)
        {
            return null;
        }

        IMethodSymbol method = candidate.PartialDefinitionPart ?? candidate;
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) => problems.Add(
            new(descriptor, GeneratorLocation.Create(location, attribute.SemanticModel.Compilation), new(arguments))), cancellationToken);
        return new(method.Name, method.ToDisplayString(), InitializeDeclaration.HasAttribute(method, "Ankus.PgInitializeAttribute"),
            InitializeDeclaration.HasAttribute(method, "Ankus.PgModuleLoadAttribute"), InitializeDeclaration.Create(method, diagnostics),
            new(problems), GeneratorLocation.Create(method.Locations.FirstOrDefault(), attribute.SemanticModel.Compilation));
    }

    /// <summary>
    /// Keeps current selection metadata and diagnostic coordinates separate from the reusable invocation model.
    /// </summary>
    /// <param name="Name">The original managed name used in diagnostics.</param>
    /// <param name="SortName">The deterministic declaration ordering key.</param>
    /// <param name="Initialize">Whether the canonical definition declares deferred initialization.</param>
    /// <param name="ModuleLoad">Whether the canonical definition declares immediate registration.</param>
    /// <param name="Model">The detached invocation contract, or null after validation fails.</param>
    /// <param name="Problems">The detached semantic diagnostics.</param>
    /// <param name="Location">The canonical definition's current source coordinates.</param>
    internal sealed record LifecycleAnalysis(string Name, string SortName, bool Initialize, bool ModuleLoad,
        LifecycleDeclaration? Model, EquatableArray<GeneratorProblem> Problems, GeneratorLocation? Location);

    /// <summary>
    /// Supplies one canonical phase analysis and independently cached emission.
    /// </summary>
    /// <param name="Analysis">The detached invocation, selection metadata and diagnostics.</param>
    /// <param name="Emission">The independently rendered artifacts, or null after validation fails.</param>
    internal sealed record LifecycleOutput(LifecycleAnalysis Analysis, LifecycleEmission? Emission);
}
