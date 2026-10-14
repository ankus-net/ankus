using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Requires explicit reference nullability for values crossing the SQL boundary.
/// </summary>
internal static class SqlNullability
{
    private static readonly DiagnosticDescriptor s_oblivious = new(
        "ANKUS024", "Ambiguous PostgreSQL nullability",
        "'{0}' uses reference type '{1}' without nullable annotations; enable nullable annotations and choose a nullable or non-nullable SQL contract",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://willibrandon.github.io/ankus/function-declarations/#sql-nullability");

    /// <summary>
    /// Checks SQL parameters and result columns without inspecting unrelated managed payload members.
    /// </summary>
    /// <param name="method">The managed SQL callable.</param>
    /// <param name="parameters">Only parameters consuming SQL arguments.</param>
    /// <param name="results">The scalar result or individual set columns.</param>
    /// <param name="context">The diagnostic output context.</param>
    /// <returns>Whether every SQL reference has an explicit nullable contract.</returns>
    internal static bool Validate(IMethodSymbol method, IEnumerable<IParameterSymbol> parameters,
        IEnumerable<ITypeSymbol> results, GeneratorDiagnostics context)
    {
        bool valid = true;
        foreach (IParameterSymbol parameter in parameters)
        {
            if (FindOblivious(parameter.Type) is not { } ambiguous)
            {
                continue;
            }

            Location? location = (parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken)
                as ParameterSyntax)?.Type?.GetLocation() ?? parameter.Locations.FirstOrDefault();
            context.Report(s_oblivious, location, parameter.Name, ambiguous.ToDisplayString());
            valid = false;
        }

        foreach (ITypeSymbol result in results)
        {
            if (FindOblivious(result) is not { } ambiguous)
            {
                continue;
            }

            Location? location = (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken)
                as MethodDeclarationSyntax)?.ReturnType.GetLocation() ?? method.Locations.FirstOrDefault();
            context.Report(s_oblivious, location, method.Name + " result", ambiguous.ToDisplayString());
            valid = false;
        }

        return valid;
    }

    /// <summary>
    /// Checks one SQL slot of a grouped aggregate argument or interface result.
    /// </summary>
    internal static bool ValidateValue(ITypeSymbol type, ISymbol source, string name, GeneratorDiagnostics context)
    {
        if (FindOblivious(type) is not { } ambiguous)
        {
            return true;
        }

        context.Report(s_oblivious, source.Locations.FirstOrDefault(), name, ambiguous.ToDisplayString());
        return false;
    }

    /// <summary>
    /// Finds ambiguity in a SQL value or its array element, retaining custom payloads as single SQL values.
    /// </summary>
    /// <param name="type">The SQL value's managed type.</param>
    /// <returns>The first oblivious reference, or null when annotations determine the contract.</returns>
    private static ITypeSymbol? FindOblivious(ITypeSymbol type)
    {
        if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.None)
        {
            return type;
        }

        return type switch
        {
            IArrayTypeSymbol array => FindOblivious(array.ElementType),
            INamedTypeSymbol { Name: "PgArray" or "PgArrayView", Arity: 1 } array
                when array.ContainingNamespace.ToDisplayString() == "Ankus" => FindOblivious(array.TypeArguments[0]),
            _ => null,
        };
    }
}
