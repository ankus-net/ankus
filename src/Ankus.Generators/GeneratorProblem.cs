using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Retains diagnostic contracts and coordinates without retaining an earlier compilation or syntax tree.
/// </summary>
/// <param name="Descriptor">The immutable diagnostic descriptor.</param>
/// <param name="Location">The source coordinates to resolve against the current compilation.</param>
/// <param name="Arguments">The original diagnostic message substitutions.</param>
internal sealed record GeneratorProblem(DiagnosticDescriptor Descriptor, GeneratorLocation? Location, EquatableArray<string> Arguments)
{
    /// <summary>
    /// Reports the cached diagnostic against the current compilation's source tree.
    /// </summary>
    /// <param name="compilation">The current compilation.</param>
    /// <param name="context">The current source production context.</param>
    internal void Report(Compilation compilation, SourceProductionContext context)
        => context.ReportDiagnostic(Diagnostic.Create(Descriptor, Location?.Resolve(compilation), Arguments.ToArray()));

    /// <summary>
    /// Captures a cached diagnostic during detached graph composition.
    /// </summary>
    /// <param name="sources">The detached current source attribution.</param>
    /// <param name="context">The composition diagnostic collector.</param>
    internal void Report(GeneratorSourceResolver sources, GeneratorDiagnostics context)
        => context.Report(Descriptor, Location?.Resolve(sources), [.. Arguments]);
}
