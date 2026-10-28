using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Detaches compilation-wide native capabilities before rendering or extension graph composition.
/// </summary>
/// <param name="Assembly">The extension assembly name that scopes generated initialization symbols.</param>
/// <param name="Binding">The unique measured binding constant, absent when unavailable or ambiguous.</param>
/// <param name="Layouts">The unique measured node layout constant, absent when unavailable or ambiguous.</param>
/// <param name="ReferencedCallbacks">Whether any resolved dependency requires native callback dispatch.</param>
internal sealed record NativeCompilationModel(string Assembly, string? Binding, string? Layouts, bool ReferencedCallbacks)
{
    /// <summary>
    /// Reads constant metadata and callback capability without loading or executing referenced assemblies.
    /// </summary>
    /// <param name="compilation">The current extension and resolved reference inventory.</param>
    /// <param name="referencedCallbacks">The independently cached dependency callback capability.</param>
    /// <param name="cancellationToken">The current semantic analysis cancellation token.</param>
    /// <returns>The immutable values required by native rendering and capability selection.</returns>
    internal static NativeCompilationModel Create(Compilation compilation, bool referencedCallbacks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        INamedTypeSymbol? binding = compilation.GetTypeByMetadataName("Ankus.Postgres.NativeBinding");
        return new(PgFunctionGenerator.SymbolScope(compilation.Assembly), Constant(binding, "Identity"), Constant(binding, "NodeLayouts"),
            referencedCallbacks);
    }

    /// <summary>
    /// Accepts one constant field and rejects incomplete editor declarations with duplicate members.
    /// </summary>
    private static string? Constant(INamedTypeSymbol? type, string name)
    {
        IFieldSymbol[] fields = [.. type?.GetMembers(name).OfType<IFieldSymbol>() ?? []];
        return fields.Length == 1 ? fields[0].ConstantValue as string : null;
    }
}
