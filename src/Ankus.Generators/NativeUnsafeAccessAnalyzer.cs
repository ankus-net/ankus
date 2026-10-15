using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Ankus.Generators;

/// <summary>
/// Makes the caller's obligations explicit for raw native operations even when their signatures contain only scalars.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NativeUnsafeAccessAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies raw operations used without acknowledging their native contracts.
    /// </summary>
    private static readonly DiagnosticDescriptor s_unsafeAccess = new(
        "ANKUS129", "Raw PostgreSQL access requires an unsafe context",
        "Use an unsafe block for '{0}' and preserve its native ownership, lifetime and synchronization requirements",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/raw-values/#fixed-native-functions");

    /// <summary>
    /// Gets the error for unacknowledged raw native operations.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [s_unsafeAccess];

    /// <summary>
    /// Checks invocations, global accesses and method-group conversions against the generated contract.
    /// </summary>
    /// <param name="context">The analyzer registration context.</param>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            INamedTypeSymbol? attribute = start.Compilation.GetTypeByMetadataName("Ankus.CompilerServices.NativeUnsafeAccessAttribute");
            if (attribute?.ContainingAssembly.Name != "Ankus.Runtime")
            {
                return;
            }

            start.RegisterOperationAction(operation =>
            {
                ISymbol symbol = operation.Operation switch
                {
                    IInvocationOperation invocation => invocation.TargetMethod,
                    IPropertyReferenceOperation property => property.Property,
                    IMethodReferenceOperation method => method.Method,
                    _ => throw new InvalidOperationException("An unregistered operation reached the native access analyzer."),
                };
                if (!symbol.GetAttributes().Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute)) ||
                    HasUnsafeContext(operation.Operation.Syntax))
                {
                    return;
                }

                for (IOperation? parent = operation.Operation.Parent; parent is not null; parent = parent.Parent)
                {
                    if (parent is INameOfOperation)
                    {
                        return;
                    }
                }

                operation.ReportDiagnostic(Diagnostic.Create(s_unsafeAccess, operation.Operation.Syntax.GetLocation(), symbol.Name));
            }, OperationKind.Invocation, OperationKind.PropertyReference, OperationKind.MethodReference);
        });
    }

    /// <summary>
    /// Recognizes lexical unsafe contexts without requiring pointer syntax or changing safe wrapper contracts.
    /// </summary>
    private static bool HasUnsafeContext(SyntaxNode syntax)
    {
        foreach (SyntaxNode ancestor in syntax.AncestorsAndSelf())
        {
            if (ancestor is UnsafeStatementSyntax ||
                ancestor is MemberDeclarationSyntax member && member.Modifiers.Any(SyntaxKind.UnsafeKeyword) ||
                ancestor is LocalFunctionStatementSyntax function && function.Modifiers.Any(SyntaxKind.UnsafeKeyword))
            {
                return true;
            }
        }

        return false;
    }
}
