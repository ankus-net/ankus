using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Retains authored custom-function identities without parsing or executing SQL during compilation.
/// </summary>
internal static class SqlFunctionProviders
{
    /// <summary>
    /// Associates declared functions with their custom SQL graph nodes.
    /// </summary>
    /// <param name="providers">The detached assembly provider declarations.</param>
    /// <param name="blocks">The validated custom SQL blocks.</param>
    /// <param name="graph">The graph and its declaration diagnostics.</param>
    /// <param name="compilation">The compiler state owning current diagnostic coordinates.</param>
    internal static void Add(EquatableArray<SqlProviderModel> providers, IReadOnlyDictionary<string, SqlEntity> blocks, SqlGraph graph,
        GeneratorSourceResolver compilation)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (SqlProviderModel provider in providers.Where(static item => item.Function))
        {
            Location? location = provider.Location?.Resolve(compilation);
            Location? blockLocation = provider.BlockLocation?.Resolve(compilation) ?? location;
            Location? signatureLocation = provider.NameLocation?.Resolve(compilation) ?? location;
            string? blockId = provider.BlockId;
            string? signature = provider.Name;
            if (blockId is null || string.IsNullOrWhiteSpace(blockId))
            {
                graph.Error(blockLocation, SqlFunctionProviderDiagnostics.s_emptyBlock);
                continue;
            }

            if (blockId!.Contains('\0') || !SqlText.IsText(blockId))
            {
                graph.Error(blockLocation, blockId.Contains('\0') ? SqlFunctionProviderDiagnostics.s_blockZero : SqlFunctionProviderDiagnostics.s_blockUnicode);
                continue;
            }

            if (!blocks.TryGetValue(blockId, out SqlEntity? block))
            {
                graph.Error(blockLocation, SqlFunctionProviderDiagnostics.s_missingBlock, blockId);
                continue;
            }

            if (signature is null || string.IsNullOrWhiteSpace(signature))
            {
                graph.Error(signatureLocation, SqlFunctionProviderDiagnostics.s_emptySignature);
                continue;
            }

            if (signature!.Contains('\0') || !SqlText.IsText(signature))
            {
                graph.Error(signatureLocation, signature.Contains('\0') ? SqlFunctionProviderDiagnostics.s_signatureZero : SqlFunctionProviderDiagnostics.s_signatureUnicode);
                continue;
            }

            if (!claimed.Add(signature!))
            {
                graph.Error(signatureLocation, SqlFunctionProviderDiagnostics.s_duplicateSignature, signature);
                continue;
            }

            block.SelectionNames.Add(signature!);
            block.Attachments.Add("FUNCTION " + signature);
        }
    }
}
