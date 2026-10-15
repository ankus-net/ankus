using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Rejects asynchronous SQL entry contracts before generating any native or managed callback.
/// </summary>
internal static class SynchronousDeclaration
{
    private const string HelpLink = "https://ankus.net/reference/execution/#backend-threads-and-tasks";
    private static readonly DiagnosticDescriptor s_async = new(
        "ANKUS030", "Asynchronous PostgreSQL function",
        "'{0}' cannot use async: PostgreSQL backend access ends when the callback returns; keep the entry method synchronous and complete its work before returning",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_task = new(
        "ANKUS031", "Task-returning PostgreSQL function",
        "'{0}' returns '{1}'; PostgreSQL functions complete synchronously, so return the completed SQL value instead of a Task or ValueTask",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_asyncSequence = new(
        "ANKUS032", "Asynchronous PostgreSQL set result",
        "'{0}' returns '{1}'; PostgreSQL SETOF and TABLE callbacks enumerate synchronously, so use IEnumerable<T> and keep backend work on the calling thread",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Validates asynchronous keywords and actual framework return contracts during transient semantic analysis.
    /// </summary>
    /// <param name="method">The attributed SQL entry method.</param>
    /// <param name="compilation">The compilation resolving framework type identity.</param>
    /// <param name="context">The diagnostic destination and cancellation token.</param>
    /// <returns>Whether ordinary synchronous signature validation may continue.</returns>
    internal static bool Validate(IMethodSymbol method, Compilation compilation, GeneratorDiagnostics context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        IAssemblySymbol? coreLibrary = compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly;
        if (method.ReturnType is INamedTypeSymbol result)
        {
            if (Matches(result, "System.Collections.Generic.IAsyncEnumerable`1") ||
                result.AllInterfaces.Any(value => Matches(value, "System.Collections.Generic.IAsyncEnumerable`1")))
            {
                context.Report(s_asyncSequence, ReturnLocation(), method.Name, result.ToDisplayString());
                return false;
            }

            for (INamedTypeSymbol? candidate = result; candidate is not null; candidate = candidate.BaseType)
            {
                if (Matches(candidate, "System.Threading.Tasks.Task") || Matches(candidate, "System.Threading.Tasks.Task`1") ||
                    Matches(candidate, "System.Threading.Tasks.ValueTask") || Matches(candidate, "System.Threading.Tasks.ValueTask`1"))
                {
                    context.Report(s_task, ReturnLocation(), method.Name, result.ToDisplayString());
                    return false;
                }
            }
        }

        IMethodSymbol?[] declarations = [method, method.PartialImplementationPart, method.PartialDefinitionPart];
        if (declarations.OfType<IMethodSymbol>().Any(static candidate => candidate.IsAsync))
        {
            Location? location = declarations.OfType<IMethodSymbol>().SelectMany(static candidate => candidate.DeclaringSyntaxReferences)
                .Select(reference => reference.GetSyntax(context.CancellationToken)).OfType<MethodDeclarationSyntax>()
                .SelectMany(static declaration => declaration.Modifiers).Where(static modifier => modifier.IsKind(SyntaxKind.AsyncKeyword))
                .Select(static modifier => modifier.GetLocation()).FirstOrDefault();
            context.Report(s_async, location ?? method.Locations.FirstOrDefault(), method.Name);
            return false;
        }

        return true;

        bool Matches(INamedTypeSymbol candidate, string metadataName)
        {
            INamedTypeSymbol? framework = coreLibrary?.GetTypeByMetadataName(metadataName) ?? coreLibrary?.ResolveForwardedType(metadataName);
            return framework is not null && !SymbolEqualityComparer.Default.Equals(framework.ContainingAssembly, compilation.Assembly) &&
                SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, framework);
        }

        Location? ReturnLocation() => MethodSyntax.Result(method.DeclaringSyntaxReferences.Select(reference =>
            reference.GetSyntax(context.CancellationToken)).OfType<BaseMethodDeclarationSyntax>().FirstOrDefault()) ?? method.Locations.FirstOrDefault();
    }
}
