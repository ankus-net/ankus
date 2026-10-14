using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Binds declared value semantics to generated PostgreSQL operators and default index operator classes.
/// </summary>
internal static class DerivedOperatorDeclaration
{
    private const string HelpLink = "https://willibrandon.github.io/ankus/operators-and-casts/#generated-type-operators";

    private static readonly DiagnosticDescriptor s_root = new(
        "ANKUS413", "Generated PostgreSQL operators require a supported type",
        "'{0}' must have a valid, accessible PgType, PgEnum, or PgDatumType declaration before generating operators",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_reader = new(
        "ANKUS414", "Generated PostgreSQL operators require a datum reader",
        "'{0}' requires an IPgDatumReader<T> for its exact PgDatumType before generating operators",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_equality = new(
        "ANKUS415", "Generated PostgreSQL operators require managed equality",
        "'{0}' must implement IEquatable<T> for its exact declared type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_ordering = new(
        "ANKUS416", "Generated PostgreSQL ordering requires managed comparison",
        "'{0}' must implement IComparable<T> for its exact declared type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_hashing = new(
        "ANKUS417", "Generated PostgreSQL hashing requires a stable managed hash",
        "'{0}' must implement IPgHashable with an equality-compatible GetPostgresHashCode implementation",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_missingEquality = new(
        "ANKUS418", "Generated PostgreSQL ordering or hashing requires equality",
        "'{0}' must declare PgEquality or a boolean same-schema PgOperator(\"=\") for its exact SQL type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Identifies explicit operator-generation attributes without requiring a valid storage declaration.
    /// </summary>
    internal static bool IsAttribute(AttributeData attribute) => attribute.AttributeClass?.ToDisplayString() is
        "Ankus.PgEqualityAttribute" or "Ankus.PgOrderingAttribute" or "Ankus.PgHashingAttribute";

    /// <summary>
    /// Emits statically bound helpers through the shared scalar boundary and adds their installation dependencies.
    /// </summary>
    internal static bool Emit(DerivedOperatorModel model, Dictionary<string, SqlEntity> types, SqlTypeProviders providers,
        Dictionary<string, SqlEntity> schemas, HashSet<string> functions,
        HashSet<string> relatedNames, Dictionary<string, SqlEntity> operators, SqlGraph graph, GeneratorDiagnostics context,
        GeneratorSourceResolver compilation, IReadOnlyDictionary<string, DerivedHelperEmission> helpers, DerivedSqlEmission? sql, bool ensureInitialized,
        GeneratorSourceBuilder managed, GeneratorSourceBuilder native, GeneratorSourceBuilder exports, out bool relocatable)
    {
        relocatable = true;
        SqlDeclarationOptions? equality = model.Equality;
        SqlDeclarationOptions? ordering = model.Ordering;
        SqlDeclarationOptions? hashing = model.Hashing;
        string managedType = model.Managed;
        FunctionType? value = model.Value;
        types.TryGetValue(managedType, out SqlEntity? typeEntity);
        if (value is null || sql is null || !HasSqlType(value, typeEntity))
        {
            return Invalid(s_root);
        }

        if (model.Error is not null)
        {
            return Invalid(model.Error.Value switch
            {
                DerivedOperatorError.Reader => s_reader,
                DerivedOperatorError.Equality => s_equality,
                DerivedOperatorError.Ordering => s_ordering,
                DerivedOperatorError.Hashing => s_hashing,
                _ => throw new InvalidOperationException("Unknown generated-operator diagnostic."),
            });
        }

        string? schema = value.CustomType?.Schema ?? value.Enumeration?.Schema ?? value.DatumType?.Schema;
        relocatable = schema is null;
        string equalitySignature = sql.EqualitySignature.Compose(providers).Replace("\0", string.Empty);
        if (equality is null && !operators.ContainsKey(equalitySignature))
        {
            return Invalid(s_missingEquality);
        }

        Dictionary<string, DerivedSqlEmission.Function> sqlFunctions = sql.Functions.ToDictionary(static function => function.Role, StringComparer.Ordinal);
        Dictionary<string, DerivedSqlEmission.Comparison> sqlOperators = sql.Operators.ToDictionary(static comparison => comparison.Role, StringComparer.Ordinal);
        var attributes = new Dictionary<SqlEntity, Location?>();

        if (equality is not null)
        {
            SqlEntity group = Group("equality", equality);
            SqlEntity equalFunction = Function("eq", group);
            SqlEntity unequalFunction = Function("ne", group);
            SqlEntity equalOperator = Comparison("eq", equalFunction);
            SqlEntity unequalOperator = Comparison("ne", unequalFunction);
            group.Dependencies.UnionWith([equalOperator, unequalOperator]);
        }

        SqlEntity equalityOperator = operators[equalitySignature];
        if (ordering is not null)
        {
            SqlEntity group = Group("ordering", ordering);
            group.Dependencies.Add(equalityOperator);
            SqlEntity comparison = Function("cmp", group);
            foreach (string role in new[] { "lt", "gt", "le", "ge" })
            {
                SqlEntity function = Function(role, group);
                group.Dependencies.Add(Comparison(role, function));
            }

            group.Dependencies.Add(comparison);
            Family(group, sql.Ordering!);
            relocatable &= SqlGeneration.ApplyOptions(ordering, group, [], [("@COMPARISON_FUNCTION_SQL@", sqlFunctions["cmp"].Name)], graph);
        }

        if (hashing is not null)
        {
            SqlEntity group = Group("hashing", hashing);
            group.Dependencies.Add(equalityOperator);
            group.Dependencies.Add(Function("hash", group));
            Family(group, sql.Hashing!);
            relocatable &= SqlGeneration.ApplyOptions(hashing, group, [], [("@HASH_FUNCTION_SQL@", sqlFunctions["hash"].Name)], graph);
        }

        return true;

        bool Invalid(DiagnosticDescriptor descriptor)
        {
            context.Report(descriptor, model.Location?.Resolve(compilation), model.DiagnosticName);
            return false;
        }

        void Family(SqlEntity group, DerivedSqlEmission.Family family)
        {
            group.SelectionNames.UnionWith([family.Identifier, family.Name.Replace("\0", string.Empty)]);
            group.Attachments.UnionWith(["OPERATOR FAMILY " + family.Name + " USING " + family.AccessMethod,
                "OPERATOR CLASS " + family.Name + " USING " + family.AccessMethod]);
            group.Sql = family.Definition.Compose(providers);
        }

        SqlEntity Group(string role, SqlDeclarationOptions options)
        {
            var entity = new SqlEntity("3:derived-" + role + ":" + managedType, string.Empty, model.Location?.Resolve(compilation)) { Kind = role };
            attributes.Add(entity, graph.Resolve(options.Locations.Attribute) ?? entity.Location);
            entity.SelectionNames.Add(model.Display + "." + role);
            RequireType(entity);
            graph.ConfigureOptions(entity, options);
            graph.Add(entity);
            graph.Register(model.Identity, model.Display, entity);
            return entity;
        }

        SqlEntity Function(string role, SqlEntity group)
        {
            DerivedHelperEmission emission = helpers[role];
            DerivedSqlEmission.Function definition = sqlFunctions[role];
            managed.Append(emission.Managed);
            emission.Native.AppendTo(native, ensureInitialized);
            exports.Append(emission.Exports);
            string signature = definition.Signature.Compose(providers);
            if (!functions.Add(signature.Replace("\0", string.Empty)))
            {
                graph.Error(attributes[group], SqlGraphDiagnostics.s_duplicateGeneratedFunction, signature.Replace("\0", string.Empty), model.DiagnosticName);
            }

            var entity = new SqlEntity("1:derived-function:" + managedType + ":" + role, definition.Definition.Compose(providers),
                model.Location?.Resolve(compilation)) { Kind = "function", Owner = group };
            entity.SelectionNames.UnionWith([definition.Identifier, definition.Name.Replace("\0", string.Empty), signature.Replace("\0", string.Empty)]);
            entity.Attachments.Add("FUNCTION " + signature);
            if (schema is not null)
            {
                entity.SelectionNames.Add(schema + "." + definition.Identifier);
            }

            RequireType(entity);
            entity.Inherit(group.Requires);
            graph.Add(entity);
            return entity;
        }

        void RequireType(SqlEntity entity)
        {
            if (typeEntity is not null)
            {
                entity.Dependencies.Add(typeEntity);
            }

            providers.Require(entity, value, requireComplete: true);
            if (schema is not null && schemas.TryGetValue(schema, out SqlEntity? schemaEntity))
            {
                entity.Dependencies.Add(schemaEntity);
            }
        }

        SqlEntity Comparison(string role, SqlEntity function)
        {
            DerivedSqlEmission.Comparison definition = sqlOperators[role];
            string signature = definition.Signature.Compose(providers);
            if (!relatedNames.Add("operator:" + signature.Replace("\0", string.Empty)))
            {
                graph.Error(attributes[function.Owner!], SqlGraphDiagnostics.s_duplicateGeneratedOperator, signature.Replace("\0", string.Empty), model.DiagnosticName);
            }

            var entity = new SqlEntity("2:derived-operator:" + managedType + ":" + role, definition.Definition.Compose(providers),
                model.Location?.Resolve(compilation)) { Kind = "operator", Owner = function.Owner };
            entity.SelectionNames.UnionWith([definition.Token, signature.Replace("\0", string.Empty)]);
            entity.Attachments.Add("OPERATOR " + signature);
            entity.Dependencies.Add(function);
            operators[signature.Replace("\0", string.Empty)] = entity;
            graph.Add(entity);
            return entity;
        }
    }

    /// <summary>
    /// Checks that the operand has a SQL type: a datum, or a custom or enum type that this extension declares.
    /// </summary>
    private static bool HasSqlType(FunctionType value, SqlEntity? typeEntity)
        => value.DatumType is not null || typeEntity is not null && (value.CustomType is not null || value.Enumeration is not null);
}
