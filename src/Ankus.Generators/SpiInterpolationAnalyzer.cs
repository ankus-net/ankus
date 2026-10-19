using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Ankus.Generators;

/// <summary>
/// Rejects interpolation that sends formatted values directly into raw PostgreSQL command text.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class SpiInterpolationAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_interpolatedSql = new(
        "ANKUS044", "SQL interpolation must bind parameters",
        "Bind values with Spi.Sql or positional SQL parameters instead of formatting them into command text",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/spi/#parameterized-interpolation");

    /// <summary>
    /// Gets the compiler error for an interpolated raw SQL command.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [s_interpolatedSql];

    /// <summary>
    /// Registers semantic checks against the actual PostgreSQL SPI entry points.
    /// </summary>
    /// <param name="context">The analyzer registration context.</param>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            INamedTypeSymbol? spi = start.Compilation.GetTypeByMetadataName("Ankus.Spi");
            INamedTypeSymbol? session = start.Compilation.GetTypeByMetadataName("Ankus.SpiSession");
            INamedTypeSymbol? enumerable = start.Compilation.GetTypeByMetadataName("System.Linq.Enumerable");
            if (spi?.ContainingAssembly.Name != "Ankus.Runtime" || session?.ContainingAssembly.Name != "Ankus.Runtime")
            {
                return;
            }

            start.RegisterOperationBlockStartAction(block =>
            {
                System.Collections.Concurrent.ConcurrentBag<IArgumentOperation> commands = new();
                block.RegisterOperationAction(operation =>
                {
                    var invocation = (IInvocationOperation)operation.Operation;
                    if (!SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, spi) &&
                        !SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, session))
                    {
                        return;
                    }

                    foreach (IArgumentOperation argument in invocation.Arguments.Where(static argument => argument.Parameter is { Name: "commandText", Type.SpecialType: SpecialType.System_String }))
                    {
                        commands.Add(argument);
                    }
                }, OperationKind.Invocation);
                block.RegisterOperationBlockEndAction(end =>
                {
                    if (commands.IsEmpty)
                    {
                        return;
                    }

                    Dictionary<(SyntaxTree Tree, Microsoft.CodeAnalysis.Text.TextSpan Span), bool> results = [];
                    foreach (IOperation root in end.OperationBlocks)
                    {
                        var analysis = new SqlFlowAnalysis(spi, session, enumerable,
                            start.Compilation.GetSpecialType(SpecialType.System_String).ContainingAssembly, end.CancellationToken);
                        foreach (KeyValuePair<(SyntaxTree Tree, Microsoft.CodeAnalysis.Text.TextSpan Span), bool> result in
                            analysis.Analyze(end.GetControlFlowGraph(root), root, end.OwningSymbol))
                        {
                            results[result.Key] = result.Value || results.TryGetValue(result.Key, out bool previous) && previous;
                        }
                    }

                    HashSet<(SyntaxTree Tree, Microsoft.CodeAnalysis.Text.TextSpan Span)> reported = [];
                    foreach (IArgumentOperation argument in commands)
                    {
                        (SyntaxTree Tree, Microsoft.CodeAnalysis.Text.TextSpan Span) key = (argument.Value.Syntax.SyntaxTree, argument.Value.Syntax.Span);
                        bool error = results.TryGetValue(key, out bool found)
                            ? found : ContainsInterpolation(argument.Value, spi);
                        if (error && reported.Add(key))
                        {
                            end.ReportDiagnostic(Diagnostic.Create(s_interpolatedSql, argument.Value.Syntax.GetLocation()));
                        }
                    }
                });
            });
        });
    }

    /// <summary>
    /// Finds direct interpolation through string conversions and concatenation without treating escaped helper results as raw interpolation.
    /// </summary>
    /// <param name="operation">The raw command expression.</param>
    /// <param name="spi">The actual runtime SPI type whose quoting results are SQL fragments.</param>
    /// <returns>Whether the expression directly formats values into command text.</returns>
    private static bool ContainsInterpolation(IOperation operation, INamedTypeSymbol spi)
        => operation switch
        {
            _ when operation.ConstantValue.HasValue => false,
            IInterpolatedStringOperation interpolation => interpolation.Parts.OfType<IInterpolationOperation>()
                .Any(hole => hole.Alignment is not null || hole.FormatString is not null ||
                    !hole.Expression.ConstantValue.HasValue && !IsQuoted(hole.Expression, spi)),
            IConversionOperation conversion => ContainsInterpolation(conversion.Operand, spi),
            IParenthesizedOperation parentheses => ContainsInterpolation(parentheses.Operand, spi),
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } binary
                => ConcatenatesRawText(binary.LeftOperand, spi) || ConcatenatesRawText(binary.RightOperand, spi),
            IConditionalOperation conditional => ContainsInterpolation(conditional.WhenTrue, spi) ||
                conditional.WhenFalse is { } otherwise && ContainsInterpolation(otherwise, spi),
            ICoalesceOperation coalesce => ContainsInterpolation(coalesce.Value, spi) || ContainsInterpolation(coalesce.WhenNull, spi),
            ISwitchExpressionOperation expression => expression.Arms.Any(arm => ContainsInterpolation(arm.Value, spi)),
            IInvocationOperation { TargetMethod: { Name: "Format", ContainingType.SpecialType: SpecialType.System_String } } format
                => format.Arguments.Any(argument => argument.Parameter?.Name == "provider"
                    ? !IsInvariantProvider(argument.Value, format.TargetMethod.ContainingAssembly)
                    : HasUnquotedFormatValue(argument.Value, spi)),
            _ => false,
        };

    /// <summary>
    /// Treats every concatenated operand as raw text unless it is constant, quoted or itself free of raw formatting.
    /// </summary>
    /// <param name="operand">One operand of a string concatenation.</param>
    /// <param name="spi">The actual runtime SPI type whose quoting results are SQL fragments.</param>
    /// <returns>Whether concatenation can place an unquoted runtime value into command text.</returns>
    private static bool ConcatenatesRawText(IOperation operand, INamedTypeSymbol spi)
        => operand switch
        {
            _ when operand.ConstantValue.HasValue => false,
            IConversionOperation { OperatorMethod: null } conversion => ConcatenatesRawText(conversion.Operand, spi),
            IParenthesizedOperation parentheses => ConcatenatesRawText(parentheses.Operand, spi),
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } binary
                => ConcatenatesRawText(binary.LeftOperand, spi) || ConcatenatesRawText(binary.RightOperand, spi),
            IConditionalOperation conditional => ConcatenatesRawText(conditional.WhenTrue, spi) ||
                conditional.WhenFalse is { } otherwise && ConcatenatesRawText(otherwise, spi),
            ICoalesceOperation coalesce => ConcatenatesRawText(coalesce.Value, spi) || ConcatenatesRawText(coalesce.WhenNull, spi),
            ISwitchExpressionOperation expression => expression.Arms.Any(arm => ConcatenatesRawText(arm.Value, spi)),
            IInterpolatedStringOperation or IInvocationOperation { TargetMethod: { Name: "Format", ContainingType.SpecialType: SpecialType.System_String } }
                => ContainsInterpolation(operand, spi),
            IThrowOperation => false,
            _ => !IsQuoted(operand, spi),
        };

    /// <summary>
    /// Checks expanded format arguments without trusting an array whose contents are supplied at runtime.
    /// </summary>
    /// <param name="operation">The format template, argument or compiler-created parameter array.</param>
    /// <param name="spi">The actual runtime SPI type.</param>
    /// <returns>Whether runtime formatting can supply an unquoted SQL value.</returns>
    private static bool HasUnquotedFormatValue(IOperation operation, INamedTypeSymbol spi)
        => operation switch
        {
            _ when operation.ConstantValue.HasValue => false,
            IConversionOperation { OperatorMethod: null } conversion => HasUnquotedFormatValue(conversion.Operand, spi),
            IParenthesizedOperation parentheses => HasUnquotedFormatValue(parentheses.Operand, spi),
            IArrayCreationOperation { Initializer: { } initializer } => initializer.ElementValues.Any(value => HasUnquotedFormatValue(value, spi)),
            _ => !IsQuoted(operation, spi),
        };

    /// <summary>
    /// Allows ordinary invariant formatting while rejecting arbitrary custom formatters that can replace a quoted fragment.
    /// </summary>
    /// <param name="operation">The optional format provider.</param>
    /// <param name="core">The assembly containing the actual System.String.Format method.</param>
    /// <returns>Whether the provider cannot install an arbitrary custom formatter.</returns>
    private static bool IsInvariantProvider(IOperation operation, IAssemblySymbol core)
        => operation switch
        {
            _ when operation.ConstantValue is { HasValue: true, Value: null } => true,
            IConversionOperation { OperatorMethod: null } conversion => IsInvariantProvider(conversion.Operand, core),
            IParenthesizedOperation parentheses => IsInvariantProvider(parentheses.Operand, core),
            IPropertyReferenceOperation { Property: { IsStatic: true, Name: "InvariantCulture", ContainingType: { } owner } }
                => owner.ToDisplayString() == "System.Globalization.CultureInfo" && SymbolEqualityComparer.Default.Equals(owner.ContainingAssembly, core),
            _ => false,
        };

    /// <summary>
    /// Recognizes only semantic calls to PostgreSQL's quoting APIs, preserving the requirement for mixed raw values.
    /// </summary>
    /// <param name="operation">The formatted expression.</param>
    /// <param name="spi">The runtime-owned SPI type.</param>
    /// <returns>Whether the expression is a direct, unchanged PostgreSQL quoting result.</returns>
    private static bool IsQuoted(IOperation operation, INamedTypeSymbol spi)
        => operation switch
        {
            IConversionOperation { OperatorMethod: null } conversion => IsQuoted(conversion.Operand, spi),
            IParenthesizedOperation parentheses => IsQuoted(parentheses.Operand, spi),
            IConditionalOperation conditional => IsQuoted(conditional.WhenTrue, spi) &&
                conditional.WhenFalse is { } otherwise && IsQuoted(otherwise, spi),
            ICoalesceOperation coalesce => IsQuoted(coalesce.Value, spi) && IsQuoted(coalesce.WhenNull, spi),
            ISwitchExpressionOperation expression => expression.Arms.All(arm => IsQuoted(arm.Value, spi)),
            ILocalReferenceOperation reference => IsQuotedLocal(reference, spi),
            _ => IsQuotedCall(operation, spi),
        };

    /// <summary>
    /// Recognizes quoted branches without following local initializers or allowing user-defined conversions.
    /// </summary>
    /// <param name="operation">The unchanged quoting result.</param>
    /// <param name="spi">The actual runtime SPI type.</param>
    /// <returns>Whether no user conversion or formatting changes the quoted fragment.</returns>
    private static bool IsQuotedCall(IOperation operation, INamedTypeSymbol spi)
        => operation switch
        {
            _ when operation.ConstantValue.HasValue => true,
            IConversionOperation { OperatorMethod: null } conversion => IsQuotedCall(conversion.Operand, spi),
            IParenthesizedOperation parentheses => IsQuotedCall(parentheses.Operand, spi),
            IConditionalOperation conditional => IsQuotedCall(conditional.WhenTrue, spi) &&
                conditional.WhenFalse is { } otherwise && IsQuotedCall(otherwise, spi),
            ICoalesceOperation coalesce => IsQuotedCall(coalesce.Value, spi) && IsQuotedCall(coalesce.WhenNull, spi),
            ISwitchExpressionOperation expression => expression.Arms.All(arm => IsQuotedCall(arm.Value, spi)),
            IThrowOperation => true,
            IInvocationOperation { TargetMethod: { IsStatic: true, ReturnType.SpecialType: SpecialType.System_String } method }
                => SymbolEqualityComparer.Default.Equals(method.ContainingType, spi) &&
                    method.Name is "QuoteIdentifier" or "QuoteQualifiedIdentifier" or "QuoteLiteral",
            _ => false,
        };

    /// <summary>
    /// Accepts an unchanged quoted local without trusting locals that can be reassigned or aliased by reference.
    /// </summary>
    /// <param name="reference">The quoted local's use in the formatted SQL.</param>
    /// <param name="spi">The runtime-owned SPI type.</param>
    /// <returns>Whether a direct quoting initializer supplies every use of this local.</returns>
    private static bool IsQuotedLocal(ILocalReferenceOperation reference, INamedTypeSymbol spi)
    {
        IOperation root = reference;
        while (root.Parent is { } parent)
        {
            root = parent;
        }

        IOperation[] operations = [.. Descendants(root)];
        IVariableDeclaratorOperation? declaration = operations.OfType<IVariableDeclaratorOperation>()
            .FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.Symbol, reference.Local));
        if (declaration?.Initializer?.Value is not { } value || reference.Local.RefKind != RefKind.None ||
            !IsQuotedCall(value, spi))
        {
            return false;
        }

        return !operations.OfType<ILocalReferenceOperation>().Any(candidate =>
            SymbolEqualityComparer.Default.Equals(candidate.Local, reference.Local) && IsWritten(candidate));
    }

    /// <summary>
    /// Visits operation children, including captures in nested functions, without resolving another semantic model.
    /// </summary>
    /// <param name="operation">The bound operation tree.</param>
    /// <returns>Its operations in source order.</returns>
    private static IEnumerable<IOperation> Descendants(IOperation operation)
    {
        yield return operation;
        foreach (IOperation child in operation.ChildOperations)
        {
            foreach (IOperation descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Detects writes and managed reference aliases to the local itself. Reads used inside a write target do not count.
    /// </summary>
    /// <param name="reference">One bound use of the quoted local.</param>
    /// <returns>Whether this use can replace its quoted value.</returns>
    private static bool IsWritten(ILocalReferenceOperation reference)
    {
        IOperation current = reference;
        while (current.Parent is { } parent)
        {
            if (parent is IAssignmentOperation assignment && ReferenceEquals(assignment.Target, current) ||
                parent is IIncrementOrDecrementOperation increment && ReferenceEquals(increment.Target, current) ||
                parent is IArgumentOperation { Parameter.RefKind: not RefKind.None } ||
                parent is IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } } ||
                parent is IAddressOfOperation)
            {
                return true;
            }

            if (parent is not (IConversionOperation or IParenthesizedOperation or ITupleOperation))
            {
                break;
            }

            current = parent;
        }

        return false;
    }
}
