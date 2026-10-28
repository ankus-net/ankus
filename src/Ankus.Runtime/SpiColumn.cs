using System;
using System.Collections.Generic;

namespace Ankus;

/// <summary>
/// Describes a column returned by a PostgreSQL SPI query.
/// </summary>
public sealed class SpiColumn
{
    /// <summary>
    /// Creates column metadata copied from a PostgreSQL tuple descriptor.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <param name="typeOid">The declared PostgreSQL type OID, including a domain's own OID.</param>
    internal SpiColumn(string name, uint typeOid)
    {
        Name = name;
        TypeOid = typeOid;
    }

    /// <summary>
    /// Gets the column name, preserving PostgreSQL identifier casing.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the declared PostgreSQL type OID.
    /// </summary>
    public uint TypeOid { get; }

    /// <summary>
    /// Resolves an exact, case-sensitive column name to its zero-based ordinal. Duplicate names resolve to the first column.
    /// </summary>
    /// <param name="columns">The result's columns.</param>
    /// <param name="name">The column name.</param>
    /// <returns>The zero-based ordinal.</returns>
    internal static int GetOrdinal(IReadOnlyList<SpiColumn> columns, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (int index = 0; index < columns.Count; index++)
        {
            if (columns[index].Name == name)
            {
                return index;
            }
        }

        throw new ArgumentException($"The SPI result has no column named '{name}'.", nameof(name));
    }
}
