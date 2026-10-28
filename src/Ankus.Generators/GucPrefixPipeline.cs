using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Caches native prefix registration independently of current assembly attribute coordinates.
/// </summary>
internal static class GucPrefixPipeline
{
    /// <summary>
    /// Registers detached literal validation and independently cached native rendering.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The literal inventory, current diagnostics and cached source fragments.</returns>
    internal static IncrementalValueProvider<Output> Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<Analysis> analysis = context.CompilationProvider.Select(static (compilation, token) =>
            Analyze(compilation, token)).WithTrackingName("GucPrefixAnalysis");
        IncrementalValueProvider<EquatableArray<string>> prefixes = analysis.Select(static (value, _) => value.Prefixes)
            .WithTrackingName("GucPrefixModel");
        IncrementalValueProvider<Emission> emission = prefixes.Select(static (value, _) => Emit(value))
            .WithTrackingName("GucPrefixEmission");
        return analysis.Combine(emission).Select(static (value, _) => new Output(value.Left, value.Right));
    }

    /// <summary>
    /// Validates literal assembly attributes while compiler objects remain transient.
    /// </summary>
    private static Analysis Analyze(Compilation compilation, CancellationToken cancellationToken)
    {
        ImmutableArray<AttributeData> attributes = [.. compilation.Assembly.GetAttributes().Where(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "Ankus.PgGucPrefixAttribute")];
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            problems.Add(new(descriptor, GeneratorLocation.Create(location, compilation), new(arguments))), cancellationToken);
        return new(!attributes.IsEmpty, new(GucPrefixDeclaration.Read(attributes, diagnostics)), new(problems));
    }

    /// <summary>
    /// Renders the validated literal inventory without compiler or final composition state.
    /// </summary>
    private static Emission Emit(EquatableArray<string> prefixes)
    {
        var native = new StringBuilder();
        var registration = new StringBuilder();
        GucPrefixDeclaration.Emit(prefixes, native, registration);
        return new(native.ToString(), registration.ToString());
    }

    /// <summary>
    /// Retains the validated literal inventory and current detached diagnostic coordinates.
    /// </summary>
    /// <param name="Declared">Whether at least one prefix attribute was authored, including invalid attributes.</param>
    /// <param name="Prefixes">The case-sensitive distinct sorted literal prefixes.</param>
    /// <param name="Problems">The detached validation failures.</param>
    internal sealed record Analysis(bool Declared, EquatableArray<string> Prefixes, EquatableArray<GeneratorProblem> Problems);

    /// <summary>
    /// Retains native prefix checking helpers and current registration statements.
    /// </summary>
    /// <param name="Native">The native version-specific helper definitions.</param>
    /// <param name="Registration">The ordered literal registration statements.</param>
    internal sealed record Emission(string Native, string Registration);

    /// <summary>
    /// Supplies current prefix validation alongside cached native source.
    /// </summary>
    /// <param name="Analysis">The current declarations and validation failures.</param>
    /// <param name="Emission">The reusable version-specific registration source.</param>
    internal sealed record Output(Analysis Analysis, Emission Emission);
}
