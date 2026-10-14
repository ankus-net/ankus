using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace Ankus.CodeFixes;

/// <summary>
/// Adds a missing typed combine capability when the existing callback implements its exact contract.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AggregateCombineCodeFixProvider))]
[Shared]
public sealed class AggregateCombineCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// Composes all selected aggregate capabilities before one compiler comparison per project.
    /// </summary>
    private static readonly FixAllProvider s_fixAll = new ProjectCodeFixAllProvider("Add missing typed aggregate combine interfaces", ApplyProjectAsync);

    /// <summary>
    /// Gets the diagnostic for an aggregate callback without its capability.
    /// </summary>
    public override ImmutableArray<string> FixableDiagnosticIds => ["ANKUS111"];

    /// <summary>
    /// Gets project batching for independent aggregate declarations.
    /// </summary>
    /// <returns>The provider that verifies the complete project correction once.</returns>
    public override FixAllProvider GetFixAllProvider() => s_fixAll;

    /// <summary>
    /// Registers additions of the existing callback's combine contract without collecting compiler diagnostics.
    /// </summary>
    /// <param name="context">The current document and diagnostic context.</param>
    /// <returns>The registration operation.</returns>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        SemanticModel? model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null ||
            FindCallback(root, model, context.Span, context.CancellationToken) is not { } method ||
            model.Compilation.GetTypeByMetadataName("Ankus.IPgCombinableAggregate`1") is not { } definition ||
            !IsRuntimeType(definition, "IPgCombinableAggregate", 1))
        {
            return;
        }

        if (!Types(model.Compilation.Assembly.GlobalNamespace).Any(type => CanAddCapability(type, method, model.Compilation)))
        {
            return;
        }

        CodeAction action = CodeAction.Create("Add missing typed aggregate combine interfaces", async token =>
            await AddCapabilitiesAsync(context.Document.Project, [method], definition, token).ConfigureAwait(false)
                ?? context.Document.Project.Solution, nameof(AggregateCombineCodeFixProvider));
        context.RegisterCodeFix(action, [.. context.Diagnostics.Where(static diagnostic => diagnostic.Id == "ANKUS111")]);
    }

    /// <summary>
    /// Resolves source callbacks and editable aggregate anchors for inherited metadata callbacks.
    /// </summary>
    /// <param name="root">The editable syntax root.</param>
    /// <param name="model">Its semantic binding.</param>
    /// <param name="span">The diagnostic's current source anchor.</param>
    /// <param name="cancellationToken">Cancels symbol binding.</param>
    /// <returns>The actual reserved callback, or null for another diagnostic or stale source.</returns>
    private static IMethodSymbol? FindCallback(SyntaxNode root, SemanticModel model, Microsoft.CodeAnalysis.Text.TextSpan span,
        CancellationToken cancellationToken)
    {
        SyntaxNode node = root.FindNode(span, getInnermostNodeForTie: true);
        if (node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } method &&
            model.GetDeclaredSymbol(method, cancellationToken) is { Name: "Combine" } callback)
        {
            return callback;
        }

        return node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } declaration &&
            model.GetDeclaredSymbol(declaration, cancellationToken) is { } type ? VisibleCombine(type, model.Compilation) : null;
    }

    /// <summary>
    /// Checks a source aggregate's exact existing state and callback contract without changing the compilation.
    /// </summary>
    /// <param name="type">The attributed source aggregate.</param>
    /// <param name="diagnosed">The callback selected by the diagnostic.</param>
    /// <param name="compilation">The current accessibility context.</param>
    /// <returns>Whether adding the actual runtime capability can preserve the existing implementation.</returns>
    private static bool CanAddCapability(INamedTypeSymbol type, IMethodSymbol diagnosed, Compilation compilation)
    {
        if (!type.GetAttributes().Any(static attribute => attribute.AttributeClass is { } name && IsRuntimeType(name, "PgAggregateAttribute", 0)) ||
            type.AllInterfaces.Any(static contract => IsRuntimeType(contract, "IPgCombinableAggregate", 1)))
        {
            return false;
        }

        INamedTypeSymbol[] contracts = [.. type.AllInterfaces.Where(static contract => IsRuntimeType(contract, "IPgAggregate", 2))];
        IMethodSymbol? implementation = VisibleCombine(type, compilation);
        return contracts.Length == 1 && implementation is not null &&
            SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, diagnosed.OriginalDefinition) &&
            MatchesContract(implementation, contracts[0].TypeArguments[0]) && !type.DeclaringSyntaxReferences.IsEmpty;
    }

    /// <summary>
    /// Collects all selected callbacks before applying and verifying their combined capability additions.
    /// </summary>
    /// <param name="project">The original editable project.</param>
    /// <param name="diagnostics">The selected source diagnostics by document.</param>
    /// <param name="cancellationToken">Cancels discovery, editing and final verification.</param>
    /// <returns>The verified project correction or the original solution.</returns>
    private static async Task<Solution> ApplyProjectAsync(Project project,
        ImmutableDictionary<DocumentId, ImmutableArray<Diagnostic>> diagnostics, CancellationToken cancellationToken)
    {
        Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation?.GetTypeByMetadataName("Ankus.IPgCombinableAggregate`1") is not { } definition ||
            !IsRuntimeType(definition, "IPgCombinableAggregate", 1))
        {
            return project.Solution;
        }

        var methods = new List<IMethodSymbol>();
        foreach (KeyValuePair<DocumentId, ImmutableArray<Diagnostic>> selection in diagnostics)
        {
            DocumentId identity = selection.Key;
            ImmutableArray<Diagnostic> values = selection.Value;
            Document document = project.GetDocument(identity)!;
            SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || model is null)
            {
                continue;
            }

            foreach (Diagnostic diagnostic in values.Where(static diagnostic => diagnostic.Id == "ANKUS111"))
            {
                if (FindCallback(root, model, diagnostic.Location.SourceSpan, cancellationToken) is not { } method)
                {
                    continue;
                }

                methods.Add(method);
            }
        }

        return await AddCapabilitiesAsync(project, methods, definition, cancellationToken).ConfigureAwait(false) ?? project.Solution;
    }

    /// <summary>
    /// Finds the attributed users of the diagnosed callback and validates the complete immutable correction with the compiler.
    /// </summary>
    /// <param name="project">The current editable project.</param>
    /// <param name="diagnosed">The selected callbacks, possibly declared by generic base classes.</param>
    /// <param name="definition">The actual runtime combine interface definition.</param>
    /// <param name="cancellationToken">The editor cancellation token.</param>
    /// <returns>The verified solution, or null when no valid correction exists.</returns>
    private static async Task<Solution?> AddCapabilitiesAsync(Project project, IReadOnlyList<IMethodSymbol> diagnosed, INamedTypeSymbol definition,
        CancellationToken cancellationToken)
    {
        Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            return null;
        }

        var corrections = new List<(DocumentId Document, TypeDeclarationSyntax Declaration, INamedTypeSymbol Capability, string Identity, string Implementation)>();
        foreach (INamedTypeSymbol type in Types(compilation.Assembly.GlobalNamespace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!type.GetAttributes().Any(static attribute => attribute.AttributeClass is { } name && IsRuntimeType(name, "PgAggregateAttribute", 0)) ||
                type.AllInterfaces.Any(static contract => IsRuntimeType(contract, "IPgCombinableAggregate", 1)))
            {
                continue;
            }

            INamedTypeSymbol[] contracts = [.. type.AllInterfaces.Where(static contract => IsRuntimeType(contract, "IPgAggregate", 2))];
            IMethodSymbol? implementation = VisibleCombine(type, compilation);
            if (contracts.Length != 1 || implementation is null ||
                !diagnosed.Any(method => SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, method.OriginalDefinition)) ||
                !MatchesContract(implementation, contracts[0].TypeArguments[0]))
            {
                continue;
            }

            TypeDeclarationSyntax? declaration = type.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax(cancellationToken)).OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (declaration is null || project.GetDocument(declaration.SyntaxTree) is not { } document ||
                type.GetDocumentationCommentId() is not { } identity ||
                implementation.OriginalDefinition.GetDocumentationCommentId() is not { } implementationIdentity)
            {
                continue;
            }

            corrections.Add((document.Id, declaration, definition.Construct(contracts[0].TypeArguments[0]), identity, implementationIdentity));
        }

        if (corrections.Count == 0)
        {
            return null;
        }

        Solution solution = project.Solution;
        SymbolDisplayFormat display = SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
        foreach (IGrouping<DocumentId, (DocumentId Document, TypeDeclarationSyntax Declaration, INamedTypeSymbol Capability, string Identity, string Implementation)> group
            in corrections.GroupBy(static correction => correction.Document))
        {
            Document document = solution.GetDocument(group.Key)!;
            SyntaxNode documentRoot = (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
            Dictionary<TypeDeclarationSyntax, INamedTypeSymbol> interfaces = group.ToDictionary(static correction => correction.Declaration,
                static correction => correction.Capability);
            SyntaxNode correctedRoot = documentRoot.ReplaceNodes(interfaces.Keys, (original, rewritten) => AddCapability(rewritten,
                SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaces[original].ToDisplayString(display)))
                    .WithAdditionalAnnotations(Simplifier.Annotation)));
            solution = solution.WithDocumentSyntaxRoot(group.Key, correctedRoot);
        }

        Compilation? corrected = await solution.GetProject(project.Id)!.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (corrected is null || CompilerErrorComparison.IntroducesErrors(compilation, corrected, cancellationToken))
        {
            return null;
        }

        foreach ((DocumentId _, TypeDeclarationSyntax _, INamedTypeSymbol _, string identity, string implementation) in corrections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DocumentationCommentId.GetFirstSymbolForDeclarationId(identity, corrected) is not INamedTypeSymbol type ||
                type.AllInterfaces.SingleOrDefault(static contract => IsRuntimeType(contract, "IPgCombinableAggregate", 1)) is not { } capability ||
                capability.GetMembers("Combine").SingleOrDefault() is not { } member ||
                type.FindImplementationForInterfaceMember(member) is not IMethodSymbol selected ||
                selected.OriginalDefinition.GetDocumentationCommentId() != implementation)
            {
                return null;
            }
        }

        return solution;
    }

    /// <summary>
    /// Enumerates source-assembly types without loading metadata assemblies or inspecting method bodies.
    /// </summary>
    /// <param name="owner">A source namespace or containing type.</param>
    /// <returns>All contained named types, including nested aggregate declarations.</returns>
    private static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol owner)
    {
        foreach (ISymbol member in owner.GetMembers())
        {
            if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (INamedTypeSymbol nested in Types(type))
                {
                    yield return nested;
                }
            }
            else if (member is INamespaceSymbol space)
            {
                foreach (INamedTypeSymbol nested in Types(space))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>
    /// Resolves the same declared or visible inherited role that receives the generator diagnostic.
    /// </summary>
    /// <param name="type">The attributed aggregate.</param>
    /// <param name="compilation">The current accessibility context.</param>
    /// <returns>The reserved callback, or null when none is visible.</returns>
    private static IMethodSymbol? VisibleCombine(INamedTypeSymbol type, Compilation compilation)
    {
        for (INamedTypeSymbol? owner = type; owner is not null; owner = owner.BaseType)
        {
            IMethodSymbol? method = owner.GetMembers("Combine").OfType<IMethodSymbol>().FirstOrDefault(candidate => candidate.IsStatic &&
                (SymbolEqualityComparer.Default.Equals(owner, type) || compilation.IsSymbolAccessibleWithin(candidate, type)));
            if (method is not null)
            {
                return method;
            }
        }

        return null;
    }

    /// <summary>
    /// Requires the compiler's exact state and NULL contract without modifying callback signatures or access.
    /// </summary>
    /// <param name="method">The actual callback after any inherited generic substitution.</param>
    /// <param name="state">The declared aggregate state.</param>
    /// <returns>Whether the callback can implement the combine interface unchanged.</returns>
    private static bool MatchesContract(IMethodSymbol method, ITypeSymbol state)
        => method is { IsStatic: true, IsAbstract: false, IsAsync: false, IsGenericMethod: false,
            DeclaredAccessibility: Accessibility.Public, MethodKind: MethodKind.Ordinary, ReturnsByRef: false, ReturnsByRefReadonly: false } &&
            method.Parameters.Length == 3 && method.Parameters.All(static parameter => parameter.RefKind == RefKind.None && !parameter.IsOptional && !parameter.IsParams) &&
            method.Parameters[0].Type is INamedTypeSymbol context && IsRuntimeType(context, "PgAggregateContext", 0) &&
            method.Parameters[0].GetAttributes().IsEmpty &&
            SymbolEqualityComparer.IncludeNullability.Equals(state, method.ReturnType) &&
            SymbolEqualityComparer.IncludeNullability.Equals(state, method.Parameters[1].Type) &&
            SymbolEqualityComparer.IncludeNullability.Equals(state, method.Parameters[2].Type);

    /// <summary>
    /// Matches the runtime's actual declarations without accepting similarly named consumer types.
    /// </summary>
    /// <param name="type">The bound symbol.</param>
    /// <param name="name">The expected type name.</param>
    /// <param name="arity">The expected generic arity.</param>
    /// <returns>Whether the symbol belongs to the Ankus runtime contract.</returns>
    private static bool IsRuntimeType(INamedTypeSymbol type, string name, int arity)
        => type.Name == name && type.Arity == arity && type.ContainingType is null &&
            type.ContainingNamespace.ToDisplayString() == "Ankus" && type.ContainingAssembly.Name == "Ankus.Runtime";

    /// <summary>
    /// Appends the capability to the base list without reformatting it: the new type takes over the trivia that ended
    /// the list, so an existing line ending or comment is never rewritten.
    /// </summary>
    /// <param name="declaration">The aggregate declaration.</param>
    /// <param name="capability">The combinable capability to implement.</param>
    /// <returns>The declaration with the capability as its last base type.</returns>
    private static TypeDeclarationSyntax AddCapability(TypeDeclarationSyntax declaration, BaseTypeSyntax capability)
    {
        if (declaration.BaseList is not { Types.Count: > 0 } list)
        {
            return (TypeDeclarationSyntax)declaration.AddBaseListTypes(capability);
        }

        int last = list.Types.Count - 1;
        IEnumerable<BaseTypeSyntax> types = list.Types.Select((type, index) => index == last ? type.WithoutTrailingTrivia() : type)
            .Append(capability.WithTrailingTrivia(list.Types[last].GetTrailingTrivia()));
        IEnumerable<SyntaxToken> separators = list.Types.GetSeparators()
            .Append(SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space));
        return declaration.WithBaseList(list.WithTypes(SyntaxFactory.SeparatedList(types, separators)));
    }
}
