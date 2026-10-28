using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Retains escaped aggregate DDL around typed identifiers that use the current provider inventory.
/// </summary>
/// <param name="Name">The escaped schema-aware aggregate name.</param>
/// <param name="Kind">The normal, ordered or hypothetical signature kind.</param>
/// <param name="Inputs">The named aggregated input fragments.</param>
/// <param name="InputIdentities">The unnamed aggregated input identity fragments.</param>
/// <param name="Direct">The named ordered direct-input fragments.</param>
/// <param name="DirectIdentities">The unnamed direct-input identity fragments.</param>
/// <param name="Options">The ordered escaped execution clauses and typed state identities.</param>
internal sealed record AggregateSqlEmission(string Name, int Kind, EquatableArray<FunctionSqlEmission.Argument> Inputs,
    EquatableArray<FunctionSqlEmission.Argument> InputIdentities, EquatableArray<FunctionSqlEmission.Argument> Direct,
    EquatableArray<FunctionSqlEmission.Argument> DirectIdentities, EquatableArray<AggregateSqlEmission.Option> Options)
{
    /// <summary>
    /// Escapes fixed catalog values while preserving current-provider qualification as typed fragments.
    /// </summary>
    /// <param name="model">The minimal immutable aggregate DDL contract.</param>
    /// <returns>The independently renderable aggregate definition and attachment fragments.</returns>
    internal static AggregateSqlEmission Create(AggregateSqlModel model)
    {
        Dictionary<string, AggregateSqlModel.Helper> helpers = model.Helpers.ToDictionary(static helper => helper.Role, StringComparer.Ordinal);
        AggregateSqlModel.Helper transition = model.Transition;
        var inputs = new EquatableArray<FunctionSqlEmission.Argument>(transition.Parameters.Select(static parameter =>
            new FunctionSqlEmission.Argument((parameter.IsVariadic ? "VARIADIC " : string.Empty) + SqlText.Identifier(parameter.Name) + " ",
                parameter.Type, string.Empty)));
        var inputIdentities = new EquatableArray<FunctionSqlEmission.Argument>(transition.Parameters.Select(static parameter =>
            new FunctionSqlEmission.Argument(parameter.IsVariadic ? "VARIADIC " : string.Empty, parameter.Type, string.Empty)));
        var direct = new EquatableArray<FunctionSqlEmission.Argument>(helpers.TryGetValue("Final", out AggregateSqlModel.Helper? final)
            ? final.Parameters.Select((parameter, index) =>
                new FunctionSqlEmission.Argument(SqlText.Identifier(parameter.Name) + " ", model.Direct[index], string.Empty))
            : []);
        var directIdentities = new EquatableArray<FunctionSqlEmission.Argument>(model.Direct.Select(static type =>
            new FunctionSqlEmission.Argument(string.Empty, type, string.Empty)));
        var options = new List<Option>
        {
            new("SFUNC = " + transition.Name, null),
            new("STYPE = ", transition.Result),
        };
        AddHelper("Final", "FINALFUNC");
        if (model.FinalExtra)
        {
            options.Add(new("FINALFUNC_EXTRA", null));
        }

        options.Add(new("FINALFUNC_MODIFY = " + Modify(model.FinalModify), null));
        AddHelper("Combine", "COMBINEFUNC");
        AddHelper("Serialize", "SERIALFUNC");
        AddHelper("Deserialize", "DESERIALFUNC");
        if (model.StateSize != 0)
        {
            options.Add(new("SSPACE = " + model.StateSize.ToString(CultureInfo.InvariantCulture), null));
        }

        if (model.Initial is not null)
        {
            options.Add(new("INITCOND = " + SqlText.Literal(model.Initial), null));
        }

        if (helpers.TryGetValue("MovingTransition", out AggregateSqlModel.Helper? moving))
        {
            AddHelper("MovingTransition", "MSFUNC");
            AddHelper("MovingInverse", "MINVFUNC");
            options.Add(new("MSTYPE = ", moving.Result));
            AddHelper("MovingFinal", "MFINALFUNC");
            if (model.MovingFinalExtra)
            {
                options.Add(new("MFINALFUNC_EXTRA", null));
            }

            options.Add(new("MFINALFUNC_MODIFY = " + Modify(model.MovingFinalModify), null));
            if (model.MovingStateSize != 0)
            {
                options.Add(new("MSSPACE = " + model.MovingStateSize.ToString(CultureInfo.InvariantCulture), null));
            }

            if (model.MovingInitial is not null)
            {
                options.Add(new("MINITCOND = " + SqlText.Literal(model.MovingInitial), null));
            }
        }
        else
        {
            if (model.MovingFinalExtra)
            {
                options.Add(new("MFINALFUNC_EXTRA", null));
            }

            if (model.MovingFinalModifyAuthored)
            {
                options.Add(new("MFINALFUNC_MODIFY = " + Modify(model.MovingFinalModify), null));
            }
        }

        if (model.SortOperator is not null)
        {
            options.Add(new("SORTOP = " + model.SortOperator, null));
        }

        options.Add(new("PARALLEL = " + (model.Parallel switch
        {
            1 => "RESTRICTED",
            2 => "SAFE",
            _ => "UNSAFE"
        }), null));
        if (model.Kind == 2)
        {
            options.Add(new("HYPOTHETICAL", null));
        }

        return new(SqlSchemaTemplate.Prefix(model.Schema) + SqlText.Identifier(model.Name), model.Kind,
            inputs, inputIdentities, direct, directIdentities, new(options));

        void AddHelper(string role, string option)
        {
            if (helpers.TryGetValue(role, out AggregateSqlModel.Helper? helper))
            {
                options.Add(new(option + " = " + helper.Name, null));
            }
        }
    }

    /// <summary>
    /// Composes the cached definition against current provider ownership without rewriting authored SQL.
    /// </summary>
    /// <param name="providers">The current graph's type-provider inventory.</param>
    /// <returns>The complete aggregate definition with schema selection markers.</returns>
    internal string Compose(SqlTypeProviders providers)
        => "CREATE AGGREGATE " + Name + "(" + Signature(Inputs, Direct, providers) + ") (\n    " +
            string.Join(",\n    ", Options.Select(option => option.Text + option.Type?.Emit(providers))) + "\n);\n";

    /// <summary>
    /// Composes the ALTER EXTENSION attachment identity from unnamed typed argument fragments.
    /// </summary>
    /// <param name="providers">The current graph's type-provider inventory.</param>
    /// <returns>The qualified aggregate name and complete unnamed signature.</returns>
    internal string Identity(SqlTypeProviders providers) => Name + "(" + Signature(InputIdentities, DirectIdentities, providers) + ")";

    /// <summary>
    /// Applies PostgreSQL's star, ordered-input and variadic signature grammar.
    /// </summary>
    private string Signature(EquatableArray<FunctionSqlEmission.Argument> inputs,
        EquatableArray<FunctionSqlEmission.Argument> direct, SqlTypeProviders providers)
    {
        string values = string.Join(", ", inputs.Select(argument => argument.Emit(providers)));
        if (Kind == 0)
        {
            return values.Length == 0 ? "*" : values;
        }

        string ordered = string.Join(", ", direct.Select(argument => argument.Emit(providers)));
        return (ordered.Length == 0 ? string.Empty : ordered + " ") + "ORDER BY " + values;
    }

    /// <summary>
    /// Formats validated final-state modification without culture-dependent values.
    /// </summary>
    private static string Modify(int value) => value switch { 2 => "SHAREABLE", 3 => "READ_WRITE", _ => "READ_ONLY" };

    /// <summary>
    /// Retains escaped static option text separately from a typed state catalog identity.
    /// </summary>
    /// <param name="Text">The complete fixed clause or prefix before a state type.</param>
    /// <param name="Type">The optional provider-qualified state type.</param>
    internal sealed record Option(string Text, SqlTypeTemplate? Type);
}
