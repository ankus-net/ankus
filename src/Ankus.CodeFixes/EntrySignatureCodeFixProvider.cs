using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace Ankus.CodeFixes;

/// <summary>
/// Gives a PostgreSQL entry the signature its native caller uses: the trigger context, worker argument or benchmark
/// driver it receives, the fixed types of cast modifiers, no C# defaults, <c>params</c> or metadata where PostgreSQL passes
/// none, and results returned by value.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EntrySignatureCodeFixProvider))]
[Shared]
public sealed class EntrySignatureCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// A function that returns by reference.
    /// </summary>
    private const string FunctionReferenceId = "ANKUS037";

    /// <summary>
    /// A function parameter with <c>Element</c> or <c>Variadic</c> options.
    /// </summary>
    private const string OptionsId = "ANKUS057";

    /// <summary>
    /// An operator or cast parameter declared with <c>params</c>.
    /// </summary>
    private const string VariadicId = "ANKUS067";

    /// <summary>
    /// A cast type-modifier parameter that is not <c>int</c>.
    /// </summary>
    private const string CastModifierId = "ANKUS076";

    /// <summary>
    /// A cast explicit-conversion parameter that is not <c>bool</c>.
    /// </summary>
    private const string CastExplicitId = "ANKUS077";

    /// <summary>
    /// An aggregate callback parameter with a C# default.
    /// </summary>
    private const string AggregateDefaultId = "ANKUS113";

    /// <summary>
    /// An aggregate invocation context with attributes.
    /// </summary>
    private const string AggregateContextId = "ANKUS114";

    /// <summary>
    /// An empty aggregate argument group with attributes.
    /// </summary>
    private const string AggregateEmptyId = "ANKUS119";


    /// <summary>
    /// A GUC property that returns by reference.
    /// </summary>
    private const string SettingReferenceId = "ANKUS172";

    /// <summary>
    /// A trigger or event trigger that returns by reference.
    /// </summary>
    private const string CallbackReferenceId = "ANKUS212";

    /// <summary>
    /// A trigger or event trigger without its context parameter.
    /// </summary>
    private const string CallbackCountId = "ANKUS215";

    /// <summary>
    /// A trigger or event trigger context with a C# default.
    /// </summary>
    private const string CallbackOptionalId = "ANKUS218";

    /// <summary>
    /// A trigger or event trigger context declared nullable.
    /// </summary>
    private const string CallbackNullableId = "ANKUS219";

    /// <summary>
    /// A background worker without its argument parameter.
    /// </summary>
    private const string WorkerCountId = "ANKUS258";

    /// <summary>
    /// A background worker argument that is not <c>nuint</c>.
    /// </summary>
    private const string WorkerTypeId = "ANKUS260";

    /// <summary>
    /// A benchmark without exactly one <c>PgBencher</c> parameter.
    /// </summary>
    private const string BenchmarkId = "ANKUS532";

    /// <summary>
    /// A native callback property that returns by reference.
    /// </summary>
    private const string PropertyReferenceId = "ANKUS313";

    /// <summary>
    /// A native callback handler that returns by reference.
    /// </summary>
    private const string HandlerReferenceId = "ANKUS339";

    /// <summary>
    /// Gets the signature diagnostics corrected by this provider.
    /// </summary>
    public override ImmutableArray<string> FixableDiagnosticIds =>
    [
        FunctionReferenceId, OptionsId, VariadicId, CastModifierId, CastExplicitId, AggregateDefaultId, AggregateContextId, AggregateEmptyId,
        SettingReferenceId, CallbackReferenceId, CallbackCountId, CallbackOptionalId, CallbackNullableId, WorkerCountId, WorkerTypeId,
        PropertyReferenceId, HandlerReferenceId, BenchmarkId,
    ];

    /// <summary>
    /// Gets batch support for independent parameter corrections.
    /// </summary>
    /// <returns>The standard Roslyn batch provider.</returns>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <summary>
    /// Registers a correction that is kept only if every caller and use of the parameter still compiles.
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
            SyntaxNode node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (diagnostic.Id is FunctionReferenceId or SettingReferenceId or CallbackReferenceId or PropertyReferenceId or HandlerReferenceId)
            {
                if (node.FirstAncestorOrSelf<RefTypeSyntax>() is { } reference && reference.Parent switch
                    {
                        MethodDeclarationSyntax method when method.ReturnType == reference => method.Identifier.ValueText,
                        PropertyDeclarationSyntax property when property.Type == reference => property.Identifier.ValueText,
                        _ => null,
                    } is { } name)
                {
                    var member = (MemberDeclarationSyntax)reference.Parent!;
                    Register(context, diagnostic, $"Return '{name}' by value", root.ReplaceNode(member, ByValue(member, reference)));
                }

                continue;
            }

            if (diagnostic.Id is CallbackCountId or WorkerCountId || diagnostic.Id == BenchmarkId && node is ParameterListSyntax)
            {
                if (node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { ParameterList: { Parameters.Count: 0 } list } method &&
                    model.GetDeclaredSymbol(method, cancellationToken) is { } symbol &&
                    Receiver(diagnostic.Id, symbol) is ({ } type, { } name))
                {
                    ParameterSyntax added = SyntaxFactory.Parameter(SyntaxFactory.Identifier(name))
                        .WithType(SyntaxFactory.ParseTypeName(type).WithAdditionalAnnotations(Simplifier.Annotation)
                            .WithTrailingTrivia(SyntaxFactory.Space));
                    string shown = type.Substring(type.LastIndexOf('.') + 1);
                    Register(context, diagnostic, $"Add the '{shown} {name}' parameter", root.ReplaceNode(list, list.AddParameters(added)));
                }

                continue;
            }

            if (node.FirstAncestorOrSelf<ParameterSyntax>() is not { } parameter)
            {
                continue;
            }

            string parameterName = parameter.Identifier.ValueText;
            switch (diagnostic.Id)
            {
                case OptionsId when node.FirstAncestorOrSelf<AttributeArgumentSyntax>() is { NameEquals: { } option } argument:
                    RegisterOption(context, diagnostic, model, root, parameter, argument, option.Name.Identifier.ValueText);
                    break;
                case VariadicId when parameter.Modifiers.IndexOf(SyntaxKind.ParamsKeyword) is var index and >= 0:
                    Register(context, diagnostic, "Remove 'params'", root.ReplaceNode(parameter, RemoveModifier(parameter, index)));
                    break;
                case CastModifierId or CastExplicitId or WorkerTypeId or BenchmarkId when parameter.Type is { } type:
                    string expected = diagnostic.Id switch
                    {
                        CastModifierId => "int",
                        CastExplicitId => "bool",
                        WorkerTypeId => "nuint",
                        _ => "global::Ankus.PgBencher",
                    };
                    string shownType = expected.Substring(expected.LastIndexOf('.') + 1);
                    Register(context, diagnostic, $"Change the type of '{parameterName}' to '{shownType}'", root.ReplaceNode(type,
                        SyntaxFactory.ParseTypeName(expected).WithTriviaFrom(type).WithAdditionalAnnotations(Simplifier.Annotation)));
                    break;
                case AggregateDefaultId or CallbackOptionalId when parameter.Default is not null:
                    Register(context, diagnostic, $"Remove the default value of '{parameterName}'", root.ReplaceNode(parameter,
                        parameter.WithDefault(null).WithIdentifier(parameter.Identifier.WithTrailingTrivia())));
                    break;
                case CallbackOptionalId when node.FirstAncestorOrSelf<AttributeSyntax>() is { } optional:
                    Register(context, diagnostic, $"Remove '{optional.Name}'", root.ReplaceNode(parameter, RemoveAttribute(parameter, optional)));
                    break;
                case CallbackNullableId when parameter.Type is NullableTypeSyntax nullable:
                    Register(context, diagnostic, $"Change the type of '{parameterName}' to '{nullable.ElementType}'",
                        root.ReplaceNode(nullable, nullable.ElementType.WithTriviaFrom(nullable)));
                    break;
                case AggregateContextId or AggregateEmptyId when parameter.AttributeLists.Count != 0:
                    ParameterSyntax bare = parameter.WithAttributeLists(default)
                        .WithLeadingTrivia(parameter.AttributeLists[0].GetLeadingTrivia());
                    Register(context, diagnostic, $"Remove the attributes of '{parameterName}'", root.ReplaceNode(parameter, bare));
                    break;
            }
        }
    }

    /// <summary>
    /// Selects the parameter PostgreSQL passes to a trigger, event trigger or background worker entry.
    /// </summary>
    /// <param name="id">The parameter-count diagnostic.</param>
    /// <param name="method">The bound entry.</param>
    /// <returns>The fully qualified parameter type and its conventional name, or nulls when the entry kind is ambiguous.</returns>
    private static (string? Type, string? Name) Receiver(string id, IMethodSymbol method)
    {
        if (id == WorkerCountId)
        {
            return ("nuint", "argument");
        }

        if (id == BenchmarkId)
        {
            return ("global::Ankus.PgBencher", "bencher");
        }

        string[] kinds = [.. method.GetAttributes().Select(static attribute => attribute.AttributeClass?.ToDisplayString())
            .Where(static name => name is "Ankus.PgTriggerAttribute" or "Ankus.PgEventTriggerAttribute").OfType<string>()];
        return kinds.Length == 1
            ? ("global::Ankus." + (kinds[0] == "Ankus.PgTriggerAttribute" ? "PgTriggerContext" : "PgEventTriggerContext"), "context")
            : (null, null);
    }

    /// <summary>
    /// Removes an <c>Element</c> option, or replaces <c>Variadic = true</c> with the C# <c>params</c> modifier on a final array.
    /// </summary>
    /// <param name="context">The registration context.</param>
    /// <param name="diagnostic">The parameter options diagnostic.</param>
    /// <param name="model">The document's semantic model.</param>
    /// <param name="root">The current document root.</param>
    /// <param name="parameter">The diagnosed parameter.</param>
    /// <param name="argument">The named attribute option.</param>
    /// <param name="option">The option name.</param>
    private static void RegisterOption(CodeFixContext context, Diagnostic diagnostic, SemanticModel model, SyntaxNode root,
        ParameterSyntax parameter, AttributeArgumentSyntax argument, string option)
    {
        if (argument.Parent?.Parent is not AttributeSyntax attribute || attribute.Parent?.Parent != parameter)
        {
            return;
        }

        ParameterSyntax edited = (ParameterSyntax)RemoveInapplicableCodeFixProvider.RemoveOption(parameter, argument);
        if (option == "Element")
        {
            Register(context, diagnostic, "Remove 'Element'", root.ReplaceNode(parameter, edited));
            return;
        }

        if (option == "Variadic" && model.GetConstantValue(argument.Expression, context.CancellationToken) is { HasValue: true, Value: true } &&
            parameter.Parent is ParameterListSyntax list && list.Parameters.Last() == parameter && parameter.Type is ArrayTypeSyntax &&
            !parameter.Modifiers.Any())
        {
            ParameterSyntax variadicParameter = edited.WithType(edited.Type!.WithoutLeadingTrivia())
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(edited.Type!.GetLeadingTrivia(), SyntaxKind.ParamsKeyword,
                    SyntaxFactory.TriviaList(SyntaxFactory.Space))));
            Register(context, diagnostic, $"Declare '{parameter.Identifier.ValueText}' with 'params'", root.ReplaceNode(parameter, variadicParameter));
        }
    }

    /// <summary>
    /// Removes <c>ref</c> or <c>ref readonly</c> from a result and from the member's own <c>return ref</c> and
    /// <c>=&gt; ref</c> expressions, leaving those in nested lambdas and local functions.
    /// </summary>
    /// <param name="member">The method or property.</param>
    /// <param name="reference">Its by-reference result type.</param>
    /// <returns>The member returning the same value by value.</returns>
    private static MemberDeclarationSyntax ByValue(MemberDeclarationSyntax member, RefTypeSyntax reference)
    {
        RefExpressionSyntax[] returned = [.. member.DescendantNodes(static node => node is not (AnonymousFunctionExpressionSyntax or
            LocalFunctionStatementSyntax)).OfType<RefExpressionSyntax>().Where(static expression => expression.Parent is ReturnStatementSyntax or
            ArrowExpressionClauseSyntax)];
        return member.ReplaceNodes<MemberDeclarationSyntax, SyntaxNode>([reference, .. returned], static (_, rewritten) => rewritten switch
        {
            RefTypeSyntax type => type.Type.WithTriviaFrom(type),
            RefExpressionSyntax expression => expression.Expression.WithTriviaFrom(expression),
            _ => rewritten,
        });
    }

    /// <summary>
    /// Removes one parameter modifier, moving its leading trivia to the next token when it comes first.
    /// </summary>
    /// <param name="parameter">The parameter.</param>
    /// <param name="index">The modifier position.</param>
    /// <returns>The edited parameter.</returns>
    private static ParameterSyntax RemoveModifier(ParameterSyntax parameter, int index)
    {
        SyntaxToken modifier = parameter.Modifiers[index];
        if (index == 0)
        {
            SyntaxToken next = modifier.GetNextToken();
            parameter = parameter.ReplaceToken(next, next.WithLeadingTrivia(modifier.LeadingTrivia));
        }

        return parameter.WithModifiers(parameter.Modifiers.RemoveAt(index));
    }

    /// <summary>
    /// Removes one parameter attribute, and its list when no attribute remains.
    /// </summary>
    /// <param name="parameter">The parameter.</param>
    /// <param name="attribute">The attribute.</param>
    /// <returns>The edited parameter.</returns>
    private static ParameterSyntax RemoveAttribute(ParameterSyntax parameter, AttributeSyntax attribute)
    {
        var list = (AttributeListSyntax)attribute.Parent!;
        if (list.Attributes.Count > 1)
        {
            return parameter.ReplaceNode(list, list.WithAttributes(list.Attributes.Remove(attribute)));
        }

        SyntaxToken next = list.GetLastToken().GetNextToken();
        ParameterSyntax moved = parameter.ReplaceToken(next, next.WithLeadingTrivia(list.GetLeadingTrivia()));
        return moved.WithAttributeLists(moved.AttributeLists.RemoveAt(parameter.AttributeLists.IndexOf(list)));
    }

    /// <summary>
    /// Registers a compile-checked single-document correction.
    /// </summary>
    /// <param name="context">The registration context.</param>
    /// <param name="diagnostic">The diagnostic corrected.</param>
    /// <param name="title">The action title.</param>
    /// <param name="corrected">The edited root.</param>
    private static void Register(CodeFixContext context, Diagnostic diagnostic, string title, SyntaxNode corrected)
        => context.RegisterCodeFix(CompilerErrorComparison.CreateAction(context.Document, corrected, title, "Ankus." + diagnostic.Id), diagnostic);
}
