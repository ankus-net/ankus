using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Resolves named SQL bindings on parameters, scalar returns and individual TABLE columns.
/// </summary>
/// <param name="Name">The exact unquoted catalog name.</param>
/// <param name="Schema">The fixed catalog schema, or null for an unqualified binding.</param>
/// <param name="IsRaw">Whether the value carries a raw PostgreSQL datum.</param>
/// <param name="IsArray">Whether the binding identifies an array type.</param>
internal sealed record SqlTypeReference(string Name, string? Schema, bool IsRaw = false, bool IsArray = false)
{
    private const string HelpLink = "https://ankus.net/function-declarations/#named-sql-type-bindings";

    private static readonly DiagnosticDescriptor s_invalidIdentifier = new(
        "ANKUS401", "Invalid PostgreSQL type-binding identifier",
        "'{0}' has an invalid PostgreSQL type or schema identifier; use nonempty names of at most 63 UTF-8 bytes with valid Unicode and no zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_tableElement = new(
        "ANKUS402", "TABLE binding cannot select an aggregate element",
        "'{0}' applies Element to a TABLE output binding; use Column to select a TABLE output",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_unknownColumn = new(
        "ANKUS403", "PostgreSQL type binding selects an unknown TABLE column",
        "'{0}' applies Column to a name that is not an output of this TABLE result",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_ambiguousOutput = new(
        "ANKUS404", "PostgreSQL TABLE type binding is ambiguous",
        "'{0}' has a return binding without exactly one matching TABLE output; set Column to select the output explicitly",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_rawRepresentation = new(
        "ANKUS405", "Raw PostgreSQL type binding requires PgDatum",
        "'{0}' applies PgSqlType to a value that is not PgDatum; use PgDatum or remove the raw binding",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_compositeRepresentation = new(
        "ANKUS406", "Composite PostgreSQL type binding requires PgHeapTuple",
        "'{0}' applies PgCompositeType to a value that is not PgHeapTuple or an array of PgHeapTuple",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_duplicateOutput = new(
        "ANKUS407", "PostgreSQL TABLE output has multiple type bindings",
        "'{0}' applies more than one SQL type binding to the same TABLE output; retain exactly one binding",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_missingOutput = new(
        "ANKUS408", "Raw PostgreSQL TABLE output requires a type binding",
        "'{0}' has a PgDatum TABLE output without PgSqlType; bind every raw output to its PostgreSQL type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_duplicateValue = new(
        "ANKUS409", "PostgreSQL value has multiple type bindings",
        "'{0}' has more than one PgSqlType or PgCompositeType binding on '{1}'; retain exactly one binding",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_missingValue = new(
        "ANKUS410", "Raw PostgreSQL value requires a type binding",
        "'{0}' uses PgDatum for '{1}' without PgSqlType; bind the raw value to its PostgreSQL type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_columnContext = new(
        "ANKUS411", "TABLE column selector requires a TABLE result",
        "'{0}' applies Column outside a TABLE result; remove Column or bind a named TABLE output",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_elementContext = new(
        "ANKUS412", "Aggregate element selector requires a tuple input",
        "'{0}' applies Element outside a typed aggregate tuple input; remove Element or bind an aggregate tuple element",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets whether the binding names PostgreSQL's internal callback type.
    /// </summary>
    internal bool IsInternal => IsBuiltin && Name == "internal";

    /// <summary>
    /// Gets whether a built-in binding needs a polymorphic input to resolve its output type.
    /// </summary>
    internal bool IsPolymorphic => IsBuiltin && Name is "anyelement" or "anyarray" or "anynonarray" or "anyenum" or
        "anyrange" or "anymultirange" or "anycompatible" or "anycompatiblearray" or "anycompatiblenonarray" or
        "anycompatiblerange" or "anycompatiblemultirange";

    /// <summary>
    /// Gets whether this scalar binding identifies a built-in type.
    /// </summary>
    private bool IsBuiltin => IsRaw && !IsArray && Schema is null or "pg_catalog";

    /// <summary>
    /// Gets a schema dependency when installation must preserve a user-selected type schema.
    /// </summary>
    internal string? DependencySchema => IsRaw && Schema == "pg_catalog" ? null : Schema;

    /// <summary>
    /// Gets the quoted SQL type identifier with its optional schema.
    /// </summary>
    internal string Sql => (Schema is null ? string.Empty : SqlText.Identifier(Schema) + ".") + SqlText.Identifier(Name) +
        (IsArray ? "[]" : string.Empty);

    /// <summary>
    /// Reads the single binding on an already validated parameter or scalar return.
    /// </summary>
    internal static SqlTypeReference? Read(ImmutableArray<AttributeData> attributes)
    {
        AttributeData? attribute = Bindings(attributes).FirstOrDefault();
        return attribute is null ? null : Read(attribute);
    }

    /// <summary>
    /// Validates scalar bindings without retaining a set result.
    /// </summary>
    /// <param name="method">The attributed scalar method.</param>
    /// <param name="context">The generator diagnostic destination.</param>
    /// <returns>Whether every parameter and result binding is valid.</returns>
    internal static bool Validate(IMethodSymbol method, GeneratorDiagnostics context)
    {
        SetResult? set = null;
        return Validate(method, ref set, context);
    }

    /// <summary>
    /// Validates context-specific bindings and replaces the immutable set model with bound output columns.
    /// </summary>
    internal static bool Validate(IMethodSymbol method, ref SetResult? set, GeneratorDiagnostics context)
    {
        bool valid = true;
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            valid &= ValidateValue(parameter.Type, parameter.GetAttributes(), method, parameter.Name, context,
                location: parameter.Locations.FirstOrDefault());
        }

        if (set is null)
        {
            valid &= ValidateValue(method.ReturnType, method.GetReturnTypeAttributes(), method, "return", context,
                location: FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
        }
        else
        {
            var bound = new HashSet<int>();
            FunctionType[] columns = [.. set.Columns];
            ITypeSymbol[] outputTypes = SetResult.OutputTypes(method);
            foreach (AttributeData attribute in Bindings(method.GetReturnTypeAttributes()))
            {
                if (!ValidateName(attribute, Error))
                {
                    continue;
                }

                if (AttributeValues.Get<string?>(attribute, "Element", null) is not null)
                {
                    Error(attribute, s_tableElement);
                    continue;
                }

                string? column = AttributeValues.Get<string?>(attribute, "Column", null);
                int index;
                if (column is not null)
                {
                    index = set.Names is null ? -1 : Array.IndexOf<string>([.. set.Names], column);
                    if (index < 0)
                    {
                        Error(attribute, s_unknownColumn);
                        continue;
                    }
                }
                else
                {
                    int[] candidates = [.. Enumerable.Range(0, columns.Length).Where(candidate => Matches(columns[candidate], attribute))];
                    if (candidates.Length != 1)
                    {
                        Error(attribute, s_ambiguousOutput);
                        continue;
                    }

                    index = candidates[0];
                }

                if (!Matches(columns[index], attribute))
                {
                    Error(attribute, RepresentationDiagnostic(attribute));
                    continue;
                }

                if (!bound.Add(index))
                {
                    Error(attribute, s_duplicateOutput);
                    continue;
                }

                columns[index] = FunctionType.Create(outputTypes[index], Read(attribute))!;
            }

            set = set with { Columns = new(columns) };

            for (int index = 0; index < columns.Length; index++)
            {
                if (columns[index].IsRaw && columns[index].Binding is null)
                {
                    valid = false;
                    context.Report(s_missingOutput, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken), method.Name);
                }
            }
        }

        return valid;

        void Error(AttributeData? attribute, DiagnosticDescriptor descriptor)
        {
            valid = false;
            Report(attribute, method, descriptor, context, method.Name);
        }
    }

    /// <summary>
    /// Validates one SQL value after any aggregate tuple-element selection.
    /// </summary>
    internal static bool ValidateValue(ITypeSymbol type, ImmutableArray<AttributeData> attributes, IMethodSymbol method,
        string target, GeneratorDiagnostics context, bool grouped = false, Location? location = null)
    {
        bool valid = true;
        AttributeData[] bindings = [.. Bindings(attributes)];
        if (bindings.Length > 1)
        {
            Error(bindings[1], s_duplicateValue, method.Name, target);
            return false;
        }

        if (bindings.Length == 0 && FunctionType.Create(type)?.IsRaw == true)
        {
            valid = false;
            context.Report(s_missingValue, location ?? method.Locations.FirstOrDefault(), method.Name, target);
        }

        foreach (AttributeData attribute in bindings)
        {
            if (!ValidateName(attribute, (invalid, descriptor) => Error(invalid, descriptor, method.Name)))
            {
                continue;
            }

            if (AttributeValues.Get<string?>(attribute, "Column", null) is not null)
            {
                Error(attribute, s_columnContext, method.Name);
            }
            else if (!grouped && AttributeValues.Get<string?>(attribute, "Element", null) is not null)
            {
                Error(attribute, s_elementContext, method.Name);
            }
            else if (!Matches(FunctionType.Create(type), attribute))
            {
                Error(attribute, RepresentationDiagnostic(attribute), method.Name);
            }
        }

        return valid;

        void Error(AttributeData? attribute, DiagnosticDescriptor descriptor, params string[] arguments)
        {
            valid = false;
            Report(attribute, method, descriptor, context, arguments);
        }
    }

    /// <summary>
    /// Checks identifiers shared by scalar, TABLE and aggregate element bindings.
    /// </summary>
    private static bool ValidateName(AttributeData attribute, Action<AttributeData?, DiagnosticDescriptor> error)
    {
        string? typeName = attribute.ConstructorArguments.FirstOrDefault().Value as string;
        string? schemaName = AttributeValues.Get<string?>(attribute, "Schema", null);
        if (!SqlText.IsIdentifier(typeName) || schemaName is not null && !SqlText.IsIdentifier(schemaName))
        {
            error(attribute, s_invalidIdentifier);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reports a binding diagnostic at its attribute or declaring method.
    /// </summary>
    private static void Report(AttributeData? attribute, IMethodSymbol method, DiagnosticDescriptor descriptor,
        GeneratorDiagnostics context, params string[] arguments)
        => context.Report(descriptor,
            attribute?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? method.Locations.FirstOrDefault(),
            arguments);

    /// <summary>
    /// Checks the managed representation selected by a binding attribute.
    /// </summary>
    private static bool Matches(FunctionType? type, AttributeData attribute)
        => IsRawBinding(attribute) ? type?.IsRaw == true : (type?.Element ?? type)?.IsComposite == true;

    /// <summary>
    /// Identifies an explicit raw datum binding.
    /// </summary>
    private static bool IsRawBinding(AttributeData attribute)
        => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgSqlTypeAttribute";

    /// <summary>
    /// Selects the managed-representation diagnostic for one binding attribute.
    /// </summary>
    private static DiagnosticDescriptor RepresentationDiagnostic(AttributeData attribute)
        => IsRawBinding(attribute) ? s_rawRepresentation : s_compositeRepresentation;

    /// <summary>
    /// Selects attributes that bind a managed value to a named SQL type.
    /// </summary>
    private static IEnumerable<AttributeData> Bindings(ImmutableArray<AttributeData> attributes)
        => attributes.Where(static attribute => attribute.AttributeClass?.ToDisplayString() is "Ankus.PgCompositeTypeAttribute" or "Ankus.PgSqlTypeAttribute");

    /// <summary>
    /// Reads the type identity and representation from a binding attribute.
    /// </summary>
    private static SqlTypeReference Read(AttributeData attribute)
        => new(attribute.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty,
            AttributeValues.Get<string?>(attribute, "Schema", null), IsRawBinding(attribute),
            AttributeValues.Get(attribute, "IsArray", false));
}
