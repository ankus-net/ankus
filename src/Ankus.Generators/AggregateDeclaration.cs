using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Resolves a declarative aggregate and validates PostgreSQL support-function relationships.
/// </summary>
internal sealed class AggregateDeclaration(INamedTypeSymbol type, AttributeData attribute)
{
    private static readonly DiagnosticDescriptor s_missingContract = new(
        "ANKUS029", "Missing PostgreSQL aggregate contract",
        "'{0}' must implement IPgAggregate<TState, TArgs>; declare optional callbacks through their aggregate capability interfaces",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/aggregates/#compiler-checked-aggregate-contracts");
    private static readonly string[] s_roles = ["Transition", "Final", "Combine", "Serialize", "Deserialize", "MovingTransition", "MovingInverse", "MovingFinal"];

    /// <summary>
    /// Gets support roles in transition-before-final order.
    /// </summary>
    internal static IEnumerable<string> Roles => s_roles;

    /// <summary>
    /// Gets the aggregate's managed declaration container.
    /// </summary>
    internal INamedTypeSymbol Type { get; } = type;

    /// <summary>
    /// Gets the aggregate options and graph metadata.
    /// </summary>
    internal AttributeData Attribute { get; } = attribute;

    /// <summary>
    /// Gets support functions keyed by conventional role.
    /// </summary>
    internal Dictionary<string, AggregateHelper> Helpers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the aggregate's unquoted SQL name.
    /// </summary>
    internal string Name
    {
        get;
        private set;
    } = string.Empty;

    /// <summary>
    /// Gets the fixed schema, or null for the extension's installation namespace.
    /// </summary>
    internal string? Schema
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the qualified SQL identifier.
    /// </summary>
    internal string QualifiedName => (Schema is null ? string.Empty : SqlText.Identifier(Schema) + ".") + SqlText.Identifier(Name);

    /// <summary>
    /// Gets the normal, ordered-set, or hypothetical-set kind.
    /// </summary>
    internal int Kind
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the aggregate-level parallel-safety setting.
    /// </summary>
    internal int Parallel
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the validated textual initial condition, preserving null separately from empty text.
    /// </summary>
    internal string? Initial
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the moving implementation's textual initial condition.
    /// </summary>
    internal string? MovingInitial
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets whether final receives typed SQL NULL input placeholders.
    /// </summary>
    internal bool FinalExtra
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets whether moving final receives typed SQL NULL input placeholders.
    /// </summary>
    internal bool MovingFinalExtra
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the resolved final mutation policy, including PostgreSQL's kind-specific default.
    /// </summary>
    internal int FinalModify
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the resolved moving final mutation policy.
    /// </summary>
    internal int MovingFinalModify
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets an optional structured sort operator name for SQL emission.
    /// </summary>
    internal string? SortOperator
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets aggregated input contracts after the transition state.
    /// </summary>
    internal AggregateType[] Inputs => [.. Helpers["Transition"].Types.Skip(1)];

    /// <summary>
    /// Gets ordered direct arguments, which reach final functions but not transitions.
    /// </summary>
    internal AggregateType[] Direct
    {
        get;
        private set;
    } = [];

    /// <summary>
    /// Gets the aggregate signature in PostgreSQL's shared function namespace.
    /// </summary>
    internal string Signature => QualifiedName + "(" + string.Join(",", Direct.Concat(Inputs).Select(static value => value.Sql)) + ")";

    /// <summary>
    /// Freezes complete validated role and catalog semantics without retaining compiler state.
    /// </summary>
    /// <returns>The immutable aggregate and support contract.</returns>
    internal AggregateModel Freeze() => new(Name, Schema, Kind, Parallel, Initial, MovingInitial, FinalExtra, MovingFinalExtra,
        FinalModify, MovingFinalModify, AttributeValues.Get(Attribute, "StateSize", 0), AttributeValues.Get(Attribute, "MovingStateSize", 0),
        Attribute.NamedArguments.Any(static argument => argument.Key == "MovingFinalModify"), SortOperator, new(Direct),
        new(Helpers.Values.Select(static helper => helper.Freeze())));

    /// <summary>
    /// Reserves selected support methods from ordinary function discovery, including invalid aggregate declarations.
    /// </summary>
    internal static IEnumerable<IMethodSymbol> SelectedMethods(INamedTypeSymbol type)
        => AggregateContract.SelectedMethods(type);

    /// <summary>
    /// Creates a complete aggregate contract or reports declaration diagnostics.
    /// </summary>
    /// <param name="type">The attributed aggregate container.</param>
    /// <param name="compilation">The current compiler context for inherited-member accessibility.</param>
    /// <param name="context">The diagnostic receiver and cancellation context.</param>
    /// <returns>The validated declaration, or null after a reported error.</returns>
    internal static AggregateDeclaration? Create(INamedTypeSymbol type, Compilation compilation, GeneratorDiagnostics context)
    {
        AttributeData attribute = type.GetAttributes().First(static item => item.AttributeClass?.ToDisplayString() == "Ankus.PgAggregateAttribute");
        var aggregate = new AggregateDeclaration(type, attribute)
        {
            Name = AttributeValues.Get(attribute, "Name", SqlText.SnakeCase(type.Name)),
            Schema = AttributeValues.Get<string?>(attribute, "Schema", null),
            Kind = AttributeValues.Get(attribute, "Kind", 0),
            Parallel = AttributeValues.Get(attribute, "ParallelSafety", 0),
            Initial = AttributeValues.Get<string?>(attribute, "InitialCondition", null),
            MovingInitial = AttributeValues.Get<string?>(attribute, "MovingInitialCondition", null),
            FinalExtra = AttributeValues.Get(attribute, "FinalExtra", false),
            MovingFinalExtra = AttributeValues.Get(attribute, "MovingFinalExtra", false),
            FinalModify = AttributeValues.Get(attribute, "FinalModify", 0),
            MovingFinalModify = AttributeValues.Get(attribute, "MovingFinalModify", 0),
        };
        AttributeData? inheritedSchema = null;
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return Invalid(AggregateDiagnostics.Container, type.Locations.FirstOrDefault());
        }

        foreach (INamedTypeSymbol container in TypeNesting.SelfAndContainers(type))
        {
            if (container.IsGenericType || container.IsFileLocal || container.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return Invalid(AggregateDiagnostics.Container, container.Locations.FirstOrDefault());
            }

            // The nearest container schema applies, including a missing or null argument while the attribute is being typed.
            AttributeData? schema = container.GetAttributes().FirstOrDefault(static item => item.AttributeClass?.ToDisplayString() == "Ankus.PgSchemaAttribute");
            if (aggregate.Schema is null && inheritedSchema is null && schema is not null)
            {
                inheritedSchema = schema;
                aggregate.Schema = schema.ConstructorArguments.FirstOrDefault().Value as string;
            }
        }

        if (!SqlText.IsIdentifier(aggregate.Name))
        {
            return Invalid(AggregateDiagnostics.Name, Option("Name"));
        }

        if ((aggregate.Schema is not null || inheritedSchema is not null) && !SqlText.IsIdentifier(aggregate.Schema))
        {
            return Invalid(FunctionDeclarationDiagnostics.Schema, inheritedSchema is null ? Option("Schema") :
                FunctionDeclarationDiagnostics.ConstructorArgument(inheritedSchema, context.CancellationToken));
        }

        if (aggregate.Kind is < 0 or > 2)
        {
            return Invalid(AggregateDiagnostics.ExecutionOption, Option("Kind"), "Kind", "PgAggregateKind");
        }

        if (aggregate.Parallel is < 0 or > 2)
        {
            return Invalid(AggregateDiagnostics.ExecutionOption, Option("ParallelSafety"), "ParallelSafety", "PgParallelSafety");
        }

        if (aggregate.FinalModify is < 0 or > 3)
        {
            return Invalid(AggregateDiagnostics.ExecutionOption, Option("FinalModify"), "FinalModify", "PgAggregateFinalModify");
        }

        if (aggregate.MovingFinalModify is < 0 or > 3)
        {
            return Invalid(AggregateDiagnostics.ExecutionOption, Option("MovingFinalModify"), "MovingFinalModify", "PgAggregateFinalModify");
        }

        aggregate.FinalModify = aggregate.FinalModify == 0 ? (aggregate.Kind == 0 ? 1 : 3) : aggregate.FinalModify;
        aggregate.MovingFinalModify = aggregate.MovingFinalModify == 0 ? (aggregate.Kind == 0 ? 1 : 3) : aggregate.MovingFinalModify;
        if (aggregate.Initial is not null && !SqlText.IsText(aggregate.Initial) ||
            aggregate.MovingInitial is not null && !SqlText.IsText(aggregate.MovingInitial))
        {
            string option = aggregate.Initial is not null && !SqlText.IsText(aggregate.Initial) ? "InitialCondition" : "MovingInitialCondition";
            return Invalid(AggregateDiagnostics.InitialText, Option(option), option);
        }

        if (!AggregateContract.Interfaces(type).Any(static capability => capability.Name == "IPgAggregate"))
        {
            context.Report(s_missingContract, type.Locations.FirstOrDefault(), type.Name);
            return null;
        }

        if (!AggregateContract.Create(aggregate, compilation, context))
        {
            return null;
        }

        AggregateHelper transition = aggregate.Helpers["Transition"];
        if (transition.Types.Length == 0 || !transition.Result.Matches(transition.Types[0]) || transition.Result.Sql == "record")
        {
            return Invalid(AggregateDiagnostics.TransitionState, Result(transition));
        }

        AggregateType state = transition.Result;
        AggregateType[] inputs = aggregate.Inputs;
        AggregateHelper? final = aggregate.Helpers.TryGetValue("Final", out AggregateHelper? finalHelper) ? finalHelper : null;
        if (final is not null)
        {
            int directCount = final.Types.Length - 1 - (aggregate.FinalExtra ? inputs.Length : 0);
            if (directCount < 0 || !final.Types[0].Matches(state))
            {
                return Invalid(AggregateDiagnostics.FinalState, final.Method.Parameters[1].Locations.FirstOrDefault());
            }

            aggregate.Direct = [.. final.Types.Skip(1).Take(directCount)];
        }

        if (aggregate.Kind == 0 && aggregate.Direct.Length != 0)
        {
            return Invalid(AggregateDiagnostics.DirectArguments, final!.Method.Parameters[2].Locations.FirstOrDefault());
        }

        if (aggregate.Kind != 0 && inputs.Length == 0)
        {
            return Invalid(AggregateDiagnostics.MissingInputs, transition.Method.Parameters[2].Locations.FirstOrDefault());
        }

        if (aggregate.Direct.Length + inputs.Length > 99)
        {
            return Invalid(AggregateDiagnostics.ArgumentCount, type.Locations.FirstOrDefault());
        }

        if (inputs.Any(static value => value.IsInternal) || aggregate.Direct.Any(static value => value.IsInternal))
        {
            AggregateHelper helper = inputs.Any(static value => value.IsInternal) ? transition : final!;
            return Invalid(AggregateDiagnostics.InternalInput, helper.Method.Parameters[2].Locations.FirstOrDefault());
        }

        string[] argumentNames = [.. (final?.Parameters.Skip(1).Take(aggregate.Direct.Length) ?? []).Concat(transition.Parameters.Skip(1))
            .Select(static parameter => parameter.Name)];
        if (argumentNames.Distinct(StringComparer.Ordinal).Count() != argumentNames.Length)
        {
            return Invalid(AggregateDiagnostics.DuplicateNames, final?.Method.Parameters[2].Locations.FirstOrDefault());
        }

        if (state.IsInternal && (final is null || final.Result.IsInternal) || final?.Result.IsInternal == true)
        {
            return Invalid(AggregateDiagnostics.InternalResult, Result(final ?? transition));
        }

        bool hasPolymorphicInput = aggregate.Direct.Concat(inputs).Any(static input => input.Datum?.IsSqlPolymorphic == true);
        if (!hasPolymorphicInput && (state.Datum?.IsSqlPolymorphic == true || final?.Result.Datum?.IsSqlPolymorphic == true))
        {
            return Invalid(AggregateDiagnostics.PolymorphicState, Result(state.Datum?.IsSqlPolymorphic == true ? transition : final!));
        }

        if (aggregate.Kind == 2 && (aggregate.Direct.Length < inputs.Length ||
            !aggregate.Direct.Skip(aggregate.Direct.Length - inputs.Length).Zip(inputs, static (left, right) => left.Matches(right)).All(static same => same)))
        {
            return Invalid(AggregateDiagnostics.HypotheticalArguments, final?.Method.Parameters[2].Locations.FirstOrDefault());
        }

        if (!ValidateTransition(transition, aggregate.Initial) || !ValidateFinal(final, state, aggregate.FinalExtra))
        {
            return null;
        }

        bool hasMoving = aggregate.Helpers.TryGetValue("MovingTransition", out AggregateHelper? moving);
        bool hasInverse = aggregate.Helpers.TryGetValue("MovingInverse", out AggregateHelper? inverse);
        if (hasMoving != hasInverse || !hasMoving && (aggregate.Helpers.ContainsKey("MovingFinal") || aggregate.MovingInitial is not null ||
            AttributeValues.Get(attribute, "MovingStateSize", 0) != 0))
        {
            aggregate.Helpers.TryGetValue("MovingFinal", out AggregateHelper? missingMovingFinal);
            return Invalid(AggregateDiagnostics.MovingCapability, aggregate.MovingInitial is not null ? Option("MovingInitialCondition") :
                AttributeValues.Get(attribute, "MovingStateSize", 0) != 0 ? Option("MovingStateSize") :
                missingMovingFinal?.Method.Locations.FirstOrDefault());
        }

        if (hasMoving)
        {
            if (!hasPolymorphicInput && moving!.Result.Datum?.IsSqlPolymorphic == true)
            {
                return Invalid(AggregateDiagnostics.PolymorphicState, Result(moving));
            }

            if (!moving!.Result.Matches(moving.Types[0]) || moving.Result.Sql == "record")
            {
                return Invalid(AggregateDiagnostics.MovingState, Result(moving));
            }

            if (moving.Types.Length != transition.Types.Length ||
                !moving.Types.Skip(1).Zip(inputs, static (left, right) => left.Matches(right)).All(static same => same))
            {
                return Invalid(AggregateDiagnostics.MovingInputs, moving.Method.Parameters[2].Locations.FirstOrDefault());
            }

            if (inverse!.Types.Length != moving.Types.Length || !inverse.Result.Matches(moving.Result) ||
                !inverse.Types.Zip(moving.Types, static (left, right) => left.Matches(right)).All(static same => same))
            {
                return Invalid(AggregateDiagnostics.InverseState, Result(inverse));
            }

            if (inverse.Declaration.Strict != moving.Declaration.Strict)
            {
                return Invalid(AggregateDiagnostics.InverseStrictness, inverse.Method.Locations.FirstOrDefault());
            }

            AggregateHelper? movingFinal = aggregate.Helpers.TryGetValue("MovingFinal", out AggregateHelper? selected) ? selected : null;
            if (!(movingFinal?.Result ?? moving.Result).Matches(final?.Result ?? state))
            {
                return Invalid(AggregateDiagnostics.MovingResult, Result(movingFinal ?? moving));
            }

            if (!ValidateTransition(moving, aggregate.MovingInitial) || !ValidateFinal(movingFinal, moving.Result, aggregate.MovingFinalExtra))
            {
                return null;
            }
        }

        if (aggregate.Helpers.TryGetValue("Combine", out AggregateHelper? combine))
        {
            if (combine.Types.Length != 2 || combine.Types.Any(value => !value.Matches(state)) || !combine.Result.Matches(state))
            {
                return Invalid(AggregateDiagnostics.CombineState, Result(combine));
            }

            if (state.IsInternal && (combine.Declaration.Strict || combine.Types.Any(static value => !value.Nullable)))
            {
                return Invalid(AggregateDiagnostics.CombineNulls, combine.Method.Locations.FirstOrDefault());
            }
        }

        _ = aggregate.Helpers.TryGetValue("Serialize", out AggregateHelper? serializer);
        _ = aggregate.Helpers.TryGetValue("Deserialize", out AggregateHelper? deserializer);
        if ((serializer is not null || deserializer is not null) &&
            (serializer is null || deserializer is null || !Serializes(serializer, deserializer, state)))
        {
            return Invalid(AggregateDiagnostics.Serialization, (serializer ?? deserializer)?.Method.Locations.FirstOrDefault());
        }

        foreach (AggregateHelper helper in aggregate.Helpers.Values)
        {
            if (helper.Parameters.Any(static parameter => parameter.IsVariadic) &&
                (aggregate.Kind != 0 || helper.Role is not ("Transition" or "MovingTransition" or "MovingInverse" or "Final" or "MovingFinal") ||
                    helper.Types.Length < 2 || helper.Types[helper.Types.Length - 1].Datum?.IsVector != true || helper.Parameters.Take(helper.Parameters.Length - 1).Any(static parameter => parameter.IsVariadic)))
            {
                return Invalid(AggregateDiagnostics.Variadic, helper.Method.Parameters[helper.Method.Parameters.Length - 1].Locations.FirstOrDefault());
            }
        }

        string? sort = AttributeValues.Get<string?>(attribute, "SortOperator", null);
        if (sort is not null)
        {
            string[] parts = sort.Split('.');
            string operation = parts[parts.Length - 1];
            if (aggregate.Direct.Length + inputs.Length != 1)
            {
                return Invalid(AggregateDiagnostics.SortArity, Option("SortOperator"));
            }

            if (parts.Length > 2 || parts.Length == 2 && !SqlText.IsIdentifier(parts[0]) || !ValidOperator(operation))
            {
                return Invalid(AggregateDiagnostics.SortOperator, Option("SortOperator"));
            }

            aggregate.SortOperator = parts.Length == 2 ? SqlText.Identifier(parts[0]) + "." + SqlText.Identifier(operation) : operation;
        }

        return aggregate;

        bool ValidateTransition(AggregateHelper helper, string? initial)
        {
            if (helper.Result.IsInternal && initial is not null)
            {
                string option = helper.Role == "Transition" ? "InitialCondition" : "MovingInitialCondition";
                Invalid(AggregateDiagnostics.InternalInitial, Option(option), option);
                return false;
            }

            if (helper.Declaration.Strict && initial is null && (aggregate.Direct.Concat(inputs).FirstOrDefault() is not { } first || !helper.Result.AcceptsSeed(first) ||
                    inputs.FirstOrDefault() is not { } seed || !helper.Result.AcceptsSeed(seed)))
            {
                Invalid(AggregateDiagnostics.Seed, helper.Method.Locations.FirstOrDefault());
                return false;
            }

            return true;
        }

        bool ValidateFinal(AggregateHelper? helper, AggregateType expectedState, bool extra)
        {
            if (helper is null)
            {
                return true;
            }

            AggregateType[] expected = [expectedState, .. aggregate.Direct, .. extra ? inputs : []];
            if (helper.Types.Length != expected.Length || !helper.Types.Zip(expected, static (left, right) => left.Matches(right)).All(static same => same))
            {
                Invalid(AggregateDiagnostics.FinalArguments, helper.Method.Locations.FirstOrDefault());
                return false;
            }

            if (extra && (helper.Declaration.Strict || helper.Types.Skip(1 + aggregate.Direct.Length).Any(static value => !value.Nullable)))
            {
                Invalid(AggregateDiagnostics.FinalExtra, Option(helper.Role == "Final" ? "FinalExtra" : "MovingFinalExtra"));
                return false;
            }

            return true;
        }

        Location? Option(string name) => FunctionDeclarationDiagnostics.Option(attribute, name, context.CancellationToken);

        Location? Result(AggregateHelper helper) => FunctionDeclarationDiagnostics.Result(helper.Method, context.CancellationToken);

        AggregateDeclaration? Invalid(DiagnosticDescriptor descriptor, Location? location, params string[] arguments)
        {
            context.Report(descriptor, location ?? type.Locations.FirstOrDefault(), arguments);
            return null;
        }
    }

    /// <summary>
    /// Checks that serialization turns an internal state into bytea and deserialization turns it back.
    /// </summary>
    private static bool Serializes(AggregateHelper serializer, AggregateHelper deserializer, AggregateType state)
        => state.IsInternal && serializer.Types.Length == 1 && serializer.Types[0].Matches(state) && serializer.Result.Sql == "bytea" &&
            deserializer.Types.Length == 1 && deserializer.Types[0].Sql == "bytea" && deserializer.Result.Matches(state);

    private static bool ValidOperator(string value)
        => value.Length is > 0 and <= 63 && value.All(static character => "+-*/<>=~!@#%^&|`?".Contains(character)) &&
            !value.Contains("--") && !value.Contains("/*") && value != "=>" &&
            (value.Length == 1 || value[value.Length - 1] is not ('+' or '-') || value.Any(static character => "~!@#%^&|`?".Contains(character)));
}
