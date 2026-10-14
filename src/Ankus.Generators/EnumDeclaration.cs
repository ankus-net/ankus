using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators;

/// <summary>
/// Holds a validated enum contract independently of compiler symbols and attribute objects.
/// </summary>
/// <param name="Name">The exact SQL type name.</param>
/// <param name="Schema">The fixed schema, or null for the installation schema.</param>
/// <param name="Managed">The fully qualified managed enum type.</param>
/// <param name="Labels">The members in source declaration order with exact constants and labels.</param>
internal sealed record EnumDeclaration(string Name, string? Schema, string Managed, EquatableArray<EnumLabel> Labels)
{
    /// <summary>
    /// Gets the qualified, quoted SQL type name.
    /// </summary>
    internal string Sql => (Schema is null ? string.Empty : SqlText.Identifier(Schema) + ".") + SqlText.Identifier(Name);

    /// <summary>
    /// Reads and validates an attributed enum, optionally reporting diagnostics.
    /// </summary>
    /// <param name="type">The attributed enum to analyze.</param>
    /// <param name="diagnostics">The optional destination for precise contract failures.</param>
    /// <param name="cancellationToken">Cancels exact attribute analysis.</param>
    /// <param name="metadataFailure">Receives a structured unreadable attribute failure.</param>
    /// <returns>The detached immutable contract, or null when invalid or not attributed.</returns>
    internal static EnumDeclaration? Create(INamedTypeSymbol type, GeneratorDiagnostics? diagnostics = null,
        Action<AttributeMetadataFailure>? metadataFailure = null, CancellationToken cancellationToken = default)
    {
        AttributeData? attribute = type.GetAttributes().FirstOrDefault(static item => item.AttributeClass?.ToDisplayString() == "Ankus.PgEnumAttribute");
        if (attribute is null)
        {
            return null;
        }

        if (type.TypeKind != TypeKind.Enum)
        {
            return Invalid(EnumDeclarationDiagnostics.Kind, Location(attribute));
        }

        AttributeData? flags = type.GetAttributes().FirstOrDefault(static item => item.AttributeClass?.ToDisplayString() == "System.FlagsAttribute");
        if (flags is not null)
        {
            return Invalid(EnumDeclarationDiagnostics.Flags, Location(flags));
        }

        foreach (INamedTypeSymbol container in TypeNesting.SelfAndContainers(type))
        {
            if (container.Arity != 0)
            {
                return Invalid(EnumDeclarationDiagnostics.GenericContainer, container.Locations.FirstOrDefault(), container.Name);
            }

            if (container.IsFileLocal)
            {
                return Invalid(EnumDeclarationDiagnostics.FileContainer, container.Locations.FirstOrDefault(), container.Name);
            }

            if (container.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return Invalid(EnumDeclarationDiagnostics.Accessibility, container.Locations.FirstOrDefault(), container.Name);
            }
        }

        if (!ExactAttributeStrings.TryRead(type, attribute, cancellationToken, out AttributeStrings? options) || options is null)
        {
            metadataFailure?.Invoke(AttributeMetadataFailure.Create(type, attribute));
            return null;
        }

        string name = options.Property("Name", SqlText.SnakeCase(type.Name))!;
        string? schemaName = options.Property("Schema", null);
        AttributeData? schemaAttribute = schemaName is null ? null : attribute;
        for (INamedTypeSymbol? container = type.ContainingType; schemaName is null && container is not null; container = container.ContainingType)
        {
            AttributeData? schema = container.GetAttributes().FirstOrDefault(static item => item.AttributeClass?.ToDisplayString() == "Ankus.PgSchemaAttribute");
            if (schema is not null)
            {
                if (!ExactAttributeStrings.TryRead(container, schema, cancellationToken, out AttributeStrings? inherited) || inherited is null)
                {
                    metadataFailure?.Invoke(AttributeMetadataFailure.Create(container, schema));
                    return null;
                }

                schemaName = inherited.Arguments[0];
                schemaAttribute = schema;
                if (schemaName is null)
                {
                    return Invalid(EnumDeclarationDiagnostics.InheritedSchema, FunctionDeclarationDiagnostics.ConstructorArgument(schema, cancellationToken));
                }
            }
        }

        if (!SqlText.IsIdentifier(name))
        {
            return Invalid(EnumDeclarationDiagnostics.Name, FunctionDeclarationDiagnostics.Option(attribute, "Name", cancellationToken));
        }

        if (schemaName is not null && !SqlText.IsIdentifier(schemaName))
        {
            Location? location = schemaAttribute == attribute
                ? FunctionDeclarationDiagnostics.Option(attribute, "Schema", cancellationToken)
                : FunctionDeclarationDiagnostics.ConstructorArgument(schemaAttribute!, cancellationToken);
            return Invalid(EnumDeclarationDiagnostics.Schema, location);
        }

        var values = new HashSet<object>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<EnumLabel>();
        foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>().Where(static field => field.HasConstantValue))
        {
            AttributeData? labelAttribute = field.GetAttributes().FirstOrDefault(static item => item.AttributeClass?.ToDisplayString() == "Ankus.PgEnumLabelAttribute");
            string? label = field.Name;
            if (labelAttribute is not null)
            {
                if (!ExactAttributeStrings.TryRead(field, labelAttribute, cancellationToken, out AttributeStrings? item) || item is null)
                {
                    metadataFailure?.Invoke(AttributeMetadataFailure.Create(field, labelAttribute));
                    return null;
                }

                label = item.Arguments[0];
            }

            Location? labelLocation = labelAttribute is null ? field.Locations.FirstOrDefault() :
                FunctionDeclarationDiagnostics.ConstructorArgument(labelAttribute, cancellationToken);
            if (label is null)
            {
                return Invalid(EnumDeclarationDiagnostics.NullLabel, labelLocation, field.Name);
            }

            if (!SqlText.IsText(label))
            {
                return Invalid(EnumDeclarationDiagnostics.LabelText, labelLocation, field.Name);
            }

            if (Encoding.UTF8.GetByteCount(label) > 63)
            {
                return Invalid(EnumDeclarationDiagnostics.LabelLength, labelLocation, field.Name);
            }

            if (!labels.Add(label))
            {
                return Invalid(EnumDeclarationDiagnostics.DuplicateLabel, labelLocation, field.Name);
            }

            if (!values.Add(field.ConstantValue!))
            {
                return Invalid(EnumDeclarationDiagnostics.NumericAlias, field.Locations.FirstOrDefault(), field.Name);
            }

            members.Add(new(field.Name, field.ConstantValue!, label));
        }

        return new(name, schemaName, type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), new(members));

        EnumDeclaration? Invalid(DiagnosticDescriptor descriptor, Location? location, params string[] details)
        {
            diagnostics?.Report(descriptor, location ?? type.Locations.FirstOrDefault(), [type.Name, .. details]);
            return null;
        }

        Location? Location(AttributeData selected)
            => selected.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation();
    }

    /// <summary>
    /// Emits a generated enum registration for module initialization, without backend access.
    /// </summary>
    internal void EmitRegistration(StringBuilder source)
    {
        source.AppendLine($"        global::Ankus.CompilerServices.PgEnumRegistry.Register<{Managed}>({SymbolDisplay.FormatLiteral(Name, true)}, " +
            (Schema is null ? "null" : SymbolDisplay.FormatLiteral(Schema, true)) + ", new global::System.Collections.Generic.KeyValuePair<" + Managed + ", string>[]");
        source.AppendLine("        {");
        foreach (EnumLabel label in Labels)
        {
            source.AppendLine($"            new({Managed}.@{label.Member}, {SymbolDisplay.FormatLiteral(label.Label, true)}),");
        }

        source.AppendLine("        });");
    }

    /// <summary>
    /// Emits CREATE TYPE with label order preserved exactly.
    /// </summary>
    internal string CreateSql() => "CREATE TYPE " + (Schema is null ? "\0" : string.Empty) + Sql + " AS ENUM (" + string.Join(", ", Labels.Select(static item => SqlText.Literal(item.Label))) + ");\n";

    /// <summary>
    /// Emits a native catalog identity check so unsupported SPI enums fail before a query's subtransaction commits.
    /// </summary>
    internal void EmitNativeTypeCheck(StringBuilder source)
    {
        source.AppendLine("    {");
        source.AppendLine("        const AnkusValue name = { .data = (unsigned char *) " + Utf8Literal(Name) + ", .length = " +
            Encoding.UTF8.GetByteCount(Name).ToString(System.Globalization.CultureInfo.InvariantCulture) + " };");
        source.AppendLine(Schema is null ? "        const AnkusValue schema = { .is_null = 1 };" :
            "        const AnkusValue schema = { .data = (unsigned char *) " + Utf8Literal(Schema) + ", .length = " +
            Encoding.UTF8.GetByteCount(Schema).ToString(System.Globalization.CultureInfo.InvariantCulture) + " };");
        source.AppendLine("        if (ankus_resolve_enum(&name, &schema, true) == type) return true;");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static string Utf8Literal(string value) => "\"" + string.Concat(Encoding.UTF8.GetBytes(value).Select(static item =>
        "\\x" + item.ToString("x2", System.Globalization.CultureInfo.InvariantCulture))) + "\"";
}
