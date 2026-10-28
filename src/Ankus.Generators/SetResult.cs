using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Resolves an enumerable return into scalar SETOF or named TABLE columns.
/// </summary>
internal sealed record SetResult
{
    private static readonly DiagnosticDescriptor s_columnNamesRequireSet = new(
        "ANKUS396", "PostgreSQL column names require a set result", "'{0}' uses PgColumnNames but does not return IEnumerable<T>",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/sets-and-tables/");
    private static readonly DiagnosticDescriptor s_unsupportedColumns = new(
        "ANKUS397", "PostgreSQL set result has unsupported columns",
        "'{0}' must return supported scalar or array values, or a flat tuple of at most 1664 supported columns",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/sets-and-tables/");
    private static readonly DiagnosticDescriptor s_nullColumnNames = new(
        "ANKUS398", "PostgreSQL table column names cannot be null", "'{0}' must pass a non-null name array to PgColumnNames",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/sets-and-tables/");
    private static readonly DiagnosticDescriptor s_missingColumnNames = new(
        "ANKUS399", "PostgreSQL table result requires column names",
        "'{0}' must name every tuple element or apply PgColumnNames to name every TABLE column",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/sets-and-tables/");
    private static readonly DiagnosticDescriptor s_invalidColumnNames = new(
        "ANKUS400", "Invalid PostgreSQL table column names",
        "'{0}' requires one distinct SQL identifier per TABLE column, each at most 63 UTF-8 bytes",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/sets-and-tables/");

    /// <summary>
    /// Gets the managed iterator element type, including tuple names and nullable annotations.
    /// </summary>
    internal string Managed
    {
        get;
        private init;
    } = string.Empty;

    /// <summary>
    /// Gets the ordered output conversion contracts.
    /// </summary>
    internal EquatableArray<FunctionType> Columns
    {
        get;
        init;
    } = new([]);

    /// <summary>
    /// Gets the column names for TABLE, or null for scalar SETOF.
    /// </summary>
    internal EquatableArray<string>? Names
    {
        get;
        private init;
    }

    /// <summary>
    /// Gets expressions that read each output column from the iterator's current value.
    /// </summary>
    internal EquatableArray<string> Values
    {
        get;
        private init;
    } = new([]);

    /// <summary>
    /// Gets the complete SQL return clause following RETURNS.
    /// </summary>
    internal string Sql => Names is null ? "SETOF " + Columns[0].Sql : "TABLE (" +
        string.Join(", ", Columns.Select((column, index) => SqlText.Identifier(Names[index]) + " " + column.Sql)) + ")";

    /// <summary>
    /// Formats typed return identifiers with extension-default schema markers.
    /// </summary>
    /// <param name="providers">The extension's declared type providers.</param>
    /// <returns>The schema-aware SQL return clause.</returns>
    internal string TemplateSql(SqlTypeProviders providers) => Names is null ? "SETOF " + SqlSchemaTemplate.Type(Columns[0], providers) : "TABLE (" +
        string.Join(", ", Columns.Select((column, index) => SqlText.Identifier(Names[index]) + " " + SqlSchemaTemplate.Type(column, providers))) + ")";

    /// <summary>
    /// Identifies the explicit generic enumerable return contract without treating scalar collections as sets.
    /// </summary>
    internal static bool IsSequence(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T };

    /// <summary>
    /// Resolves source output types only during semantic validation, without retaining compiler symbols in the set model.
    /// </summary>
    /// <param name="method">The attributed method returning an explicit generic enumerable.</param>
    /// <returns>The ordered scalar or tuple-element source types.</returns>
    internal static ITypeSymbol[] OutputTypes(IMethodSymbol method)
    {
        ITypeSymbol row = ((INamedTypeSymbol)method.ReturnType).TypeArguments[0];
        return row switch
        {
            INamedTypeSymbol { IsTupleType: true } tuple => [.. tuple.TupleElements.Select(static field => field.Type)],
            INamedTypeSymbol { Name: "ValueTuple", Arity: 1 } single when single.ContainingNamespace.ToDisplayString() == "System" => [single.TypeArguments[0]],
            _ => [row],
        };
    }

    /// <summary>
    /// Validates a method's set shape and optional column names, reporting invalid result declarations.
    /// </summary>
    internal static SetResult? Create(IMethodSymbol method, GeneratorDiagnostics context, out bool valid)
    {
        valid = true;
        AttributeData? namesAttribute = method.GetReturnTypeAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "Ankus.PgColumnNamesAttribute");
        if (!IsSequence(method.ReturnType))
        {
            if (namesAttribute is not null)
            {
                valid = false;
                Error(s_columnNamesRequireSet, AttributeLocation());
            }

            return null;
        }

        ITypeSymbol row = ((INamedTypeSymbol)method.ReturnType).TypeArguments[0];
        string managed = row.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
        ITypeSymbol[] types = OutputTypes(method);
        string[] values;
        string[]? names = null;
        if (row is INamedTypeSymbol { IsTupleType: true } tuple)
        {
            values = [.. tuple.TupleElements.Select(static field => "value.@" + field.Name)];
            if (tuple.TupleElements.All(static field => !field.IsImplicitlyDeclared))
            {
                names = [.. tuple.TupleElements.Select(static field => SqlText.SnakeCase(field.Name))];
            }
        }
        else if (row is INamedTypeSymbol { Name: "ValueTuple", Arity: 1 } single && single.ContainingNamespace.ToDisplayString() == "System")
        {
            values = ["value.Item1"];
        }
        else
        {
            values = ["value"];
        }

        FunctionType?[] columns = [.. types.Select(static type => FunctionType.Create(type))];
        if (columns.Length > 1664 || columns.Any(static column => column is null || column.Managed == "void"))
        {
            valid = false;
            for (int index = 0; index < columns.Length; index++)
            {
                if (columns[index] is null && AttributeMetadataFailure.ReportConversion(types[index],
                    FunctionDeclarationDiagnostics.Result(method, context.CancellationToken), context))
                {
                    return null;
                }
            }

            Error(s_unsupportedColumns, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
            return null;
        }

        if (namesAttribute is not null)
        {
            // Only an array exposes Values; an unfinished application may not have bound its params array.
            if (namesAttribute.ConstructorArguments.Length != 1 || namesAttribute.ConstructorArguments[0].Kind != TypedConstantKind.Array ||
                namesAttribute.ConstructorArguments[0].IsNull)
            {
                valid = false;
                Error(s_nullColumnNames, AttributeLocation());
                return null;
            }

            names = [.. namesAttribute.ConstructorArguments[0].Values.Select(static value => value.Value as string ?? string.Empty)];
        }

        if (values[0] != "value" && names is null)
        {
            valid = false;
            Error(s_missingColumnNames, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
            return null;
        }

        if (names is not null && (names.Length != columns.Length ||
            names.Any(static name => !SqlText.IsIdentifier(name)) ||
            names.Distinct(StringComparer.Ordinal).Count() != names.Length))
        {
            valid = false;
            Error(s_invalidColumnNames, AttributeLocation());
            return null;
        }

        return new()
        {
            Managed = managed,
            Columns = new(columns.Select(static column => column!)),
            Names = names is null ? null : new(names),
            Values = new(values),
        };

        Location? AttributeLocation() => namesAttribute?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
            ?? method.Locations.FirstOrDefault();

        void Error(DiagnosticDescriptor descriptor, Location? location)
            => context.Report(descriptor, location, method.Name);
    }
}
