using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Matches explicit catalog bindings to generated declarations and custom SQL provider inventories.
/// </summary>
/// <param name="graph">The installation graph and diagnostic sink.</param>
internal sealed class SqlTypeProviders(SqlGraph graph)
{
    private readonly Dictionary<(string? Schema, string Name), (SqlEntity Entity, bool Custom)> _providers = [];
    private readonly Dictionary<ManagedTypeIdentity, SqlEntity> _managed = [];

    /// <summary>
    /// Checks whether a named type is supplied by this extension's SQL graph.
    /// </summary>
    /// <param name="reference">The declared type identity.</param>
    /// <returns>Whether selection must qualify the type with the extension's schema.</returns>
    internal bool Contains(SqlTypeReference reference) => _providers.ContainsKey((reference.Schema, reference.Name));

    /// <summary>
    /// Reserves a generated type identity, including declarations whose SQL is disabled or replaced.
    /// </summary>
    /// <param name="name">The exact unquoted type name.</param>
    /// <param name="schema">The fixed schema, or null for an unqualified identity.</param>
    /// <param name="entity">The generated type or enum declaration.</param>
    internal void Reserve(string name, string? schema, SqlEntity entity)
    {
        if (!_providers.ContainsKey((schema, name)))
        {
            _providers.Add((schema, name), (entity, false));
        }
    }

    /// <summary>
    /// Validates declared providers and adds their known schema prerequisites.
    /// </summary>
    /// <param name="providers">The detached assembly provider declarations.</param>
    /// <param name="blocks">Valid inline and file SQL blocks.</param>
    /// <param name="schemas">Declared schema nodes.</param>
    /// <param name="mappings">Validated closed mappings requiring managed provider identities.</param>
    /// <param name="compilation">The compilation owning current provider diagnostic coordinates.</param>
    /// <returns>Whether all provider names are independent of a fixed schema.</returns>
    internal bool Add(EquatableArray<SqlProviderModel> providers, IReadOnlyDictionary<string, SqlEntity> blocks,
        IReadOnlyDictionary<string, SqlEntity> schemas, IReadOnlyList<DatumTypeModel> mappings, GeneratorSourceResolver compilation)
    {
        bool relocatable = true;
        var namedClaims = new HashSet<(string? Schema, string Name)>();
        foreach (SqlProviderModel provider in providers.Where(static item => !item.Function))
        {
            Location? location = provider.Location?.Resolve(compilation);
            Location? blockLocation = provider.BlockLocation?.Resolve(compilation) ?? location;
            Location? nameLocation = provider.NameLocation?.Resolve(compilation) ?? location;
            Location? schemaLocation = provider.SchemaLocation?.Resolve(compilation) ?? location;
            string? sqlId = provider.BlockId;
            string? name = provider.Name;
            string? schema = provider.Schema;
            bool managed = provider.Managed;
            DatumTypeModel? mapping = null;
            if (sqlId is null || string.IsNullOrWhiteSpace(sqlId))
            {
                graph.Error(blockLocation, SqlTypeProviderDiagnostics.s_emptyBlock);
                continue;
            }

            if (sqlId!.Contains('\0') || !SqlText.IsText(sqlId))
            {
                graph.Error(blockLocation, sqlId.Contains('\0') ? SqlTypeProviderDiagnostics.s_blockZero : SqlTypeProviderDiagnostics.s_blockUnicode);
                continue;
            }

            if (managed)
            {
                mapping = provider.Type is { } supplied ? mappings.FirstOrDefault(item => item.Reference.Type == supplied) : null;
                if (mapping is null)
                {
                    graph.Error(nameLocation, SqlTypeProviderDiagnostics.s_unregisteredManaged);
                    continue;
                }

                if (mapping.Reference.External)
                {
                    graph.Error(nameLocation, SqlTypeProviderDiagnostics.s_externalManaged, mapping.Reference.Managed);
                    continue;
                }

                if (provider.SchemaAuthored)
                {
                    graph.Error(schemaLocation, SqlTypeProviderDiagnostics.s_managedSchema);
                    continue;
                }

                name = mapping.Reference.Name;
                schema = mapping.Reference.Schema;
            }

            if (!ValidateIdentifier(name, nameLocation, false) || schema is not null && !ValidateIdentifier(schema, schemaLocation, true))
            {
                continue;
            }

            if (!blocks.TryGetValue(sqlId!, out SqlEntity? block))
            {
                graph.Error(blockLocation, SqlTypeProviderDiagnostics.s_missingBlock, sqlId);
                continue;
            }

            if (mapping is not null && _managed.ContainsKey(mapping.Reference.Type))
            {
                graph.Error(nameLocation, SqlTypeProviderDiagnostics.s_duplicateManaged, mapping.Reference.Managed);
                continue;
            }

            bool claimedName = !managed && !namedClaims.Add((schema, name!));
            bool otherProvider = _providers.TryGetValue((schema, name!), out (SqlEntity Entity, bool Custom) existing) &&
                (!existing.Custom || existing.Entity != block);
            if (claimedName || otherProvider)
            {
                string identity = new SqlTypeReference(name!, schema).Sql;
                graph.Error(nameLocation, SqlTypeProviderDiagnostics.s_duplicateCatalog, identity);
                continue;
            }

            string qualified = new SqlTypeReference(name!, schema).Sql;
            block.SelectionNames.UnionWith([name!, qualified]);
            block.Attachments.Add("TYPE " + (schema is null ? "\0" : string.Empty) + qualified);
            _providers[(schema, name!)] = (block, true);
            if (mapping is not null)
            {
                _managed.Add(mapping.Reference.Type, block);
            }

            if (schema is not null)
            {
                relocatable = false;
                if (schemas.TryGetValue(schema, out SqlEntity? dependency))
                {
                    block.Dependencies.Add(dependency);
                }
            }
        }

        foreach (DatumTypeModel mapping in mappings)
        {
            if (!mapping.Reference.External && !_managed.ContainsKey(mapping.Reference.Type))
            {
                graph.Error(mapping.Location?.Resolve(compilation), SqlTypeProviderDiagnostics.s_missingManaged, mapping.Reference.Managed);
            }

            if (!mapping.Reference.External && mapping.RangeBound is { External: false } bound &&
                _managed.TryGetValue(mapping.Reference.Type, out SqlEntity? rangeProvider) &&
                _managed.TryGetValue(bound.Type, out SqlEntity? boundProvider) && rangeProvider != boundProvider)
            {
                rangeProvider.Dependencies.Add(boundProvider);
            }
        }

        return relocatable;
    }

    /// <summary>
    /// Validates an exact catalog identifier without folding, trimming or truncating it.
    /// </summary>
    /// <param name="value">The authored unquoted catalog type or schema name.</param>
    /// <param name="location">The exact authored expression requiring correction.</param>
    /// <param name="schema">Whether the identifier names a fixed schema.</param>
    /// <returns>Whether PostgreSQL can retain every UTF-8 byte of the complete identifier.</returns>
    private bool ValidateIdentifier(string? value, Location? location, bool schema)
    {
        if (value is null || value.Length == 0)
        {
            graph.Error(location, schema ? SqlTypeProviderDiagnostics.s_emptySchema : SqlTypeProviderDiagnostics.s_emptyName);
            return false;
        }

        if (value.Contains('\0'))
        {
            graph.Error(location, schema ? SqlTypeProviderDiagnostics.s_schemaZero : SqlTypeProviderDiagnostics.s_nameZero);
            return false;
        }

        if (!SqlText.IsText(value))
        {
            graph.Error(location, schema ? SqlTypeProviderDiagnostics.s_schemaUnicode : SqlTypeProviderDiagnostics.s_nameUnicode);
            return false;
        }

        if (Encoding.UTF8.GetByteCount(value) > 63)
        {
            graph.Error(location, schema ? SqlTypeProviderDiagnostics.s_schemaLength : SqlTypeProviderDiagnostics.s_nameLength);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Adds a leaf-type prerequisite for a raw or named composite signature slot.
    /// </summary>
    /// <param name="consumer">The generated function or aggregate helper.</param>
    /// <param name="contract">The scalar, array or output-column contract.</param>
    /// <param name="requireComplete">Whether the complete type must precede this consumer even along an explicit reverse dependency.</param>
    internal void Require(SqlEntity consumer, FunctionType? contract, bool requireComplete = false)
    {
        DatumTypeReference? mapping = (contract?.Element ?? contract)?.DatumType;
        if (mapping is not null)
        {
            if (!mapping.External && _managed.TryGetValue(mapping.Type, out SqlEntity? managed))
            {
                if (requireComplete)
                {
                    consumer.Dependencies.Add(managed);
                }
                else
                {
                    consumer.TypeDependencies.Add(managed);
                }
            }

            return;
        }

        SqlTypeReference? binding = (contract?.Element ?? contract)?.Binding;
        if (binding is not null && _providers.TryGetValue((binding.Schema, binding.Name), out (SqlEntity Entity, bool Custom) provider))
        {
            if (provider.Custom && !requireComplete)
            {
                consumer.TypeDependencies.Add(provider.Entity);
            }
            else
            {
                consumer.Dependencies.Add(provider.Entity);
            }
        }
    }
}
