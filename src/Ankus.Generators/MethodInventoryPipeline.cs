using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Detaches the current canonical method inventory before extension graph and native capability composition.
/// </summary>
internal static class MethodInventoryPipeline
{
    /// <summary>
    /// Converts transient method discovery into ordered comparable inventory values.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <param name="methods">The existing deduplicated semantic discovery.</param>
    /// <returns>The detached selection and capability inventory with current source coordinates.</returns>
    internal static IncrementalValueProvider<EquatableArray<MethodInventoryModel>> Register(IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<ImmutableArray<IMethodSymbol>> methods)
        => context.CompilationProvider.Combine(methods).Select(static (value, token) =>
            new EquatableArray<MethodInventoryModel>(value.Right.Select(method => MethodInventoryModel.Create(method, value.Left, token))))
            .WithTrackingName("MethodInventoryAnalysis");
}
