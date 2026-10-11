using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.CodeFixes;

/// <summary>
/// Makes an <c>async</c> PostgreSQL entry synchronous when its body awaits nothing: the modifier is removed and a
/// <c>Task</c>, <c>ValueTask</c> or <c>IAsyncEnumerable</c> result becomes the value or sequence it already produces.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SynchronousEntryCodeFixProvider))]
[Shared]
public sealed class SynchronousEntryCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// The diagnostics for asynchronous functions, set results, triggers, initializers, workers, backend tests, native
    /// callback handlers, output plugin initializers and benchmarks.
    /// </summary>
    private static readonly ImmutableArray<string> s_ids =
        ["ANKUS030", "ANKUS031", "ANKUS032", "ANKUS209", "ANKUS233", "ANKUS252", "ANKUS292", "ANKUS335", "ANKUS517", "ANKUS527"];

    /// <summary>
    /// Gets the asynchronous entry diagnostics corrected by this provider.
    /// </summary>
    public override ImmutableArray<string> FixableDiagnosticIds => s_ids;

    /// <summary>
    /// Gets batch support for independent method corrections.
    /// </summary>
    /// <returns>The standard Roslyn batch provider.</returns>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <summary>
    /// Registers a correction for an <c>async</c> method that never awaits. The edit is kept only if no caller awaits or
    /// otherwise relies on the asynchronous result.
    /// </summary>
    /// <param name="context">The document, diagnostics and cancellation context.</param>
    /// <returns>The registration operation.</returns>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? model = await context.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return;
        }

        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            if (root.FindToken(diagnostic.Location.SourceSpan.Start).Parent?.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } method &&
                method.Modifiers.Any(SyntaxKind.AsyncKeyword) && !method.Modifiers.Any(SyntaxKind.PartialKeyword) &&
                (method.Body ?? (SyntaxNode?)method.ExpressionBody) is { } body && !Awaits(body) &&
                model.GetDeclaredSymbol(method, cancellationToken) is { } symbol && !UsesEnumeratorCancellation(symbol) &&
                Synchronous(method.ReturnType, symbol.ReturnType, model.Compilation) is { } result)
            {
                MethodDeclarationSyntax typed = method.WithReturnType(result);
                MemberDeclarationSyntax edited = DeclarationModifiers.Remove(typed, typed.Modifiers.IndexOf(SyntaxKind.AsyncKeyword));
                context.RegisterCodeFix(CompilerErrorComparison.CreateAction(context.Document, root.ReplaceNode(method, edited),
                    $"Make '{method.Identifier.ValueText}' synchronous", "Ankus.MakeSynchronous"), diagnostic);
            }
        }
    }

    /// <summary>
    /// Finds an <c>await</c> that belongs to the method itself rather than to a nested lambda or local function.
    /// </summary>
    /// <param name="body">The method body or expression body.</param>
    /// <returns>Whether the method awaits anything.</returns>
    private static bool Awaits(SyntaxNode body)
        => body.DescendantNodesAndSelf(static node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .Any(static node => node switch
            {
                AwaitExpressionSyntax => true,
                CommonForEachStatementSyntax loop => loop.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
                UsingStatementSyntax statement => statement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
                LocalDeclarationStatementSyntax declaration => declaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
                _ => false,
            });

    /// <summary>
    /// Finds an <c>EnumeratorCancellation</c> parameter, which has meaning only for an asynchronous iterator.
    /// </summary>
    /// <param name="method">The bound method.</param>
    /// <returns>Whether a parameter carries the attribute.</returns>
    private static bool UsesEnumeratorCancellation(IMethodSymbol method)
        => method.Parameters.Any(static parameter => parameter.GetAttributes().Any(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.EnumeratorCancellationAttribute"));

    /// <summary>
    /// Maps an asynchronous result to the synchronous one its <c>return</c> and <c>yield</c> statements already produce.
    /// </summary>
    /// <param name="syntax">The authored return type.</param>
    /// <param name="type">The bound return type.</param>
    /// <param name="compilation">The compilation resolving framework types.</param>
    /// <returns>The synchronous return type, or null when the result has no direct synchronous form.</returns>
    private static TypeSyntax? Synchronous(TypeSyntax syntax, ITypeSymbol type, Compilation compilation)
    {
        if (type.SpecialType == SpecialType.System_Void)
        {
            return syntax;
        }

        if (Is(type, "System.Threading.Tasks.Task") || Is(type, "System.Threading.Tasks.ValueTask"))
        {
            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)).WithTriviaFrom(syntax);
        }

        GenericNameSyntax? generic = syntax switch
        {
            GenericNameSyntax name => name,
            QualifiedNameSyntax { Right: GenericNameSyntax name } => name,
            AliasQualifiedNameSyntax { Name: GenericNameSyntax name } => name,
            _ => null,
        };
        if (generic is not { TypeArgumentList.Arguments.Count: 1 })
        {
            return null;
        }

        TypeSyntax argument = generic.TypeArgumentList.Arguments[0];

        if (Is(type, "System.Threading.Tasks.Task`1") || Is(type, "System.Threading.Tasks.ValueTask`1"))
        {
            return argument.WithTriviaFrom(syntax);
        }

        return Is(type, "System.Collections.Generic.IAsyncEnumerable`1")
            ? syntax.ReplaceToken(generic.Identifier, SyntaxFactory.Identifier(generic.Identifier.LeadingTrivia, "IEnumerable",
                generic.Identifier.TrailingTrivia))
            : null;

        bool Is(ITypeSymbol candidate, string metadataName)
            => compilation.GetTypeByMetadataName(metadataName) is { } framework &&
                SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, framework);
    }
}
