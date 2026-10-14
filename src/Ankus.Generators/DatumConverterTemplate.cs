using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Infers one finite converter construction from exact reader and writer interface patterns.
/// </summary>
internal static class DatumConverterTemplate
{
    /// <summary>
    /// Closes a converter and its containing types without guessing unmentioned type parameters.
    /// </summary>
    internal static INamedTypeSymbol? Close(INamedTypeSymbol template, INamedTypeSymbol target, out DiagnosticDescriptor? error)
    {
        INamedTypeSymbol definition = template.OriginalDefinition;
        INamedTypeSymbol[] containers = [.. TypeNesting.SelfAndContainers(definition).Reverse()];
        ITypeParameterSymbol[] parameters = [.. containers.SelectMany(static type => type.TypeParameters)];
        var variables = new HashSet<ITypeParameterSymbol>(parameters, SymbolEqualityComparer.Default);
        List<Dictionary<ITypeParameterSymbol, ITypeSymbol>> assignments = [new(SymbolEqualityComparer.Default)];
        foreach (INamedTypeSymbol contract in definition.AllInterfaces.Where(static item => item.Arity == 1 &&
            item.ContainingNamespace.ToDisplayString() == "Ankus" && item.Name is "IPgDatumReader" or "IPgDatumWriter"))
        {
            var inferred = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            if (!Match(contract.TypeArguments[0], target, inferred))
            {
                continue;
            }

            foreach (Dictionary<ITypeParameterSymbol, ITypeSymbol> existing in assignments.ToArray())
            {
                var combined = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(existing, SymbolEqualityComparer.Default);
                bool compatible = true;
                foreach (KeyValuePair<ITypeParameterSymbol, ITypeSymbol> binding in inferred)
                {
                    if (combined.TryGetValue(binding.Key, out ITypeSymbol? previous) &&
                        !SymbolEqualityComparer.IncludeNullability.Equals(previous, binding.Value))
                    {
                        compatible = false;
                        break;
                    }

                    combined[binding.Key] = binding.Value;
                }

                if (compatible && !assignments.Any(item => Same(item, combined)))
                {
                    assignments.Add(combined);
                }
            }
        }

        INamedTypeSymbol[] candidates = [.. assignments.Where(item => parameters.All(item.ContainsKey))
            .Select(Construct).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.IncludeNullability)];
        error = candidates.Length switch
        {
            0 => DatumMappingDiagnostics.Inference,
            > 1 => DatumMappingDiagnostics.AmbiguousInference,
            _ => null,
        };
        return candidates.Length == 1 ? candidates[0] : null;

        bool Match(ITypeSymbol pattern, ITypeSymbol actual, Dictionary<ITypeParameterSymbol, ITypeSymbol> inferred)
        {
            if (pattern is ITypeParameterSymbol parameter && variables.Contains(parameter))
            {
                if (parameter.NullableAnnotation == NullableAnnotation.Annotated && actual.IsReferenceType)
                {
                    actual = actual.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
                }

                if (inferred.TryGetValue(parameter, out ITypeSymbol? previous))
                {
                    return SymbolEqualityComparer.IncludeNullability.Equals(previous, actual);
                }

                inferred.Add(parameter, actual);
                return true;
            }

            if (pattern is IArrayTypeSymbol array)
            {
                return actual is IArrayTypeSymbol other && array.Rank == other.Rank && array.IsSZArray == other.IsSZArray &&
                    Match(array.ElementType, other.ElementType, inferred);
            }

            if (pattern is INamedTypeSymbol named && actual is INamedTypeSymbol value)
            {
                if (!SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, value.OriginalDefinition) ||
                    named.TypeArguments.Length != value.TypeArguments.Length)
                {
                    return false;
                }

                for (int index = 0; index < named.TypeArguments.Length; index++)
                {
                    if (!Match(named.TypeArguments[index], value.TypeArguments[index], inferred))
                    {
                        return false;
                    }
                }

                return named.ContainingType is null ? value.ContainingType is null :
                    value.ContainingType is not null && Match(named.ContainingType, value.ContainingType, inferred);
            }

            return SymbolEqualityComparer.Default.Equals(pattern, actual);
        }

        INamedTypeSymbol Construct(Dictionary<ITypeParameterSymbol, ITypeSymbol> inferred)
        {
            INamedTypeSymbol? containing = null;
            foreach (INamedTypeSymbol level in containers)
            {
                INamedTypeSymbol current = containing is null ? level : containing.GetTypeMembers(level.Name, level.Arity)
                    .Single(item => SymbolEqualityComparer.Default.Equals(item.OriginalDefinition, level));
                containing = level.Arity == 0 ? current : current.Construct([.. level.TypeParameters.Select(parameter => inferred[parameter])]);
            }

            return containing!;
        }
    }

    /// <summary>
    /// Uses C# declaration binding to validate all inferred constraints before emitting consumer source.
    /// </summary>
    internal static bool Validate(Compilation compilation, IReadOnlyList<DatumTypeDeclaration> declarations, GeneratorDiagnostics context)
    {
        DatumTypeDeclaration[] inferred = [.. declarations.Where(static item => item.HasInferredConverter)];
        if (inferred.Length == 0)
        {
            return true;
        }

        var source = new StringBuilder("#nullable enable\nnamespace Ankus.Generated.__DatumConverterConstraints {\n");
        for (int index = 0; index < inferred.Length; index++)
        {
            source.Append("using Contract").Append(index).Append(" = ")
                .Append(inferred[index].Converter.ToDisplayString(DatumTypeDeclaration.ManagedFormat)).AppendLine(";");
        }

        source.AppendLine("}");
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source.ToString(),
            compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions, cancellationToken: context.CancellationToken);
        Compilation validation = compilation.AddSyntaxTrees(tree);
        UsingDirectiveSyntax[] aliases = [.. tree.GetRoot(context.CancellationToken).DescendantNodes().OfType<UsingDirectiveSyntax>()];
        bool valid = true;
        foreach (Diagnostic diagnostic in validation.GetSemanticModel(tree).GetDeclarationDiagnostics(cancellationToken: context.CancellationToken))
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error &&
                !(diagnostic.Severity == DiagnosticSeverity.Warning && diagnostic.Id is "CS8631" or "CS8634" or "CS8714"))
            {
                continue;
            }

            int index = Array.FindIndex(aliases, alias => alias.Span.Contains(diagnostic.Location.SourceSpan));
            DatumTypeDeclaration declaration = inferred[Math.Max(0, index)];
            context.Report(DatumMappingDiagnostics.Constraint, declaration.ConverterLocation, declaration.Converter.ToDisplayString(), diagnostic.Id);
            valid = false;
        }

        return valid;
    }

    /// <summary>
    /// Compares partial inference assignments without erasing nullable type arguments.
    /// </summary>
    private static bool Same(Dictionary<ITypeParameterSymbol, ITypeSymbol> first, Dictionary<ITypeParameterSymbol, ITypeSymbol> second)
        => first.Count == second.Count && first.All(item => second.TryGetValue(item.Key, out ITypeSymbol? value) &&
            SymbolEqualityComparer.IncludeNullability.Equals(item.Value, value));
}
