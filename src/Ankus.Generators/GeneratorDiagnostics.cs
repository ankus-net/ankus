using System;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Lets semantic validators report through either source production or detached incremental analysis.
/// </summary>
internal readonly struct GeneratorDiagnostics
{
    private readonly SourceProductionContext? _production;
    private readonly Action<DiagnosticDescriptor, Location?, string[]>? _report;

    /// <summary>
    /// Uses the current source output as the diagnostic destination.
    /// </summary>
    /// <param name="context">The current production context.</param>
    internal GeneratorDiagnostics(SourceProductionContext context)
    {
        _production = context;
        CancellationToken = context.CancellationToken;
    }

    /// <summary>
    /// Captures diagnostics during semantic analysis without requiring a source output callback.
    /// </summary>
    /// <param name="report">The transient diagnostic receiver.</param>
    /// <param name="cancellationToken">The current analysis cancellation token.</param>
    internal GeneratorDiagnostics(Action<DiagnosticDescriptor, Location?, string[]> report, CancellationToken cancellationToken)
    {
        _report = report;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the cancellation token for current syntax and semantic queries.
    /// </summary>
    internal CancellationToken CancellationToken { get; }

    /// <summary>
    /// Reports the original descriptor, location and message arguments to the selected destination.
    /// </summary>
    /// <param name="descriptor">The established diagnostic contract.</param>
    /// <param name="location">The current source location, when available.</param>
    /// <param name="arguments">The exact message substitutions.</param>
    internal void Report(DiagnosticDescriptor descriptor, Location? location, params string[] arguments)
    {
        if (_report is not null)
        {
            _report(descriptor, location, arguments);
        }
        else
        {
            _production!.Value.ReportDiagnostic(Diagnostic.Create(descriptor, location, arguments));
        }
    }

    /// <summary>
    /// Adapts existing production callers to the shared validation boundary.
    /// </summary>
    /// <param name="context">The current source production context.</param>
    /// <returns>A diagnostic destination with the same cancellation and reporting behavior.</returns>
    public static implicit operator GeneratorDiagnostics(SourceProductionContext context) => new(context);
}
