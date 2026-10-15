using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Reports independently correctable SQL declaration contracts at their authored syntax.
/// </summary>
internal static class FunctionDeclarationDiagnostics
{
    private const string HelpLink = "https://ankus.net/function-declarations/#declaration-diagnostics";

    private static readonly DiagnosticDescriptor s_executionOption = new("ANKUS045", "Invalid PostgreSQL execution policy",
        "'{0}' must be a defined {1} value; choose a named member of that enum",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_cost = new("ANKUS046", "Invalid PostgreSQL planner cost",
        "Cost must be positive, finite, and representable as PostgreSQL's real planner cost",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_internalResult = new("ANKUS047", "PostgreSQL internal result requires an input",
        "A PostgreSQL internal result requires an internal SQL input; add that input or choose a different result type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_polymorphicResult = new("ANKUS048", "PostgreSQL polymorphic result requires an input",
        "A polymorphic result requires a polymorphic SQL input to resolve its type; add that input or return a concrete SQL type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_nullInput = new("ANKUS049", "PostgreSQL NULL policy conflicts with parameter types",
        "CalledOnNull requires nullable declarations for every SQL parameter; accept NULL in those types or change NullInput",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_schema = new("ANKUS050", "Invalid PostgreSQL schema identifier",
        "A schema must be a non-null, nonempty identifier of at most 63 UTF-8 bytes with valid Unicode and no zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_rows = new("ANKUS051", "Invalid PostgreSQL row estimate",
        "Rows must be positive, finite, and representable as PostgreSQL's real row estimate",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_setMode = new("ANKUS052", "Invalid PostgreSQL set mode",
        "SetMode must be a defined PgSetMode value; choose Auto, ValuePerCall, or Materialize",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_scalarSetOption = new("ANKUS053", "Set option requires a PostgreSQL set result",
        "'{0}' requires an IEnumerable result; remove this option or declare a set-returning function",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_supportFunction = new("ANKUS054", "Invalid PostgreSQL support-function name",
        "SupportFunction must name one function, optionally prefixed by one schema",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_searchPath = new("ANKUS055", "Invalid PostgreSQL search-path entry",
        "Each SearchPath entry must be a nonempty schema identifier of at most 63 UTF-8 bytes with valid Unicode and no zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_injectedParameter = new("ANKUS056", "Injected parameter has no SQL metadata",
        "Injected parameter '{0}' has no SQL argument name or default; remove PgParameter from it",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_parameterOptions = new("ANKUS057", "Invalid PostgreSQL parameter options",
        "Ordinary parameter '{0}' accepts one PgParameter without Element or Variadic; declare variadic functions with params",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_parameterName = new("ANKUS058", "Invalid PostgreSQL parameter name",
        "SQL parameter '{0}' needs a nonempty identifier of at most 63 UTF-8 bytes with valid Unicode and no zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_duplicateParameterName = new("ANKUS059", "Duplicate PostgreSQL parameter name",
        "SQL argument name '{0}' is already used; assign a distinct PgParameter.Name",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_sqlDefault = new("ANKUS060", "Invalid PostgreSQL default expression",
        "A SQL default must be one complete expression: valid Unicode without zero characters, balanced parentheses, closed strings, quoted names, dollar quotes and block comments, and no semicolon outside them",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_optionalDefault = new("ANKUS061", "Managed default needs an explicit SQL expression",
        "The optional default for '{0}' needs an explicit PgParameter.Default SQL expression",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_missingDefault = new("ANKUS062", "PostgreSQL parameter requires a following default",
        "Parameter '{0}' follows a defaulted SQL argument; declare a SQL default for every following input parameter",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_reservedSchema = new("ANKUS063", "PostgreSQL schema prefix is reserved",
        "Schemas created by an extension cannot begin with pg_; choose another name or use Create = false for an existing schema",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for an undefined execution-policy enum value.
    /// </summary>
    internal static DiagnosticDescriptor ExecutionOption => s_executionOption;

    /// <summary>
    /// Gets the error for an unrepresentable planner cost.
    /// </summary>
    internal static DiagnosticDescriptor Cost => s_cost;

    /// <summary>
    /// Gets the error for an internal result without its required input.
    /// </summary>
    internal static DiagnosticDescriptor InternalResult => s_internalResult;

    /// <summary>
    /// Gets the error for an unresolved polymorphic result.
    /// </summary>
    internal static DiagnosticDescriptor PolymorphicResult => s_polymorphicResult;

    /// <summary>
    /// Gets the error for calling non-nullable parameters with SQL NULL.
    /// </summary>
    internal static DiagnosticDescriptor NullInput => s_nullInput;

    /// <summary>
    /// Gets the error for an invalid schema identifier.
    /// </summary>
    internal static DiagnosticDescriptor Schema => s_schema;

    /// <summary>
    /// Gets the error for an unrepresentable planner row estimate.
    /// </summary>
    internal static DiagnosticDescriptor Rows => s_rows;

    /// <summary>
    /// Gets the error for an undefined set execution mode.
    /// </summary>
    internal static DiagnosticDescriptor SetMode => s_setMode;

    /// <summary>
    /// Gets the error for applying a set-only option to a scalar result.
    /// </summary>
    internal static DiagnosticDescriptor ScalarSetOption => s_scalarSetOption;

    /// <summary>
    /// Gets the error for an invalid native support-function name.
    /// </summary>
    internal static DiagnosticDescriptor SupportFunction => s_supportFunction;

    /// <summary>
    /// Gets the error for an invalid search-path element.
    /// </summary>
    internal static DiagnosticDescriptor SearchPath => s_searchPath;

    /// <summary>
    /// Gets the error for SQL metadata on an injected managed context.
    /// </summary>
    internal static DiagnosticDescriptor InjectedParameter => s_injectedParameter;

    /// <summary>
    /// Gets the error for aggregate-only or duplicate parameter attributes.
    /// </summary>
    internal static DiagnosticDescriptor ParameterOptions => s_parameterOptions;

    /// <summary>
    /// Gets the error for an invalid SQL parameter identifier.
    /// </summary>
    internal static DiagnosticDescriptor ParameterName => s_parameterName;

    /// <summary>
    /// Gets the error for two parameters resolving to the same SQL name.
    /// </summary>
    internal static DiagnosticDescriptor DuplicateParameterName => s_duplicateParameterName;

    /// <summary>
    /// Gets the error for malformed explicit SQL default text.
    /// </summary>
    internal static DiagnosticDescriptor SqlDefault => s_sqlDefault;

    /// <summary>
    /// Gets the error for a managed default that has no exact SQL translation.
    /// </summary>
    internal static DiagnosticDescriptor OptionalDefault => s_optionalDefault;

    /// <summary>
    /// Gets the error for a required argument following a SQL default.
    /// </summary>
    internal static DiagnosticDescriptor MissingDefault => s_missingDefault;

    /// <summary>
    /// Gets the error for creating a schema in PostgreSQL's reserved namespace.
    /// </summary>
    internal static DiagnosticDescriptor ReservedSchema => s_reservedSchema;

    /// <summary>
    /// Locates the value assigned to an attribute property.
    /// </summary>
    /// <param name="attribute">The transient semantic attribute.</param>
    /// <param name="name">The named property.</param>
    /// <param name="cancellationToken">The current generation token.</param>
    /// <returns>The authored value location, or null when the option is implicit.</returns>
    internal static Location? Option(AttributeData? attribute, string name, CancellationToken cancellationToken)
        => OptionExpression(attribute, name, cancellationToken)?.GetLocation();

    /// <summary>
    /// Locates a constructor argument, including a named constructor argument.
    /// </summary>
    /// <param name="attribute">The transient semantic attribute.</param>
    /// <param name="cancellationToken">The current generation token.</param>
    /// <returns>The first constructor value location, or the attribute when no value is present.</returns>
    internal static Location? ConstructorArgument(AttributeData attribute, CancellationToken cancellationToken)
    {
        var syntax = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) as AttributeSyntax;
        return syntax?.ArgumentList?.Arguments.FirstOrDefault(static argument => argument.NameEquals is null)?.Expression.GetLocation()
            ?? syntax?.GetLocation();
    }

    /// <summary>
    /// Locates one invalid array value within an attribute option.
    /// </summary>
    /// <param name="attribute">The transient semantic attribute.</param>
    /// <param name="name">The array-valued option.</param>
    /// <param name="index">The invalid semantic element index.</param>
    /// <param name="cancellationToken">The current generation token.</param>
    /// <returns>The element location, falling back to the complete option value.</returns>
    internal static Location? OptionElement(AttributeData? attribute, string name, int index, CancellationToken cancellationToken)
    {
        ExpressionSyntax? expression = OptionExpression(attribute, name, cancellationToken);
        SyntaxNode? element = expression switch
        {
            ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions.ElementAtOrDefault(index),
            ImplicitArrayCreationExpressionSyntax array => array.Initializer.Expressions.ElementAtOrDefault(index),
            CollectionExpressionSyntax collection => collection.Elements.ElementAtOrDefault(index),
            _ => null,
        };
        return (element ?? expression)?.GetLocation();
    }

    /// <summary>
    /// Locates a method's declared result type without retaining its syntax.
    /// </summary>
    /// <param name="method">The transient authored method.</param>
    /// <param name="cancellationToken">The current generation token.</param>
    /// <returns>The result type location, or the method name when no source is available.</returns>
    internal static Location? Result(IMethodSymbol method, CancellationToken cancellationToken)
        => (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) switch
        {
            MethodDeclarationSyntax syntax => syntax.ReturnType,
            OperatorDeclarationSyntax syntax => syntax.ReturnType,
            ConversionOperatorDeclarationSyntax syntax => syntax.Type,
            _ => null,
        })?.GetLocation() ?? method.Locations.FirstOrDefault();

    /// <summary>
    /// Reads an attribute option's current syntax only while reporting a failure.
    /// </summary>
    private static ExpressionSyntax? OptionExpression(AttributeData? attribute, string name, CancellationToken cancellationToken)
        => (attribute?.ApplicationSyntaxReference?.GetSyntax(cancellationToken) as AttributeSyntax)?.ArgumentList?.Arguments
            .FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == name)?.Expression;
}
