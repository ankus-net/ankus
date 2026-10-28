using System.Collections.Generic;
using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Places compiler-only schema markers at typed SQL identifier boundaries, never inside authored SQL text.
/// </summary>
internal static class SqlSchemaTemplate
{
    /// <summary>
    /// Marks the extension's default schema or quotes an explicitly declared schema.
    /// </summary>
    /// <param name="schema">The explicit schema, or null for the extension default.</param>
    /// <returns>A qualified-name prefix; NUL is reserved because authored SQL rejects it.</returns>
    internal static string Prefix(string? schema) => schema is null ? "\0" : SqlText.Identifier(schema) + ".";

    /// <summary>
    /// Formats a type with default-schema markers only for extension-owned declarations.
    /// </summary>
    /// <param name="type">The resolved datum contract.</param>
    /// <param name="providers">The declared SQL providers, when available.</param>
    /// <returns>The type template, retaining built-in and external type resolution.</returns>
    internal static string Type(FunctionType type, SqlTypeProviders? providers = null)
        => SqlTypeTemplate.Create(type).Emit(providers);

    /// <summary>
    /// Formats a function identity while excluding injected managed parameters.
    /// </summary>
    /// <param name="declaration">The validated function name.</param>
    /// <param name="parameters">The SQL argument contracts.</param>
    /// <param name="providers">The declared SQL providers.</param>
    /// <returns>A schema-aware function identity.</returns>
    internal static string Function(FunctionDeclaration declaration, IEnumerable<FunctionType> parameters, SqlTypeProviders providers)
        => declaration.TemplateName + "(" + string.Join(",", parameters.Select(type => Type(type, providers))) + ")";
}
