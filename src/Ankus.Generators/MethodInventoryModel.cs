using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Retains method selection, provenance and native capability requirements without compiler symbols.
/// </summary>
/// <param name="Identity">The exact assembly-qualified method identity.</param>
/// <param name="Display">The original managed display used for ordering, graph keys and provenance.</param>
/// <param name="Name">The method's unqualified managed name used for item selection.</param>
/// <param name="Owner">The containing type's original managed display.</param>
/// <param name="Test">Whether the method declares a backend test.</param>
/// <param name="Benchmark">Whether the method declares a backend benchmark.</param>
/// <param name="Trigger">Whether the row-trigger marker is present.</param>
/// <param name="EventTrigger">Whether the event-trigger marker is present.</param>
/// <param name="Initializer">Whether either initialization phase marker is present.</param>
/// <param name="NativeEntry">Whether a background-worker or output-plugin marker exports the method under its own native symbol.</param>
/// <param name="Sequence">Whether the managed result requires native iterator support.</param>
/// <param name="RawTransport">Whether any SQL parameter requires raw datum transport.</param>
/// <param name="Options">The optional authored ordinary-function SQL policy.</param>
/// <param name="Location">The current method source coordinates, outside its rendering contracts.</param>
internal sealed record MethodInventoryModel(DeclarationIdentity Identity, string Display, string Name, string Owner,
    bool Test, bool Benchmark, bool Trigger, bool EventTrigger, bool Initializer, bool NativeEntry, bool Sequence, bool RawTransport,
    SqlDeclarationOptions? Options, GeneratorLocation? Location)
{
    /// <summary>
    /// Freezes current method selection and capability metadata during semantic analysis.
    /// </summary>
    /// <param name="method">The current discovered declaration.</param>
    /// <param name="compilation">The compiler state owning current source coordinates.</param>
    /// <param name="cancellationToken">The current analysis cancellation token.</param>
    /// <returns>The immutable inventory entry consumed by graph composition.</returns>
    internal static MethodInventoryModel Create(IMethodSymbol method, Compilation compilation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(DeclarationIdentity.Create(method), method.ToDisplayString(), method.Name, method.ContainingType.ToDisplayString(),
            PgTestDeclaration.IsTest(method), method.GetAttributes().Any(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "Ankus.PgBenchmarkAttribute"),
            TriggerDeclaration.IsTrigger(method), EventTriggerDeclaration.IsEventTrigger(method),
            InitializeDeclaration.IsInitializer(method),
            BackgroundWorkerDeclaration.IsWorker(method) || OutputPluginDeclaration.IsOutputPlugin(method), SetResult.IsSequence(method.ReturnType),
            FunctionParameter.Create(method).Any(static parameter => parameter.Type?.UsesRawTransport == true),
            SqlDeclarationOptions.Read(method.GetAttributes().FirstOrDefault(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "Ankus.PgFunctionAttribute"), compilation, cancellationToken),
            GeneratorLocation.Create(method.Locations.FirstOrDefault(), compilation));
    }
}
