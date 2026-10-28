using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Retains one authored Requires or Before entry and the value to correct.
/// </summary>
/// <param name="Value">The authored dependency identifier, or null for a null entry or list.</param>
/// <param name="Location">The authored entry, or the complete list value when the list itself is null.</param>
/// <param name="NullList">Whether the complete list was authored as null.</param>
internal readonly record struct SqlDependencyName(string? Value, GeneratorLocation? Location, bool NullList = false);

/// <summary>
/// Stores authored SQL generation and dependency options independently of compiler attributes.
/// </summary>
/// <param name="Id">The optional explicit dependency identifier.</param>
/// <param name="Requires">The ordered prerequisite names, retaining invalid null values for diagnostics.</param>
/// <param name="Before">The ordered successor names, retaining invalid null values for diagnostics.</param>
/// <param name="GenerateSql">Whether the declaration emits installation SQL.</param>
/// <param name="Sql">The optional exact replacement SQL.</param>
/// <param name="SqlRelocatable">Whether replacement SQL permits schema relocation.</param>
/// <param name="Locations">The authored attribute and option values that diagnostics identify.</param>
internal sealed record SqlDeclarationOptions(string? Id, EquatableArray<SqlDependencyName> Requires, EquatableArray<SqlDependencyName> Before,
    bool GenerateSql, string? Sql, bool SqlRelocatable, SqlDeclarationOptions.Coordinates Locations)
{
    /// <summary>
    /// Extracts authored constants and detached option coordinates while preserving default and invalid-array semantics.
    /// </summary>
    /// <param name="attribute">The optional declaration attribute.</param>
    /// <param name="compilation">The compilation owning the attribute's source coordinates.</param>
    /// <param name="cancellationToken">The current analysis cancellation token.</param>
    /// <returns>The immutable options, or null when no declaration attribute exists.</returns>
    internal static SqlDeclarationOptions? Read(AttributeData? attribute, Compilation compilation, CancellationToken cancellationToken)
        => attribute is null ? null : Create(attribute, compilation, cancellationToken);

    /// <summary>
    /// Reads one present declaration attribute and its authored option coordinates.
    /// </summary>
    private static SqlDeclarationOptions Create(AttributeData attribute, Compilation compilation, CancellationToken cancellationToken)
    {
        string? id = AttributeValues.Get<string?>(attribute, "Id", null);
        string? sql = AttributeValues.Get<string?>(attribute, "Sql", null);
        return new(id, Names("Requires"), Names("Before"), AttributeValues.Get(attribute, "GenerateSql", true), sql,
            AttributeValues.Get(attribute, "SqlRelocatable", false),
            new(GeneratorLocation.Create(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation(), compilation),
                id is null ? null : Option("Id"), Option("GenerateSql"), sql is null ? null : Option("Sql"), Option("BinaryProtocol")));

        GeneratorLocation? Option(string name)
            => GeneratorLocation.Create(FunctionDeclarationDiagnostics.Option(attribute, name, cancellationToken), compilation);

        EquatableArray<SqlDependencyName> Names(string name)
        {
            var names = new List<SqlDependencyName>();
            foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
            {
                if (argument.Key != name)
                {
                    continue;
                }

                if (argument.Value.IsNull || argument.Value.Kind != TypedConstantKind.Array)
                {
                    names.Add(new(null, Option(name), NullList: true));
                    continue;
                }

                for (int index = 0; index < argument.Value.Values.Length; index++)
                {
                    names.Add(new(argument.Value.Values[index].Value as string, GeneratorLocation.Create(
                        FunctionDeclarationDiagnostics.OptionElement(attribute, name, index, cancellationToken), compilation)));
                }
            }

            return new(names);
        }
    }

    /// <summary>
    /// Enumerates every authored coordinate that graph composition can report.
    /// </summary>
    /// <returns>The attribute, option and dependency-entry coordinates.</returns>
    internal IEnumerable<GeneratorLocation?> SourceLocations()
    {
        yield return Locations.Attribute;
        yield return Locations.Id;
        yield return Locations.GenerateSql;
        yield return Locations.Sql;
        yield return Locations.BinaryProtocol;
        foreach (SqlDependencyName name in Requires.Concat(Before))
        {
            yield return name.Location;
        }
    }

    /// <summary>
    /// Retains declaration-relative coordinates for the authored values that SQL graph diagnostics identify.
    /// </summary>
    /// <param name="Attribute">The complete attribute application.</param>
    /// <param name="Id">The authored Id value, when present.</param>
    /// <param name="GenerateSql">The authored GenerateSql value, when present.</param>
    /// <param name="Sql">The authored Sql value, when present.</param>
    /// <param name="BinaryProtocol">The authored BinaryProtocol value, when present.</param>
    internal sealed record Coordinates(GeneratorLocation? Attribute, GeneratorLocation? Id, GeneratorLocation? GenerateSql,
        GeneratorLocation? Sql, GeneratorLocation? BinaryProtocol);
}
