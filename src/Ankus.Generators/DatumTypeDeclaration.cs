using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Validates a closed managed datum converter independently of generated storage codecs.
/// </summary>
internal sealed class DatumTypeDeclaration(INamedTypeSymbol type, INamedTypeSymbol converter, string name, string? schema,
    bool external, bool canRead, bool canWrite, bool inferred, DatumTypeDeclaration? rangeBound = null, Location? converterLocation = null)
{
    /// <summary>
    /// Gets the exact managed root identity.
    /// </summary>
    internal INamedTypeSymbol Type { get; } = type;

    /// <summary>
    /// Gets the catalog leaf name.
    /// </summary>
    internal string Name { get; } = name;

    /// <summary>
    /// Gets the fixed schema or null for the owning extension schema.
    /// </summary>
    internal string? Schema { get; } = schema;

    /// <summary>
    /// Gets whether the type exists independently of this extension's SQL graph.
    /// </summary>
    internal bool External { get; } = external;

    /// <summary>
    /// Gets whether the converter implements the exact input contract.
    /// </summary>
    internal bool CanRead { get; } = canRead;

    /// <summary>
    /// Gets whether the converter implements the exact output contract.
    /// </summary>
    internal bool CanWrite { get; } = canWrite;

    /// <summary>
    /// Gets whether compiler constraint validation is required for an inferred converter construction.
    /// </summary>
    internal bool HasInferredConverter { get; } = inferred;

    /// <summary>
    /// Gets the current converter expression or source usage for constraint diagnostics.
    /// </summary>
    internal Location? ConverterLocation { get; } = converterLocation;

    /// <summary>
    /// Gets the scalar conversion shared by a synthetic mapped range contract.
    /// </summary>
    internal DatumTypeDeclaration? RangeBound { get; } = rangeBound;

    /// <summary>
    /// Gets the source format preserving nullable arguments inside a constructed managed identity.
    /// </summary>
    internal static SymbolDisplayFormat ManagedFormat { get; } = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
        SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    /// Gets the globally qualified managed name.
    /// </summary>
    internal string Managed => Type.ToDisplayString(ManagedFormat);

    /// <summary>
    /// Gets the quoted SQL identity without inferring a schema from the function placement.
    /// </summary>
    internal string Sql => new SqlTypeReference(Name, Schema).Sql;

    /// <summary>
    /// Finds mapping metadata without constructing user code.
    /// </summary>
    internal static bool IsMapped(INamedTypeSymbol type) => type.GetAttributes().Any(static attribute =>
        attribute.AttributeClass?.ToDisplayString() == "Ankus.PgDatumTypeAttribute");

    /// <summary>
    /// Distinguishes a finite constructed root from an open annotated generic definition.
    /// </summary>
    internal static bool IsClosed(INamedTypeSymbol type) => !ContainsTypeParameter(type);

    /// <summary>
    /// Resolves an attributed type and optionally reports its invalid contract.
    /// </summary>
    internal static DatumTypeDeclaration? Create(INamedTypeSymbol type, IAssemblySymbol? assembly = null,
        GeneratorDiagnostics? context = null, Location? usage = null, Compilation? compilation = null)
    {
        type = (INamedTypeSymbol)type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        if (RangeTypeDeclaration.Bound(type) is { } bound)
        {
            DatumTypeDeclaration? scalar = Create(bound, assembly, context, usage, compilation);
            return scalar is not null && RangeTypeDeclaration.TryCreate(scalar, out DatumTypeDeclaration? range, context, usage, compilation) ? range : null;
        }

        AttributeData[]? attributes = Declarations(type, context, usage, compilation);
        if (attributes is null || attributes.Length == 0)
        {
            return null;
        }

        assembly ??= type.ContainingAssembly;
        Location? root = DatumMappingDiagnostics.CurrentLocation(type.Locations.FirstOrDefault(static item => item.IsInSource), compilation, usage);
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Enum) || type.IsStatic || type.IsAbstract ||
            type.IsRefLikeType || !IsClosed(type))
        {
            return Invalid(DatumMappingDiagnostics.RootShape, root);
        }

        if (!Accessible(type, assembly))
        {
            return Invalid(DatumMappingDiagnostics.RootAccess, root, type.ToDisplayString());
        }

        AttributeData? storage = type.GetAttributes().FirstOrDefault(static item =>
            item.AttributeClass?.ToDisplayString() is "Ankus.PgTypeAttribute" or "Ankus.PgEnumAttribute");
        if (storage is not null)
        {
            return Invalid(DatumMappingDiagnostics.StorageConflict,
                storage.ApplicationSyntaxReference?.GetSyntax(context?.CancellationToken ?? default).GetLocation() ?? root, type.Name);
        }

        AttributeData? attribute = attributes.FirstOrDefault(item => item.ConstructorArguments.Length == 3 &&
            SymbolEqualityComparer.Default.Equals(item.ConstructorArguments[0].Value as ITypeSymbol, type)) ??
            attributes.FirstOrDefault(static item => item.ConstructorArguments.Length == 2);
        if (attribute is null)
        {
            return Invalid(DatumMappingDiagnostics.MissingMapping, usage ?? root, type.ToDisplayString());
        }

        int offset = attribute.ConstructorArguments.Length == 3 ? 1 : 0;
        string? name = attribute.ConstructorArguments[offset].Value as string;
        string? schema = AttributeValues.Get<string?>(attribute, "Schema", null);
        CancellationToken cancellationToken = context?.CancellationToken ?? default;
        if (compilation is not null && !MappedIdentityMetadata.TryRead(type, attribute, compilation, cancellationToken, out name, out schema))
        {
            return Invalid(DatumMappingDiagnostics.Metadata, usage ?? root);
        }

        Location? converterSource = DatumMappingDiagnostics.CurrentLocation(
            DatumMappingDiagnostics.Argument(attribute, offset + 1, cancellationToken), compilation, usage ?? root);
        if (!SqlText.IsIdentifier(name))
        {
            return Invalid(DatumMappingDiagnostics.Name, DatumMappingDiagnostics.Argument(attribute, offset, cancellationToken));
        }

        if (schema is not null && !SqlText.IsIdentifier(schema))
        {
            return Invalid(DatumMappingDiagnostics.Schema, FunctionDeclarationDiagnostics.Option(attribute, "Schema", cancellationToken));
        }

        int origin = AttributeValues.Get(attribute, "Origin", 0);
        if (origin is not (0 or 1))
        {
            return Invalid(DatumMappingDiagnostics.Origin, FunctionDeclarationDiagnostics.Option(attribute, "Origin", cancellationToken));
        }

        if (origin == 1 && schema is null)
        {
            return Invalid(DatumMappingDiagnostics.ExternalSchema, FunctionDeclarationDiagnostics.Option(attribute, "Origin", cancellationToken));
        }

        if (attribute.ConstructorArguments[offset + 1].Value is not INamedTypeSymbol converter)
        {
            return Invalid(DatumMappingDiagnostics.ConverterShape, converterSource);
        }

        bool inferred = converter.IsUnboundGenericType;
        if (inferred)
        {
            INamedTypeSymbol? constructed = DatumConverterTemplate.Close(converter, type, out DiagnosticDescriptor? error);
            if (constructed is null)
            {
                return Invalid(error!, converterSource);
            }

            converter = constructed;
        }

        if (converter.TypeKind is not (TypeKind.Class or TypeKind.Struct) || converter.IsAbstract || converter.IsStatic ||
            converter.IsRefLikeType || !IsClosed(converter))
        {
            return Invalid(DatumMappingDiagnostics.ConverterShape, converterSource);
        }

        if (!Accessible(converter, assembly))
        {
            return Invalid(DatumMappingDiagnostics.ConverterAccess, converterSource, converter.ToDisplayString());
        }

        IMethodSymbol? constructor = converter.InstanceConstructors.FirstOrDefault(item => item.Parameters.Length == 0 &&
            IsAccessible(item, assembly));
        if (constructor is null)
        {
            return Invalid(DatumMappingDiagnostics.Constructor, converterSource);
        }

        INamedTypeSymbol[] contracts = [.. converter.AllInterfaces.Where(static item => item.Arity == 1 &&
            item.ContainingNamespace.ToDisplayString() == "Ankus" && item.Name is "IPgDatumReader" or "IPgDatumWriter")
            .Where(item => SymbolEqualityComparer.Default.Equals(item.TypeArguments[0], type))];
        if (contracts.Length == 0 || type.IsReferenceType && contracts.Any(static item => item.TypeArguments[0].NullableAnnotation == NullableAnnotation.Annotated))
        {
            return Invalid(DatumMappingDiagnostics.Contract, converterSource, type.ToDisplayString());
        }

        bool required = false;
        for (INamedTypeSymbol? current = converter; current is not null; current = current.BaseType)
        {
            required |= current.GetMembers().Any(static member => member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true });
        }

        if (required && !constructor.GetAttributes().Any(static item =>
            item.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"))
        {
            return Invalid(DatumMappingDiagnostics.RequiredMembers, converterSource);
        }

        return new(type, converter, name!, schema, origin == 1,
            contracts.Any(static item => item.Name == "IPgDatumReader"), contracts.Any(static item => item.Name == "IPgDatumWriter"), inferred,
            converterLocation: converterSource);

        DatumTypeDeclaration? Invalid(DiagnosticDescriptor descriptor, Location? location, params string[] arguments)
        {
            if (context is { } output)
            {
                output.Report(descriptor, DatumMappingDiagnostics.CurrentLocation(location, compilation, usage ?? root), arguments);
            }

            return null;
        }
    }

    /// <summary>
    /// Collects local, signature-referenced and explicitly provided closed mappings before emitting any artifacts.
    /// </summary>
    internal static List<DatumTypeDeclaration>? Discover(Compilation compilation, ImmutableArray<INamedTypeSymbol> local,
        ImmutableArray<INamedTypeSymbol> rangeTypes,
        ImmutableArray<IMethodSymbol> methods, ImmutableArray<INamedTypeSymbol> aggregates, ImmutableArray<AttributeData> attributes,
        GeneratorDiagnostics context)
    {
        var candidates = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.IncludeNullability);
        var requestedRanges = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.IncludeNullability);
        var usages = new Dictionary<INamedTypeSymbol, Location>(SymbolEqualityComparer.Default);
        foreach (INamedTypeSymbol type in rangeTypes.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
        {
            AttributeData[]? declared = RangeTypeDeclaration.Declarations(type, context, compilation: compilation);
            if (declared is null)
            {
                return null;
            }

            if (IsClosed(type))
            {
                candidates.Add(type);
            }

            foreach (AttributeData attribute in declared.Where(static item => item.ConstructorArguments.Length == 2))
            {
                candidates.Add((INamedTypeSymbol)attribute.ConstructorArguments[0].Value!);
            }
        }

        foreach (INamedTypeSymbol type in local.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
        {
            AttributeData[]? declared = Declarations(type, context, compilation: compilation);
            if (declared is null)
            {
                return null;
            }

            if (IsClosed(type))
            {
                candidates.Add(type);
            }

            foreach (AttributeData attribute in declared.Where(static item => item.ConstructorArguments.Length == 3))
            {
                candidates.Add((INamedTypeSymbol)attribute.ConstructorArguments[0].Value!);
            }
        }

        IMethodSymbol[] signatures = [.. methods.Concat(aggregates.SelectMany(AggregateDeclaration.SelectedMethods))
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)];
        foreach (IMethodSymbol method in signatures)
        {
            Collect(method.ReturnType, DatumMappingDiagnostics.Slot(method, context.CancellationToken));
            foreach (IParameterSymbol parameter in method.Parameters)
            {
                Collect(parameter.Type, DatumMappingDiagnostics.Slot(parameter, context.CancellationToken));
            }
        }

        foreach (AttributeData attribute in attributes)
        {
            if (!(attribute.AttributeClass?.ToDisplayString() == "Ankus.PgSqlTypeProviderAttribute" &&
                attribute.ConstructorArguments.Length == 2 && attribute.ConstructorArguments[1].Value is ITypeSymbol supplied))
            {
                continue;
            }

            Collect(supplied, DatumMappingDiagnostics.Argument(attribute, 1, context.CancellationToken));
        }

        bool valid = true;
        var invalidRangeBounds = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var declarations = new List<DatumTypeDeclaration>();
        foreach (INamedTypeSymbol type in candidates.OrderBy(static item => item.ToDisplayString(), StringComparer.Ordinal))
        {
            usages.TryGetValue(type, out Location? usage);
            DatumTypeDeclaration? declaration = Create(type, compilation.Assembly, context, usage, compilation);
            if (declaration is null)
            {
                invalidRangeBounds.Add(type);
                valid = false;
            }
            else if (!Resolves(type) || !Resolves(declaration.Converter))
            {
                invalidRangeBounds.Add(type);
                context.Report(DatumMappingDiagnostics.GlobalIdentity, declaration.ConverterLocation ?? usage ??
                    type.Locations.FirstOrDefault(static item => item.IsInSource));
                valid = false;
            }
            else
            {
                declarations.Add(declaration);
            }
        }

        valid &= DatumConverterTemplate.Validate(compilation, declarations, context);
        foreach (DatumTypeDeclaration scalar in declarations.ToArray())
        {
            usages.TryGetValue(scalar.Type, out Location? usage);
            if (!RangeTypeDeclaration.TryCreate(scalar, out DatumTypeDeclaration? range, context, usage, compilation))
            {
                invalidRangeBounds.Add(scalar.Type);
                valid = false;
            }
            else if (range is not null)
            {
                if (!Resolves(range.Type))
                {
                    context.Report(RangeMappingDiagnostics.GlobalIdentity, usage ?? scalar.Type.Locations.FirstOrDefault(static item => item.IsInSource),
                        range.Type.ToDisplayString());
                    valid = false;
                }
                else
                {
                    declarations.Add(range);
                }
            }
        }

        foreach (INamedTypeSymbol requested in requestedRanges.Where(requested => !invalidRangeBounds.Contains(RangeTypeDeclaration.Bound(requested)!) &&
                !declarations.Any(item => SymbolEqualityComparer.Default.Equals(item.Type, requested))))
        {
            usages.TryGetValue(requested, out Location? usage);
            context.Report(RangeMappingDiagnostics.MissingSelection, usage ??
                RangeTypeDeclaration.Bound(requested)!.Locations.FirstOrDefault(static item => item.IsInSource), requested.ToDisplayString());
            valid = false;
        }

        foreach (IMethodSymbol method in signatures)
        {
            foreach (IParameterSymbol parameter in method.Parameters)
            {
                ValidateSlot(parameter.Type, parameter.GetAttributes(), parameter, read: true);
            }

            ITypeSymbol result = method.ReturnType;
            if (SetResult.IsSequence(result))
            {
                result = ((INamedTypeSymbol)result).TypeArguments[0];
                if (result is INamedTypeSymbol { IsTupleType: true } tuple)
                {
                    AttributeData? namesAttribute = method.GetReturnTypeAttributes().FirstOrDefault(static attribute =>
                        attribute.AttributeClass?.ToDisplayString() == "Ankus.PgColumnNamesAttribute");
                    string[]? columnNames = namesAttribute is { ConstructorArguments.Length: 1 } &&
                        namesAttribute.ConstructorArguments[0] is { Kind: TypedConstantKind.Array, IsNull: false } names
                        ? [.. names.Values.Select(static item => item.Value as string ?? string.Empty)] : null;
                    for (int index = 0; index < tuple.TupleElements.Length; index++)
                    {
                        IFieldSymbol field = tuple.TupleElements[index];
                        string column = columnNames is not null && index < columnNames.Length ? columnNames[index] : SqlText.SnakeCase(field.Name);
                        ImmutableArray<AttributeData> bindings = [.. method.GetReturnTypeAttributes().Where(attribute =>
                            AttributeValues.Get<string?>(attribute, "Column", null) is { } target ? target == column :
                            !tuple.TupleElements.Any(element => attribute.AttributeClass?.ToDisplayString() switch
                            {
                                "Ankus.PgSqlTypeAttribute" => FunctionType.Create(element.Type)?.IsRaw == true,
                                "Ankus.PgCompositeTypeAttribute" => FunctionType.Create(element.Type)?.IsComposite == true,
                                _ => false,
                            }))];
                        ValidateSlot(field.Type, bindings, method, read: false);
                    }

                    continue;
                }

                if (result is INamedTypeSymbol { Name: "ValueTuple", Arity: 1 } single &&
                    single.ContainingNamespace.ToDisplayString() == "System")
                {
                    result = single.TypeArguments[0];
                }
            }

            ValidateSlot(result, method.GetReturnTypeAttributes(), method, read: false);
        }

        return valid ? [.. declarations.GroupBy(static declaration => declaration.Type, SymbolEqualityComparer.Default)
            .Select(static group => group.First())] : null;

        void Collect(ITypeSymbol type, Location? location)
        {
            if (IsManagedState(type))
            {
                return;
            }

            if (type is IArrayTypeSymbol array)
            {
                Collect(array.ElementType, location);
            }
            else if (type is INamedTypeSymbol named)
            {
                if (location is not null && !usages.ContainsKey(named))
                {
                    usages.Add(named, location);
                }

                if (RangeTypeDeclaration.Bound(named) is not null)
                {
                    requestedRanges.Add(named);
                }

                if (IsMapped(named))
                {
                    candidates.Add(named);
                    return;
                }

                foreach (ITypeSymbol argument in named.TypeArguments)
                {
                    Collect(argument, location);
                }
            }
        }

        bool Resolves(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array)
            {
                return Resolves(array.ElementType);
            }

            if (type is not INamedTypeSymbol named)
            {
                return false;
            }

            string metadata = MetadataTypeName.Create(named);
            return SymbolEqualityComparer.Default.Equals(compilation.GetTypeByMetadataName(metadata), named.OriginalDefinition) &&
                named.TypeArguments.All(Resolves) && (named.ContainingType is null || Resolves(named.ContainingType));
        }

        void ValidateSlot(ITypeSymbol slot, ImmutableArray<AttributeData> bindings, ISymbol owner, bool read)
        {
            if (IsManagedState(slot))
            {
                return;
            }

            bool borrowedArray = slot is INamedTypeSymbol { Name: "PgArrayView", Arity: 1 } borrowed &&
                borrowed.ContainingNamespace.ToDisplayString() == "Ankus";
            slot = slot switch
            {
                IArrayTypeSymbol { Rank: 1, IsSZArray: true } array => array.ElementType,
                INamedTypeSymbol { Name: "PgArray" or "PgArrayView", Arity: 1 } array when array.ContainingNamespace.ToDisplayString() == "Ankus"
                    => array.TypeArguments[0],
                _ => slot,
            };
            if (slot is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } optional)
            {
                slot = optional.TypeArguments[0];
            }

            if (slot is INamedTypeSymbol named && (IsMapped(named) || RangeTypeDeclaration.Bound(named) is not null))
            {
                DatumTypeDeclaration? declaration = declarations.FirstOrDefault(item => SymbolEqualityComparer.Default.Equals(item.Type, named));
                AttributeData? binding = bindings.FirstOrDefault(static item =>
                    item.AttributeClass?.ToDisplayString() is "Ankus.PgSqlTypeAttribute" or "Ankus.PgCompositeTypeAttribute");
                if (binding is not null)
                {
                    context.Report(DatumMappingDiagnostics.SlotOverride,
                        binding.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ??
                        DatumMappingDiagnostics.Slot(owner, context.CancellationToken));
                    valid = false;
                }

                if (declaration is not null && (read ? !declaration.CanRead : !borrowedArray && !declaration.CanWrite))
                {
                    context.Report(read ? DatumMappingDiagnostics.Reader : DatumMappingDiagnostics.Writer,
                        DatumMappingDiagnostics.Slot(owner, context.CancellationToken), declaration.Managed);
                    valid = false;
                }
            }
            else if (ContainsMapping(slot))
            {
                context.Report(DatumMappingDiagnostics.Container, DatumMappingDiagnostics.Slot(owner, context.CancellationToken));
                valid = false;
            }
        }
    }

    /// <summary>
    /// Gets the statically constructible converter identity.
    /// </summary>
    internal INamedTypeSymbol Converter { get; } = converter;

    /// <summary>
    /// Validates deterministic exact and default declarations without instantiating open generic roots.
    /// </summary>
    private static AttributeData[]? Declarations(INamedTypeSymbol type, GeneratorDiagnostics? context, Location? usage = null,
        Compilation? compilation = null)
    {
        AttributeData[] attributes = [.. type.GetAttributes().Where(static item =>
            item.AttributeClass?.ToDisplayString() == "Ankus.PgDatumTypeAttribute")];
        var targets = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        bool hasDefault = false;
        foreach (AttributeData attribute in attributes)
        {
            if (attribute.ConstructorArguments.IsEmpty && type.DeclaringSyntaxReferences.Length == 0)
            {
                return Invalid(DatumMappingDiagnostics.Metadata, attribute);
            }

            if (attribute.ConstructorArguments.Length == 2)
            {
                if (hasDefault)
                {
                    return Invalid(DatumMappingDiagnostics.DuplicateDefault, attribute);
                }

                hasDefault = true;
            }
            else if (attribute.ConstructorArguments.Length != 3 ||
                attribute.ConstructorArguments[0].Value is not INamedTypeSymbol target || !IsClosed(target) ||
                !SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, type.OriginalDefinition))
            {
                return Invalid(DatumMappingDiagnostics.Target, attribute);
            }
            else if (!targets.Add(target))
            {
                return Invalid(DatumMappingDiagnostics.DuplicateExact, attribute);
            }
        }

        return attributes;

        AttributeData[]? Invalid(DiagnosticDescriptor descriptor, AttributeData attribute)
        {
            if (context is { } output)
            {
                Location? location = descriptor == DatumMappingDiagnostics.Target
                    ? DatumMappingDiagnostics.Argument(attribute, 0, output.CancellationToken)
                    : attribute.ApplicationSyntaxReference?.GetSyntax(output.CancellationToken).GetLocation();
                output.Report(descriptor, DatumMappingDiagnostics.CurrentLocation(location, compilation, usage));
            }

            return null;
        }
    }

    /// <summary>
    /// Freezes lazy registration constants without retaining compiler symbols or resolving backend catalogs.
    /// </summary>
    /// <returns>The immutable scalar or range registration contract.</returns>
    internal DatumRegistrationModel FreezeRegistration()
        => new(Managed, Type.IsValueType, Name, Schema, External,
            RangeBound is null ? Converter.ToDisplayString(ManagedFormat) : null, CanRead, CanWrite, RangeBound?.Managed);

    /// <summary>
    /// Detaches closed mapping and provider values while preserving independent diagnostic coordinates.
    /// </summary>
    /// <param name="compilation">The compilation owning the current declaration locations.</param>
    /// <returns>The immutable mapping and registration contracts.</returns>
    internal DatumTypeModel Freeze(Compilation compilation)
        => new(DatumTypeReference.Create(this), FreezeRegistration(),
            RangeBound is null ? null : DatumTypeReference.Create(RangeBound),
            GeneratorLocation.Create(Type.Locations.FirstOrDefault(), compilation));

    /// <summary>
    /// Finds a mapped leaf inside an unsupported container shape.
    /// </summary>
    private static bool ContainsMapping(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => ContainsMapping(array.ElementType),
        IPointerTypeSymbol pointer => ContainsMapping(pointer.PointedAtType),
        INamedTypeSymbol named => IsMapped(named) || named.TypeArguments.Any(ContainsMapping),
        _ => false,
    };

    /// <summary>
    /// Finds unbound parameters in a root, its arguments or any constructed containing type.
    /// </summary>
    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        IPointerTypeSymbol pointer => ContainsTypeParameter(pointer.PointedAtType),
        INamedTypeSymbol named => named.IsUnboundGenericType || named.TypeArguments.Any(ContainsTypeParameter) ||
            named.ContainingType is not null && ContainsTypeParameter(named.ContainingType),
        _ => false,
    };

    /// <summary>
    /// Distinguishes an arbitrary managed aggregate payload from a SQL datum container.
    /// </summary>
    private static bool IsManagedState(ITypeSymbol type)
        => type is INamedTypeSymbol { Name: "PgAggregateState", Arity: 1 } state && state.ContainingNamespace.ToDisplayString() == "Ankus";

    /// <summary>
    /// Checks closed type accessibility from generated code, including generic arguments and friend assemblies.
    /// </summary>
    private static bool Accessible(ITypeSymbol type, IAssemblySymbol assembly)
        => type is IArrayTypeSymbol array ? Accessible(array.ElementType, assembly) :
            type is INamedTypeSymbol named && TypeNesting.SelfAndContainers(named).All(current => !current.IsUnboundGenericType && !current.IsFileLocal &&
                IsAccessible(current, assembly) && current.TypeArguments.All(argument => Accessible(argument, assembly)));

    /// <summary>
    /// Accepts public declarations and internal access granted to the generated assembly.
    /// </summary>
    private static bool IsAccessible(ISymbol symbol, IAssemblySymbol assembly)
        => symbol.DeclaredAccessibility == Accessibility.Public ||
            symbol.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal && symbol.ContainingAssembly.GivesAccessTo(assembly);
}
