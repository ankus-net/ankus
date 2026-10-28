using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Validates a generated base type and its statically constructed storage codec.
/// </summary>
internal sealed class CustomTypeDeclaration(INamedTypeSymbol type, INamedTypeSymbol? codec, INamedTypeSymbol? textCodec,
    SerializationModel? serializer, int nativeSize, string nativePadding, AttributeData attribute, string name, string? schema,
    string? nullInputErrorMessage)
{
    /// <summary>
    /// Gets the attributed managed type.
    /// </summary>
    internal INamedTypeSymbol Type { get; } = type;

    /// <summary>
    /// Gets the attribute carrying installation dependencies.
    /// </summary>
    internal AttributeData Attribute { get; } = attribute;

    /// <summary>
    /// Gets the unquoted SQL type name.
    /// </summary>
    internal string Name { get; } = name;

    /// <summary>
    /// Gets the fixed schema or null for the installation schema.
    /// </summary>
    internal string? Schema { get; } = schema;

    /// <summary>
    /// Gets the qualified managed name.
    /// </summary>
    internal string Managed => Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>
    /// Gets the qualified, quoted SQL name.
    /// </summary>
    internal string Sql => Qualify(Name);

    /// <summary>
    /// Gets whether binary send and receive functions are generated.
    /// </summary>
    internal bool BinaryProtocol => AttributeValues.Get(Attribute, "BinaryProtocol", false);

    /// <summary>
    /// Gets the validated PostgreSQL datum alignment for variable-length storage.
    /// </summary>
    internal string Alignment => AttributeValues.Get(Attribute, "Alignment", 0) == 0 ? "int4" : "double";

    /// <summary>
    /// Gets whether this type has a statically proved native payload.
    /// </summary>
    internal bool NativeLayout => nativeSize != 0;

    /// <summary>
    /// Gets the optional error raised by a NULL call to the text input function.
    /// </summary>
    internal string? NullInputErrorMessage => nullInputErrorMessage;

    /// <summary>
    /// Gets a stable assembly-specific native symbol suffix.
    /// </summary>
    internal string Symbol
    {
        get
        {
            using SHA256 hash = SHA256.Create();
            byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(PgFunctionGenerator.SymbolScope(Type.ContainingAssembly) + ":" + Managed));
            return string.Concat(bytes.Take(16).Select(static value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// Quotes an identifier in the type's selected schema.
    /// </summary>
    private string Qualify(string identifier) => (Schema is null ? string.Empty : SqlText.Identifier(Schema) + ".") + SqlText.Identifier(identifier);

    /// <summary>
    /// Reads and validates an attributed base type, optionally reporting diagnostics.
    /// </summary>
    internal static CustomTypeDeclaration? Create(INamedTypeSymbol type, Action<string>? report = null,
        Action<AttributeMetadataFailure>? metadataFailure = null,
        Action<DiagnosticDescriptor, Location?, string[]>? diagnostic = null, CancellationToken cancellationToken = default)
    {
        AttributeData? attribute = type.GetAttributes().FirstOrDefault(static item => item.AttributeClass?.ToDisplayString() == "Ankus.PgTypeAttribute");
        if (attribute is null)
        {
            return null;
        }

        CancellationToken token = cancellationToken;
        Location? typeLocation = type.Locations.FirstOrDefault();
        if (!ExactAttributeStrings.TryRead(type, attribute, token, out AttributeStrings? options) || options is null)
        {
            metadataFailure?.Invoke(AttributeMetadataFailure.Create(type, attribute));
            return null;
        }

        if (AttributeValues.Get(attribute, "Alignment", 0) is not (0 or 1))
        {
            return Invalid(CustomTypeDiagnosticKind.Alignment, Option("Alignment"), type.Name);
        }

        object? codecArgument = attribute.ConstructorArguments.FirstOrDefault().Value;
        TypedConstant textArgument = attribute.NamedArguments.FirstOrDefault(static argument => argument.Key == "TextCodec").Value;
        if (codecArgument is not null and not INamedTypeSymbol || textArgument.Kind == TypedConstantKind.Array ||
            textArgument.Value is not null and not INamedTypeSymbol)
        {
            return Invalid(CustomTypeDiagnosticKind.CodecType,
                codecArgument is not null and not INamedTypeSymbol ? DatumMappingDiagnostics.Argument(attribute, 0, token) : Option("TextCodec"), type.Name);
        }

        var codec = codecArgument as INamedTypeSymbol;
        var textCodec = textArgument.Value as INamedTypeSymbol;
        bool nativeLayout = AttributeValues.Get(attribute, "NativeLayout", false);
        if (codec is not null && textCodec is not null)
        {
            return Invalid(CustomTypeDiagnosticKind.ConflictingCodecs, Option("TextCodec"), type.Name);
        }

        if (nativeLayout && (codec is not null || textCodec is null))
        {
            return Invalid(CustomTypeDiagnosticKind.NativeLayoutCodec, Option("NativeLayout"), type.Name);
        }

        if (options.Property("NullInputErrorMessage", null) is { } nullMessage && !SqlText.IsText(nullMessage))
        {
            return Invalid(CustomTypeDiagnosticKind.NullInputMessage, Option("NullInputErrorMessage"), type.Name);
        }

        if (type.IsRefLikeType)
        {
            return Invalid(CustomTypeDiagnosticKind.RefLikeType, typeLocation, type.Name);
        }

        if (type.IsStatic)
        {
            return Invalid(CustomTypeDiagnosticKind.StaticType, typeLocation, type.Name);
        }

        if (type.IsAbstract && codec is not null)
        {
            return Invalid(CustomTypeDiagnosticKind.AbstractCodecType, typeLocation, type.Name);
        }

        if (type.IsAbstract &&
            !type.GetAttributes().Any(static item => item.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonDerivedTypeAttribute"))
        {
            return Invalid(CustomTypeDiagnosticKind.AbstractType, typeLocation, type.Name);
        }

        if (type.IsUnboundGenericType || TypeNesting.SelfAndContainers(type).Any(static current => current.IsGenericType))
        {
            return Invalid(CustomTypeDiagnosticKind.GenericType, typeLocation, type.Name);
        }

        if (!Accessible(type))
        {
            return Invalid(CustomTypeDiagnosticKind.InaccessibleType, typeLocation, type.Name);
        }

        if (type.GetAttributes().Any(static item => item.AttributeClass?.ToDisplayString() == "Ankus.PgEnumAttribute"))
        {
            return Invalid(CustomTypeDiagnosticKind.ConflictingTypeKinds, typeLocation, type.Name);
        }

        SerializationModel? serializer = null;
        if (textCodec is not null && ValidateCodec(textCodec, type, "PgTypeTextCodec", Option("TextCodec")) is { } textError)
        {
            return InvalidFailure(textError);
        }

        int nativeSize = 0;
        string nativePadding = string.Empty;
        if (nativeLayout)
        {
            nativeSize = NativeTypeLayout.Validate(type, out CustomTypeValidationFailure? failure, out nativePadding, token);
            if (failure is not null)
            {
                return InvalidFailure(failure);
            }
        }
        else if (codec is null)
        {
            CustomTypeValidationFailure? failure = null;
            serializer = DefaultTypeSerializer.Create(type, out _, metadataFailure, value => failure = value, token);
            if (serializer is null)
            {
                return failure is null ? null : InvalidFailure(failure);
            }
        }
        else if (ValidateCodec(codec, type, "PgTypeCodec", DatumMappingDiagnostics.Argument(attribute, 0, token)) is { } codecError)
        {
            return InvalidFailure(codecError);
        }

        string name = options.Property("Name", SqlText.SnakeCase(type.Name))!;
        string? schema = options.Property("Schema", null);
        AttributeData? inheritedSchema = null;
        for (INamedTypeSymbol? container = type.ContainingType; schema is null && container is not null; container = container.ContainingType)
        {
            AttributeData? inherited = container.GetAttributes().FirstOrDefault(static item => item.AttributeClass?.ToDisplayString() == "Ankus.PgSchemaAttribute");
            if (inherited is not null)
            {
                inheritedSchema = inherited;
                if (!ExactAttributeStrings.TryRead(container, inherited, token, out AttributeStrings? enclosing) || enclosing is null)
                {
                    metadataFailure?.Invoke(AttributeMetadataFailure.Create(container, inherited));
                    return null;
                }

                schema = enclosing.Arguments[0];
                if (schema is null)
                {
                    return Invalid(CustomTypeDiagnosticKind.NullInheritedSchema,
                        FunctionDeclarationDiagnostics.ConstructorArgument(inherited, token), type.Name);
                }
            }
        }

        if (!SqlText.IsIdentifier(name))
        {
            return Invalid(CustomTypeDiagnosticKind.TypeName, Option("Name"), type.Name);
        }

        if (schema is not null && !SqlText.IsIdentifier(schema))
        {
            Location? schemaLocation = inheritedSchema is null ? Option("Schema") :
                FunctionDeclarationDiagnostics.ConstructorArgument(inheritedSchema, token);
            return Invalid(CustomTypeDiagnosticKind.SchemaName, schemaLocation, type.Name);
        }

        return new(type, codec, textCodec, serializer, nativeSize, nativePadding, attribute, name, schema,
            options.Property("NullInputErrorMessage", null));

        Location? Option(string name) => FunctionDeclarationDiagnostics.Option(attribute, name, token);

        CustomTypeDeclaration? InvalidFailure(CustomTypeValidationFailure failure)
            => Invalid(failure.Kind, failure.Location, [.. failure.Arguments]);

        CustomTypeDeclaration? Invalid(CustomTypeDiagnosticKind kind, Location? location, params string[] arguments)
        {
            DiagnosticDescriptor descriptor = CustomTypeDiagnostics.Get(kind);
            diagnostic?.Invoke(descriptor, location ?? typeLocation, arguments);
            report?.Invoke(CustomTypeDiagnostics.Message(kind, arguments));
            return null;
        }
    }

    /// <summary>
    /// Validates direct codec construction and its exact non-null managed contract.
    /// </summary>
    private static CustomTypeValidationFailure? ValidateCodec(INamedTypeSymbol codec, INamedTypeSymbol type, string baseName, Location? location)
    {
        IMethodSymbol? constructor = codec.InstanceConstructors.FirstOrDefault(constructor => constructor.Parameters.Length == 0 &&
            (constructor.DeclaredAccessibility == Accessibility.Public ||
             constructor.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal &&
             codec.ContainingAssembly.GivesAccessTo(type.ContainingAssembly)));
        if (!AccessibleCodec(codec, type.ContainingAssembly) || codec.IsAbstract || codec.IsStatic || constructor is null)
        {
            return new(CustomTypeDiagnosticKind.CodecConstruction, location, new([codec.ToDisplayString()]));
        }

        bool required = false;
        INamedTypeSymbol? contract = null;
        for (INamedTypeSymbol? current = codec; current is not null; current = current.BaseType)
        {
            required |= current.GetMembers().Any(static member => member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true });
            if (current.Name == baseName && current.Arity == 1 && current.ContainingNamespace.ToDisplayString() == "Ankus")
            {
                contract = current;
            }
        }

        if (contract is null || !SymbolEqualityComparer.Default.Equals(contract.TypeArguments[0], type) ||
            type.IsReferenceType && contract.TypeArguments[0].NullableAnnotation == NullableAnnotation.Annotated)
        {
            return new(CustomTypeDiagnosticKind.CodecContract, location,
                new([codec.ToDisplayString(), baseName, type.ToDisplayString()]));
        }

        if (required && !constructor.GetAttributes().Any(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"))
        {
            return new(CustomTypeDiagnosticKind.CodecRequiredMembers, location, new([codec.ToDisplayString()]));
        }

        return null;
    }

    /// <summary>
    /// Accepts statically closed codec types and verifies containing declarations and generic arguments.
    /// </summary>
    private static bool AccessibleCodec(ITypeSymbol type, IAssemblySymbol assembly)
    {
        if (type is IArrayTypeSymbol array)
        {
            return AccessibleCodec(array.ElementType, assembly);
        }

        if (type is not INamedTypeSymbol named || named.IsUnboundGenericType)
        {
            return false;
        }

        if (TypeNesting.SelfAndContainers(named).Any(current => current.IsFileLocal || !(current.DeclaredAccessibility == Accessibility.Public ||
            current.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal &&
            current.ContainingAssembly.GivesAccessTo(assembly)) ||
            current.TypeArguments.Any(argument => !AccessibleCodec(argument, assembly))))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Requires every containing type to be visible to the generated dispatchers.
    /// </summary>
    private static bool Accessible(INamedTypeSymbol type)
        => TypeNesting.SelfAndContainers(type).All(static current => !current.IsGenericType && !current.IsFileLocal &&
            current.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal);

    /// <summary>
    /// Detaches the fully validated storage contract without retaining compiler symbols or attributes.
    /// </summary>
    /// <returns>The immutable custom-type model.</returns>
    internal CustomTypeModel Freeze() => new(Name, Schema, Managed, Symbol, Type.IsValueType,
        codec?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        textCodec?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), serializer, nativeSize, nativePadding,
        BinaryProtocol, Alignment, NullInputErrorMessage);
}
