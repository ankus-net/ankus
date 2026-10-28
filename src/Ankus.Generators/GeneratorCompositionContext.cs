using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Collects deterministic source artifacts and original diagnostics without a compiler output context.
/// </summary>
internal sealed class GeneratorCompositionContext
{
    private readonly List<Artifact> _artifacts = [];
    private readonly List<GeneratorProblem> _problems = [];
    private Manifest? _manifest;

    /// <summary>
    /// Creates the transient collector for one detached extension composition.
    /// </summary>
    /// <param name="sources">The transient source resolver for this composition.</param>
    /// <param name="cancellationToken">The current composition cancellation token.</param>
    internal GeneratorCompositionContext(GeneratorSourceResolver sources, CancellationToken cancellationToken)
    {
        Diagnostics = new((descriptor, location, arguments) => _problems.Add(new(descriptor,
            sources.Coordinates(location), new(arguments))), cancellationToken);
    }

    /// <summary>
    /// Gets the original diagnostic contract collector used by deterministic validators.
    /// </summary>
    internal GeneratorDiagnostics Diagnostics { get; }

    /// <summary>
    /// Collects one generated source with its established compiler hint name.
    /// </summary>
    /// <param name="name">The unique generated source name.</param>
    /// <param name="source">The complete source text.</param>
    internal void AddSource(string name, string source) => AddSource(name, new GeneratorSourcePlan(new EquatableArray<string>([source])));

    /// <summary>
    /// Retains a complete source plan for separately cached artifact rendering.
    /// </summary>
    /// <param name="name">The established compiler hint name.</param>
    /// <param name="plan">The ordered source fragments.</param>
    /// <param name="requiresGraph">Whether the source requires successful embedded graph encoding.</param>
    internal void AddSource(string name, GeneratorSourcePlan plan, bool requiresGraph = false) => _artifacts.Add(new(name, plan, requiresGraph));

    /// <summary>
    /// Retains validated graph metadata separately from native and export rendering inputs.
    /// </summary>
    /// <param name="manifest">The complete validated manifest plan.</param>
    internal void SetManifest(Manifest manifest) => _manifest = manifest;

    /// <summary>
    /// Preserves the descriptor, coordinates and message substitutions for later current-tree reporting.
    /// </summary>
    /// <param name="descriptor">The original diagnostic descriptor.</param>
    /// <param name="location">A detached source location, when present.</param>
    /// <param name="arguments">The exact message substitutions.</param>
    internal void Report(DiagnosticDescriptor descriptor, Location? location, params string[] arguments)
        => Diagnostics.Report(descriptor, location, arguments);

    /// <summary>
    /// Freezes collected output without retaining graph state, compiler objects or callbacks.
    /// </summary>
    /// <returns>The immutable composition result.</returns>
    internal Output Freeze() => new(new(_artifacts), new(_problems), _manifest);

    /// <summary>
    /// Adapts detached composition to shared semantic validation contracts.
    /// </summary>
    /// <param name="context">The current detached composition collector.</param>
    /// <returns>The original diagnostic sink and cancellation contract.</returns>
    public static implicit operator GeneratorDiagnostics(GeneratorCompositionContext context) => context.Diagnostics;

    /// <summary>
    /// Carries one independently comparable generated source artifact.
    /// </summary>
    /// <param name="Name">The established compiler hint name.</param>
    /// <param name="Plan">The cached source fragments.</param>
    /// <param name="RequiresGraph">Whether bounds validation must succeed before publishing the source.</param>
    internal sealed record Artifact(string Name, GeneratorSourcePlan Plan, bool RequiresGraph);

    /// <summary>
    /// Keeps native sources independently comparable to installation and catalog metadata.
    /// </summary>
    /// <param name="Native">The complete native source plan.</param>
    /// <param name="Exports">The complete export list plan.</param>
    /// <param name="Metadata">The independently comparable graph and installation metadata.</param>
    internal sealed record Manifest(GeneratorSourcePlan Native, GeneratorSourcePlan Exports, ManifestMetadata Metadata);

    /// <summary>
    /// Contains only metadata that belongs to the installation and manifest path.
    /// </summary>
    /// <param name="Graph">The independently rendered installation dependency graph.</param>
    /// <param name="Relocatable">Whether the extension permits schema relocation.</param>
    /// <param name="NativeCallbacks">Whether the extension exposes native callback capability.</param>
    internal sealed record ManifestMetadata(InstallationGraphModel Graph, bool Relocatable, bool NativeCallbacks);

    /// <summary>
    /// Contains source outputs and diagnostics independently of the compiler used to analyze them.
    /// </summary>
    /// <param name="Artifacts">The sources emitted in their established order.</param>
    /// <param name="Problems">The original diagnostics in validation order.</param>
    /// <param name="Manifest">The validated manifest, or none after composition fails.</param>
    internal sealed record Output(EquatableArray<Artifact> Artifacts, EquatableArray<GeneratorProblem> Problems, Manifest? Manifest);
}
