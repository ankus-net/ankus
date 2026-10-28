using System.Collections.Generic;
using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Projects aggregate DDL independently of managed invocation, conversion and numeric rescaling.
/// </summary>
/// <param name="Name">The unquoted aggregate name.</param>
/// <param name="Schema">The fixed schema or extension-default selection.</param>
/// <param name="Kind">The normal, ordered or hypothetical aggregate kind.</param>
/// <param name="Parallel">The validated parallel execution policy.</param>
/// <param name="Initial">The ordinary initial condition.</param>
/// <param name="MovingInitial">The moving initial condition.</param>
/// <param name="FinalExtra">Whether ordinary final receives extra placeholder arguments.</param>
/// <param name="MovingFinalExtra">Whether moving final receives extra placeholder arguments.</param>
/// <param name="FinalModify">The ordinary final modification policy.</param>
/// <param name="MovingFinalModify">The moving final modification policy.</param>
/// <param name="StateSize">The ordinary state-size estimate.</param>
/// <param name="MovingStateSize">The moving state-size estimate.</param>
/// <param name="MovingFinalModifyAuthored">Whether moving modification was explicitly authored.</param>
/// <param name="SortOperator">The optional sort operator spelling.</param>
/// <param name="Direct">The ordered direct-input SQL identities.</param>
/// <param name="Helpers">Support names and typed SQL contracts in role order.</param>
internal sealed record AggregateSqlModel(string Name, string? Schema, int Kind, int Parallel, string? Initial,
    string? MovingInitial, bool FinalExtra, bool MovingFinalExtra, int FinalModify, int MovingFinalModify,
    int StateSize, int MovingStateSize, bool MovingFinalModifyAuthored, string? SortOperator,
    EquatableArray<SqlTypeTemplate> Direct, EquatableArray<AggregateSqlModel.Helper> Helpers)
{
    /// <summary>
    /// Gets the required transition SQL contract.
    /// </summary>
    internal Helper Transition => Helpers.Single(static helper => helper.Role == "Transition");

    /// <summary>
    /// Gets aggregated SQL inputs after the transition state.
    /// </summary>
    internal EquatableArray<SqlTypeTemplate> Inputs => new(Transition.Parameters.Select(static parameter => parameter.Type));

    /// <summary>
    /// Freezes only values that affect the aggregate definition and attachment identity.
    /// </summary>
    /// <param name="model">The complete validated semantic contract.</param>
    /// <returns>The immutable aggregate DDL inputs.</returns>
    internal static AggregateSqlModel Create(AggregateModel model)
        => new(model.Name, model.Schema, model.Kind, model.Parallel, model.Initial, model.MovingInitial,
            model.FinalExtra, model.MovingFinalExtra, model.FinalModify, model.MovingFinalModify, model.StateSize,
            model.MovingStateSize, model.MovingFinalModifyAuthored, model.SortOperator,
            new(model.Direct.Select(Type)), new(model.Helpers.Select(helper => ProjectHelper(helper, model.Direct.Count))));

    /// <summary>
    /// Excludes support conversions and signatures that do not appear in the aggregate definition.
    /// </summary>
    private static Helper ProjectHelper(AggregateHelperModel helper, int directCount)
    {
        IEnumerable<AggregateParameter> parameters = helper.Role switch
        {
            "Transition" => helper.Parameters.Skip(1),
            "Final" => helper.Parameters.Skip(1).Take(directCount),
            _ => [],
        };
        return new(helper.Role, helper.Declaration.TemplateName,
            helper.Role is "Transition" or "MovingTransition" ? Type(helper.Result) : null,
            new(parameters.Select(static parameter => new Input(parameter.Name, parameter.IsVariadic, Type(parameter.Type)))));
    }

    /// <summary>
    /// Preserves catalog ownership for ordinary data and the built-in internal state type.
    /// </summary>
    /// <param name="type">The validated datum or owned-state conversion.</param>
    /// <returns>The SQL-only type identity and current-provider selection.</returns>
    internal static SqlTypeTemplate Type(AggregateType type)
        => type.Datum is { } datum ? SqlTypeTemplate.Create(datum) : new("internal", false, null);

    /// <summary>
    /// Retains a support function's catalog name and SQL signature without managed implementation details.
    /// </summary>
    /// <param name="Role">The PostgreSQL support role.</param>
    /// <param name="Name">The escaped schema-aware support function name.</param>
    /// <param name="Result">The transition state SQL type, absent for other support roles.</param>
    /// <param name="Parameters">The aggregated inputs or ordered direct inputs used in the aggregate signature.</param>
    internal sealed record Helper(string Role, string Name, SqlTypeTemplate? Result, EquatableArray<Input> Parameters);

    /// <summary>
    /// Retains one SQL argument independently of its managed conversion and nullability.
    /// </summary>
    /// <param name="Name">The unquoted SQL argument name.</param>
    /// <param name="IsVariadic">Whether the argument expands a variadic array.</param>
    /// <param name="Type">The exact SQL type and provider ownership.</param>
    internal sealed record Input(string Name, bool IsVariadic, SqlTypeTemplate Type);
}
