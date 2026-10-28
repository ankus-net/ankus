using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Caches function DDL fragments while leaving provider ownership and planner references to the current graph.
/// </summary>
/// <param name="Header">The rendered CREATE clause through its argument-list opening.</param>
/// <param name="Arguments">The ordered escaped inputs, exact defaults and typed catalog fragments.</param>
/// <param name="Result">The scalar, SETOF or TABLE result fragments.</param>
/// <param name="Tail">The library, native entry and execution clauses before graph-selected planner support.</param>
internal sealed record FunctionSqlEmission(string Header, EquatableArray<FunctionSqlEmission.Argument> Arguments,
    FunctionSqlEmission.ReturnClause Result, string Tail)
{
    /// <summary>
    /// Renders static function text and typed identifiers without compiler objects or mutable graph state.
    /// </summary>
    /// <param name="model">The exact SQL catalog, execution and entry identities.</param>
    /// <returns>Independently cached function SQL fragments.</returns>
    internal static FunctionSqlEmission Create(FunctionSqlModel model)
    {
        var arguments = new EquatableArray<Argument>(model.Parameters.Select(static parameter =>
            new Argument((parameter.Variadic ? "VARIADIC " : string.Empty) + SqlText.Identifier(parameter.Name) + " ",
                parameter.Type, parameter.Default is null ? string.Empty : SqlText.Default(parameter.Default))));
        bool table = model.Set && model.Columns[0].Name is not null;
        var result = new ReturnClause(table ? "TABLE (" : model.Set ? "SETOF " : string.Empty,
            new(model.Columns.Select(static column =>
                new Argument(column.Name is null ? string.Empty : SqlText.Identifier(column.Name) + " ", column.Type, string.Empty))),
            table ? ")" : string.Empty);
        return new(Head(model.Name, model.Replace), arguments, result, End(model.Options, model.NativeName));
    }

    /// <summary>
    /// Composes cached fragments against current type providers, before planner support is resolved.
    /// </summary>
    /// <param name="providers">The current graph's validated catalog identities.</param>
    /// <returns>The function definition preceding graph-selected support and the statement terminator.</returns>
    internal string Compose(SqlTypeProviders providers)
        => Header + string.Join(", ", Arguments.Select(argument => argument.Emit(providers))) + ")\nRETURNS " +
            Result.Emit(providers) + Tail;

    /// <summary>
    /// Renders specialized scalar callbacks through the shared grammar while retaining typed input and result fragments.
    /// </summary>
    /// <param name="name">The escaped schema-aware function name.</param>
    /// <param name="replace">Whether installation replaces a compatible function.</param>
    /// <param name="options">The validated execution clauses.</param>
    /// <param name="arguments">The ordered typed argument fragments, including unnamed callback inputs.</param>
    /// <param name="result">The exact scalar catalog identity and ownership.</param>
    /// <param name="nativeName">The assembly-specific native entry identity.</param>
    /// <returns>The immutable SQL fragments preceding planner support.</returns>
    internal static FunctionSqlEmission CreateScalar(string name, bool replace, string options, EquatableArray<Argument> arguments,
        SqlTypeTemplate result, string nativeName)
        => new(Head(name, replace), arguments, new(string.Empty, new([new Argument(string.Empty, result, string.Empty)]), string.Empty),
            End(options, nativeName));

    /// <summary>
    /// Formats specialized callback families through the same function definition grammar.
    /// </summary>
    /// <param name="declaration">The validated SQL declaration.</param>
    /// <param name="arguments">The complete specialized SQL argument clauses.</param>
    /// <param name="result">The complete specialized SQL return clause.</param>
    /// <param name="nativeName">The native entry identity.</param>
    /// <returns>The definition preceding graph-selected support and the statement terminator.</returns>
    internal static string Format(FunctionDeclaration declaration, string arguments, string result, string nativeName)
        => Head(declaration.TemplateName, declaration.Replace) + arguments + ")\nRETURNS " + result + End(declaration.Options, nativeName);

    /// <summary>
    /// Renders the common CREATE prefix from immutable declaration values.
    /// </summary>
    private static string Head(string name, bool replace)
        => $"CREATE {(replace ? "OR REPLACE " : string.Empty)}FUNCTION {name}(";

    /// <summary>
    /// Renders the common library identity and validated execution clauses.
    /// </summary>
    private static string End(string options, string nativeName)
        => $" AS 'MODULE_PATHNAME', '{nativeName}' LANGUAGE c {options}";

    /// <summary>
    /// Retains escaped static text around one typed input or output catalog identifier.
    /// </summary>
    /// <param name="Prefix">The rendered name, variadic modifier or other preceding text.</param>
    /// <param name="Type">The catalog fragment and current provider-ownership rule.</param>
    /// <param name="Suffix">The exact default expression or other following text.</param>
    internal sealed record Argument(string Prefix, SqlTypeTemplate Type, string Suffix)
    {
        /// <summary>
        /// Applies provider ownership only to the typed identifier in this fragment.
        /// </summary>
        /// <param name="providers">The current validated provider inventory.</param>
        /// <returns>The complete input or output fragment.</returns>
        internal string Emit(SqlTypeProviders providers) => Prefix + Type.Emit(providers) + Suffix;
    }

    /// <summary>
    /// Retains scalar or iterator syntax around independently typed output columns.
    /// </summary>
    /// <param name="Prefix">The scalar, SETOF or TABLE opening.</param>
    /// <param name="Columns">The ordered output fragments.</param>
    /// <param name="Suffix">The TABLE closing or empty scalar/set suffix.</param>
    internal sealed record ReturnClause(string Prefix, EquatableArray<Argument> Columns, string Suffix)
    {
        /// <summary>
        /// Composes the result with current typed catalog qualification.
        /// </summary>
        /// <param name="providers">The current validated provider inventory.</param>
        /// <returns>The complete SQL return clause.</returns>
        internal string Emit(SqlTypeProviders providers)
            => Prefix + string.Join(", ", Columns.Select(column => column.Emit(providers))) + Suffix;
    }
}
