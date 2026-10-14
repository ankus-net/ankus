using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace Ankus.CodeFixes;

/// <summary>
/// Gives raw native operations an explicit lexical unsafe context without changing their implementation.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NativeUnsafeAccessCodeFixProvider))]
[Shared]
public sealed class NativeUnsafeAccessCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// Composes all selected native contexts before one compiler validation per project.
    /// </summary>
    private static readonly FixAllProvider s_fixAll = new ProjectCodeFixAllProvider("Use unsafe blocks", ApplyProjectAsync);

    /// <summary>
    /// Gets the raw-access diagnostic corrected by this provider.
    /// </summary>
    public override ImmutableArray<string> FixableDiagnosticIds => ["ANKUS129"];

    /// <summary>
    /// Gets project batching for independent unsafe contexts.
    /// </summary>
    /// <returns>The provider that validates each project's composed correction once.</returns>
    public override FixAllProvider GetFixAllProvider() => s_fixAll;

    /// <summary>
    /// Registers a minimal correction for the current runtime-owned native contract without collecting compiler diagnostics.
    /// </summary>
    /// <param name="context">The editable document, diagnostics and cancellation token.</param>
    /// <returns>The registration operation.</returns>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        SemanticModel? model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null || model.Compilation.Options is not CSharpCompilationOptions { AllowUnsafe: true } ||
            CreateEdit(root, model, context.Span, context.CancellationToken) is null)
        {
            return;
        }

        foreach (Diagnostic diagnostic in context.Diagnostics.Where(static value => value.Id == "ANKUS129"))
        {
            context.RegisterCodeFix(CodeAction.Create("Use an unsafe block", async token =>
            {
                Solution corrected = await ApplyProjectAsync(context.Document.Project,
                    ImmutableDictionary.Create<DocumentId, ImmutableArray<Diagnostic>>().Add(context.Document.Id, [diagnostic]), token).ConfigureAwait(false);
                return corrected.GetDocument(context.Document.Id)!;
            }, nameof(NativeUnsafeAccessCodeFixProvider)), diagnostic);
        }
    }

    /// <summary>
    /// Selects the smallest legal statement or expression body and preserves local declarations' enclosing scope.
    /// </summary>
    /// <param name="root">The current document root.</param>
    /// <param name="model">The current bound source.</param>
    /// <param name="span">The diagnosed runtime reference.</param>
    /// <param name="cancellationToken">Cancels semantic inspection.</param>
    /// <returns>The exact immutable edit, or null when an automatic correction cannot preserve the contract.</returns>
    private static (SyntaxNode Target, ImmutableArray<SyntaxNode> Replacements)? CreateEdit(SyntaxNode root, SemanticModel model,
        Microsoft.CodeAnalysis.Text.TextSpan span, CancellationToken cancellationToken)
    {
        SyntaxNode expression = root.FindNode(span, getInnermostNodeForTie: true);
        ISymbol? symbol = model.GetSymbolInfo(expression, cancellationToken).Symbol;
        INamedTypeSymbol? attribute = model.Compilation.GetTypeByMetadataName("Ankus.CompilerServices.NativeUnsafeAccessAttribute");
        if (attribute?.ContainingAssembly.Name != "Ankus.Runtime" || symbol is null ||
            !symbol.GetAttributes().Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute)) ||
            expression.AncestorsAndSelf().Any(static node => node is UnsafeStatementSyntax ||
                node is MemberDeclarationSyntax member && member.Modifiers.Any(SyntaxKind.UnsafeKeyword) ||
                node is LocalFunctionStatementSyntax local && local.Modifiers.Any(SyntaxKind.UnsafeKeyword)) ||
            IsMetadataReference(model.GetOperation(expression, cancellationToken)))
        {
            return null;
        }

        SyntaxNode? owner = expression.AncestorsAndSelf().FirstOrDefault(static node =>
            node is StatementSyntax and not BlockSyntax || node is ArrowExpressionClauseSyntax or LambdaExpressionSyntax);
        if (owner is null || owner.DescendantTokens().Any(static token => token.IsKind(SyntaxKind.AwaitKeyword) || token.IsKind(SyntaxKind.YieldKeyword)))
        {
            return null;
        }

        if (owner is StatementSyntax statement)
        {
            return CreateStatementEdit(statement, model, cancellationToken);
        }

        SyntaxNode? replacement = owner switch
        {
            ArrowExpressionClauseSyntax arrow => ConvertArrow(arrow, model, cancellationToken),
            LambdaExpressionSyntax lambda when lambda.Body is ExpressionSyntax body => ConvertLambda(lambda, body, model, cancellationToken),
            _ => null,
        };
        SyntaxNode? target = owner is ArrowExpressionClauseSyntax ? owner.Parent : owner;
        return replacement is null || target is null ? null
            : (target, [replacement.WithAdditionalAnnotations(Formatter.Annotation)]);
    }

    /// <summary>
    /// Retains escaping declaration expressions outside the smallest native acknowledgment.
    /// </summary>
    /// <param name="statement">The original statement containing the native operation.</param>
    /// <param name="model">The original declaration types and variable scopes.</param>
    /// <param name="cancellationToken">Cancels semantic inspection.</param>
    /// <returns>The original target and complete scope-preserving statement replacements.</returns>
    private static (SyntaxNode Target, ImmutableArray<SyntaxNode> Replacements)? CreateStatementEdit(StatementSyntax statement,
        SemanticModel model, CancellationToken cancellationToken)
    {
        DeclarationExpressionSyntax[] declarations = [.. statement.DescendantNodes().OfType<DeclarationExpressionSyntax>()];
        SingleVariableDesignationSyntax[] patterns = [.. statement.DescendantNodes().OfType<SingleVariableDesignationSyntax>()
            .Where(static designation => !designation.Ancestors().Any(static node => node is DeclarationExpressionSyntax))];
        var hoisted = new List<SyntaxNode>();
        StatementSyntax rewritten = statement;
        if (declarations.Length != 0 || patterns.Length != 0)
        {
            DataFlowAnalysis? flow = model.AnalyzeDataFlow(statement);
            if (flow is not { Succeeded: true })
            {
                return null;
            }

            HashSet<ISymbol> escaping = new(flow.ReadOutside.Concat(flow.WrittenOutside), SymbolEqualityComparer.Default);
            if (patterns.Any(designation => model.GetDeclaredSymbol(designation, cancellationToken) is ILocalSymbol local && escaping.Contains(local)))
            {
                // A pattern variable cannot be declared apart from its pattern; an unsafe block would hide it from later statements.
                return null;
            }

            declarations = [.. declarations.Where(declaration => declaration.Designation.DescendantNodesAndSelf()
                .OfType<SingleVariableDesignationSyntax>().Any(designation =>
                    model.GetDeclaredSymbol(designation, cancellationToken) is ILocalSymbol local && escaping.Contains(local)))];
            foreach (DeclarationExpressionSyntax declaration in declarations)
            {
                foreach (SingleVariableDesignationSyntax designation in declaration.Designation.DescendantNodesAndSelf()
                    .OfType<SingleVariableDesignationSyntax>())
                {
                    if (model.GetDeclaredSymbol(designation, cancellationToken) is not ILocalSymbol local ||
                        !local.Type.CanBeReferencedByName)
                    {
                        return null;
                    }

                    TypeSyntax type = SyntaxFactory.ParseTypeName(local.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)));
                    hoisted.Add(SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(type,
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(designation.Identifier.WithoutTrivia()))))
                        .WithAdditionalAnnotations(Formatter.Annotation));
                }
            }

            rewritten = statement.ReplaceNodes(declarations, static (original, _) =>
                DesignationExpression(original.Designation, original.Type).WithTriviaFrom(original));
        }

        if (rewritten is LocalDeclarationStatementSyntax declarationStatement)
        {
            (SyntaxNode Target, ImmutableArray<SyntaxNode> Replacements)? localEdit = HoistDeclaration(declarationStatement,
                (LocalDeclarationStatementSyntax)statement, hoisted, model, cancellationToken);
            return localEdit is null ? null : (statement, [.. hoisted, .. localEdit.Value.Replacements]);
        }

        SyntaxNode region = SyntaxFactory.UnsafeStatement(SyntaxFactory.Block(rewritten.WithoutLeadingTrivia()))
            .WithLeadingTrivia(statement.GetLeadingTrivia()).WithAdditionalAnnotations(Formatter.Annotation);
        return (statement, [.. hoisted, region]);
    }

    /// <summary>
    /// Converts an already declared out or tuple designation to its equivalent assignment expression.
    /// </summary>
    /// <param name="designation">The original bound variable designation.</param>
    /// <param name="type">The declaration's original inferred or explicit type.</param>
    /// <returns>The equivalent identifier, discard or nested tuple expression.</returns>
    private static ExpressionSyntax DesignationExpression(VariableDesignationSyntax designation, TypeSyntax type) => designation switch
    {
        SingleVariableDesignationSyntax single => SyntaxFactory.IdentifierName(single.Identifier.WithoutTrivia()),
        DiscardDesignationSyntax discard => SyntaxFactory.DeclarationExpression(type.WithoutTrivia(), discard.WithoutTrivia()),
        ParenthesizedVariableDesignationSyntax tuple => SyntaxFactory.TupleExpression(
            SyntaxFactory.SeparatedList(tuple.Variables.Select(variable => SyntaxFactory.Argument(DesignationExpression(variable, type))))),
        _ => throw new InvalidOperationException("An unsupported declaration designation reached the native scope correction."),
    };

    /// <summary>
    /// Leaves a local's declaration in its original scope while acknowledging only its initializer assignments.
    /// </summary>
    /// <param name="declaration">The original local declaration.</param>
    /// <param name="original">The declaration retained in the original semantic model.</param>
    /// <param name="hoisted">The escaping declaration-expression locals declared before the statement.</param>
    /// <param name="model">Its declared managed types.</param>
    /// <param name="cancellationToken">Cancels binding.</param>
    /// <returns>The declaration and unsafe assignments, or null for a contract requiring a broader manual edit.</returns>
    private static (SyntaxNode Target, ImmutableArray<SyntaxNode> Replacements)? HoistDeclaration(LocalDeclarationStatementSyntax declaration,
        LocalDeclarationStatementSyntax original, List<SyntaxNode> hoisted, SemanticModel model, CancellationToken cancellationToken)
    {
        TypeSyntax declared = declaration.Declaration.Type;
        ScopedTypeSyntax? scoped = declared as ScopedTypeSyntax;
        TypeSyntax type = scoped?.Type ?? declared;
        if (declaration.UsingKeyword != default || declaration.AwaitKeyword != default ||
            declaration.Modifiers.Any(SyntaxKind.ConstKeyword) || type is RefTypeSyntax ||
            original.Parent is not (BlockSyntax or SwitchSectionSyntax))
        {
            return null;
        }

        if (type.IsVar)
        {
            if (model.GetDeclaredSymbol(original.Declaration.Variables[0], cancellationToken) is not ILocalSymbol local ||
                !local.Type.CanBeReferencedByName)
            {
                return null;
            }

            type = SyntaxFactory.ParseTypeName(local.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)))
                .WithTriviaFrom(type);
        }

        if (declaration.Declaration.Variables.Any(static variable => variable.Initializer?.Value is RefExpressionSyntax))
        {
            return null;
        }

        StatementSyntax[] assignments = [.. declaration.Declaration.Variables.Where(static variable => variable.Initializer is not null)
            .Select(static variable => SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(variable.Identifier.WithoutTrivia()), variable.Initializer!.Value)))];
        if (scoped is not null)
        {
            type = scoped.WithType(type);
        }
        else if (RequiresScopedDeclaration(declaration, original, hoisted, type, assignments, model, cancellationToken))
        {
            // An initialized ref struct takes its initializer's safe context; an uninitialized declaration would widen it to the caller.
            type = SyntaxFactory.ScopedType(SyntaxFactory.Token(type.GetLeadingTrivia(), SyntaxKind.ScopedKeyword,
                SyntaxFactory.TriviaList(SyntaxFactory.Space)), type.WithoutLeadingTrivia());
        }

        LocalDeclarationStatementSyntax outer = declaration.WithDeclaration(declaration.Declaration.WithType(type)
            .WithVariables(SyntaxFactory.SeparatedList(declaration.Declaration.Variables.Select(static variable => variable.WithInitializer(null)))))
            .WithAdditionalAnnotations(Formatter.Annotation);
        return (declaration, [outer, SyntaxFactory.UnsafeStatement(SyntaxFactory.Block(assignments)).WithAdditionalAnnotations(Formatter.Annotation)]);
    }

    /// <summary>
    /// Determines whether separating a ref struct declaration from its initializer would widen the local's safe context.
    /// </summary>
    /// <param name="declaration">The declaration after earlier escaping-local rewrites.</param>
    /// <param name="original">The declaration retained in the original semantic model.</param>
    /// <param name="hoisted">The escaping declaration-expression locals declared before the statement.</param>
    /// <param name="type">The explicit local type.</param>
    /// <param name="assignments">The initializer assignments that will run after the declaration.</param>
    /// <param name="model">The original semantic model.</param>
    /// <param name="cancellationToken">Cancels binding.</param>
    /// <returns>Whether the uninitialized declaration must be scoped to keep the original lifetime.</returns>
    private static bool RequiresScopedDeclaration(LocalDeclarationStatementSyntax declaration, LocalDeclarationStatementSyntax original,
        List<SyntaxNode> hoisted, TypeSyntax type, StatementSyntax[] assignments, SemanticModel model, CancellationToken cancellationToken)
    {
        if (assignments.Length == 0 || original.FirstAncestorOrSelf<MemberDeclarationSyntax>() is not { } member ||
            model.GetDeclaredSymbol(original.Declaration.Variables[0], cancellationToken) is not ILocalSymbol { Type.IsRefLikeType: true })
        {
            return false;
        }

        // Compile the same split in safe code, where widening a stack-bound initializer is a ref-safety error rather than a warning.
        LocalDeclarationStatementSyntax unscoped = declaration.WithDeclaration(declaration.Declaration.WithType(type)
            .WithVariables(SyntaxFactory.SeparatedList(declaration.Declaration.Variables.Select(static variable => variable.WithInitializer(null)))));
        SyntaxTree tree = original.SyntaxTree;
        SyntaxNode tracked = tree.GetRoot(cancellationToken).TrackNodes(member, original);
        SyntaxNode probeRoot = tracked.ReplaceNode(tracked.GetCurrentNode(original)!, [.. hoisted, unscoped, .. assignments]);
        SyntaxTree probeTree = tree.WithRootAndOptions(probeRoot, tree.Options);
        Compilation probe = model.Compilation.ReplaceSyntaxTree(tree, probeTree);
        int originalErrors = model.GetDiagnostics(member.Span, cancellationToken)
            .Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        int probeErrors = probe.GetSemanticModel(probeTree).GetDiagnostics(probeRoot.GetCurrentNode(member)!.Span, cancellationToken)
            .Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return probeErrors > originalErrors;
    }

    /// <summary>
    /// Composes all selected edits without repeatedly compiling intermediate document corrections.
    /// </summary>
    /// <param name="project">The original project.</param>
    /// <param name="diagnostics">The selected source diagnostics by document.</param>
    /// <param name="cancellationToken">Cancels editing and the final compiler comparison.</param>
    /// <returns>The verified correction, or the unchanged solution when the complete edit introduces an error.</returns>
    private static async Task<Solution> ApplyProjectAsync(Project project,
        ImmutableDictionary<DocumentId, ImmutableArray<Diagnostic>> diagnostics, CancellationToken cancellationToken)
    {
        Compilation? original = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (original?.Options is not CSharpCompilationOptions { AllowUnsafe: true })
        {
            return project.Solution;
        }

        Solution solution = project.Solution;
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

            (SyntaxNode Target, ImmutableArray<SyntaxNode> Replacements)[] edits = [.. values
                .Where(static diagnostic => diagnostic.Id == "ANKUS129")
                .Select(diagnostic => CreateEdit(root, model, diagnostic.Location.SourceSpan, cancellationToken))
                .Where(static edit => edit.HasValue).Select(static edit => edit!.Value)
                .GroupBy(static edit => edit.Target).Select(static group => group.First())];
            edits = [.. edits.Where(edit => !edits.Any(other => !ReferenceEquals(edit.Target, other.Target) &&
                edit.Target.Ancestors().Contains(other.Target)))];
            SyntaxNode correctedRoot = root.TrackNodes(edits.Select(static edit => edit.Target));
            foreach ((SyntaxNode target, ImmutableArray<SyntaxNode> replacements) in edits)
            {
                SyntaxNode current = correctedRoot.GetCurrentNode(target)!;
                correctedRoot = replacements.Length == 1 ? correctedRoot.ReplaceNode(current, replacements[0])
                    : correctedRoot.ReplaceNode(current, replacements);
            }

            solution = solution.WithDocumentSyntaxRoot(identity, correctedRoot);
        }

        Compilation? corrected = await solution.GetProject(project.Id)!.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        return corrected is null || CompilerErrorComparison.IntroducesErrors(original, corrected, cancellationToken)
            ? project.Solution : solution;
    }

    /// <summary>
    /// Changes an expression body to an equivalent unsafe block, preserving return and throw semantics.
    /// </summary>
    /// <param name="arrow">The original expression body.</param>
    /// <param name="model">The bound declaration and return contract.</param>
    /// <param name="cancellationToken">The editor cancellation token.</param>
    /// <returns>The corrected declaration, or null when no declaration can be converted.</returns>
    private static SyntaxNode? ConvertArrow(ArrowExpressionClauseSyntax arrow, SemanticModel model, CancellationToken cancellationToken)
    {
        SyntaxNode declaration = arrow.Parent!;
        ISymbol? symbol = model.GetDeclaredSymbol(declaration, cancellationToken);
        bool returnsValue = symbol is IPropertySymbol || symbol is IMethodSymbol { ReturnsVoid: false };
        BlockSyntax body = CreateBody(arrow.Expression, returnsValue);
        StatementSyntax statement = body.Statements[0];
        body = body.ReplaceNode(statement, statement.WithLeadingTrivia(arrow.ArrowToken.LeadingTrivia
            .AddRange(arrow.ArrowToken.TrailingTrivia).AddRange(statement.GetLeadingTrivia())));
        SyntaxToken semicolon = declaration.GetLastToken();
        body = body.WithCloseBraceToken(body.CloseBraceToken.WithTrailingTrivia(semicolon.TrailingTrivia));
        return declaration switch
        {
            MethodDeclarationSyntax method => method.WithExpressionBody(null).WithSemicolonToken(default).WithBody(body),
            LocalFunctionStatementSyntax local => local.WithExpressionBody(null).WithSemicolonToken(default).WithBody(body),
            ConstructorDeclarationSyntax constructor => constructor.WithExpressionBody(null).WithSemicolonToken(default).WithBody(body),
            DestructorDeclarationSyntax destructor => destructor.WithExpressionBody(null).WithSemicolonToken(default).WithBody(body),
            OperatorDeclarationSyntax operation => operation.WithExpressionBody(null).WithSemicolonToken(default).WithBody(body),
            ConversionOperatorDeclarationSyntax conversion => conversion.WithExpressionBody(null).WithSemicolonToken(default).WithBody(body),
            AccessorDeclarationSyntax accessor => accessor.WithExpressionBody(null).WithSemicolonToken(default).WithBody(body),
            PropertyDeclarationSyntax property => property.WithExpressionBody(null).WithSemicolonToken(default)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(body)))),
            IndexerDeclarationSyntax indexer => indexer.WithExpressionBody(null).WithSemicolonToken(default)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(body)))),
            _ => null,
        };
    }

    /// <summary>
    /// Preserves the delegate's value or void return contract when changing a lambda body.
    /// </summary>
    /// <param name="lambda">The bound lambda expression.</param>
    /// <param name="expression">Its original expression body.</param>
    /// <param name="model">The current semantic model.</param>
    /// <param name="cancellationToken">The editor cancellation token.</param>
    /// <returns>The corrected lambda, or null for an incompatible conversion.</returns>
    private static SyntaxNode? ConvertLambda(LambdaExpressionSyntax lambda, ExpressionSyntax expression,
        SemanticModel model, CancellationToken cancellationToken)
    {
        if (model.GetTypeInfo(lambda, cancellationToken).ConvertedType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } type ||
            type.DelegateInvokeMethod is not { } invoke)
        {
            return null;
        }

        BlockSyntax body = CreateBody(expression, !invoke.ReturnsVoid);
        return lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.WithBody(body),
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.WithBody(body),
            _ => null,
        };
    }

    /// <summary>
    /// Keeps the original expression and its evaluation order inside a single native-contract acknowledgment.
    /// </summary>
    /// <param name="expression">The unchanged expression to execute.</param>
    /// <param name="returnsValue">Whether the surrounding callable returns a value.</param>
    /// <returns>The new body containing an explicit unsafe statement.</returns>
    private static BlockSyntax CreateBody(ExpressionSyntax expression, bool returnsValue)
    {
        StatementSyntax statement = expression is ThrowExpressionSyntax thrown
            ? SyntaxFactory.ThrowStatement(thrown.Expression).WithTriviaFrom(expression)
            : returnsValue ? SyntaxFactory.ReturnStatement(expression) : SyntaxFactory.ExpressionStatement(expression);
        return SyntaxFactory.Block(SyntaxFactory.UnsafeStatement(SyntaxFactory.Block(statement)));
    }

    /// <summary>
    /// Excludes metadata-only references that do not invoke or access native storage.
    /// </summary>
    /// <param name="operation">The operation at the diagnostic's current location.</param>
    /// <returns>Whether a nameof operation contains the reference.</returns>
    private static bool IsMetadataReference(IOperation? operation)
    {
        for (IOperation? current = operation; current is not null; current = current.Parent)
        {
            if (current.Kind == OperationKind.NameOf)
            {
                return true;
            }
        }

        return false;
    }

}
