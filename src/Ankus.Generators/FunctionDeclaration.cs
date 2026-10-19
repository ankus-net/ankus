using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Resolves and validates SQL declaration options without evaluating extension code.
/// </summary>
internal sealed record FunctionDeclaration
{
    /// <summary>
    /// Gets the fixed schema, or null to use the extension's installation schema.
    /// </summary>
    internal string? Schema
    {
        get;
        private init;
    }

    /// <summary>
    /// Gets the validated unquoted SQL function name for item selection.
    /// </summary>
    internal string Name
    {
        get;
        private init;
    } = string.Empty;

    /// <summary>
    /// Gets the quoted function name, qualified when a fixed schema is declared.
    /// </summary>
    internal string QualifiedName
    {
        get;
        private init;
    } = string.Empty;

    /// <summary>
    /// Gets the function name with a compiler marker for an extension-controlled default schema.
    /// </summary>
    internal string TemplateName => (Schema is null ? "\0" : string.Empty) + QualifiedName;

    /// <summary>
    /// Gets the validated SQL argument contracts before extension-provider qualification.
    /// </summary>
    internal EquatableArray<SqlFunctionParameter> Parameters
    {
        get;
        private init;
    } = new([]);

    /// <summary>
    /// Renders named SQL arguments with the current extension's catalog providers.
    /// </summary>
    /// <param name="providers">The graph's validated catalog identities, or null when no provider inventory is available.</param>
    /// <returns>The ordered argument declarations, including variadic and exact default clauses.</returns>
    internal string Arguments(SqlTypeProviders? providers = null)
        => string.Join(", ", Parameters.Select(parameter => parameter.Emit(providers)));

    /// <summary>
    /// Gets the validated SQL execution options.
    /// </summary>
    internal string Options
    {
        get;
        private init;
    } = string.Empty;

    /// <summary>
    /// Gets whether generated execution options require PostgreSQL's installation-schema substitution.
    /// </summary>
    internal bool UsesExtensionSchema
    {
        get;
        private init;
    }

    /// <summary>
    /// Gets whether the declaration replaces an existing compatible function.
    /// </summary>
    internal bool Replace
    {
        get;
        private init;
    }

    /// <summary>
    /// Gets whether PostgreSQL skips this function when any SQL input is null.
    /// </summary>
    internal bool Strict
    {
        get;
        private init;
    }

    /// <summary>
    /// Resolves schema inheritance, parameter contracts, and planner/execution options for one function.
    /// </summary>
    /// <param name="method">The attributed static method.</param>
    /// <param name="name">The validated SQL function name.</param>
    /// <param name="context">The generator context receiving declaration diagnostics.</param>
    /// <param name="set">The validated set return, or null for a scalar function.</param>
    /// <param name="contextParameter">
    /// Whether the managed parameter is a backend invocation context; otherwise it is a SQL argument.
    /// </param>
    /// <param name="sqlNullability">Explicit SQL parameter nullability for a specialized callback, excluding synthetic arguments that cannot be null.</param>
    /// <param name="schemaFallback">The specialized declaration's schema when the callback does not override it.</param>
    /// <param name="parameterModels">The ordered SQL and injected parameters, or null to resolve them from the method.</param>
    /// <returns>The declaration, or null after reporting an invalid contract.</returns>
    internal static FunctionDeclaration? Create(IMethodSymbol method, string name, GeneratorDiagnostics context, SetResult? set = null,
        bool contextParameter = false, IReadOnlyList<bool>? sqlNullability = null, string? schemaFallback = null, FunctionParameter[]? parameterModels = null)
    {
        AttributeData? attribute = method.GetAttributes().FirstOrDefault(static value => value.AttributeClass?.ToDisplayString() is
            "Ankus.PgFunctionAttribute" or "Ankus.PgTestAttribute");
        var declaration = new FunctionDeclaration();
        int volatility = Value(attribute, "Volatility", 0);
        int parallel = Value(attribute, "ParallelSafety", 0);
        int nullInput = Value(attribute, "NullInput", 0);
        double cost = Value(attribute, "Cost", 1d);
        if (volatility is < 0 or > 2)
        {
            return Invalid(FunctionDeclarationDiagnostics.ExecutionOption, Option("Volatility"), "Volatility", "PgVolatility");
        }

        if (parallel is < 0 or > 2)
        {
            return Invalid(FunctionDeclarationDiagnostics.ExecutionOption, Option("ParallelSafety"), "ParallelSafety", "PgParallelSafety");
        }

        if (nullInput is < 0 or > 2)
        {
            return Invalid(FunctionDeclarationDiagnostics.ExecutionOption, Option("NullInput"), "NullInput", "PgNullInput");
        }

        if (!IsPositiveSingle(cost))
        {
            return Invalid(FunctionDeclarationDiagnostics.Cost, Option("Cost"));
        }

        FunctionParameter[] sqlParameters = contextParameter ? [] :
            [.. (parameterModels ?? FunctionParameter.Create(method)).Where(static parameter => !parameter.IsInjected)];
        if (!contextParameter && (set?.Columns.Any(static column => column.IsSqlInternal) ?? FunctionType.CreateResult(method)?.IsSqlInternal == true) &&
            !sqlParameters.Any(static parameter => parameter.Type?.IsSqlInternal == true))
        {
            return Invalid(FunctionDeclarationDiagnostics.InternalResult, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
        }

        if (!contextParameter && (set?.Columns.Any(static column => column.IsSqlPolymorphic) ?? FunctionType.CreateResult(method)?.IsSqlPolymorphic == true) &&
            !sqlParameters.Any(static parameter => parameter.Type?.IsSqlPolymorphic == true))
        {
            return Invalid(FunctionDeclarationDiagnostics.PolymorphicResult, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
        }

        bool allNullable = sqlNullability?.All(static nullable => nullable) ??
            (contextParameter || sqlParameters.All(static parameter => parameter.Type!.Nullable));
        bool allRequired = sqlNullability?.All(static nullable => !nullable) ??
            (!contextParameter && sqlParameters.All(static parameter => !parameter.Type!.Nullable));
        if (nullInput == 2 && !allNullable)
        {
            return Invalid(FunctionDeclarationDiagnostics.NullInput, Option("NullInput"));
        }

        string? schema = Value(attribute, "Schema", schemaFallback);
        AttributeData? inheritedSchema = null;
        for (INamedTypeSymbol? container = method.ContainingType; schema is null && container is not null; container = container.ContainingType)
        {
            AttributeData? schemaAttribute = container.GetAttributes().FirstOrDefault(static value =>
                value.AttributeClass?.ToDisplayString() == "Ankus.PgSchemaAttribute");
            if (schemaAttribute is not null)
            {
                inheritedSchema = schemaAttribute;
                schema = schemaAttribute.ConstructorArguments.FirstOrDefault().Value as string;
                if (schema is null)
                {
                    return Invalid(FunctionDeclarationDiagnostics.Schema,
                        FunctionDeclarationDiagnostics.ConstructorArgument(schemaAttribute, context.CancellationToken));
                }
            }
        }

        if (schema is not null && !SqlText.IsIdentifier(schema))
        {
            return Invalid(FunctionDeclarationDiagnostics.Schema, inheritedSchema is null ? Option("Schema") :
                FunctionDeclarationDiagnostics.ConstructorArgument(inheritedSchema, context.CancellationToken));
        }

        declaration = declaration with
        {
            Schema = schema,
            Name = name,
            QualifiedName = (schema is null ? string.Empty : SqlText.Identifier(schema) + ".") + SqlText.Identifier(name),
            Replace = Value(attribute, "CreateOrReplace", false),
            Strict = nullInput == 1 || (nullInput == 0 && allRequired),
        };

        var options = new List<string>
        {
            volatility switch { 1 => "STABLE", 2 => "IMMUTABLE", _ => "VOLATILE" },
            "PARALLEL " + (parallel switch { 1 => "RESTRICTED", 2 => "SAFE", _ => "UNSAFE" }),
            declaration.Strict ? "STRICT" : "CALLED ON NULL INPUT",
            Value(attribute, "SecurityDefiner", false) ? "SECURITY DEFINER" : "SECURITY INVOKER",
            Value(attribute, "Leakproof", false) ? "LEAKPROOF" : "NOT LEAKPROOF",
            "COST " + cost.ToString("R", CultureInfo.InvariantCulture),
        };

        if (set is not null)
        {
            double rows = Value(attribute, "Rows", 1000d);
            int mode = Value(attribute, "SetMode", 0);
            if (!IsPositiveSingle(rows))
            {
                return Invalid(FunctionDeclarationDiagnostics.Rows, Option("Rows"));
            }

            if (mode is < 0 or > 2)
            {
                return Invalid(FunctionDeclarationDiagnostics.SetMode, Option("SetMode"));
            }

            options.Add("ROWS " + rows.ToString("R", CultureInfo.InvariantCulture));
        }
        else if (attribute?.NamedArguments.FirstOrDefault(static argument => argument.Key is "Rows" or "SetMode").Key is string option)
        {
            return Invalid(FunctionDeclarationDiagnostics.ScalarSetOption, Option(option), option);
        }

        string? support = Value<string?>(attribute, "SupportFunction", null);
        if (support is not null)
        {
            string[] parts = support.Split('.');
            if (parts.Length is < 1 or > 2 || parts.Any(static part => !SqlText.IsIdentifier(part)))
            {
                return Invalid(FunctionDeclarationDiagnostics.SupportFunction, Option("SupportFunction"));
            }

            options.Add("SUPPORT " + string.Join(".", parts.Select(SqlText.Identifier)));
        }

        foreach (KeyValuePair<string, TypedConstant> argument in attribute?.NamedArguments ?? [])
        {
            if (argument.Key != "SearchPath" || argument.Value.IsNull)
            {
                continue;
            }

            string?[] path = [.. argument.Value.Values.Select(static value => value.Value as string)];
            int invalidEntry = Array.FindIndex(path, static entry => !SqlText.IsIdentifier(entry));
            if (invalidEntry >= 0)
            {
                return Invalid(FunctionDeclarationDiagnostics.SearchPath,
                    FunctionDeclarationDiagnostics.OptionElement(attribute, "SearchPath", invalidEntry, context.CancellationToken));
            }

            declaration = declaration with { UsesExtensionSchema = path.Contains("@extschema@", StringComparer.Ordinal) };
            options.Add("SET search_path TO " + (path.Length == 0 ? "''" : string.Join(", ", path.Select(static entry =>
                entry == "@extschema@" ? entry : SqlText.Identifier(entry!)))));
        }

        declaration = declaration with { Options = string.Join(" ", options) };
        if (contextParameter)
        {
            return declaration;
        }

        var parameters = new List<SqlFunctionParameter>();
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        bool defaultSeen = false;
        FunctionParameter[] managedParameters = parameterModels ?? FunctionParameter.Create(method);
        for (int index = 0; index < managedParameters.Length; index++)
        {
            FunctionParameter model = managedParameters[index];
            IParameterSymbol parameter = method.Parameters[index];
            AttributeData[] parameterAttributes = [.. parameter.GetAttributes().Where(static candidate =>
                candidate.AttributeClass?.ToDisplayString() == "Ankus.PgParameterAttribute")];
            AttributeData? parameterAttribute = parameterAttributes.FirstOrDefault();
            if (model.IsInjected)
            {
                if (!model.SqlOptions.IsEmpty)
                {
                    return Invalid(FunctionDeclarationDiagnostics.InjectedParameter,
                        parameterAttribute?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation(), model.Name);
                }

                continue;
            }

            FunctionType type = model.Type!;
            if (model.SqlOptions.Count > 1 || model.SqlOptions.Any(static value => value.Element is not null || value.Variadic))
            {
                Location? location = parameterAttributes.Length > 1
                    ? parameterAttributes[1].ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
                    : FunctionDeclarationDiagnostics.Option(parameterAttribute, model.SqlOptions[0].Element is not null ? "Element" : "Variadic",
                        context.CancellationToken);
                return Invalid(FunctionDeclarationDiagnostics.ParameterOptions, location, model.Name);
            }

            SqlParameterOptions? parameterOptions = model.SqlOptions.IsEmpty ? null : model.SqlOptions[0];
            string parameterName = parameterOptions?.Name ?? SqlText.SnakeCase(model.Name);
            if (!SqlText.IsIdentifier(parameterName))
            {
                return Invalid(FunctionDeclarationDiagnostics.ParameterName, ParameterOption("Name"), model.Name);
            }

            if (!parameterNames.Add(parameterName))
            {
                return Invalid(FunctionDeclarationDiagnostics.DuplicateParameterName, ParameterOption("Name"), parameterName);
            }

            string? expression = parameterOptions?.Default;
            if (expression is not null && (string.IsNullOrWhiteSpace(expression) || !SqlText.IsText(expression) ||
                !SqlText.IsCompleteExpression(expression)))
            {
                return Invalid(FunctionDeclarationDiagnostics.SqlDefault, ParameterOption("Default"));
            }

            if (expression is null && model.HasExplicitDefaultValue)
            {
                expression = model.OptionalDefaultSql;
                if (expression is null)
                {
                    Location? location = (parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken)
                        as ParameterSyntax)?.Default?.Value.GetLocation() ?? parameter.Locations.FirstOrDefault();
                    return Invalid(FunctionDeclarationDiagnostics.OptionalDefault, location, model.Name);
                }
            }

            if (defaultSeen && expression is null)
            {
                return Invalid(FunctionDeclarationDiagnostics.MissingDefault, parameter.Locations.FirstOrDefault(), model.Name);
            }

            defaultSeen |= expression is not null;
            parameters.Add(new(parameterName, type, model.IsParams, expression));

            Location? ParameterOption(string option) => FunctionDeclarationDiagnostics.Option(parameterAttribute, option, context.CancellationToken)
                ?? parameter.Locations.FirstOrDefault();
        }

        declaration = declaration with { Parameters = new(parameters) };
        return declaration;

        Location? Option(string option) => FunctionDeclarationDiagnostics.Option(attribute, option, context.CancellationToken);

        FunctionDeclaration? Invalid(DiagnosticDescriptor descriptor, Location? location, params string[] arguments)
        {
            context.Report(descriptor, location ?? method.Locations.FirstOrDefault(), arguments);
            return null;
        }
    }

    /// <summary>
    /// Checks that an estimate is positive and stays positive as the float4 PostgreSQL stores it in.
    /// </summary>
    /// <remarks>NaN fails every comparison, so it is rejected too.</remarks>
    private static bool IsPositiveSingle(double value) => value > 0 && value <= float.MaxValue && (float)value > 0;

    private static T Value<T>(AttributeData? attribute, string name, T fallback)
    {
        if (attribute is not null)
        {
            foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
            {
                if (argument.Key == name && argument.Value.Value is T value)
                {
                    return value;
                }
            }
        }

        return fallback;
    }
}
