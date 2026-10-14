using System.Collections.Concurrent;
using System.ComponentModel;

namespace Ankus.CompilerServices;

/// <summary>
/// Registers statically closed scalar datum converters without executing user code or backend lookups.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class PgDatumRegistry
{
    private static readonly ConcurrentDictionary<Type, DatumTypeMapping> s_mappings = new();
    private static readonly ConcurrentDictionary<Type, DatumArrayMapping> s_arrays = new();

    /// <summary>
    /// Registers a value type, its nullable representation and closed array conversions.
    /// </summary>
    /// <typeparam name="T">The closed mapped value type.</typeparam>
    /// <param name="name">The exact SQL type name.</param>
    /// <param name="schema">The fixed schema or current extension schema.</param>
    /// <param name="origin">The declared SQL type ownership.</param>
    /// <param name="converterType">The exact closed converter identity emitted by the generator.</param>
    /// <param name="createConverter">The lazy converter factory.</param>
    /// <param name="canRead">Whether the converter implements the reader contract.</param>
    /// <param name="canWrite">Whether the converter implements the writer contract.</param>
    public static void RegisterValue<T>(string name, string? schema, PgTypeOrigin origin, Type converterType, Func<object> createConverter,
        bool canRead, bool canWrite) where T : struct
    {
        DatumTypeMapping<T> mapping = Register<T>(name, schema, origin, converterType, createConverter, canRead, canWrite);
        s_mappings[typeof(T?)] = mapping;
        RegisterArrays<T?>(mapping);
    }

    /// <summary>
    /// Registers a reference type and its closed arrays without selecting runtime derived types as alternate mappings.
    /// </summary>
    /// <typeparam name="T">The closed mapped reference type.</typeparam>
    /// <param name="name">The exact SQL type name.</param>
    /// <param name="schema">The fixed schema or current extension schema.</param>
    /// <param name="origin">The declared SQL type ownership.</param>
    /// <param name="converterType">The exact closed converter identity emitted by the generator.</param>
    /// <param name="createConverter">The lazy converter factory.</param>
    /// <param name="canRead">Whether the converter implements the reader contract.</param>
    /// <param name="canWrite">Whether the converter implements the writer contract.</param>
    public static void RegisterReference<T>(string name, string? schema, PgTypeOrigin origin, Type converterType, Func<object> createConverter,
        bool canRead, bool canWrite) where T : class => Register<T>(name, schema, origin, converterType, createConverter, canRead, canWrite);

    /// <summary>
    /// Registers a finite range and its arrays around an already registered scalar bound without backend access.
    /// </summary>
    /// <typeparam name="T">The closed mapped value-type bound.</typeparam>
    /// <param name="name">The exact SQL range type name.</param>
    /// <param name="schema">The fixed range schema or current extension schema.</param>
    /// <param name="origin">The range's declared SQL ownership.</param>
    public static void RegisterRange<T>(string name, string? schema, PgTypeOrigin origin) where T : struct
    {
        DatumTypeMapping bound = Require(typeof(T));
        Register<PgRange<T>>(name, schema, origin, typeof(DatumRangeConverter<T>), () => new DatumRangeConverter<T>(bound),
            bound.CanRead, bound.CanWrite, bound);
    }

    /// <summary>
    /// Finds only the requested managed scalar identity, never a first matching PostgreSQL OID.
    /// </summary>
    internal static DatumTypeMapping? Find(Type type) => s_mappings.GetValueOrDefault(type);

    /// <summary>
    /// Finds the closed target-directed vector or shaped array descriptor.
    /// </summary>
    internal static DatumArrayMapping? FindArray(Type type) => s_arrays.GetValueOrDefault(type);

    /// <summary>
    /// Rejects a mapped array at a boundary that has discarded its declared converter.
    /// </summary>
    internal static void RejectOrdinaryArray(Type type)
    {
        if (FindArray(type) is not null)
        {
            throw new NotSupportedException("Mapped datum arrays require PgDatum.Read<T>(); ordinary array conversion is not supported.");
        }
    }

    /// <summary>
    /// Requires the generated mapping for a requested scalar type.
    /// </summary>
    internal static DatumTypeMapping Require(Type type) => Find(type) ??
        throw new NotSupportedException($"Type '{type}' has no generated PgDatumType mapping.");

    /// <summary>
    /// Rejects ordinary result APIs whose detached canonical materialization does not select a datum converter.
    /// </summary>
    internal static void RejectOrdinaryResult<T>()
    {
        RejectOrdinaryArray(typeof(T));

        if (Find(typeof(T)) is not null)
        {
            throw new NotSupportedException("Mapped datum results require PgDatum.Read<T>(); ordinary typed result conversion is not supported.");
        }
    }

    /// <summary>
    /// Validates metadata and atomically registers the root contract without constructing its converter.
    /// </summary>
    private static DatumTypeMapping<T> Register<T>(string name, string? schema, PgTypeOrigin origin, Type converterType, Func<object> createConverter,
        bool canRead, bool canWrite, DatumTypeMapping? rangeBound = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(converterType);
        ArgumentNullException.ThrowIfNull(createConverter);
        if (schema is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(schema);
        }

        if (origin is not PgTypeOrigin.ThisExtension and not PgTypeOrigin.External)
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }

        if (origin == PgTypeOrigin.External && schema is null)
        {
            throw new ArgumentException("An external datum mapping requires an explicit schema.", nameof(schema));
        }

        if (!canRead && !canWrite)
        {
            throw new ArgumentException("A datum mapping requires a reader or writer.", nameof(canRead));
        }

        SpiType.RejectBuiltinMapping<T>();
        if (PgTypeRegistry.Find(typeof(T)) is not null || PgEnumRegistry.Find(typeof(T)) is not null)
        {
            throw new InvalidOperationException($"Type '{typeof(T)}' already has a generated PostgreSQL mapping.");
        }

        var mapping = new DatumTypeMapping<T>(name, schema, origin, converterType, createConverter, canRead, canWrite, rangeBound);
        DatumTypeMapping registered = s_mappings.GetOrAdd(typeof(T), mapping);
        if (!registered.Matches(name, schema, origin, converterType, canRead, canWrite, rangeBound))
        {
            throw new InvalidOperationException($"Type '{typeof(T)}' already has a different generated PostgreSQL datum mapping.");
        }

        RegisterArrays<T>(registered);
        return (DatumTypeMapping<T>)registered;
    }

    /// <summary>
    /// Closes target-directed array conversions around the first registered scalar converter.
    /// </summary>
    private static void RegisterArrays<T>(DatumTypeMapping scalar)
    {
        var mapping = new DatumArrayMapping<T>(scalar);
        s_arrays.TryAdd(typeof(T[]), mapping);
        s_arrays.TryAdd(typeof(PgArray<T>), mapping);
        PgArrayViews.Register<T>();
    }
}
