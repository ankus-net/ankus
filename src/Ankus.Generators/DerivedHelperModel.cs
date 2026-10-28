using System.Collections.Generic;
using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Retains one derived helper's conversion and interface call independently of SQL policies and source locations.
/// </summary>
/// <param name="Managed">The exact globally qualified managed argument type.</param>
/// <param name="Symbol">The assembly-scoped helper identity.</param>
/// <param name="Role">The comparison or hashing operation.</param>
/// <param name="Expression">The statically bound interface expression.</param>
/// <param name="Value">The immutable input conversion contract.</param>
/// <param name="Comparison">Whether the result is a boolean; otherwise it is an index support integer.</param>
/// <param name="Unary">Whether the helper takes one argument; otherwise it takes two.</param>
internal sealed record DerivedHelperModel(string Managed, string Symbol, string Role, string Expression,
    FunctionType Value, bool Comparison, bool Unary)
{
    /// <summary>
    /// Projects only the native and managed helper contracts from selected value semantics.
    /// </summary>
    /// <param name="model">The detached semantic root.</param>
    /// <returns>The original ordered helper inventory.</returns>
    internal static EquatableArray<DerivedHelperModel> Create(DerivedOperatorModel model)
    {
        if (model.Value is null || model.Error is not null)
        {
            return new([]);
        }

        var helpers = new List<DerivedHelperModel>();
        string prefix = "ankus_operator_" + model.Symbol + "_";
        if (model.Equality is not null)
        {
            helpers.Add(new(model.Managed, model.Symbol, "eq", model.Enumeration ? "left == right" :
                "((global::System.IEquatable<" + model.Managed + ">)left).Equals(right)", model.Value, true, false));
            helpers.Add(new(model.Managed, model.Symbol, "ne", "!" + prefix + "eq(left, right)", model.Value, true, false));
        }

        if (model.Ordering is not null)
        {
            helpers.Add(new(model.Managed, model.Symbol, "cmp", model.Enumeration
                ? "((" + model.Underlying + ")left).CompareTo((" + model.Underlying + ")right)"
                : "((global::System.IComparable<" + model.Managed + ">)left).CompareTo(right)", model.Value, false, false));
            foreach ((string role, string token) in new[] { ("lt", "<"), ("gt", ">"), ("le", "<="), ("ge", ">=") })
            {
                helpers.Add(new(model.Managed, model.Symbol, role, prefix + "cmp(left, right) " + token + " 0", model.Value, true, false));
            }
        }

        if (model.Hashing is not null)
        {
            helpers.Add(new(model.Managed, model.Symbol, "hash", model.Enumeration
                ? "global::Ankus.PgHash.Compute(unchecked((ulong)left))"
                : "((global::Ankus.IPgHashable)left).GetPostgresHashCode()", model.Value, false, true));
        }

        return new(helpers);
    }

    /// <summary>
    /// Renders an exact helper boundary without current graph ownership or extension initialization.
    /// </summary>
    /// <returns>The immutable managed/native/export fragments.</returns>
    internal DerivedHelperEmission Emit()
    {
        FunctionType result = Comparison ? FunctionType.ComparisonResult() : FunctionType.IndexSupportResult();
        FunctionType[] parameters = Unary ? [Value] : [Value, Value];
        string helper = "ankus_operator_" + Symbol + "_" + Role;
        var managed = new StringBuilder();
        managed.AppendLine("    internal static " + result.Managed + " " + helper + "(" + Managed + " left" +
            (Unary ? string.Empty : ", " + Managed + " right") + ")");
        managed.AppendLine("        => " + Expression + ";");
        managed.AppendLine();
        string invocation = helper + "(" + ManagedConversion.Read(Value, "arguments[0]", string.Empty) +
            (Unary ? string.Empty : ", " + ManagedConversion.Read(Value, "arguments[1]", string.Empty)) + ")";
        string callback = "ankus_managed_operator_" + Symbol + "_" + Role;
        string nativeName = "ankus_fn_operator_" + Symbol + "_" + Role;
        PgFunctionEmitter.EmitManaged(callback, result, invocation, string.Empty, false, managed);
        return new(managed.ToString(), PgFunctionEmitter.CreateNative(nativeName, callback, parameters, result),
            new StringBuilder().AppendLine(nativeName).AppendLine("pg_finfo_" + nativeName).ToString(), nativeName);
    }
}

/// <summary>
/// Carries one cached helper boundary with initialization composed only at the current extension boundary.
/// </summary>
/// <param name="Managed">The helper and managed dispatcher source.</param>
/// <param name="Native">The independent native header and body.</param>
/// <param name="Exports">The exact linker exports.</param>
/// <param name="NativeName">The original native function symbol used by SQL.</param>
internal sealed record DerivedHelperEmission(string Managed, NativeFunctionEmission Native, string Exports, string NativeName);
