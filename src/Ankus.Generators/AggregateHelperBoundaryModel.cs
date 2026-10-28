using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Projects only aggregate conversion, invocation and entry identity values that affect native or managed dispatch.
/// </summary>
/// <param name="Callback">The assembly-specific managed entry symbol.</param>
/// <param name="Parameters">The ordered owned-input conversion contracts.</param>
/// <param name="Result">The exact result conversion and owned-state policy.</param>
/// <param name="Invocation">The closed constrained interface call.</param>
/// <param name="Deserialize">Whether PostgreSQL supplies a synthetic internal dummy input.</param>
/// <param name="ResultPrecision">The optional numeric output rescaling policy.</param>
internal sealed record AggregateHelperBoundaryModel(string Callback, EquatableArray<AggregateHelperBoundaryModel.Input> Parameters,
    AggregateType Result, AggregateInvocation Invocation, bool Deserialize, NumericPrecision? ResultPrecision)
{
    /// <summary>
    /// Gets conversions before the synthetic internal deserializer dummy.
    /// </summary>
    internal EquatableArray<AggregateType> Types => new(Parameters.Select(static parameter => parameter.Type));

    /// <summary>
    /// Excludes SQL names and execution clauses from the cached dispatcher contract.
    /// </summary>
    /// <param name="helper">The validated support function.</param>
    /// <param name="callback">The assembly-specific managed entry identity.</param>
    /// <returns>The immutable boundary inputs.</returns>
    internal static AggregateHelperBoundaryModel Create(AggregateHelperModel helper, string callback)
        => new(callback, new(helper.Parameters.Select(static parameter => new Input(parameter.Type, parameter.Precision))),
            helper.Result, helper.Invocation, helper.Deserialize, helper.ResultPrecision);

    /// <summary>
    /// Retains only one input's ownership/conversion and numeric rescaling values.
    /// </summary>
    /// <param name="Type">The SQL datum or owned-state conversion.</param>
    /// <param name="Precision">The optional numeric rescaling policy.</param>
    internal sealed record Input(AggregateType Type, NumericPrecision? Precision);
}
