using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Ankus.CodeFixes;

/// <summary>
/// Makes a PostgreSQL entry method static, or makes a declaration or its containing type reachable from generated code.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DeclarationModifierCodeFixProvider))]
[Shared]
public sealed class DeclarationModifierCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// The instance-method diagnostic.
    /// </summary>
    private const string InstanceId = "ANKUS033";

    /// <summary>
    /// The inaccessible-function diagnostic.
    /// </summary>
    private const string AccessId = "ANKUS034";

    /// <summary>
    /// The inaccessible-setting diagnostic.
    /// </summary>
    private const string SettingAccessId = "ANKUS174";

    /// <summary>
    /// The inaccessible setting-container diagnostic.
    /// </summary>
    private const string SettingContainerAccessId = "ANKUS179";

    /// <summary>
    /// Gets the declaration diagnostics corrected by this provider.
    /// </summary>
    public override ImmutableArray<string> FixableDiagnosticIds => [InstanceId, AccessId, SettingAccessId, SettingContainerAccessId];

    /// <summary>
    /// Gets batch support for independent declaration corrections.
    /// </summary>
    /// <returns>The standard Roslyn batch provider.</returns>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <summary>
    /// Registers a correction only where it compiles without further edits.
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
            SyntaxToken identifier = root.FindToken(diagnostic.Location.SourceSpan.Start);
            if (diagnostic.Id == InstanceId && identifier.Parent is MethodDeclarationSyntax method && method.Identifier == identifier &&
                model.GetDeclaredSymbol(method, cancellationToken) is { } instance &&
                await CanMakeStaticAsync(context.Document.Project.Solution, method, instance, model, cancellationToken).ConfigureAwait(false))
            {
                context.RegisterCodeFix(CodeAction.Create($"Make '{instance.Name}' static",
                    token => MakeStaticAsync(context.Document, method, token), "Ankus.MakeStatic"), diagnostic);
            }
            else if (diagnostic.Id is AccessId or SettingAccessId or SettingContainerAccessId && identifier.Parent is MemberDeclarationSyntax member &&
                model.GetDeclaredSymbol(member, cancellationToken) is { } hidden && CanMakeInternal(hidden))
            {
                context.RegisterCodeFix(CodeAction.Create($"Make '{hidden.Name}' internal",
                    token => MakeInternalAsync(context.Document.Project.Solution, member, hidden, token), "Ankus.MakeInternal"), diagnostic);
            }
        }
    }

    /// <summary>
    /// Requires an ordinary method whose body reads no instance state and whose callers use no instance receiver.
    /// </summary>
    /// <param name="solution">The solution searched for callers.</param>
    /// <param name="method">The diagnosed declaration.</param>
    /// <param name="symbol">Its bound method.</param>
    /// <param name="model">The declaration's semantic model.</param>
    /// <param name="cancellationToken">Cancels binding and the caller search.</param>
    /// <returns>Whether adding <c>static</c> compiles without other edits.</returns>
    private static async Task<bool> CanMakeStaticAsync(Solution solution, MethodDeclarationSyntax method, IMethodSymbol symbol,
        SemanticModel model, CancellationToken cancellationToken)
    {
        if (symbol.IsStatic || symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride || symbol.MethodKind != MethodKind.Ordinary ||
            symbol.PartialDefinitionPart is not null || symbol.PartialImplementationPart is not null ||
            !symbol.ExplicitInterfaceImplementations.IsEmpty || ImplementsInterface(symbol) ||
            method.DescendantNodes().Any(node => ReadsInstanceState(node, symbol.ContainingType, model, cancellationToken)))
        {
            return false;
        }

        foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false))
        {
            foreach (ReferenceLocation location in referenced.Locations)
            {
                SyntaxNode? root = await location.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                SemanticModel? callerModel = await location.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                SyntaxNode? name = root?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (name?.Parent is MemberAccessExpressionSyntax access && access.Name == name && callerModel is not null &&
                    callerModel.GetSymbolInfo(access.Expression, cancellationToken).Symbol is not INamedTypeSymbol)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Finds whether the method implicitly implements an interface member, which an instance method must do.
    /// </summary>
    /// <param name="symbol">The bound method.</param>
    /// <returns>Whether any interface member of its type resolves to it.</returns>
    private static bool ImplementsInterface(IMethodSymbol symbol)
        => symbol.ContainingType.AllInterfaces.SelectMany(static type => type.GetMembers()).Any(member =>
            SymbolEqualityComparer.Default.Equals(symbol.ContainingType.FindImplementationForInterfaceMember(member), symbol));

    /// <summary>
    /// Detects <c>this</c>, <c>base</c>, or an unqualified instance member of the containing type or its bases.
    /// </summary>
    /// <param name="node">A node in the method declaration.</param>
    /// <param name="owner">The method's containing type.</param>
    /// <param name="model">The declaration's semantic model.</param>
    /// <param name="cancellationToken">Cancels binding.</param>
    /// <returns>Whether the node needs an instance receiver.</returns>
    private static bool ReadsInstanceState(SyntaxNode node, INamedTypeSymbol owner, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is ThisExpressionSyntax or BaseExpressionSyntax)
        {
            return true;
        }

        if (node is not SimpleNameSyntax name ||
            name.Parent is MemberAccessExpressionSyntax access && access.Name == name ||
            name.Parent is MemberBindingExpressionSyntax or QualifiedNameSyntax ||
            name.Parent is AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } assignment && assignment.Left == name ||
            name.Ancestors().OfType<InvocationExpressionSyntax>().Any(invocation =>
                invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } &&
                model.GetSymbolInfo(invocation, cancellationToken).Symbol is null))
        {
            return false;
        }

        ISymbol? symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
        if (symbol is not (IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary }) ||
            symbol.IsStatic)
        {
            return false;
        }

        for (INamedTypeSymbol? type = owner; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(symbol.ContainingType, type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Inserts <c>static</c> after the method's accessibility modifiers, keeping its leading trivia in place.
    /// </summary>
    /// <param name="document">The document containing the method.</param>
    /// <param name="method">The diagnosed declaration.</param>
    /// <param name="cancellationToken">Cancels the edit.</param>
    /// <returns>The corrected document.</returns>
    private static async Task<Document> MakeStaticAsync(Document document, MethodDeclarationSyntax method, CancellationToken cancellationToken)
    {
        SyntaxNode root = (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        SyntaxTokenList modifiers = method.Modifiers;
        int index = 0;
        while (index < modifiers.Count && IsAccessibility(modifiers[index]))
        {
            index++;
        }

        MethodDeclarationSyntax corrected;
        if (modifiers.Count == 0)
        {
            SyntaxToken keyword = SyntaxFactory.Token(method.ReturnType.GetLeadingTrivia(), SyntaxKind.StaticKeyword,
                SyntaxFactory.TriviaList(SyntaxFactory.Space));
            corrected = method.WithReturnType(method.ReturnType.WithoutLeadingTrivia()).WithModifiers(SyntaxFactory.TokenList(keyword));
        }
        else if (index == 0)
        {
            SyntaxToken keyword = SyntaxFactory.Token(modifiers[0].LeadingTrivia, SyntaxKind.StaticKeyword,
                SyntaxFactory.TriviaList(SyntaxFactory.Space));
            corrected = method.WithModifiers(modifiers.Replace(modifiers[0], modifiers[0].WithLeadingTrivia()).Insert(0, keyword));
        }
        else
        {
            corrected = method.WithModifiers(modifiers.Insert(index, SyntaxFactory.Token(SyntaxFactory.TriviaList(), SyntaxKind.StaticKeyword,
                SyntaxFactory.TriviaList(SyntaxFactory.Space))));
        }

        return document.WithSyntaxRoot(root.ReplaceNode(method, corrected));
    }

    /// <summary>
    /// Accepts private and protected members or types, which internal accessibility reaches without other edits.
    /// </summary>
    /// <param name="symbol">The inaccessible method, property or type.</param>
    /// <returns>Whether internal accessibility can replace the declared one.</returns>
    private static bool CanMakeInternal(ISymbol symbol)
        => symbol is IMethodSymbol { IsOverride: false, MethodKind: MethodKind.Ordinary } or IPropertySymbol { IsOverride: false } or INamedTypeSymbol &&
            symbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal;

    /// <summary>
    /// Replaces stated accessibility on every partial declaration, or states it on the diagnosed one when none does.
    /// </summary>
    /// <param name="solution">The solution containing every declaration.</param>
    /// <param name="diagnosed">The declaration carrying the diagnostic.</param>
    /// <param name="symbol">The inaccessible method, property or type.</param>
    /// <param name="cancellationToken">Cancels the edit.</param>
    /// <returns>The corrected solution.</returns>
    private static async Task<Solution> MakeInternalAsync(Solution solution, MemberDeclarationSyntax diagnosed, ISymbol symbol,
        CancellationToken cancellationToken)
    {
        MemberDeclarationSyntax[] declarations = [.. symbol.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax(cancellationToken)).OfType<MemberDeclarationSyntax>()];
        MemberDeclarationSyntax[] stated = [.. declarations.Where(static declaration => declaration.Modifiers.Any(IsAccessibility))];
        MemberDeclarationSyntax[] targets = stated.Length == 0 ? [diagnosed] : stated;
        foreach (IGrouping<SyntaxTree, MemberDeclarationSyntax> group in targets.GroupBy(static declaration => declaration.SyntaxTree))
        {
            Document? document = solution.GetDocument(group.Key);
            if (document is null)
            {
                continue;
            }

            SyntaxNode root = await group.Key.GetRootAsync(cancellationToken).ConfigureAwait(false);
            solution = solution.WithDocumentSyntaxRoot(document.Id,
                root.ReplaceNodes(group, static (original, _) => WithInternal(original)));
        }

        return solution;
    }

    /// <summary>
    /// Replaces accessibility keywords with one <c>internal</c> keyword, or states it first and keeps the indentation.
    /// </summary>
    /// <param name="declaration">The original declaration.</param>
    /// <returns>The declaration with internal accessibility.</returns>
    private static MemberDeclarationSyntax WithInternal(MemberDeclarationSyntax declaration)
    {
        SyntaxTokenList modifiers = declaration.Modifiers;
        SyntaxToken[] access = [.. modifiers.Where(IsAccessibility)];
        if (access.Length != 0)
        {
            SyntaxToken first = access[0];
            SyntaxToken replacement = SyntaxFactory.Token(first.LeadingTrivia, SyntaxKind.InternalKeyword, access[access.Length - 1].TrailingTrivia);
            return declaration.WithModifiers(SyntaxFactory.TokenList(modifiers.Where(token => !IsAccessibility(token) || token == first)
                .Select(token => token == first ? replacement : token)));
        }

        // The first token after any attributes carries the declaration's indentation.
        SyntaxToken next = declaration.AttributeLists.Count == 0
            ? declaration.GetFirstToken()
            : declaration.AttributeLists.Last().GetLastToken().GetNextToken();
        SyntaxToken keyword = SyntaxFactory.Token(next.LeadingTrivia, SyntaxKind.InternalKeyword, SyntaxFactory.TriviaList(SyntaxFactory.Space));
        MemberDeclarationSyntax moved = declaration.ReplaceToken(next, next.WithLeadingTrivia());
        return moved.WithModifiers(moved.Modifiers.Insert(0, keyword));
    }

    /// <summary>
    /// Identifies an accessibility modifier.
    /// </summary>
    /// <param name="token">A declaration modifier.</param>
    /// <returns>Whether the modifier states accessibility.</returns>
    private static bool IsAccessibility(SyntaxToken token)
        => token.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword;
}
