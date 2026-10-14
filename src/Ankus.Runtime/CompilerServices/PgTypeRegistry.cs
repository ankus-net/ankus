using System.Collections.Concurrent;
using System.ComponentModel;

namespace Ankus.CompilerServices;

/// <summary>
/// Receives closed, generated custom-type conversions without runtime reflection or code generation.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class PgTypeRegistry
{
    private static readonly ConcurrentDictionary<Type, CustomTypeMapping> s_scalars = new();
    private static readonly ConcurrentDictionary<Type, CustomTypeMapping> s_arrays = new();

    /// <summary>
    /// Registers a value type and its nullable and array representations.
    /// </summary>
    /// <typeparam name="T">The generated managed value type.</typeparam>
    /// <param name="name">The SQL type name.</param>
    /// <param name="schema">The fixed schema, or null for the installation schema.</param>
    /// <param name="createCodec">The generated factory, first invoked inside the managed error boundary.</param>
    public static void RegisterValue<T>(string name, string? schema, Func<PgTypeCodec<T>> createCodec) where T : struct
        => Register<T, T?>(name, schema, createCodec);

    /// <summary>
    /// Registers a reference type and its array representations.
    /// </summary>
    /// <typeparam name="T">The generated managed reference type.</typeparam>
    /// <param name="name">The SQL type name.</param>
    /// <param name="schema">The fixed schema, or null for the installation schema.</param>
    /// <param name="createCodec">The generated factory, first invoked inside the managed error boundary.</param>
    public static void RegisterReference<T>(string name, string? schema, Func<PgTypeCodec<T>> createCodec) where T : class
        => Register<T, T?>(name, schema, createCodec);

    /// <summary>
    /// Registers a packed value and its checked varlena views as alternative representations of one SQL type.
    /// </summary>
    /// <typeparam name="T">The statically validated native payload.</typeparam>
    /// <param name="name">The SQL type name.</param>
    /// <param name="schema">The fixed schema, or null for the installation schema.</param>
    /// <param name="size">The statically computed size.</param>
    /// <param name="padding">Offset and length pairs of the layout's padding.</param>
    /// <param name="createCodec">The single shared codec factory.</param>
    public static void RegisterNative<T>(string name, string? schema, int size, int[] padding, Func<PgTypeCodec<T>> createCodec)
        where T : unmanaged
    {
        Register<T, T?>(name, schema, createCodec);
        var mapping = new PgVarlenaTypeMapping<T>(name, schema, size, NativeLayoutPadding.Validate(padding, size), Require(typeof(T)));
        s_scalars[typeof(PgVarlena<T>)] = mapping;
        s_arrays[typeof(PgVarlena<T>[])] = mapping;
        s_arrays[typeof(PgArray<PgVarlena<T>>)] = mapping;
        PgArrayViews.Register<PgVarlena<T>>();
    }

    /// <summary>
    /// Requires the statically generated native layout without constructing a text codec.
    /// </summary>
    internal static PgVarlenaTypeMapping<T> RequireVarlena<T>() where T : unmanaged =>
        Find(typeof(PgVarlena<T>)) as PgVarlenaTypeMapping<T> ??
        throw new NotSupportedException($"Type '{typeof(T)}' has no generated native-layout PgType mapping.");

    /// <summary>
    /// Converts a text input to the stored representation through the generated codec.
    /// </summary>
    /// <typeparam name="T">The generated managed type.</typeparam>
    /// <param name="input">The UTF-8 text transport.</param>
    /// <returns>The owned storage transport.</returns>
    public static NativeValue Input<T>(NativeValue input)
    {
        CustomTypeMapping mapping = Require(typeof(T));
        return mapping.Write(mapping.Parse(input.ReadString()));
    }

    /// <summary>
    /// Formats a stored value through the generated codec.
    /// </summary>
    /// <typeparam name="T">The generated managed type.</typeparam>
    /// <param name="input">The borrowed storage transport.</param>
    /// <returns>The owned UTF-8 text transport.</returns>
    public static NativeValue Output<T>(NativeValue input)
    {
        CustomTypeMapping mapping = Require(typeof(T));
        return NativeValue.FromString(mapping.Format(mapping.Read(input)));
    }

    /// <summary>
    /// Validates and writes a complete binary protocol payload through the generated codec.
    /// </summary>
    /// <typeparam name="T">The generated managed type.</typeparam>
    /// <param name="input">The borrowed storage or binary input transport.</param>
    /// <returns>The owned binary payload.</returns>
    public static NativeValue Binary<T>(NativeValue input)
    {
        CustomTypeMapping mapping = Require(typeof(T));
        return mapping.Write(mapping.Read(input));
    }

    /// <summary>
    /// Registers the complete set of statically instantiated conversions.
    /// </summary>
    private static void Register<T, TOptional>(string name, string? schema, Func<PgTypeCodec<T>> createCodec)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(createCodec);
        if (schema is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(schema);
        }

        var mapping = new CustomTypeMapping<T, TOptional>(name, schema, createCodec);
        if (!s_scalars.TryAdd(typeof(T), mapping))
        {
            throw new InvalidOperationException($"Type '{typeof(T)}' already has a generated PostgreSQL mapping.");
        }

        s_scalars[typeof(TOptional)] = mapping;
        s_arrays[typeof(T[])] = mapping;
        s_arrays[typeof(TOptional[])] = mapping;
        s_arrays[typeof(PgArray<T>)] = mapping;
        s_arrays[typeof(PgArray<TOptional>)] = mapping;
        PgArrayViews.Register<T>();
        PgArrayViews.Register<TOptional>();
    }

    /// <summary>
    /// Gets a generated scalar mapping, including its nullable form.
    /// </summary>
    internal static CustomTypeMapping? Find(Type type) => s_scalars.GetValueOrDefault(type);

    /// <summary>
    /// Gets a generated vector or shaped-array mapping.
    /// </summary>
    internal static CustomTypeMapping? FindArray(Type type) => s_arrays.GetValueOrDefault(type);

    /// <summary>
    /// Selects a tuple cell's declared SQL contract when its runtime value is a registered variant.
    /// </summary>
    internal static CustomTypeMapping? FindValue(object? value, uint oid)
    {
        if (value is null || IsBuiltIn(oid))
        {
            return null;
        }

        foreach (CustomTypeMapping mapping in s_scalars.Values)
        {
            if (mapping.Accepts(value) && mapping.GetOid(missingOk: true) == oid)
            {
                return mapping;
            }
        }

        return null;
    }

    /// <summary>
    /// Selects the declared SQL element contract for an erased, possibly covariant tuple vector.
    /// </summary>
    internal static CustomTypeMapping? FindArrayValue(Array value, uint oid)
    {
        if (IsBuiltIn(oid))
        {
            return null;
        }

        foreach (CustomTypeMapping mapping in s_scalars.Values)
        {
            if (!mapping.AcceptsArray(value))
            {
                continue;
            }

            uint elementOid = mapping.GetOid(missingOk: true);
            if (elementOid != 0 && NativeBackend.EnumArrayOid(elementOid) == oid)
            {
                return mapping;
            }
        }

        return null;
    }

    /// <summary>
    /// Recognizes a type OID that no generated mapping can own.
    /// </summary>
    /// <param name="oid">The declared SQL type, array type or base type.</param>
    /// <returns>
    /// Whether the OID belongs to a built-in type. Extension types are created after initdb, at or above PostgreSQL's
    /// <c>FirstNormalObjectId</c>; zero stays searchable because a mapping whose type is missing reports it.
    /// </returns>
    private static bool IsBuiltIn(uint oid) => oid is > 0 and < 16384;

    /// <summary>
    /// Requires an already generated mapping without invoking a backend operation.
    /// </summary>
    internal static CustomTypeMapping Require(Type type) => Find(type) ??
        throw new NotSupportedException($"Type '{type}' has no generated PgType mapping.");

    /// <summary>
    /// Matches a current backend OID without caching identities across DDL or backend calls.
    /// </summary>
    internal static CustomTypeMapping FindOid(uint oid)
    {
        foreach (CustomTypeMapping mapping in s_scalars.Values.Distinct())
        {
            if (!mapping.IsAlternate && mapping.GetOid(missingOk: true) == oid)
            {
                return mapping;
            }
        }

        throw new NotSupportedException($"PostgreSQL type OID {oid} has no generated PgType mapping.");
    }
}
