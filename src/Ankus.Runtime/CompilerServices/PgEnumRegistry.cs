using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace Ankus.CompilerServices;

/// <summary>
/// Receives generated closed enum conversions without reflecting over enum members at runtime.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class PgEnumRegistry
{
    private static readonly ConcurrentDictionary<Type, EnumMapping> s_scalars = new();
    private static readonly ConcurrentDictionary<Type, EnumMapping> s_arrays = new();

    /// <summary>
    /// Registers a generated enum contract and its nullable, vector, and shaped-array conversions.
    /// </summary>
    /// <typeparam name="T">The statically referenced enum.</typeparam>
    /// <param name="name">The SQL type identifier.</param>
    /// <param name="schema">The fixed schema, or null for the installation schema.</param>
    /// <param name="labels">The distinct values and labels in declaration order.</param>
    public static void Register<T>(string name, string? schema, KeyValuePair<T, string>[] labels) where T : struct, Enum
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(labels);
        var mapping = new EnumMapping<T>(name, schema, labels);
        if (!s_scalars.TryAdd(typeof(T), mapping))
        {
            throw new InvalidOperationException($"Enum '{typeof(T)}' has already been registered.");
        }

        s_scalars[typeof(T?)] = mapping;
        s_arrays[typeof(T[])] = mapping;
        s_arrays[typeof(T?[])] = mapping;
        s_arrays[typeof(PgArray<T>)] = mapping;
        s_arrays[typeof(PgArray<T?>)] = mapping;
        PgArrayViews.Register<T>();
        PgArrayViews.Register<T?>();
    }

    /// <summary>
    /// Gets a generated scalar mapping, including nullable enums, or null for other types.
    /// </summary>
    internal static EnumMapping? Find(Type type) => s_scalars.GetValueOrDefault(type);

    /// <summary>
    /// Gets a generated vector or shaped-array mapping, or null for other types.
    /// </summary>
    internal static EnumMapping? FindArray(Type type) => s_arrays.GetValueOrDefault(type);

    /// <summary>
    /// Requires a generated enum mapping without backend access.
    /// </summary>
    internal static EnumMapping Require(Type type) => Find(type) ?? throw new NotSupportedException($"Enum '{type}' has no generated PgEnum mapping.");

    /// <summary>
    /// Matches a current backend OID without retaining catalog identities across DDL or backend calls.
    /// </summary>
    internal static EnumMapping FindOid(uint oid)
    {
        foreach (EnumMapping mapping in s_scalars.Values.Distinct())
        {
            if (mapping.GetOid(missingOk: true) == oid)
            {
                return mapping;
            }
        }

        throw new NotSupportedException($"PostgreSQL enum OID {oid} has no generated managed mapping.");
    }
}
