using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Projects aggregate support DDL independently of managed ownership, invocation and numeric conversions.
/// </summary>
/// <param name="Name">The escaped schema-aware support function name.</param>
/// <param name="Replace">Whether installation replaces a compatible function.</param>
/// <param name="Options">The validated SQL execution clauses.</param>
/// <param name="Parameters">The named inputs and optional unnamed deserializer dummy.</param>
/// <param name="Result">The exact SQL result and provider ownership.</param>
/// <param name="NativeName">The assembly-specific native entry identity.</param>
/// <param name="IsPlannerSupport">Whether PostgreSQL accepts the nonvariadic internal signature for planner support.</param>
internal sealed record AggregateHelperSqlModel(string Name, bool Replace, string Options,
    EquatableArray<AggregateHelperSqlModel.Input> Parameters, SqlTypeTemplate Result, string NativeName, bool IsPlannerSupport)
{
    /// <summary>
    /// Freezes only values that affect the support function's catalog declaration.
    /// </summary>
    /// <param name="helper">The complete validated support contract.</param>
    /// <param name="callback">The exact managed entry identity.</param>
    /// <returns>The immutable SQL rendering inputs.</returns>
    internal static AggregateHelperSqlModel Create(AggregateHelperModel helper, string callback)
        => new(helper.Declaration.TemplateName, helper.Declaration.Replace, helper.Declaration.Options,
            new(helper.Parameters.Select(static parameter => new Input(parameter.Name, parameter.IsVariadic,
                AggregateSqlModel.Type(parameter.Type))).Concat(helper.Deserialize ? [new Input(null, false, new("internal", false, null))] : [])),
            AggregateSqlModel.Type(helper.Result), callback.Replace("ankus_managed_", "ankus_fn_"),
            !helper.Deserialize && helper.Types.Count == 1 && helper.Types[0].IsInternal && helper.Result.IsInternal &&
                !helper.Parameters.Any(static parameter => parameter.IsVariadic));

    /// <summary>
    /// Escapes named support parameters while preserving PostgreSQL's unnamed deserializer dummy.
    /// </summary>
    /// <returns>The typed SQL fragments preceding current graph-selected planner support.</returns>
    internal FunctionSqlEmission Emit()
        => FunctionSqlEmission.CreateScalar(Name, Replace, Options,
            new(Parameters.Select(static parameter => new FunctionSqlEmission.Argument(
                (parameter.IsVariadic ? "VARIADIC " : string.Empty) +
                    (parameter.Name is null ? string.Empty : SqlText.Identifier(parameter.Name) + " "), parameter.Type, string.Empty))),
            Result, NativeName);

    /// <summary>
    /// Holds a SQL argument independently of its managed conversion.
    /// </summary>
    /// <param name="Name">The unquoted argument name, or null for the internal deserializer dummy.</param>
    /// <param name="IsVariadic">Whether the input expands a variadic array.</param>
    /// <param name="Type">The exact typed catalog identity and ownership.</param>
    internal sealed record Input(string? Name, bool IsVariadic, SqlTypeTemplate Type);
}
