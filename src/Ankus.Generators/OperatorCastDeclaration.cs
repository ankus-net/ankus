using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Validates operator and cast contracts and adds their backing-function dependencies to installation SQL.
/// </summary>
internal static class OperatorCastDeclaration
{
    private const string HelpLink = "https://ankus.net/operators-and-casts/#declaration-diagnostics";

    private static readonly DiagnosticDescriptor s_setResult = new("ANKUS064", "PostgreSQL operator or cast returns a set",
        "Operators and casts cannot return sets; return one value per call",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_operatorName = new("ANKUS065", "Invalid PostgreSQL operator token",
        "The operator name must contain 1-63 valid PostgreSQL operator characters, without comment starts or ambiguous trailing + or -",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_operatorArity = new("ANKUS066", "Invalid PostgreSQL operator operand count",
        "An operator requires one prefix operand or two binary operands; injected contexts are not SQL operands",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_variadic = new("ANKUS067", "PostgreSQL operator or cast has a variadic argument",
        "PostgreSQL {0} declarations cannot use params; declare fixed SQL arguments",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_voidResult = new("ANKUS068", "PostgreSQL operator or cast has no result",
        "A PostgreSQL {0} requires a non-void result; return a supported SQL value",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_selfNegator = new("ANKUS069", "PostgreSQL operator is its own negator",
        "An operator cannot be its own negator; name a distinct operator with the complementary result",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_binaryOption = new("ANKUS070", "PostgreSQL operator option requires two operands",
        "Only binary operators can declare {0}; remove the option or declare two SQL operands",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_booleanOption = new("ANKUS071", "PostgreSQL operator option requires a boolean result",
        "Only boolean operators can declare {0}; remove the option or return a SQL boolean",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_operatorReference = new("ANKUS072", "Invalid PostgreSQL operator reference",
        "{0} must name one operator, optionally prefixed by one schema",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_estimatorReference = new("ANKUS073", "Invalid PostgreSQL estimator reference",
        "{0} must name one estimator function, optionally prefixed by one schema",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_castContext = new("ANKUS074", "Invalid PostgreSQL cast context",
        "The cast context must be Explicit, Assignment, or Implicit",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_castArity = new("ANKUS075", "Invalid PostgreSQL cast argument count",
        "A cast requires one to three SQL arguments; injected contexts are not SQL arguments",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_castModifier = new("ANKUS076", "Invalid PostgreSQL cast type modifier",
        "A cast's optional second parameter must be non-nullable int to receive the target type modifier",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_castExplicit = new("ANKUS077", "Invalid PostgreSQL cast conversion flag",
        "A cast's optional third parameter must be non-nullable bool to receive the explicit-conversion flag",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_recordCast = new("ANKUS078", "PostgreSQL cast endpoint has no composite identity",
        "PostgreSQL casts cannot use the record pseudo-type; bind composite source and result values with PgCompositeType",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_identityCast = new("ANKUS079", "PostgreSQL cast has identical endpoint types",
        "A one-parameter cast must convert between distinct PostgreSQL types; CLR aliases and nullability do not create distinct SQL types",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Adds cached operator and cast declarations with current source attribution, providers and graph policy.
    /// </summary>
    /// <param name="outputs">The independently analyzed and rendered attached declarations.</param>
    /// <param name="dependency">The current backing-function graph node.</param>
    /// <param name="graph">The current installation graph.</param>
    /// <param name="names">The current operator/cast signature inventory.</param>
    /// <param name="context">The diagnostic destination.</param>
    /// <param name="operators">The boolean-operator inventory for related declarations.</param>
    /// <param name="providers">The current catalog provider inventory.</param>
    /// <param name="compilation">The current compilation used to reattach diagnostic coordinates.</param>
    /// <returns>The current attached nodes and their prerequisites.</returns>
    internal static List<SqlEntity> Add(IEnumerable<OperatorCastPipeline.Output> outputs, SqlEntity dependency,
        SqlGraph graph, HashSet<string> names, GeneratorDiagnostics context, Dictionary<string, SqlEntity> operators,
        SqlTypeProviders providers, GeneratorSourceResolver compilation)
    {
        var result = new List<SqlEntity>();
        foreach (OperatorCastPipeline.Output output in outputs)
        {
            OperatorCastPipeline.Analysis analysis = output.Analysis;
            foreach (GeneratorProblem problem in analysis.Problems)
            {
                problem.Report(compilation, context);
            }

            if (output.Emission is null)
            {
                continue;
            }

            Location? location = analysis.Location?.Resolve(compilation);
            if (!names.Add(analysis.Kind + ":" + analysis.Signature))
            {
                graph.Error(graph.Resolve(analysis.Options.Locations.Attribute) ?? location,
                    analysis.Kind == "operator" ? SqlGraphDiagnostics.s_duplicateOperator : SqlGraphDiagnostics.s_duplicateCast, analysis.Signature);
            }

            (string sql, string attachment) = output.Emission.Compose(providers);
            var entity = new SqlEntity("2:" + analysis.Kind + ":" + analysis.Display, sql, location) { Kind = analysis.Kind };
            entity.SelectionNames.UnionWith(analysis.Names);
            entity.Attachments.Add(attachment);
            entity.Dependencies.Add(dependency);
            graph.ConfigureOptions(entity, analysis.Options);
            graph.Add(entity);
            graph.Register(analysis.Identity, analysis.Display, entity);
            result.Add(entity);
            if (analysis.BooleanOperator)
            {
                operators[analysis.Signature!] = entity;
            }
        }

        return result;
    }

    /// <summary>
    /// Validates one attached declaration using the common backing-function contracts and detached diagnostics.
    /// </summary>
    /// <param name="method">The semantically analyzed backing method.</param>
    /// <param name="function">The validated backing declaration and conversion contracts.</param>
    /// <param name="attribute">The authored operator or cast attribute.</param>
    /// <param name="compilation">The analyzed compilation owning source coordinates.</param>
    /// <param name="cancellationToken">The current semantic-analysis cancellation token.</param>
    /// <returns>The immutable catalog declaration, policy and diagnostics.</returns>
    internal static OperatorCastPipeline.Analysis Analyze(IMethodSymbol method, FunctionPipeline.FunctionAnalysis function,
        AttributeData attribute, Compilation compilation, CancellationToken cancellationToken)
    {
        string kind = attribute.AttributeClass?.Name == "PgOperatorAttribute" ? "operator" : "cast";
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            problems.Add(new(descriptor, GeneratorLocation.Create(location, compilation), new(arguments))), cancellationToken);
        FunctionPipeline.FunctionModel conversion = function.Model!;
        FunctionParameter[] parameters = [.. conversion.Parameters.Where(static parameter => !parameter.IsInjected)];
        OperatorCastModel? model = null;
        if (conversion.Set is not null)
        {
            diagnostics.Report(s_setResult, FunctionDeclarationDiagnostics.Result(method, cancellationToken));
        }
        else
        {
            model = kind == "operator" ? CreateOperator(method, parameters, function.Declaration!, attribute, diagnostics)
                : CreateCast(method, parameters, function.Declaration!, attribute, diagnostics);
        }

        string signature = model?.Operator is { } operation
            ? operation.Name + "(" + (operation.Left?.Sql ?? "NONE") + "," + operation.Right.Sql + ")"
            : model?.Cast is { } cast ? cast.Arguments[0].Sql + " AS " + cast.Result.Sql : string.Empty;
        var names = new List<string>();
        if (model is not null)
        {
            names.Add(signature);
            if (model.Operator is not null)
            {
                string name = (string)attribute.ConstructorArguments[0].Value!;
                names.Add(name);
                if (function.Declaration!.Schema is { } schema)
                {
                    names.Add(schema + "." + name);
                }
            }
        }

        return new(function.Identity, method.ToDisplayString(), kind, signature, new(names),
            kind == "operator" && conversion.Result?.Sql == "boolean", model, SqlDeclarationOptions.Read(attribute, compilation, cancellationToken)!,
            new(problems), GeneratorLocation.Create(method.Locations.FirstOrDefault(), compilation));
    }

    private static OperatorCastModel? CreateOperator(IMethodSymbol method, FunctionParameter[] parameters, FunctionDeclaration function,
        AttributeData attribute, GeneratorDiagnostics context)
    {
        string? name = attribute.ConstructorArguments.FirstOrDefault().Value as string;
        string? qualified = OperatorReference(name, function.Schema);
        if (name is null || name.Contains('.') || qualified is null)
        {
            return Invalid(s_operatorName, FunctionDeclarationDiagnostics.ConstructorArgument(attribute, context.CancellationToken));
        }

        if (parameters.Length is < 1 or > 2)
        {
            return Invalid(s_operatorArity, ParameterList(method, context.CancellationToken));
        }

        if (method.ReturnsVoid)
        {
            return Invalid(s_voidResult, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken), "operator");
        }

        if (parameters.FirstOrDefault(static parameter => parameter.IsParams) is { } variadic)
        {
            return Invalid(s_variadic, ParameterLocation(method, variadic, context.CancellationToken, variadic: true), "operator");
        }

        string? commutator = AttributeValues.Get<string?>(attribute, "Commutator", null);
        string? negator = AttributeValues.Get<string?>(attribute, "Negator", null);
        string? restrict = AttributeValues.Get<string?>(attribute, "RestrictionEstimator", null);
        string? join = AttributeValues.Get<string?>(attribute, "JoinEstimator", null);
        bool hashes = AttributeValues.Get(attribute, "Hashes", false);
        bool merges = AttributeValues.Get(attribute, "Merges", false);
        if (negator is not null && OperatorReference(negator, function.Schema) == qualified)
        {
            return Invalid(s_selfNegator, Option("Negator"));
        }

        if (parameters.Length == 1 && (commutator is not null || join is not null || hashes || merges))
        {
            string option = commutator is not null ? "Commutator" : join is not null ? "JoinEstimator" : hashes ? "Hashes" : "Merges";
            return Invalid(s_binaryOption, Option(option), option);
        }

        if (FunctionType.CreateResult(method)!.Sql != "boolean" && (negator is not null || restrict is not null || join is not null || hashes || merges))
        {
            string option = negator is not null ? "Negator" : restrict is not null ? "RestrictionEstimator" :
                join is not null ? "JoinEstimator" : hashes ? "Hashes" : "Merges";
            return Invalid(s_booleanOption, Option(option), option);
        }

        string? left = parameters.Length == 2 ? parameters[0].Type!.Sql : null;
        var options = new List<string>();
        if (!AddReference("COMMUTATOR", "Commutator", commutator, true) || !AddReference("NEGATOR", "Negator", negator, true) ||
            !AddReference("RESTRICT", "RestrictionEstimator", restrict, false) || !AddReference("JOIN", "JoinEstimator", join, false))
        {
            return null;
        }

        if (hashes)
        {
            options.Add("HASHES");
        }

        if (merges)
        {
            options.Add("MERGES");
        }

        string templateName = (function.Schema is null ? "\0" : string.Empty) + qualified;
        return new(new(qualified, templateName, function.TemplateName,
            left is null ? null : SqlTypeTemplate.Create(parameters[0].Type!),
            SqlTypeTemplate.Create(parameters[parameters.Length - 1].Type!), new(options)), null);

        bool AddReference(string option, string property, string? reference, bool isOperator)
        {
            if (reference is null)
            {
                return true;
            }

            string? sql = isOperator ? OperatorReference(reference, function.Schema) : FunctionReference(reference);
            if (sql is null)
            {
                context.Report(isOperator ? s_operatorReference : s_estimatorReference, Option(property), property);
                return false;
            }

            options.Add(option + " = " + (isOperator ? "OPERATOR(" +
                (function.Schema is null && !reference.Contains('.') ? "\0" : string.Empty) + sql + ")" : sql));
            return true;
        }

        Location? Option(string name) => FunctionDeclarationDiagnostics.Option(attribute, name, context.CancellationToken);

        OperatorCastModel? Invalid(DiagnosticDescriptor descriptor, Location? location, params string[] arguments)
        {
            context.Report(descriptor, location ?? method.Locations.FirstOrDefault(), arguments);
            return null;
        }
    }

    private static OperatorCastModel? CreateCast(IMethodSymbol method, FunctionParameter[] parameters, FunctionDeclaration function,
        AttributeData attribute, GeneratorDiagnostics context)
    {
        int castContext = attribute.ConstructorArguments.FirstOrDefault().Value is int value ? value : 0;
        if (castContext is < 0 or > 2)
        {
            return Invalid(s_castContext, FunctionDeclarationDiagnostics.ConstructorArgument(attribute, context.CancellationToken));
        }

        if (parameters.Length is < 1 or > 3)
        {
            return Invalid(s_castArity, ParameterList(method, context.CancellationToken));
        }

        if (method.ReturnsVoid)
        {
            return Invalid(s_voidResult, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken), "cast");
        }

        if (parameters.FirstOrDefault(static parameter => parameter.IsParams) is { } variadic)
        {
            return Invalid(s_variadic, ParameterLocation(method, variadic, context.CancellationToken, variadic: true), "cast");
        }

        if (parameters.Length > 1 && parameters[1].DeclaredSpecialType != SpecialType.System_Int32)
        {
            return Invalid(s_castModifier, ParameterLocation(method, parameters[1], context.CancellationToken));
        }

        if (parameters.Length > 2 && parameters[2].DeclaredSpecialType != SpecialType.System_Boolean)
        {
            return Invalid(s_castExplicit, ParameterLocation(method, parameters[2], context.CancellationToken));
        }

        string source = parameters[0].Type!.Sql;
        string target = FunctionType.CreateResult(method)!.Sql;
        if (source == "record" || target == "record")
        {
            return Invalid(s_recordCast, source == "record" ? ParameterLocation(method, parameters[0], context.CancellationToken) :
                FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
        }

        if (source == target && parameters.Length == 1)
        {
            return Invalid(s_identityCast, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
        }

        return new(null, new(function.TemplateName,
            new(parameters.Select(static parameter => SqlTypeTemplate.Create(parameter.Type!))),
            SqlTypeTemplate.Create(FunctionType.CreateResult(method)!), castContext));

        OperatorCastModel? Invalid(DiagnosticDescriptor descriptor, Location? location, params string[] arguments)
        {
            context.Report(descriptor, location ?? method.Locations.FirstOrDefault(), arguments);
            return null;
        }
    }

    /// <summary>
    /// Locates the authored argument list when its SQL operand count is invalid.
    /// </summary>
    private static Location? ParameterList(IMethodSymbol method, CancellationToken cancellationToken)
        => (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as BaseMethodDeclarationSyntax)
            ?.ParameterList.GetLocation() ?? method.Locations.FirstOrDefault();

    /// <summary>
    /// Maps a SQL operand back to its managed type or params modifier, including intervening injected arguments.
    /// </summary>
    private static Location? ParameterLocation(IMethodSymbol method, FunctionParameter parameter, CancellationToken cancellationToken,
        bool variadic = false)
    {
        IParameterSymbol symbol = method.Parameters.First(candidate => candidate.Name == parameter.Name);
        var syntax = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as ParameterSyntax;
        if (variadic && syntax is not null)
        {
            SyntaxToken modifier = syntax.Modifiers.FirstOrDefault(static token => token.IsKind(SyntaxKind.ParamsKeyword));
            if (modifier.RawKind != 0)
            {
                return modifier.GetLocation();
            }
        }

        return syntax?.Type?.GetLocation() ?? symbol.Locations.FirstOrDefault();
    }

    private static string? FunctionReference(string value)
    {
        string[] parts = value.Split('.');
        return parts.Length is >= 1 and <= 2 && parts.All(SqlText.IsIdentifier)
            ? string.Join(".", parts.Select(SqlText.Identifier)) : null;
    }

    private static string? OperatorReference(string? value, string? defaultSchema)
    {
        if (value is null)
        {
            return null;
        }

        string[] parts = value.Split('.');
        if (parts.Length is < 1 or > 2 || (parts.Length == 2 && !SqlText.IsIdentifier(parts[0])))
        {
            return null;
        }

        string name = parts[parts.Length - 1];
        if (name.Length is < 1 or > 63 || name == "=>" || name.Any(static c => "~!@#^&|`?+-*/%<>=".IndexOf(c) < 0) ||
            name.Contains("--") || name.Contains("/*") ||
            (name.Length > 1 && name[name.Length - 1] is '+' or '-' && !name.Any(static c => "~!@#^&|`?%".IndexOf(c) >= 0)))
        {
            return null;
        }

        // The PostgreSQL lexer uses <> for both spellings of inequality.
        name = name == "!=" ? "<>" : name;
        string? schema = parts.Length == 2 ? parts[0] : defaultSchema;
        return (schema is null ? string.Empty : SqlText.Identifier(schema) + ".") + name;
    }
}
