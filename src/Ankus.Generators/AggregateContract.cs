using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Resolves compiler-checked aggregate capabilities into SQL helpers and managed calls.
/// </summary>
internal static class AggregateContract
{
    private static readonly (string Name, int Arity)[] s_interfaces =
    [
        ("IPgAggregate", 2), ("IPgFinalizingAggregate", 3), ("IPgCombinableAggregate", 1),
        ("IPgSerializableAggregate", 1), ("IPgMovingAggregate", 2), ("IPgMovingFinalizingAggregate", 3),
    ];

    /// <summary>
    /// Finds supported closed capability interfaces on an aggregate container.
    /// </summary>
    internal static INamedTypeSymbol[] Interfaces(INamedTypeSymbol type)
        => [.. type.AllInterfaces.Where(static candidate => candidate.ContainingNamespace.ToDisplayString() == "Ankus" &&
            s_interfaces.Contains((candidate.Name, candidate.Arity)))];

    /// <summary>
    /// Reserves implementations from ordinary SQL function discovery, including explicit implementations.
    /// </summary>
    internal static IEnumerable<IMethodSymbol> SelectedMethods(INamedTypeSymbol type)
        => Interfaces(type).SelectMany(static capability => capability.GetMembers().OfType<IMethodSymbol>())
            .Select(type.FindImplementationForInterfaceMember).OfType<IMethodSymbol>();

    /// <summary>
    /// Builds optional capability helpers without using conventional method-name lookup.
    /// </summary>
    /// <param name="aggregate">The attributed aggregate declaration.</param>
    /// <param name="compilation">The current compiler context for visible inherited members.</param>
    /// <param name="context">The diagnostic receiver and cancellation context.</param>
    /// <returns>Whether every declared or inherited role has its required capability.</returns>
    internal static bool Create(AggregateDeclaration aggregate, Compilation compilation, GeneratorDiagnostics context)
    {
        INamedTypeSymbol type = aggregate.Type;
        INamedTypeSymbol[] interfaces = Interfaces(type);
        if (interfaces.Count(static capability => capability.Name == "IPgAggregate") != 1 ||
            interfaces.GroupBy(static capability => capability.Name, StringComparer.Ordinal).Any(static group => group.Count() != 1))
        {
            return Invalid(AggregateDiagnostics.CapabilityAmbiguity, type.Locations.FirstOrDefault());
        }

        var roles = new Dictionary<string, IMethodSymbol>(StringComparer.Ordinal);
        foreach (IMethodSymbol contract in interfaces.SelectMany(static capability => capability.GetMembers().OfType<IMethodSymbol>()))
        {
            roles.Add(contract.Name, contract);
        }

        INamedTypeSymbol aggregateContract = interfaces.Single(static capability => capability.Name == "IPgAggregate");
        ITypeSymbol stateType = aggregateContract.TypeArguments[0];
        ITypeSymbol argumentType = aggregateContract.TypeArguments[1];

        foreach (string role in AggregateDeclaration.Roles)
        {
            if (!roles.TryGetValue(role, out IMethodSymbol? contract))
            {
                if (FindVisibleRole(type, role, stateType, argumentType, compilation) is { } uncontracted)
                {
                    Location? location = uncontracted.Locations.FirstOrDefault(static candidate => candidate.IsInSource)
                        ?? type.Locations.FirstOrDefault(static candidate => candidate.IsInSource);
                    return Invalid(AggregateDiagnostics.UncontractedRole, location, role);
                }

                continue;
            }

            if (type.FindImplementationForInterfaceMember(contract) is not IMethodSymbol method || !method.IsStatic || method.IsAbstract ||
                method.IsAsync || method.IsGenericMethod)
            {
                return Invalid(AggregateDiagnostics.Implementation, type.Locations.FirstOrDefault(), role, contract.ContainingType.ToDisplayString());
            }

            if (method.Parameters.FirstOrDefault(static parameter => parameter.RefKind != RefKind.None || parameter.IsOptional) is { } invalidInput)
            {
                return Invalid(AggregateDiagnostics.CallbackInput, invalidInput.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken).GetLocation());
            }

            IParameterSymbol invocation = method.Parameters[0];
            if (invocation.GetAttributes().Length != 0 || invocation.IsParams)
            {
                return Invalid(AggregateDiagnostics.ContextMetadata,
                    invocation.GetAttributes().FirstOrDefault()?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ??
                    invocation.Locations.FirstOrDefault());
            }

            AttributeData? conflict = method.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() is
                "Ankus.PgTriggerAttribute" or "Ankus.PgEventTriggerAttribute" or "Ankus.PgOperatorAttribute" or "Ankus.PgCastAttribute") ??
                method.GetReturnTypeAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgColumnNamesAttribute");
            if (conflict is not null)
            {
                return Invalid(AggregateDiagnostics.ConflictingAttribute,
                    conflict.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation());
            }

            if (SetResult.IsSequence(contract.ReturnType))
            {
                return Invalid(AggregateDiagnostics.SetResult, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
            }

            if (!AggregateContractNullability.Validate(contract, method, context) ||
                !SqlTypeReference.ValidateValue(contract.ReturnType, method.GetReturnTypeAttributes(), method, "return", context) ||
                !NumericConstraint.ValidateValue(contract.ReturnType, method.GetReturnTypeAttributes(), context) ||
                !SqlNullability.ValidateValue(contract.ReturnType, method, method.Name + " result", context))
            {
                return false;
            }

            AggregateType? result = AggregateType.Create(contract.ReturnType, method.GetReturnTypeAttributes());
            if (result is null)
            {
                if (AttributeMetadataFailure.ReportConversion(contract.ReturnType,
                    FunctionDeclarationDiagnostics.Result(method, context.CancellationToken), context))
                {
                    return false;
                }

                return Invalid(AggregateDiagnostics.ResultType, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
            }

            var slots = new List<AggregateParameter>();
            var slotLocations = new List<Location?>();
            var arguments = new List<AggregateArgument>();
            for (int index = 1; index < contract.Parameters.Length; index++)
            {
                ITypeSymbol parameterType = contract.Parameters[index].Type;
                IParameterSymbol parameter = method.Parameters[index];
                bool group = index == 2 && role is "Transition" or "MovingTransition" or "MovingInverse" or "Final" or "MovingFinal";
                bool empty = group && parameterType is INamedTypeSymbol { Name: "ValueTuple", Arity: 0, ContainingNamespace: { } ns } && ns.ToDisplayString() == "System";
                bool tuple = group && !empty && parameterType is INamedTypeSymbol { IsTupleType: true };
                int start = slots.Count;
                if (tuple)
                {
                    INamedTypeSymbol tupleType = (INamedTypeSymbol)parameterType;
                    ImmutableArray<AttributeData> metadata = [.. parameter.GetAttributes().Where(AggregateParameter.IsSqlMetadata)];
                    AttributeData? invalidMetadata = metadata.FirstOrDefault(attribute => AttributeValues.Get<string?>(attribute, "Element", null) is not { } element ||
                        !tupleType.TupleElements.Any(field => field.Name == element));
                    if (parameter.IsParams || invalidMetadata is not null)
                    {
                        return Invalid(AggregateDiagnostics.TupleMetadata,
                            FunctionDeclarationDiagnostics.Option(invalidMetadata, "Element", context.CancellationToken) ??
                            invalidMetadata?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? Parameter(parameter));
                    }

                    foreach (IFieldSymbol element in tupleType.TupleElements)
                    {
                        ImmutableArray<AttributeData> attributes = [.. metadata.Where(attribute => AttributeValues.Get<string?>(attribute, "Element", null) == element.Name)];
                        if (!AddSlot(element.Type, AggregateParameter.ReadName(attributes, element.Name), AggregateParameter.ReadVariadic(attributes), attributes, parameter, grouped: true))
                        {
                            return false;
                        }
                    }
                }
                else if (empty)
                {
                    if (parameter.GetAttributes().Length != 0 || parameter.IsParams)
                    {
                        return Invalid(AggregateDiagnostics.EmptyMetadata,
                            parameter.GetAttributes().FirstOrDefault()?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? Parameter(parameter));
                    }
                }
                else if (!AddSlot(parameterType, AggregateParameter.ReadName(parameter), parameter.IsParams || AggregateParameter.ReadVariadic(parameter.GetAttributes()),
                    parameter.GetAttributes(), parameter))
                {
                    return false;
                }

                arguments.Add(new(AggregateArgument.Format(parameterType), start, slots.Count - start, tuple));
            }

            bool extra = role == "Final" && aggregate.FinalExtra || role == "MovingFinal" && aggregate.MovingFinalExtra;
            if (extra)
            {
                var names = new HashSet<string>(slots.Select(static slot => slot.Name), StringComparer.Ordinal);
                int suffix = 0;
                foreach (AggregateParameter input in aggregate.Helpers["Transition"].Parameters.Skip(1))
                {
                    string extraName;
                    do
                    {
                        extraName = "__ankus_extra_" + (++suffix).ToString(CultureInfo.InvariantCulture);
                    }
                    while (!names.Add(extraName));

                    slots.Add(new(input.Type.AsNullable(), extraName, false, input.Precision));
                    slotLocations.Add(method.Locations.FirstOrDefault());
                }
            }

            if (slots.Count + (role == "Deserialize" ? 1 : 0) > 100)
            {
                return Invalid(AggregateDiagnostics.HelperCount, method.Locations.FirstOrDefault());
            }

            var argumentNames = new HashSet<string>(StringComparer.Ordinal);
            int duplicate = slots.FindIndex(slot => !argumentNames.Add(slot.Name));
            if (duplicate >= 0)
            {
                return Invalid(AggregateDiagnostics.HelperDuplicateName, slotLocations[duplicate]);
            }

            if (result.Datum?.IsSqlPolymorphic == true && !slots.Any(static slot => slot.Type.Datum?.IsSqlPolymorphic == true))
            {
                return Invalid(AggregateDiagnostics.PolymorphicResult, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
            }

            AttributeData? function = method.GetAttributes().FirstOrDefault(static value => value.AttributeClass?.ToDisplayString() == "Ankus.PgFunctionAttribute");
            string helperName = SqlText.SnakeCase(type.Name) + "_" + SqlText.SnakeCase(role);
            if (function is not null)
            {
                helperName = AttributeValues.Get(function, "Name", helperName);
            }

            if (!SqlText.IsIdentifier(helperName))
            {
                return Invalid(AggregateDiagnostics.HelperName,
                    FunctionDeclarationDiagnostics.Option(function, "Name", context.CancellationToken) ?? method.Locations.FirstOrDefault());
            }

            FunctionDeclaration? declaration = FunctionDeclaration.Create(method, helperName, context, contextParameter: true,
                sqlNullability: [.. slots.Select(static slot => slot.Type.Nullable)], schemaFallback: aggregate.Schema);
            if (declaration is null)
            {
                return false;
            }

            aggregate.Helpers.Add(role, new(method, role, [.. slots], result, declaration, AggregateInvocation.Create(type, contract, arguments)));

            bool AddSlot(ITypeSymbol valueType, string name, bool variadic, ImmutableArray<AttributeData> attributes, IParameterSymbol source, bool grouped = false)
            {
                if (!SqlNullability.ValidateValue(valueType, source, source.Name, context) ||
                    !SqlTypeReference.ValidateValue(valueType, attributes, method, name, context, grouped) ||
                    !NumericConstraint.ValidateValue(valueType, attributes, context, grouped))
                {
                    return false;
                }

                AggregateType? value = AggregateType.Create(valueType, attributes);
                AttributeData[] naming = [.. attributes.Where(static attribute => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgParameterAttribute")];
                if (value is null)
                {
                    if (AttributeMetadataFailure.ReportConversion(valueType, Parameter(source), context))
                    {
                        return false;
                    }

                    return Invalid(AggregateDiagnostics.InputType, Parameter(source));
                }

                if (!SqlText.IsIdentifier(name))
                {
                    return Invalid(AggregateDiagnostics.InputName,
                        FunctionDeclarationDiagnostics.Option(naming.FirstOrDefault(), "Name", context.CancellationToken) ?? source.Locations.FirstOrDefault());
                }

                if (naming.Length > 1)
                {
                    return Invalid(AggregateDiagnostics.InputMetadata,
                        naming[1].ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation());
                }

                if (naming.FirstOrDefault(attribute => attribute.NamedArguments.Any(static argument => argument.Key == "Default")) is { } defaulted)
                {
                    return Invalid(AggregateDiagnostics.InputDefault,
                        FunctionDeclarationDiagnostics.Option(defaulted, "Default", context.CancellationToken));
                }

                if (!grouped && naming.FirstOrDefault(attribute => AttributeValues.Get<string?>(attribute, "Element", null) is not null) is { } selected)
                {
                    return Invalid(AggregateDiagnostics.ScalarElement,
                        FunctionDeclarationDiagnostics.Option(selected, "Element", context.CancellationToken));
                }

                slots.Add(new(value, name, variadic, NumericConstraint.Read(attributes)));
                slotLocations.Add(FunctionDeclarationDiagnostics.Option(naming.FirstOrDefault(), "Name", context.CancellationToken) ??
                    source.Locations.FirstOrDefault());
                return true;
            }
        }

        return true;

        Location? Parameter(IParameterSymbol parameter)
            => (parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as ParameterSyntax)?.Type?.GetLocation()
                ?? parameter.Locations.FirstOrDefault();

        bool Invalid(DiagnosticDescriptor descriptor, Location? location, params string[] arguments)
        {
            context.Report(descriptor, location ?? type.Locations.FirstOrDefault(), arguments);
            return false;
        }
    }

    /// <summary>
    /// Detects authored roles without guessing a capability from their name or including inaccessible base helpers.
    /// </summary>
    /// <param name="type">The concrete aggregate container.</param>
    /// <param name="role">The optional support role.</param>
    /// <param name="stateType">The aggregate's primary state type.</param>
    /// <param name="argumentType">The aggregate's primary input type.</param>
    /// <param name="compilation">The compiler context used for inherited accessibility.</param>
    /// <returns>The first declared or visible inherited method with the reserved role name, or null.</returns>
    private static IMethodSymbol? FindVisibleRole(INamedTypeSymbol type, string role, ITypeSymbol stateType,
        ITypeSymbol argumentType, Compilation compilation)
    {
        for (INamedTypeSymbol? owner = type; owner is not null; owner = owner.BaseType)
        {
            if (owner.GetMembers(role).OfType<IMethodSymbol>().FirstOrDefault(method =>
                IsRoleShape(method, role, stateType, argumentType, compilation) &&
                (SymbolEqualityComparer.Default.Equals(owner, type) || compilation.IsSymbolAccessibleWithin(method, type))) is { } method)
            {
                return method;
            }
        }

        return null;
    }

    /// <summary>
    /// Distinguishes a likely omitted aggregate capability from an unrelated static helper that happens to use a reserved role name.
    /// </summary>
    private static bool IsRoleShape(IMethodSymbol method, string role, ITypeSymbol stateType,
        ITypeSymbol argumentType, Compilation compilation)
    {
        int parameterCount = role is "Serialize" or "Deserialize" ? 2 : 3;
        if (method is not { IsStatic: true, IsGenericMethod: false, ReturnsVoid: false, MethodKind: MethodKind.Ordinary } ||
            method.Parameters.Length != parameterCount ||
            !SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type,
                compilation.GetTypeByMetadataName("Ankus.PgAggregateContext")) ||
            method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
        {
            return false;
        }

        return role switch
        {
            "Final" => Same(method.Parameters[1].Type, stateType),
            "Combine" => Same(method.ReturnType, stateType) && Same(method.Parameters[1].Type, stateType) &&
                Same(method.Parameters[2].Type, stateType),
            "Serialize" => IsByteArray(method.ReturnType) && Same(method.Parameters[1].Type, stateType),
            "Deserialize" => Same(method.ReturnType, stateType) && IsByteArray(method.Parameters[1].Type),
            "MovingTransition" or "MovingInverse" => Same(method.ReturnType, method.Parameters[1].Type) &&
                Same(method.Parameters[2].Type, argumentType),
            "MovingFinal" => true,
            _ => false,
        };

        static bool Same(ITypeSymbol left, ITypeSymbol right) => SymbolEqualityComparer.IncludeNullability.Equals(left, right);
        static bool IsByteArray(ITypeSymbol type) => type is IArrayTypeSymbol
        {
            Rank: 1,
            ElementType.SpecialType: SpecialType.System_Byte,
        };
    }
}
