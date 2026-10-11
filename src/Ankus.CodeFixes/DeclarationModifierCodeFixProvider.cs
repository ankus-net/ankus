using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    /// The file-local function container diagnostic.
    /// </summary>
    private const string FileLocalId = "ANKUS043";

    /// <summary>
    /// The inaccessible-setting diagnostic.
    /// </summary>
    private const string SettingAccessId = "ANKUS174";

    /// <summary>
    /// The file-local setting container diagnostic.
    /// </summary>
    private const string SettingFileLocalId = "ANKUS178";

    /// <summary>
    /// The inaccessible setting-container diagnostic.
    /// </summary>
    private const string SettingContainerAccessId = "ANKUS179";

    /// <summary>
    /// Gets the declaration diagnostics corrected by this provider.
    /// </summary>
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [InstanceId, FileLocalId, AccessId, SettingAccessId, SettingFileLocalId, SettingContainerAccessId];

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
            else if (diagnostic.Id is FileLocalId or SettingFileLocalId && identifier.Parent is TypeDeclarationSyntax local &&
                model.GetDeclaredSymbol(local, cancellationToken) is { IsFileLocal: true } file &&
                file.ContainingNamespace.GetTypeMembers(file.Name, file.Arity).Length == 1)
            {
                context.RegisterCodeFix(CodeAction.Create($"Make '{file.Name}' internal",
                    token => ReplaceFileAsync(context.Document, local, token), "Ankus.MakeFileTypeInternal"), diagnostic);
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

        return !await DeclarationModifiers.HasInstanceReferenceAsync(solution, symbol, cancellationToken).ConfigureAwait(false);
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
        return document.WithSyntaxRoot(root.ReplaceNode(method, DeclarationModifiers.AddStatic(method)));
    }

    /// <summary>
    /// Replaces <c>file</c> with <c>internal</c>, which keeps the type out of other assemblies.
    /// </summary>
    /// <param name="document">The document containing the type.</param>
    /// <param name="type">The file-local declaration.</param>
    /// <param name="cancellationToken">Cancels the edit.</param>
    /// <returns>The corrected document.</returns>
    private static async Task<Document> ReplaceFileAsync(Document document, TypeDeclarationSyntax type, CancellationToken cancellationToken)
    {
        SyntaxNode root = (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        SyntaxToken file = type.Modifiers.First(static modifier => modifier.IsKind(SyntaxKind.FileKeyword));
        return document.WithSyntaxRoot(root.ReplaceToken(file,
            SyntaxFactory.Token(file.LeadingTrivia, SyntaxKind.InternalKeyword, file.TrailingTrivia)));
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
        MemberDeclarationSyntax[] stated = [.. declarations.Where(static declaration => declaration.Modifiers.Any(DeclarationModifiers.IsAccessibility))];
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
                root.ReplaceNodes(group, static (original, _) => DeclarationModifiers.WithInternal(original)));
        }

        return solution;
    }
}
