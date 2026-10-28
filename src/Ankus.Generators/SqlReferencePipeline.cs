using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Separates exact compiler dependency selection from current SQL graph binding.
/// </summary>
internal static class SqlReferencePipeline
{
    /// <summary>
    /// Registers comparable typed reference analysis without preserving compiler symbols after its boundary.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <param name="declarations">The attributed local declaration inventory.</param>
    /// <returns>The compiler-selected references and current diagnostic coordinates.</returns>
    internal static IncrementalValueProvider<EquatableArray<SqlReferenceModel>> Register(IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<ImmutableArray<ISymbol>> declarations)
        => context.CompilationProvider.Combine(declarations).Select(static (value, token) => Analyze(value.Left, value.Right, token))
            .WithTrackingName("SqlReferenceAnalysis");

    /// <summary>
    /// Freezes references in the original declaration and attribute order, including assembly-level declarations.
    /// </summary>
    private static EquatableArray<SqlReferenceModel> Analyze(Compilation compilation, ImmutableArray<ISymbol> declarations,
        CancellationToken cancellationToken)
        => new(declarations.Add(compilation.Assembly).Distinct(SymbolEqualityComparer.Default)
            .OrderBy(static symbol => symbol.ToDisplayString(), StringComparer.Ordinal).SelectMany(declaration =>
                declaration.GetAttributes().Where(SqlGraph.IsReference).Select(attribute =>
                    SqlReferenceModel.Create(declaration, attribute, compilation, cancellationToken))));
}
