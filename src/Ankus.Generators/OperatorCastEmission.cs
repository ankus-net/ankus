using System.Collections.Generic;
using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Caches operator and cast DDL while applying current provider ownership only to typed catalog fragments.
/// </summary>
/// <param name="Sql">The independently rendered creation statement fragments.</param>
/// <param name="Attachment">The independently rendered connected-object identity fragments.</param>
internal sealed record OperatorCastEmission(EquatableArray<OperatorCastEmission.Part> Sql, EquatableArray<OperatorCastEmission.Part> Attachment)
{
    /// <summary>
    /// Renders static SQL grammar from validated catalog contracts without compiler objects or graph nodes.
    /// </summary>
    /// <param name="model">The validated operator or cast contract.</param>
    /// <returns>The cached creation and attachment fragments.</returns>
    internal static OperatorCastEmission Create(OperatorCastModel model)
    {
        if (model.Operator is { } declaration)
        {
            var sql = new List<Part> { new("CREATE OPERATOR " + declaration.TemplateName + " (FUNCTION = " + declaration.Function, null) };
            if (declaration.Left is { } left)
            {
                sql.Add(new(", LEFTARG = ", left));
            }

            sql.Add(new(", RIGHTARG = ", declaration.Right));
            sql.Add(new((declaration.Options.IsEmpty ? string.Empty : ", " + string.Join(", ", declaration.Options)) + ");\n", null));
            return new(new(sql), new([
                new("OPERATOR " + declaration.TemplateName + "(" + (declaration.Left is null ? "NONE" : string.Empty), declaration.Left),
                new(",", declaration.Right), new(")", null)]));
        }

        OperatorCastModel.CastDefinition conversion = model.Cast!;
        var statement = new List<Part>
        {
            new("CREATE CAST (", conversion.Arguments[0]), new(" AS ", conversion.Result),
            new(") WITH FUNCTION " + conversion.Function + "(", null),
        };
        for (int index = 0; index < conversion.Arguments.Count; index++)
        {
            statement.Add(new(index == 0 ? string.Empty : ", ", conversion.Arguments[index]));
        }

        string suffix = conversion.Context switch
        {
            1 => " AS ASSIGNMENT",
            2 => " AS IMPLICIT",
            _ => string.Empty,
        };
        statement.Add(new(")" + suffix + ";\n", null));
        return new(new(statement), new([new("CAST (", conversion.Arguments[0]), new(" AS ", conversion.Result), new(")", null)]));
    }

    /// <summary>
    /// Composes cached DDL with the current type provider inventory.
    /// </summary>
    /// <param name="providers">The current validated provider inventory.</param>
    /// <returns>The complete creation statement and connected-object identity.</returns>
    internal (string Sql, string Attachment) Compose(SqlTypeProviders providers)
        => (string.Concat(Sql.Select(value => value.Emit(providers))), string.Concat(Attachment.Select(value => value.Emit(providers))));

    /// <summary>
    /// Separates static SQL text from one optional typed catalog identifier.
    /// </summary>
    /// <param name="Text">The exact static grammar preceding the identifier.</param>
    /// <param name="Type">The optional catalog fragment whose ownership is determined by the current graph.</param>
    internal sealed record Part(string Text, SqlTypeTemplate? Type)
    {
        /// <summary>
        /// Applies provider ownership only to this part's typed identifier.
        /// </summary>
        /// <param name="providers">The current validated provider inventory.</param>
        /// <returns>The exact static and typed SQL fragment.</returns>
        internal string Emit(SqlTypeProviders providers) => Text + (Type?.Emit(providers) ?? string.Empty);
    }
}
