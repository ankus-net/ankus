using System;
using System.Collections.Generic;
using System.Linq;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Contains a managed copy of one SPI result row, independent of PostgreSQL memory contexts.
/// </summary>
public sealed class SpiRow
{
    private readonly object?[] _values;
    private readonly IReadOnlyList<SpiColumn> _columns;
    private uint[]? _typeOids;

    /// <summary>
    /// Creates a row from converted native cells and shared column metadata.
    /// </summary>
    /// <param name="values">The managed cells.</param>
    /// <param name="columns">The query's columns.</param>
    internal SpiRow(object?[] values, IReadOnlyList<SpiColumn> columns)
    {
        _values = values;
        _columns = columns;
    }

    /// <summary>
    /// Gets a cell by its zero-based column ordinal. SQL NULL is represented by null.
    /// A cell whose type has no managed conversion throws; read it with its <see cref="PgDatumTypeAttribute"/> type.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The managed cell.</returns>
    public object? this[int ordinal] => DetachedDatum.Expose(_values[ordinal]);

    /// <summary>
    /// Gets a cell by its exact, case-sensitive column name. Duplicate names resolve to the first column.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <returns>The managed cell or null for SQL NULL.</returns>
    public object? this[string name] => this[GetOrdinal(name)];

    /// <summary>
    /// Gets the number of cells in this row.
    /// </summary>
    public int Count => _values.Length;

    /// <summary>
    /// Replaces a local cell and its type with a supported managed datum. The edit does not update PostgreSQL or result metadata.
    /// Raw datums and <see cref="PgDatumTypeAttribute"/> values are copied into independent managed storage before
    /// assignment; relations retain only their OIDs.
    /// </summary>
    /// <typeparam name="T">The replacement type, including its type when the value is null.</typeparam>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <param name="value">The replacement value.</param>
    public void Set<[PgConvertible] T>(int ordinal, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, Count);
        uint typeOid;
        object? managedValue;
        if (PgDatumRegistry.Find(typeof(T)) is { } mapping)
        {
            typeOid = mapping.GetOid();
            managedValue = mapping.WriteCell(value, typeOid);
        }
        else if (PgDatumRegistry.FindArray(typeof(T)) is { } arrays)
        {
            typeOid = arrays.GetOid();
            managedValue = arrays.WriteCell(value, typeOid);
        }
        else
        {
            if (value is not null)
            {
                PgDatumRegistry.RejectOrdinaryArray(value.GetType());
            }

            (typeOid, managedValue) = value is PgDatum datum
                ? (datum.TypeOid, NativeBackend.DetachDatum(datum))
                : (SpiType.GetOid(value), PgRelationIdentity.Snapshot(value));
        }

        _typeOids ??= [.. _columns.Select(static column => column.TypeOid)];
        _values[ordinal] = managedValue;
        _typeOids[ordinal] = typeOid;
    }

    /// <summary>
    /// Replaces a local cell by exact name. Raw datums retain their PostgreSQL type identity and are copied into managed storage.
    /// </summary>
    /// <typeparam name="T">The replacement managed type.</typeparam>
    /// <param name="name">The column name; duplicate names resolve to the first column.</param>
    /// <param name="value">The replacement value.</param>
    public void Set<[PgConvertible] T>(string name, T value) => Set(GetOrdinal(name), value);

    /// <summary>
    /// Gets a cell's PostgreSQL type OID, including its original domain OID until replaced by Set.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The cell's current type OID.</returns>
    public uint GetTypeOid(int ordinal) => _typeOids is null ? _columns[ordinal].TypeOid : _typeOids[ordinal];

    /// <summary>
    /// Gets a cell's current PostgreSQL type OID by exact column name.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <returns>The cell's current type OID.</returns>
    public uint GetTypeOid(string name) => GetTypeOid(GetOrdinal(name));

    /// <summary>
    /// Gets a typed cell without implicit numeric or textual conversion. Temporal cells also accept exact .NET conversions.
    /// SQL NULL is accepted for nullable value types and reference types.
    /// A <see cref="PgDatumTypeAttribute"/> type, or an array of one, uses its reader when the cell has exactly the mapped
    /// PostgreSQL type. <see cref="PgDatum"/> rebuilds the cell, including SQL NULL, as a datum of its type owned by the
    /// current callback. Reading PgRelation or its arrays opens fresh references that the caller must dispose.
    /// </summary>
    /// <typeparam name="T">The expected managed type.</typeparam>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The cell value.</returns>
    public T Get<[PgConvertible] T>(int ordinal)
    {
        if (typeof(T) == typeof(PgDatum))
        {
            return GenericValue.As<T>(ReadRaw(_values[ordinal], GetTypeOid(ordinal)));
        }

        // A scalar cell that already has the requested type came from a built-in or generated conversion; mapped types
        // are stored as their PostgreSQL representation, so no mapping applies. Copies without a conversion and arrays
        // keep their checks.
        if (_values[ordinal] is T cell && cell is not (DetachedDatum or Array))
        {
            return cell;
        }

        if (PgDatumRegistry.Find(typeof(T)) is { } mapping)
        {
            return Convert<T>(mapping.ReadCell(_values[ordinal], GetTypeOid(ordinal)));
        }

        if (PgDatumRegistry.FindArray(typeof(T)) is { } arrays)
        {
            return (T)arrays.ReadCell(_values[ordinal], GetTypeOid(ordinal), typeof(T))!;
        }

        return Convert<T>(_values[ordinal]);
    }

    /// <summary>
    /// Gets a typed cell by an exact, case-sensitive column name. Duplicate names resolve to the first column.
    /// </summary>
    /// <typeparam name="T">The expected managed type.</typeparam>
    /// <param name="name">The column name.</param>
    /// <returns>The cell value.</returns>
    public T Get<[PgConvertible] T>(string name) => Get<T>(GetOrdinal(name));

    /// <summary>
    /// Resolves an exact, case-sensitive column name to its zero-based ordinal. Duplicate names resolve to the first column.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <returns>The zero-based ordinal.</returns>
    public int GetOrdinal(string name) => SpiColumn.GetOrdinal(_columns, name);

    /// <summary>
    /// Rebuilds a row or composite cell as a datum of its exact type owned by the current callback, as pgrx's raw cell
    /// reads return a datum with its type and NULL flag.
    /// </summary>
    /// <param name="cell">The managed cell, or null for SQL NULL.</param>
    /// <param name="typeOid">The cell's PostgreSQL type OID.</param>
    /// <returns>The rebuilt datum.</returns>
    internal static PgDatum ReadRaw(object? cell, uint typeOid)
    {
        var lifetime = new PgDatumLifetime(PgMemoryContext.Current);
        return cell is null ? new PgDatum(0, typeOid, true, lifetime) : NativeBackend.RebuildCell(cell, typeOid, lifetime);
    }

    /// <summary>
    /// Reads an empty result: nullable types receive null, as for SQL NULL, and other types fail without implying a NULL cell.
    /// </summary>
    /// <typeparam name="T">The expected managed type.</typeparam>
    /// <returns>Null for a nullable type.</returns>
    /// <exception cref="InvalidOperationException">The type cannot represent a missing row.</exception>
    internal static T ConvertMissing<T>()
        => default(T) is null
            ? default!
            : throw new InvalidOperationException("The query returned no rows; read a nullable type to treat an empty result as SQL NULL.");

    /// <summary>
    /// Converts a managed cell while preserving the distinction between SQL NULL and a default value.
    /// </summary>
    /// <typeparam name="T">The expected managed type.</typeparam>
    /// <param name="value">The managed cell.</param>
    /// <returns>The typed value.</returns>
    internal static T Convert<T>(object? value)
    {
        PgDatumRegistry.RejectOrdinaryArray(typeof(T));
        if (value is DetachedDatum detached)
        {
            throw detached.Unsupported();
        }

        if (value is not null)
        {
            PgDatumRegistry.RejectOrdinaryArray(value.GetType());
        }

        if (value is Array source && typeof(T).IsArray && source.GetType() != typeof(T) &&
            (PgEnumRegistry.FindArray(source.GetType()) is not null || PgEnumRegistry.FindArray(typeof(T)) is not null ||
             PgTypeRegistry.FindArray(source.GetType()) is not null || PgTypeRegistry.FindArray(typeof(T)) is not null))
        {
            if (source.GetType() == typeof(byte[]) || typeof(T) == typeof(byte[]))
            {
                throw new InvalidCastException("PostgreSQL mapped arrays and binary values have distinct type identities.");
            }

            if (value is T && PgTypeRegistry.FindArray(typeof(T)) is { IsReferenceType: true } custom)
            {
                return (T)custom.Convert(custom.Wrap(source), typeof(T));
            }

            return (T)SpiArray.Convert(SpiArray.Wrap(source), typeof(T));
        }

        if (value is T result)
        {
            return result;
        }

        if (value is null)
        {
            if (default(T) is null)
            {
                return default!;
            }

            throw new InvalidOperationException("SQL NULL cannot be read as a non-nullable managed value.");
        }

        if (value is PgNumeric numeric && (typeof(T) == typeof(decimal) || typeof(T) == typeof(decimal?)))
        {
            return GenericValue.As<T>(numeric.ToDecimal());
        }

        if (value is string characters && (typeof(T) == typeof(System.Text.Rune) || typeof(T) == typeof(System.Text.Rune?)))
        {
            return GenericValue.As<T>(PgRuneText.Parse(characters));
        }

        if (value is System.Text.Rune rune && typeof(T) == typeof(string))
        {
            return GenericValue.As<T>(rune.ToString());
        }

        if (value is PgRelationIdentity relation && typeof(T) == typeof(PgRelation))
        {
            return GenericValue.As<T>(PgRelation.Open(relation.Oid));
        }

        if (value is decimal number && (typeof(T) == typeof(PgNumeric) || typeof(T) == typeof(PgNumeric?)))
        {
            return GenericValue.As<T>(PgNumeric.FromDecimal(number));
        }

        if (value is IPgArray array)
        {
            return (T)SpiArray.Convert(array, typeof(T));
        }

        if (value is IPgRange range)
        {
            return (T)SpiRange.Convert(range, typeof(T));
        }

        if (PgTypeRegistry.Find(typeof(T)) is { } mapping)
        {
            return (T)mapping.ConvertScalar(value);
        }

        if (value is PgInet address && typeof(T) == typeof(System.Net.IPAddress))
        {
            return GenericValue.As<T>(address.ToIPAddress());
        }

        if (value is System.Net.IPAddress ip && (typeof(T) == typeof(PgInet) || typeof(T) == typeof(PgInet?)))
        {
            return GenericValue.As<T>(new PgInet(ip));
        }

        if (value is PgCidr network && (typeof(T) == typeof(System.Net.IPNetwork) || typeof(T) == typeof(System.Net.IPNetwork?)))
        {
            return GenericValue.As<T>(network.ToIPNetwork());
        }

        if (value is System.Net.IPNetwork ipNetwork && (typeof(T) == typeof(PgCidr) || typeof(T) == typeof(PgCidr?)))
        {
            return GenericValue.As<T>(new PgCidr(ipNetwork));
        }

        if (value is Array vector && value.GetType() != typeof(byte[]))
        {
            return (T)SpiArray.Convert(SpiArray.Wrap(vector), typeof(T));
        }

        return (T)SpiTemporal.Convert(value, typeof(T));
    }
}
