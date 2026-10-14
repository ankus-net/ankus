using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Builds a closed serialization graph and emits direct member access and constructor calls.
/// </summary>
internal sealed class DefaultTypeSerializer(INamedTypeSymbol root, IAssemblySymbol assembly, Action<AttributeMetadataFailure>? metadataFailure,
    CancellationToken cancellationToken)
{
    private readonly Dictionary<string, SerializationNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<SerializationNode> _ordered = [];

    /// <summary>
    /// Validates every reachable type before emitting a serializer.
    /// </summary>
    internal static SerializationModel? Create(INamedTypeSymbol type, out string? error,
        Action<AttributeMetadataFailure>? metadataFailure = null, Action<CustomTypeValidationFailure>? failure = null,
        CancellationToken cancellationToken = default)
    {
        var serializer = new DefaultTypeSerializer(type, type.ContainingAssembly, metadataFailure, cancellationToken);
        try
        {
            serializer.Add(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
            foreach (SerializationNode contract in serializer._ordered.Where(static node => node.Kind == "polymorphic"))
            {
                if (contract.Variants.Select(static variant => variant.Shape).Concat(contract.BaseShape is { } baseShape ? [baseShape] : [])
                    .Any(shape => shape.Members.Any(member => member.SerializedName == contract.DiscriminatorName)))
                {
                    throw Invalid(CustomTypeDiagnosticKind.DiscriminatorCollision, contract.Type.Locations.FirstOrDefault(),
                        Display(contract.Type), contract.DiscriminatorName);
                }
            }

            error = null;
            return serializer.Freeze();
        }
        catch (ValidationException exception)
        {
            CustomTypeValidationFailure validation = exception.Failure.Location is { IsInSource: true } ? exception.Failure :
                exception.Failure with { Location = type.Locations.FirstOrDefault(static location => location.IsInSource) };
            failure?.Invoke(validation);
            error = exception.Message;
            return null;
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            return null;
        }
    }

    /// <summary>
    /// Reads exact scalar attribute strings from source or compiler-owned metadata.
    /// </summary>
    /// <param name="owner">The declaring type or member.</param>
    /// <param name="attribute">Its selected serialization attribute.</param>
    /// <returns>Exact nullable strings without Roslyn import normalization.</returns>
    private AttributeStrings Strings(ISymbol owner, AttributeData attribute)
    {
        ISymbol declaration = owner;
        while (declaration is IPropertySymbol { OverriddenProperty: { } parent } &&
            !declaration.GetAttributes().Any(candidate => ReferenceEquals(candidate, attribute)))
        {
            declaration = parent;
        }

        if (!ExactAttributeStrings.TryRead(declaration, attribute, cancellationToken, out AttributeStrings? strings) || strings is null)
        {
            metadataFailure?.Invoke(AttributeMetadataFailure.Create(declaration, attribute));
            throw new InvalidOperationException("The exact serialization attribute metadata cannot be read; rebuild its defining assembly.");
        }

        if (strings.Arguments.Values.Concat(strings.Properties.Values).Any(static value => !ExactAttributeStrings.IsUnicode(value)))
        {
            ITypeSymbol type = owner as ITypeSymbol ?? owner.ContainingType!;
            throw Invalid(CustomTypeDiagnosticKind.SerializationUnicode,
                attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? owner.Locations.FirstOrDefault(), Display(type));
        }

        return strings;
    }

    /// <summary>
    /// Detaches the validated graph without retaining symbols or metadata readers.
    /// </summary>
    /// <returns>The exact ordered contracts needed by codec rendering.</returns>
    private SerializationModel Freeze() => new(new(_ordered.Select(static node => new SerializationModel.Node(
        node.Managed, Display(node.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)),
        node.Type.IsReferenceType, node.CanBeNull, node.Index, node.Kind, node.Primitive, node.Element?.Index,
        node.DiscriminatorName, new(node.Variants.Select(static variant => new SerializationModel.Variant(
            variant.Shape.Index, variant.Tag as string, variant.Tag is int number ? number : null))),
        node.BaseShape?.Index, new(node.Members.Select(static member => new SerializationModel.Member(
            member.Name, member.SerializedName, member.Value.Index, member.Writable, member.Required))),
        new(node.EnumMembers), new(node.ConstructorMembers.Select(member => node.Members.IndexOf(member)))))));

    /// <summary>
    /// Creates a node before visiting its children so recursive contracts remain finite.
    /// </summary>
    private SerializationNode Add(ITypeSymbol type, bool objectShape = false, Location? usage = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string managed = Display(type);
        string key = objectShape ? "object:" + managed : managed;
        if (_nodes.TryGetValue(key, out SerializationNode? existing))
        {
            return existing;
        }

        if (_ordered.Count >= 256)
        {
            throw Invalid(CustomTypeDiagnosticKind.SerializationGraphLimit, root.Locations.FirstOrDefault(), Display(root));
        }

        var node = new SerializationNode(type, _ordered.Count);
        _nodes.Add(key, node);
        _ordered.Add(node);
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            node.Kind = "nullable";
            node.Element = Add(nullable.TypeArguments[0], usage: usage);
            return node;
        }

        node.Primitive = type.SpecialType switch
        {
            SpecialType.System_Boolean => "Boolean",
            SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64 => "Int64",
            SpecialType.System_Byte or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64 => "UInt64",
            SpecialType.System_Single => "Single",
            SpecialType.System_Double => "Double",
            SpecialType.System_Decimal => "Decimal",
            SpecialType.System_String => "String",
            SpecialType.System_DateTime => "DateTime",
            _ => FrameworkPrimitive(type),
        };
        if (node.Primitive is not null)
        {
            node.Kind = "primitive";
            return node;
        }

        if (type is IArrayTypeSymbol array && array.Rank == 1 && array.IsSZArray)
        {
            node.Kind = "array";
            node.Element = Add(array.ElementType, usage: usage);
            return node;
        }

        if (type is not INamedTypeSymbol named || named.IsRefLikeType || named.IsStatic || named.IsUnboundGenericType ||
            named.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Enum) || !Accessible(named, assembly))
        {
            throw Invalid(CustomTypeDiagnosticKind.SerializationContract, SourceLocation(type.Locations.FirstOrDefault(), usage), Display(type));
        }

        ValidateAttributes(named);
        if (!objectShape && (Attribute(named, "JsonPolymorphicAttribute") is not null ||
            Attribute(named, "JsonDerivedTypeAttribute") is not null))
        {
            AddPolymorphic(node, named, usage);
            return node;
        }

        if (named.IsAbstract)
        {
            throw Invalid(CustomTypeDiagnosticKind.SerializationAbstract, SourceLocation(named.Locations.FirstOrDefault(), usage), Display(type));
        }

        string definition = named.OriginalDefinition.ToDisplayString();
        if (definition is "System.Collections.Generic.List<T>" or "System.Collections.Generic.Dictionary<TKey, TValue>")
        {
            node.Kind = definition == "System.Collections.Generic.List<T>" ? "list" : "dictionary";
            if (node.Kind == "dictionary" && (named.TypeArguments[0].SpecialType != SpecialType.System_String ||
                named.TypeArguments[0].NullableAnnotation == NullableAnnotation.Annotated))
            {
                throw Invalid(CustomTypeDiagnosticKind.DictionaryKey, SourceLocation(named.Locations.FirstOrDefault(), usage), Display(type));
            }

            node.Element = Add(named.TypeArguments[named.TypeArguments.Length - 1], usage: usage);
            return node;
        }

        if (named.TypeKind == TypeKind.Enum)
        {
            node.Kind = "enum";
            var names = new HashSet<string>(StringComparer.Ordinal);
            var values = new HashSet<object>();
            foreach (IFieldSymbol field in named.GetMembers().OfType<IFieldSymbol>().Where(static item => item.HasConstantValue))
            {
                ValidateAttributes(field);
                AttributeData? rename = Attribute(field, "JsonStringEnumMemberNameAttribute");
                string name = rename is null ? field.Name : Strings(field, rename).Arguments[0] ?? field.Name;
                if (!names.Add(name) || !values.Add(field.ConstantValue!))
                {
                    throw Invalid(CustomTypeDiagnosticKind.EnumIdentity, SourceLocation(field.Locations.FirstOrDefault(), usage), Display(type));
                }

                node.EnumMembers.Add((field.Name, name));
            }

            return node;
        }

        if (named.SpecialType != SpecialType.None)
        {
            throw Invalid(CustomTypeDiagnosticKind.FrameworkContract, SourceLocation(named.Locations.FirstOrDefault(), usage), Display(type));
        }

        node.Kind = "object";
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        var memberSymbols = new Dictionary<SerializationMember, ISymbol>();
        bool ignoredRequired = false;
        foreach (ISymbol member in ObjectMembers(named, usage))
        {
            if (member.IsStatic)
            {
                continue;
            }

            AttributeData? ignore = Attribute(member, "JsonIgnoreAttribute");
            if (ignore is not null)
            {
                int condition = AttributeValues.Get(ignore, "Condition", 1);
                if (condition == 1)
                {
                    ignoredRequired |= member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true };
                    continue;
                }

                if (condition != 0)
                {
                    throw Invalid(CustomTypeDiagnosticKind.ConditionalIgnore,
                        SourceLocation(ignore.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? member.Locations.FirstOrDefault(), usage),
                        member.Name);
                }
            }

            ValidateAttributes(member);
            if (member.DeclaredAccessibility != Accessibility.Public || member.IsImplicitlyDeclared && member is IFieldSymbol)
            {
                ignoredRequired |= member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true };
                continue;
            }

            ITypeSymbol memberType;
            bool writable;
            bool required;
            if (member is IPropertySymbol property)
            {
                if (property.IsIndexer)
                {
                    throw Invalid(CustomTypeDiagnosticKind.Indexer, SourceLocation(property.Locations.FirstOrDefault(), usage), property.Name);
                }

                memberType = property.Type;
                writable = property.SetMethod is { } setter && IsVisible(setter, assembly);
                required = property.IsRequired;
                if (property.GetMethod?.DeclaredAccessibility != Accessibility.Public)
                {
                    throw Invalid(CustomTypeDiagnosticKind.PublicGetter, SourceLocation(property.Locations.FirstOrDefault(), usage), property.Name);
                }
            }
            else if (member is IFieldSymbol field)
            {
                memberType = field.Type;
                writable = !field.IsReadOnly;
                required = field.IsRequired;
            }
            else
            {
                continue;
            }

            AttributeData? rename = Attribute(member, "JsonPropertyNameAttribute");
            string name = rename is null ? member.Name : Strings(member, rename).Arguments[0] ??
                throw Invalid(CustomTypeDiagnosticKind.NullMemberName,
                    SourceLocation(rename.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? member.Locations.FirstOrDefault(), usage),
                    member.Name);
            if (!memberNames.Add(name))
            {
                throw Invalid(CustomTypeDiagnosticKind.DuplicateMemberName,
                    SourceLocation(rename?.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? member.Locations.FirstOrDefault(), usage),
                    name, Display(type));
            }

            Location? memberLocation = SourceLocation(member.Locations.FirstOrDefault(), usage);
            var serialized = new SerializationMember(member.Name, name, Add(memberType, usage: memberLocation), writable,
                required || Attribute(member, "JsonRequiredAttribute") is not null, required);
            node.Members.Add(serialized);
            memberSymbols.Add(serialized, member);
        }

        IMethodSymbol[] constructors = [.. named.InstanceConstructors.Where(constructor => IsVisible(constructor, assembly))];
        IMethodSymbol[] marked = [.. named.InstanceConstructors.Where(static constructor => Attribute(constructor, "JsonConstructorAttribute") is not null)];
        node.Constructor = marked.Length switch
        {
            0 => constructors.FirstOrDefault(static constructor => constructor.Parameters.Length == 0) ??
                (constructors.Length == 1 ? constructors[0] : null),
            1 when IsVisible(marked[0], assembly) => marked[0],
            _ => null,
        };
        if (node.Constructor is null)
        {
            throw Invalid(CustomTypeDiagnosticKind.SerializationConstructor, SourceLocation(named.Locations.FirstOrDefault(), usage), Display(type));
        }

        foreach (IParameterSymbol parameter in node.Constructor.Parameters)
        {
            SerializationMember[] matches = [.. node.Members.Where(member => string.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase) &&
                SymbolEqualityComparer.IncludeNullability.Equals(member.Value.Type, parameter.Type))];
            if (parameter.RefKind != RefKind.None || matches.Length != 1 || node.ConstructorMembers.Contains(matches[0]))
            {
                throw Invalid(CustomTypeDiagnosticKind.ConstructorParameter, SourceLocation(parameter.Locations.FirstOrDefault(), usage), parameter.Name);
            }

            node.ConstructorMembers.Add(matches[0]);
        }

        if (node.Members.FirstOrDefault(member => !member.Writable && !node.ConstructorMembers.Contains(member)) is { } readOnly)
        {
            throw Invalid(CustomTypeDiagnosticKind.ReadOnlyMember,
                SourceLocation(memberSymbols[readOnly].Locations.FirstOrDefault(), usage), readOnly.Name);
        }

        if ((ignoredRequired || node.ConstructorMembers.Any(static member => member.InitializerRequired)) &&
            !node.Constructor.GetAttributes().Any(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"))
        {
            throw Invalid(CustomTypeDiagnosticKind.RequiredMembers, SourceLocation(node.Constructor.Locations.FirstOrDefault(), usage), Display(type));
        }

        return node;
    }

    /// <summary>
    /// Resolves explicitly tagged concrete variants without permitting identity-losing fallback.
    /// </summary>
    private void AddPolymorphic(SerializationNode node, INamedTypeSymbol type, Location? usage)
    {
        if (type.TypeKind != TypeKind.Class)
        {
            throw Invalid(CustomTypeDiagnosticKind.PolymorphicClass, SourceLocation(type.Locations.FirstOrDefault(), usage), Display(type));
        }

        node.Kind = "polymorphic";
        AttributeData? configuration = Attribute(type, "JsonPolymorphicAttribute");
        if (configuration is not null)
        {
            node.DiscriminatorName = Strings(type, configuration).Property("TypeDiscriminatorPropertyName", "$type")!;
            if (AttributeValues.Get(configuration, "IgnoreUnrecognizedTypeDiscriminators", false) ||
                AttributeValues.Get(configuration, "UnknownDerivedTypeHandling", 0) != 0)
            {
                Location? option = AttributeValues.Get(configuration, "IgnoreUnrecognizedTypeDiscriminators", false)
                    ? FunctionDeclarationDiagnostics.Option(configuration, "IgnoreUnrecognizedTypeDiscriminators", cancellationToken)
                    : FunctionDeclarationDiagnostics.Option(configuration, "UnknownDerivedTypeHandling", cancellationToken);
                throw Invalid(CustomTypeDiagnosticKind.PolymorphicFallback, SourceLocation(option ?? type.Locations.FirstOrDefault(), usage), Display(type));
            }
        }

        var tags = new HashSet<object>();
        var types = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (AttributeData registration in type.GetAttributes().Where(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonDerivedTypeAttribute"))
        {
            AttributeStrings exact = Strings(type, registration);
            if (registration.ConstructorArguments.Length != 2 ||
                registration.ConstructorArguments[0].Value is not INamedTypeSymbol derived ||
                !registration.ConstructorArguments[1].IsNull && registration.ConstructorArguments[1].Value is not (string or int))
            {
                throw Invalid(CustomTypeDiagnosticKind.VariantRegistration,
                    SourceLocation(registration.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? type.Locations.FirstOrDefault(), usage),
                    Display(type));
            }

            object tag = registration.ConstructorArguments[1].IsNull ?
                throw Invalid(CustomTypeDiagnosticKind.NullDiscriminator,
                    SourceLocation(DatumMappingDiagnostics.Argument(registration, 1, cancellationToken), usage), Display(type)) :
                registration.ConstructorArguments[1].Value is string ? exact.Arguments[1] ??
                    throw Invalid(CustomTypeDiagnosticKind.NullDiscriminator,
                        SourceLocation(DatumMappingDiagnostics.Argument(registration, 1, cancellationToken), usage), Display(type)) :
                    registration.ConstructorArguments[1].Value!;
            bool related = false;
            for (INamedTypeSymbol? current = derived; current is not null; current = current.BaseType)
            {
                related |= SymbolEqualityComparer.Default.Equals(current, type);
            }

            if (!related || derived.IsAbstract || derived.IsStatic || derived.IsUnboundGenericType || !Accessible(derived, assembly))
            {
                throw Invalid(CustomTypeDiagnosticKind.VariantType,
                    SourceLocation(DatumMappingDiagnostics.Argument(registration, 0, cancellationToken), usage), Display(derived), Display(type));
            }

            if (!types.Add(derived) || !tags.Add(tag))
            {
                throw Invalid(CustomTypeDiagnosticKind.DuplicateVariant,
                    SourceLocation(registration.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? type.Locations.FirstOrDefault(), usage),
                    Display(type));
            }

            node.Variants.Add((Add(derived.WithNullableAnnotation(NullableAnnotation.NotAnnotated), true,
                SourceLocation(DatumMappingDiagnostics.Argument(registration, 0, cancellationToken), usage)), tag));
        }

        if (node.Variants.Count == 0)
        {
            throw Invalid(CustomTypeDiagnosticKind.MissingVariants,
                SourceLocation(configuration?.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? type.Locations.FirstOrDefault(), usage),
                Display(type));
        }

        if (!type.IsAbstract)
        {
            node.BaseShape = Add(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated), true,
                SourceLocation(type.Locations.FirstOrDefault(), usage));
        }
    }

    /// <summary>
    /// Names the framework value types that System.Text.Json writes as strings and generated storage supports directly.
    /// </summary>
    /// <param name="type">The member or element type.</param>
    /// <returns>The reader and writer operation, or null for another type.</returns>
    private static string? FrameworkPrimitive(ITypeSymbol type)
        => type is INamedTypeSymbol { Arity: 0, ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } named &&
            named.Name is "Guid" or "DateOnly" or "TimeOnly" or "TimeSpan" or "DateTimeOffset"
            ? named.Name
            : null;

    /// <summary>
    /// Includes inherited state once, honoring overrides and rejecting hidden serialized members.
    /// </summary>
    private IEnumerable<ISymbol> ObjectMembers(INamedTypeSymbol type, Location? usage)
    {
        var hiddenNames = new HashSet<string>(StringComparer.Ordinal);
        var overridden = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        for (INamedTypeSymbol? current = type; current is not null &&
            current.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); current = current.BaseType)
        {
            string namespaceName = current.ContainingNamespace.ToDisplayString();
            if (current.SpecialType != SpecialType.None || namespaceName == "System" || namespaceName.StartsWith("System.", StringComparison.Ordinal))
            {
                throw Invalid(CustomTypeDiagnosticKind.FrameworkContract,
                    SourceLocation(current.Locations.FirstOrDefault(), usage ?? type.Locations.FirstOrDefault()), Display(current));
            }

            ValidateAttributes(current);
            foreach (ISymbol member in current.GetMembers())
            {
                if (overridden.Contains(member))
                {
                    continue;
                }

                if (member is IPropertySymbol property)
                {
                    bool ignored = Attribute(property, "JsonIgnoreAttribute") is { } propertyIgnore &&
                        AttributeValues.Get(propertyIgnore, "Condition", 1) == 1;
                    for (IPropertySymbol? parent = property.OverriddenProperty; parent is not null; parent = parent.OverriddenProperty)
                    {
                        if (!ignored)
                        {
                            ValidateAttributes(parent);
                        }

                        overridden.Add(parent);
                    }
                }

                if (!member.IsStatic && member.DeclaredAccessibility == Accessibility.Public && member is IPropertySymbol or IFieldSymbol &&
                    hiddenNames.Contains(member.Name) && !(Attribute(member, "JsonIgnoreAttribute") is { } ignore &&
                    AttributeValues.Get(ignore, "Condition", 1) == 1))
                {
                    throw Invalid(CustomTypeDiagnosticKind.HiddenMember,
                        SourceLocation(member.Locations.FirstOrDefault(), usage ?? type.Locations.FirstOrDefault()), member.Name);
                }

                yield return member;
            }

            hiddenNames.UnionWith(current.GetMembers().Select(static member => member.Name));
        }
    }

    /// <summary>
    /// Finds standard serialization metadata without instantiating attributes.
    /// </summary>
    private static AttributeData? Attribute(ISymbol symbol, string name) => symbol.GetAttributes().FirstOrDefault(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization." + name) ??
        (symbol is IPropertySymbol { OverriddenProperty: { } parent } ? Attribute(parent, name) : null);

    /// <summary>
    /// Prevents silently ignoring customization that would change persisted meaning.
    /// </summary>
    private void ValidateAttributes(ISymbol symbol)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            for (INamedTypeSymbol? type = attribute.AttributeClass; type is not null; type = type.BaseType)
            {
                if (type.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization")
                {
                    if (type.Name is "JsonPropertyNameAttribute" or "JsonIgnoreAttribute" or "JsonConstructorAttribute" or
                        "JsonRequiredAttribute" or "JsonStringEnumMemberNameAttribute" or "JsonPolymorphicAttribute" or "JsonDerivedTypeAttribute")
                    {
                        break;
                    }

                    throw Invalid(CustomTypeDiagnosticKind.SerializationAttribute,
                        attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? symbol.Locations.FirstOrDefault(),
                        type.Name, symbol.Name);
                }
            }
        }
    }

    /// <summary>
    /// Requires every containing declaration to be visible to generated code.
    /// </summary>
    private static bool Accessible(INamedTypeSymbol type, IAssemblySymbol assembly)
        => !TypeNesting.SelfAndContainers(type).Any(current => current.IsFileLocal ||
            current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal) ||
            current.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal &&
            !current.ContainingAssembly.GivesAccessTo(assembly));

    /// <summary>
    /// Accepts public and assembly-visible constructor and setter access.
    /// </summary>
    private static bool IsVisible(IMethodSymbol method, IAssemblySymbol assembly) => method.DeclaredAccessibility == Accessibility.Public ||
        method.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal &&
        method.ContainingAssembly.GivesAccessTo(assembly);

    /// <summary>
    /// Keeps diagnostics on the authored use when a traversed contract comes from metadata.
    /// </summary>
    private Location? SourceLocation(Location? candidate, Location? usage)
        => candidate is { IsInSource: true } ? candidate : usage is { IsInSource: true } ? usage :
            root.Locations.FirstOrDefault(static location => location.IsInSource);

    /// <summary>
    /// Creates a local unwind carrying a closed diagnostic rather than formatted free text.
    /// </summary>
    private static ValidationException Invalid(CustomTypeDiagnosticKind kind, Location? location, params string[] arguments)
        => new(new(kind, location, new(arguments)));

    /// <summary>
    /// Unwinds recursive serializer discovery while preserving the exact failed contract.
    /// </summary>
    private sealed class ValidationException(CustomTypeValidationFailure failure) : Exception(CustomTypeDiagnostics.Message(failure.Kind, [.. failure.Arguments]))
    {
        /// <summary>
        /// Gets the diagnostic to report at the custom-type boundary.
        /// </summary>
        internal CustomTypeValidationFailure Failure { get; } = failure;
    }

    /// <summary>
    /// Preserves nullable annotations throughout nested generic and array types.
    /// </summary>
    internal static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
        SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
}
