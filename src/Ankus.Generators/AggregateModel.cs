using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Holds all validated aggregate and support-function semantics independently of compiler symbols and attributes.
/// </summary>
/// <param name="Name">The exact unquoted aggregate name.</param>
/// <param name="Schema">The fixed schema, or null for the extension schema.</param>
/// <param name="Kind">The normal, ordered or hypothetical aggregate policy.</param>
/// <param name="Parallel">The validated parallel safety.</param>
/// <param name="Initial">The optional ordinary initial condition.</param>
/// <param name="MovingInitial">The optional moving initial condition.</param>
/// <param name="FinalExtra">Whether ordinary final receives synthetic nullable values.</param>
/// <param name="MovingFinalExtra">Whether moving final receives synthetic nullable values.</param>
/// <param name="FinalModify">The resolved ordinary final modification policy.</param>
/// <param name="MovingFinalModify">The resolved moving final modification policy.</param>
/// <param name="StateSize">The authored ordinary state-size estimate.</param>
/// <param name="MovingStateSize">The authored moving state-size estimate.</param>
/// <param name="MovingFinalModifyAuthored">Whether moving modification was explicitly specified without a moving implementation.</param>
/// <param name="SortOperator">The optional validated sort operator spelling.</param>
/// <param name="Direct">The ordered direct-input contracts.</param>
/// <param name="Helpers">The validated support contracts in role order.</param>
internal sealed record AggregateModel(string Name, string? Schema, int Kind, int Parallel, string? Initial, string? MovingInitial,
    bool FinalExtra, bool MovingFinalExtra, int FinalModify, int MovingFinalModify, int StateSize, int MovingStateSize,
    bool MovingFinalModifyAuthored, string? SortOperator, EquatableArray<AggregateType> Direct, EquatableArray<AggregateHelperModel> Helpers)
{
    /// <summary>
    /// Gets the qualified catalog name.
    /// </summary>
    internal string QualifiedName => (Schema is null ? string.Empty : SqlText.Identifier(Schema) + ".") + SqlText.Identifier(Name);

    /// <summary>
    /// Gets the validated required transition support contract.
    /// </summary>
    internal AggregateHelperModel Transition => Helpers.Single(static helper => helper.Role == "Transition");

    /// <summary>
    /// Gets aggregated input contracts after the transition state.
    /// </summary>
    internal EquatableArray<AggregateType> Inputs => new(Transition.Types.Skip(1));

    /// <summary>
    /// Gets the aggregate's shared function-namespace identity.
    /// </summary>
    internal string Signature => QualifiedName + "(" + string.Join(",", Direct.Concat(Inputs).Select(static type => type.Sql)) + ")";
}
