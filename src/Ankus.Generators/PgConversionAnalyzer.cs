using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Ankus.Generators;

/// <summary>
/// Rejects a type that a <c>PgConvertible</c> type parameter or <see cref="System.Type"/> parameter can never convert,
/// as pgrx's <c>IntoDatum</c>, <c>FromDatum</c> and <c>RangeSubType</c> bounds do.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PgConversionAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_value = new(
        "ANKUS541", "Unsupported PostgreSQL value type",
        "Type '{0}' has no supported SQL conversion; use a supported SQL type or declare an explicit datum mapping",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: "https://ankus.net/spi/#value-types");

    private static readonly DiagnosticDescriptor s_bound = new(
        "ANKUS542", "Unsupported PostgreSQL range bound",
        "Type '{0}' is not a PostgreSQL range bound; use a built-in bound type or a PgRangeType mapping",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: "https://ankus.net/ranges/");

    /// <summary>
    /// Gets the compiler errors for values and range bounds without a PostgreSQL conversion.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [s_value, s_bound];

    /// <summary>
    /// Registers checks of invocations and constructions whose type arguments the runtime converts.
    /// </summary>
    /// <param name="context">The analyzer registration context.</param>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            INamedTypeSymbol? attribute = start.Compilation.GetTypeByMetadataName("Ankus.PgConvertibleAttribute");
            if (attribute?.ContainingAssembly.Name != "Ankus.Runtime")
            {
                return;
            }

            var types = new PgConversionTypes(start.Compilation);
            start.RegisterOperationAction(operation => AnalyzeInvocation(operation, attribute, types), OperationKind.Invocation);
            start.RegisterOperationAction(operation => AnalyzeMethodReference(operation, attribute, types), OperationKind.MethodReference);
            start.RegisterOperationAction(operation => AnalyzeCreation(operation, attribute, types), OperationKind.ObjectCreation);
        });
    }

    /// <summary>
    /// Checks a call's marked type arguments and the <c>typeof</c> operands of its marked <see cref="System.Type"/> parameters.
    /// </summary>
    private static void AnalyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol attribute, PgConversionTypes types)
    {
        var invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod;
        IMethodSymbol definition = method.OriginalDefinition;
        bool bound = IsRange(definition.ContainingType);
        for (int index = 0; index < method.TypeArguments.Length; index++)
        {
            ITypeParameterSymbol parameter = definition.TypeParameters[index];
            if (IsMarked(parameter, attribute) && IsUnsupported(types, method.TypeArguments[index], bound))
            {
                Report(context, method.TypeArguments[index], bound, TypeArgument(invocation, parameter, index));
            }
        }

        foreach (ITypeOfOperation operand in invocation.Arguments
            .Where(argument => argument.Parameter is { } parameter && HoldsTypes(parameter.Type) && IsMarked(parameter.OriginalDefinition, attribute))
            .SelectMany(static argument => TypeOperands(argument.Value))
            .Where(operand => IsUnsupported(types, operand.TypeOperand, bound: false)))
        {
            SyntaxNode syntax = operand.Syntax is TypeOfExpressionSyntax expression ? expression.Type : operand.Syntax;
            Report(context, operand.TypeOperand, bound: false, syntax.GetLocation());
        }
    }

    /// <summary>
    /// Checks a method group, such as <c>values.Select(SpiParameter.Create)</c>, whose delegate converts each call's value.
    /// </summary>
    private static void AnalyzeMethodReference(OperationAnalysisContext context, INamedTypeSymbol attribute, PgConversionTypes types)
    {
        var reference = (IMethodReferenceOperation)context.Operation;
        IMethodSymbol method = reference.Method;
        IMethodSymbol definition = method.OriginalDefinition;
        bool bound = IsRange(definition.ContainingType);
        for (int index = 0; index < method.TypeArguments.Length; index++)
        {
            if (IsMarked(definition.TypeParameters[index], attribute) && IsUnsupported(types, method.TypeArguments[index], bound))
            {
                SyntaxNode name = reference.Syntax is MemberAccessExpressionSyntax member ? member.Name : reference.Syntax;
                Report(context, method.TypeArguments[index], bound, (TypeArgument(name as TypeSyntax, index) ?? reference.Syntax).GetLocation());
            }
        }
    }

    /// <summary>
    /// Checks the marked type arguments of a constructed generic type, such as <c>new PgRange&lt;ushort&gt;()</c>.
    /// </summary>
    private static void AnalyzeCreation(OperationAnalysisContext context, INamedTypeSymbol attribute, PgConversionTypes types)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Type is not INamedTypeSymbol { IsGenericType: true } type)
        {
            return;
        }

        INamedTypeSymbol definition = type.OriginalDefinition;
        bool bound = IsRange(definition);
        for (int index = 0; index < type.TypeArguments.Length; index++)
        {
            if (IsMarked(definition.TypeParameters[index], attribute) && IsUnsupported(types, type.TypeArguments[index], bound))
            {
                SyntaxNode syntax = TypeArgument(creation.Syntax is ObjectCreationExpressionSyntax expression ? expression.Type : null, index) ??
                    creation.Syntax;
                Report(context, type.TypeArguments[index], bound, syntax.GetLocation());
            }
        }
    }

    /// <summary>
    /// Limits attribute reads to parameters that can hold <c>typeof</c> operands, such as <c>params ReadOnlySpan&lt;Type&gt;</c>.
    /// </summary>
    private static bool HoldsTypes(ITypeSymbol type)
        => (type is IArrayTypeSymbol array ? array.ElementType : type is INamedTypeSymbol { Arity: 1 } named ? named.TypeArguments[0] : type)
            is { Name: "Type", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } };

    /// <summary>
    /// Applies the range bound rules or the value rules.
    /// </summary>
    private static bool IsUnsupported(PgConversionTypes types, ITypeSymbol type, bool bound)
        => (bound ? PgConversionTypes.Bound(type) : types.Value(type)) == PgConversionSupport.Unsupported;

    /// <summary>
    /// Reports the type with ANKUS542 for a range bound and ANKUS541 otherwise.
    /// </summary>
    private static void Report(OperationAnalysisContext context, ITypeSymbol type, bool bound, Location location)
        => context.ReportDiagnostic(Diagnostic.Create(bound ? s_bound : s_value, location, type.ToDisplayString()));

    /// <summary>
    /// Locates an explicit type argument, or else the argument whose value inferred it.
    /// </summary>
    private static Location TypeArgument(IInvocationOperation invocation, ITypeParameterSymbol parameter, int index)
    {
        TypeSyntax? name = invocation.Syntax is InvocationExpressionSyntax syntax ? syntax.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            ExpressionSyntax expression => expression as SimpleNameSyntax,
        } : null;
        if (TypeArgument(name, index) is { } explicitType)
        {
            return explicitType.GetLocation();
        }

        IArgumentOperation? inferred = invocation.Arguments.FirstOrDefault(argument => !argument.IsImplicit &&
            argument.Parameter is { } declared && Contains(declared.OriginalDefinition.Type, parameter));
        SyntaxNode value = inferred?.Value.Syntax ?? invocation.Syntax;
        return (value is InterpolationSyntax hole ? hole.Expression : value).GetLocation();
    }

    /// <summary>
    /// Finds a type argument in a generic name, including a qualified or alias-qualified one.
    /// </summary>
    private static TypeSyntax? TypeArgument(TypeSyntax? type, int index)
        => type switch
        {
            GenericNameSyntax generic when index < generic.TypeArgumentList.Arguments.Count => generic.TypeArgumentList.Arguments[index],
            QualifiedNameSyntax qualified => TypeArgument(qualified.Right, index),
            AliasQualifiedNameSyntax alias => TypeArgument(alias.Name, index),
            _ => null,
        };

    /// <summary>
    /// Finds whether a parameter's declared type uses the type parameter, so its argument inferred the type.
    /// </summary>
    private static bool Contains(ITypeSymbol type, ITypeParameterSymbol parameter)
        => type switch
        {
            ITypeParameterSymbol candidate => SymbolEqualityComparer.Default.Equals(candidate, parameter),
            IArrayTypeSymbol array => Contains(array.ElementType, parameter),
            INamedTypeSymbol named => named.TypeArguments.Any(argument => Contains(argument, parameter)),
            _ => false,
        };

    /// <summary>
    /// Finds the <c>typeof</c> operands passed directly or as elements of a parameter collection.
    /// </summary>
    private static IEnumerable<ITypeOfOperation> TypeOperands(IOperation value)
        => value switch
        {
            ITypeOfOperation operand => [operand],
            IConversionOperation conversion => TypeOperands(conversion.Operand),
            IArrayCreationOperation { Initializer: { } initializer } => initializer.ElementValues.SelectMany(TypeOperands),
            ICollectionExpressionOperation collection => collection.Elements.SelectMany(TypeOperands),
            _ => [],
        };

    private static bool IsMarked(ISymbol symbol, INamedTypeSymbol attribute)
        => symbol.GetAttributes().Any(data => SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute));

    /// <summary>
    /// Selects range bound rules for <c>PgRange&lt;T&gt;</c> and its static factory.
    /// </summary>
    private static bool IsRange(INamedTypeSymbol? type)
        => type is { Name: "PgRange", ContainingType: null, ContainingNamespace: { Name: "Ankus", ContainingNamespace.IsGlobalNamespace: true } };
}
