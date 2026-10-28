using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Applies declarative SQL suppression or replacement while preserving native wrappers and installation dependencies.
/// </summary>
internal static class SqlGeneration
{
    /// <summary>
    /// Applies detached SQL policy while retaining compiled contracts and dependency nodes.
    /// </summary>
    /// <param name="options">The optional immutable declaration options.</param>
    /// <param name="entity">The declaration's graph node.</param>
    /// <param name="related">Additional declarations owned by this replacement.</param>
    /// <param name="substitutions">Context-specific tokens and their values; null marks a token that requires the binary protocol.</param>
    /// <param name="graph">The installation graph and diagnostic sink.</param>
    /// <returns>Whether this policy permits schema relocation.</returns>
    internal static bool ApplyOptions(SqlDeclarationOptions? options, SqlEntity entity, IReadOnlyList<SqlEntity> related,
        IReadOnlyList<(string Token, string? Value)> substitutions, SqlGraph graph)
    {
        foreach (SqlEntity member in related)
        {
            member.Owner = entity;
        }

        if (options is null)
        {
            return true;
        }

        bool enabled = options.GenerateSql;
        string? sql = options.Sql;
        Location? sqlLocation = graph.Resolve(options.Locations.Sql) ?? entity.Location;
        if (!enabled && sql is not null)
        {
            graph.Error(graph.Resolve(options.Locations.GenerateSql) ?? entity.Location, SqlGraphDiagnostics.s_disabledReplacement);
            return false;
        }

        if (sql is not null && SqlGraph.TextError(sql, null, SqlGraphDiagnostics.s_replacementZero, SqlGraphDiagnostics.s_replacementUnicode) is { } error)
        {
            graph.Error(sqlLocation, error);
            return false;
        }

        if (!enabled)
        {
            entity.Sql = string.Empty;
            foreach (SqlEntity member in related)
            {
                member.Sql = string.Empty;
            }
        }
        else if (sql is not null)
        {
            sql = sql.Replace("@MODULE_PATHNAME@", "MODULE_PATHNAME");
            foreach ((string token, string? value) in substitutions)
            {
                if (value is not null)
                {
                    sql = sql.Replace(token, value);
                }
                else if (sql.IndexOf(token, StringComparison.Ordinal) >= 0)
                {
                    graph.Error(graph.Resolve(options.Locations.BinaryProtocol) ?? sqlLocation, SqlGraphDiagnostics.s_binaryToken, token);
                    return false;
                }
            }

            graph.Replace(entity, sql, related);
            return options.SqlRelocatable;
        }

        return true;
    }
}
