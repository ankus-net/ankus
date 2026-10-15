using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Identifies independently correctable aggregate catalog and capability contracts.
/// </summary>
internal static class AggregateDiagnostics
{
    private const string HelpLink = "https://ankus.net/aggregates/#declaration-diagnostics";

    private static readonly DiagnosticDescriptor s_container = new("ANKUS080", "Invalid aggregate container",
        "Aggregate containers must be accessible, non-generic, non-file-local classes or structs",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_name = new("ANKUS081", "Invalid aggregate name",
        "An aggregate name must be a nonempty identifier of at most 63 UTF-8 bytes with valid Unicode and no zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_executionOption = new("ANKUS082", "Invalid aggregate execution policy",
        "'{0}' must be a defined {1} value; choose a named member of that enum",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_initialText = new("ANKUS083", "Invalid aggregate initial text",
        "'{0}' must contain valid Unicode without zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_transitionState = new("ANKUS084", "Invalid aggregate transition state",
        "Transition must return its state type; composite state requires a named PostgreSQL type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_finalState = new("ANKUS085", "Invalid aggregate final state",
        "Final must accept the ordinary transition state followed by its direct arguments",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_directArguments = new("ANKUS086", "Normal aggregate has direct arguments",
        "Normal aggregates require ValueTuple for the final capability's direct argument group",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_missingInputs = new("ANKUS087", "Ordered aggregate has no aggregated inputs",
        "Ordered-set and hypothetical aggregates require at least one aggregated SQL input",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_argumentCount = new("ANKUS088", "Too many aggregate arguments",
        "Aggregates support at most 99 direct and aggregated SQL arguments",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_internalInput = new("ANKUS089", "Aggregate input uses internal state",
        "Direct and aggregated inputs must be SQL values; internal is reserved for aggregate state",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_duplicateNames = new("ANKUS090", "Duplicate aggregate argument name",
        "Direct and aggregated SQL argument names must be distinct across the aggregate signature",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_internalResult = new("ANKUS091", "Aggregate result uses internal state",
        "An internal-state aggregate requires a final capability returning a supported SQL value",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_polymorphicState = new("ANKUS092", "Unresolved polymorphic aggregate state",
        "A polymorphic aggregate state or result requires a polymorphic aggregate input to resolve its type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_hypotheticalArguments = new("ANKUS093", "Mismatched hypothetical arguments",
        "Hypothetical direct arguments must end with the same types as all aggregated inputs",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_movingCapability = new("ANKUS094", "Missing moving aggregate capability",
        "Moving options and MovingFinal require IPgMovingAggregate with both transition and inverse callbacks",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_movingState = new("ANKUS095", "Invalid moving aggregate state",
        "MovingTransition must return its moving state type; composite state requires a named PostgreSQL type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_movingInputs = new("ANKUS096", "Mismatched moving aggregate inputs",
        "MovingTransition must accept the same aggregated SQL inputs as Transition",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_inverseState = new("ANKUS097", "Mismatched moving inverse signature",
        "MovingInverse must accept the moving transition's state and inputs and return the same state type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_inverseStrictness = new("ANKUS098", "Mismatched moving inverse NULL policy",
        "MovingTransition and MovingInverse must have the same PostgreSQL STRICT policy",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_movingResult = new("ANKUS099", "Mismatched moving aggregate result",
        "Moving and ordinary implementations must produce the same PostgreSQL result type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_combineState = new("ANKUS100", "Mismatched aggregate combine state",
        "Combine must accept two ordinary transition states and return that same state type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_combineNulls = new("ANKUS101", "Invalid internal combine NULL policy",
        "An internal-state Combine must accept two nullable states and cannot be STRICT",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_serialization = new("ANKUS102", "Invalid aggregate state transport",
        "Serialization requires internal state, Serialize(state) returning byte[], and Deserialize(byte[]) returning the same state",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_variadic = new("ANKUS103", "Invalid variadic aggregate argument",
        "Variadic inputs require one trailing array on a normal aggregate; ordered-set VARIADIC ANY is not a concrete array",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_sortArity = new("ANKUS104", "Aggregate sort operator requires one input",
        "SortOperator requires exactly one aggregate SQL argument",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_sortOperator = new("ANKUS105", "Invalid aggregate sort operator",
        "SortOperator must name a valid PostgreSQL operator, optionally qualified by one schema",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_internalInitial = new("ANKUS106", "Internal state has a textual initial condition",
        "Internal state cannot use '{0}'; construct managed state in the transition callback",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_seed = new("ANKUS107", "Strict transition cannot infer its initial state",
        "A strict transition without an initial condition requires the first declared and first aggregated SQL inputs to be binary compatible with its state",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_finalArguments = new("ANKUS108", "Mismatched aggregate final arguments",
        "Final arguments must match the transition state and direct inputs, followed by aggregate input slots when FinalExtra is enabled",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_finalExtra = new("ANKUS109", "Invalid aggregate final-extra NULL policy",
        "FinalExtra requires nullable trailing aggregate input slots and a non-STRICT final callback",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_capabilityAmbiguity = new("ANKUS110", "Ambiguous aggregate capabilities",
        "Implement exactly one IPgAggregate state/input contract and at most one of each optional aggregate capability",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_uncontractedRole = new("ANKUS111", "Aggregate callback lacks its capability",
        "The {0} method requires its aggregate capability interface on the attributed aggregate",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_implementation = new("ANKUS112", "Invalid aggregate capability implementation",
        "Implement the static {0} member of {1} with a synchronous, non-generic method",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_callbackInput = new("ANKUS113", "Invalid aggregate callback parameter",
        "Aggregate callbacks require by-value parameters without optional C# defaults",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_contextMetadata = new("ANKUS114", "Aggregate invocation context has metadata",
        "The aggregate invocation context cannot have attributes or a params modifier",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_conflictingAttribute = new("ANKUS115", "Conflicting aggregate callback attribute",
        "Aggregate callbacks cannot also declare triggers, event triggers, operators, casts, or table-column names",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_setResult = new("ANKUS116", "Aggregate callback returns a set",
        "Aggregate capabilities require scalar results; return one state or final SQL value per call",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_resultType = new("ANKUS117", "Unsupported aggregate callback result",
        "An aggregate callback result must be a supported SQL value or a concrete PgAggregateState<T>",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_tupleMetadata = new("ANKUS118", "Invalid aggregate tuple metadata",
        "Tuple input metadata must select an existing C# tuple element by its exact Element name; the tuple group cannot use params",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_emptyMetadata = new("ANKUS119", "Empty aggregate group has metadata",
        "An empty ValueTuple argument group has no SQL values to annotate",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_helperCount = new("ANKUS120", "Too many aggregate callback arguments",
        "Aggregate support functions permit at most 100 SQL arguments, including the deserializer's native dummy argument",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_helperDuplicateName = new("ANKUS121", "Duplicate aggregate callback argument name",
        "Every SQL argument in an aggregate support function must have a distinct name",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_polymorphicResult = new("ANKUS122", "Unresolved aggregate callback result",
        "A polymorphic callback result requires a polymorphic input; use FinalExtra for an internal-state final callback",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_helperName = new("ANKUS123", "Invalid aggregate support function name",
        "An aggregate support function name must be a valid identifier of at most 63 UTF-8 bytes",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_inputType = new("ANKUS124", "Unsupported aggregate input type",
        "Aggregate inputs require supported SQL values or concrete PgAggregateState<T> state",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_inputName = new("ANKUS125", "Invalid aggregate input name",
        "Aggregate input names must be nonempty identifiers of at most 63 UTF-8 bytes with valid Unicode and no zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_inputMetadata = new("ANKUS126", "Repeated aggregate input metadata",
        "An aggregate SQL input accepts at most one PgParameter attribute",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_inputDefault = new("ANKUS127", "Aggregate input has a SQL default",
        "Aggregate inputs cannot declare PgParameter.Default",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_scalarElement = new("ANKUS128", "Scalar aggregate input selects a tuple element",
        "PgParameter.Element applies only to a grouped tuple argument",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the diagnostic for invalid aggregate container.
    /// </summary>
    internal static DiagnosticDescriptor Container => s_container;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate name.
    /// </summary>
    internal static DiagnosticDescriptor Name => s_name;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate execution policy.
    /// </summary>
    internal static DiagnosticDescriptor ExecutionOption => s_executionOption;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate initial text.
    /// </summary>
    internal static DiagnosticDescriptor InitialText => s_initialText;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate transition state.
    /// </summary>
    internal static DiagnosticDescriptor TransitionState => s_transitionState;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate final state.
    /// </summary>
    internal static DiagnosticDescriptor FinalState => s_finalState;

    /// <summary>
    /// Gets the diagnostic for normal aggregate has direct arguments.
    /// </summary>
    internal static DiagnosticDescriptor DirectArguments => s_directArguments;

    /// <summary>
    /// Gets the diagnostic for ordered aggregate has no aggregated inputs.
    /// </summary>
    internal static DiagnosticDescriptor MissingInputs => s_missingInputs;

    /// <summary>
    /// Gets the diagnostic for too many aggregate arguments.
    /// </summary>
    internal static DiagnosticDescriptor ArgumentCount => s_argumentCount;

    /// <summary>
    /// Gets the diagnostic for aggregate input uses internal state.
    /// </summary>
    internal static DiagnosticDescriptor InternalInput => s_internalInput;

    /// <summary>
    /// Gets the diagnostic for duplicate aggregate argument name.
    /// </summary>
    internal static DiagnosticDescriptor DuplicateNames => s_duplicateNames;

    /// <summary>
    /// Gets the diagnostic for aggregate result uses internal state.
    /// </summary>
    internal static DiagnosticDescriptor InternalResult => s_internalResult;

    /// <summary>
    /// Gets the diagnostic for unresolved polymorphic aggregate state.
    /// </summary>
    internal static DiagnosticDescriptor PolymorphicState => s_polymorphicState;

    /// <summary>
    /// Gets the diagnostic for mismatched hypothetical arguments.
    /// </summary>
    internal static DiagnosticDescriptor HypotheticalArguments => s_hypotheticalArguments;

    /// <summary>
    /// Gets the diagnostic for missing moving aggregate capability.
    /// </summary>
    internal static DiagnosticDescriptor MovingCapability => s_movingCapability;

    /// <summary>
    /// Gets the diagnostic for invalid moving aggregate state.
    /// </summary>
    internal static DiagnosticDescriptor MovingState => s_movingState;

    /// <summary>
    /// Gets the diagnostic for mismatched moving aggregate inputs.
    /// </summary>
    internal static DiagnosticDescriptor MovingInputs => s_movingInputs;

    /// <summary>
    /// Gets the diagnostic for mismatched moving inverse signature.
    /// </summary>
    internal static DiagnosticDescriptor InverseState => s_inverseState;

    /// <summary>
    /// Gets the diagnostic for mismatched moving inverse NULL policy.
    /// </summary>
    internal static DiagnosticDescriptor InverseStrictness => s_inverseStrictness;

    /// <summary>
    /// Gets the diagnostic for mismatched moving aggregate result.
    /// </summary>
    internal static DiagnosticDescriptor MovingResult => s_movingResult;

    /// <summary>
    /// Gets the diagnostic for mismatched aggregate combine state.
    /// </summary>
    internal static DiagnosticDescriptor CombineState => s_combineState;

    /// <summary>
    /// Gets the diagnostic for invalid internal combine NULL policy.
    /// </summary>
    internal static DiagnosticDescriptor CombineNulls => s_combineNulls;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate state transport.
    /// </summary>
    internal static DiagnosticDescriptor Serialization => s_serialization;

    /// <summary>
    /// Gets the diagnostic for invalid variadic aggregate argument.
    /// </summary>
    internal static DiagnosticDescriptor Variadic => s_variadic;

    /// <summary>
    /// Gets the diagnostic for aggregate sort operator requires one input.
    /// </summary>
    internal static DiagnosticDescriptor SortArity => s_sortArity;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate sort operator.
    /// </summary>
    internal static DiagnosticDescriptor SortOperator => s_sortOperator;

    /// <summary>
    /// Gets the diagnostic for internal state has a textual initial condition.
    /// </summary>
    internal static DiagnosticDescriptor InternalInitial => s_internalInitial;

    /// <summary>
    /// Gets the diagnostic for strict transition cannot infer its initial state.
    /// </summary>
    internal static DiagnosticDescriptor Seed => s_seed;

    /// <summary>
    /// Gets the diagnostic for mismatched aggregate final arguments.
    /// </summary>
    internal static DiagnosticDescriptor FinalArguments => s_finalArguments;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate final-extra NULL policy.
    /// </summary>
    internal static DiagnosticDescriptor FinalExtra => s_finalExtra;

    /// <summary>
    /// Gets the diagnostic for ambiguous aggregate capabilities.
    /// </summary>
    internal static DiagnosticDescriptor CapabilityAmbiguity => s_capabilityAmbiguity;

    /// <summary>
    /// Gets the diagnostic for aggregate callback lacks its capability.
    /// </summary>
    internal static DiagnosticDescriptor UncontractedRole => s_uncontractedRole;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate capability implementation.
    /// </summary>
    internal static DiagnosticDescriptor Implementation => s_implementation;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate callback parameter.
    /// </summary>
    internal static DiagnosticDescriptor CallbackInput => s_callbackInput;

    /// <summary>
    /// Gets the diagnostic for aggregate invocation context has metadata.
    /// </summary>
    internal static DiagnosticDescriptor ContextMetadata => s_contextMetadata;

    /// <summary>
    /// Gets the diagnostic for conflicting aggregate callback attribute.
    /// </summary>
    internal static DiagnosticDescriptor ConflictingAttribute => s_conflictingAttribute;

    /// <summary>
    /// Gets the diagnostic for aggregate callback returns a set.
    /// </summary>
    internal static DiagnosticDescriptor SetResult => s_setResult;

    /// <summary>
    /// Gets the diagnostic for unsupported aggregate callback result.
    /// </summary>
    internal static DiagnosticDescriptor ResultType => s_resultType;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate tuple metadata.
    /// </summary>
    internal static DiagnosticDescriptor TupleMetadata => s_tupleMetadata;

    /// <summary>
    /// Gets the diagnostic for empty aggregate group has metadata.
    /// </summary>
    internal static DiagnosticDescriptor EmptyMetadata => s_emptyMetadata;

    /// <summary>
    /// Gets the diagnostic for too many aggregate callback arguments.
    /// </summary>
    internal static DiagnosticDescriptor HelperCount => s_helperCount;

    /// <summary>
    /// Gets the diagnostic for duplicate aggregate callback argument name.
    /// </summary>
    internal static DiagnosticDescriptor HelperDuplicateName => s_helperDuplicateName;

    /// <summary>
    /// Gets the diagnostic for unresolved aggregate callback result.
    /// </summary>
    internal static DiagnosticDescriptor PolymorphicResult => s_polymorphicResult;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate support function name.
    /// </summary>
    internal static DiagnosticDescriptor HelperName => s_helperName;

    /// <summary>
    /// Gets the diagnostic for unsupported aggregate input type.
    /// </summary>
    internal static DiagnosticDescriptor InputType => s_inputType;

    /// <summary>
    /// Gets the diagnostic for invalid aggregate input name.
    /// </summary>
    internal static DiagnosticDescriptor InputName => s_inputName;

    /// <summary>
    /// Gets the diagnostic for repeated aggregate input metadata.
    /// </summary>
    internal static DiagnosticDescriptor InputMetadata => s_inputMetadata;

    /// <summary>
    /// Gets the diagnostic for aggregate input has a SQL default.
    /// </summary>
    internal static DiagnosticDescriptor InputDefault => s_inputDefault;

    /// <summary>
    /// Gets the diagnostic for scalar aggregate input selects a tuple element.
    /// </summary>
    internal static DiagnosticDescriptor ScalarElement => s_scalarElement;
}
