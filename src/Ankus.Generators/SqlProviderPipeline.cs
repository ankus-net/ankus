using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Freezes provider declarations before current type and function inventory composition.
/// </summary>
internal static class SqlProviderPipeline
{
    /// <summary>
    /// Registers comparable assembly provider metadata while leaving catalog and dependency validation to the graph.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The original ordered provider inventory and current diagnostic coordinates.</returns>
    internal static IncrementalValueProvider<EquatableArray<SqlProviderModel>> Register(IncrementalGeneratorInitializationContext context)
        => context.CompilationProvider.Select(static (compilation, token) =>
            new EquatableArray<SqlProviderModel>(compilation.Assembly.GetAttributes().Where(static attribute => attribute.AttributeClass?.ToDisplayString() is
                "Ankus.PgSqlTypeProviderAttribute" or "Ankus.PgSqlFunctionProviderAttribute").Select(attribute =>
                    SqlProviderModel.Create(attribute, compilation, token))))
            .WithTrackingName("SqlProviderAnalysis");
}
