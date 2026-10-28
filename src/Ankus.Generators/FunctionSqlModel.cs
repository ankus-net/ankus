using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Projects only the catalog, execution and entry identities that affect a function's SQL definition.
/// </summary>
/// <param name="Name">The escaped function identity with its default-schema marker.</param>
/// <param name="Replace">Whether installation replaces a compatible function.</param>
/// <param name="Options">The validated execution clauses.</param>
/// <param name="Parameters">The ordered SQL input names, catalog types and defaults.</param>
/// <param name="Set">Whether the result uses iterator syntax.</param>
/// <param name="Columns">The ordered scalar or iterator outputs, with names only for TABLE.</param>
/// <param name="NativeName">The generated native entry identity.</param>
internal sealed record FunctionSqlModel(string Name, bool Replace, string Options, EquatableArray<FunctionSqlModel.Parameter> Parameters,
    bool Set, EquatableArray<FunctionSqlModel.Column> Columns, string NativeName)
{
    /// <summary>
    /// Excludes managed conversion policies that do not affect SQL catalog identity.
    /// </summary>
    /// <param name="declaration">The validated function declaration.</param>
    /// <param name="scalar">The scalar return contract, or null for an iterator.</param>
    /// <param name="set">The iterator return contract, or null for a scalar.</param>
    /// <param name="nativeName">The exact generated entry identity.</param>
    /// <returns>The minimal immutable rendering inputs.</returns>
    internal static FunctionSqlModel Create(FunctionDeclaration declaration, FunctionType? scalar, SetResult? set, string nativeName)
        => new(declaration.TemplateName, declaration.Replace, declaration.Options,
            new(declaration.Parameters.Select(static value => new Parameter(value.Name, SqlTypeTemplate.Create(value.Type), value.Variadic, value.Default))),
            set is not null, set is null ? new([new Column(null, SqlTypeTemplate.Create(scalar!))]) :
                new(set.Columns.Select((value, index) => new Column(set.Names?[index], SqlTypeTemplate.Create(value)))), nativeName);

    /// <summary>
    /// Retains exact SQL input policy without managed conversion metadata.
    /// </summary>
    /// <param name="Name">The unquoted argument name.</param>
    /// <param name="Type">The typed catalog rendering and ownership policy.</param>
    /// <param name="Variadic">Whether the final input expands an array.</param>
    /// <param name="Default">The exact default expression, or null when required.</param>
    internal sealed record Parameter(string Name, SqlTypeTemplate Type, bool Variadic, string? Default);

    /// <summary>
    /// Retains exact SQL output identity without iterator implementation or conversion metadata.
    /// </summary>
    /// <param name="Name">The unquoted TABLE column name, or null for scalar/SETOF.</param>
    /// <param name="Type">The typed catalog rendering and ownership policy.</param>
    internal sealed record Column(string? Name, SqlTypeTemplate Type);
}
