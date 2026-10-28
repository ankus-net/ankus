using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Supplies finite closed borrowed-array factories without runtime generic type construction.
/// </summary>
internal static class PgArrayViews
{
    private static readonly ConcurrentDictionary<Type, PgArrayViewMapping> s_views = CreateBuiltIns();

    /// <summary>
    /// Registers a generated scalar's borrowed collection without invoking converters or catalog lookups.
    /// </summary>
    internal static void Register<T>() => s_views.TryAdd(typeof(PgArrayView<T>), new PgArrayViewMapping<T>());

    /// <summary>
    /// Gets the exact requested closed collection representation.
    /// </summary>
    internal static PgArrayViewMapping? Find(Type type) => s_views.GetValueOrDefault(type);

    /// <summary>
    /// Closes the supported built-in scalar, nullable and range representations for Native AOT.
    /// </summary>
    private static ConcurrentDictionary<Type, PgArrayViewMapping> CreateBuiltIns()
    {
        var views = new ConcurrentDictionary<Type, PgArrayViewMapping>();
        AddValue<bool>(views);
        AddValue<sbyte>(views);
        AddValue<short>(views);
        AddValue<int>(views);
        AddValue<long>(views);
        AddValue<uint>(views);
        AddValue<float>(views);
        AddValue<double>(views);
        AddValue<Guid>(views);
        AddValue<PgItemPointer>(views);
        AddValue<PgTransactionId>(views);
        AddValue<PgJson>(views);
        AddValue<PgJsonb>(views);
        AddValue<PgNumeric>(views);
        AddValue<decimal>(views);
        AddValue<PgDate>(views);
        AddValue<DateOnly>(views);
        AddValue<PgTime>(views);
        AddValue<TimeOnly>(views);
        AddValue<PgTimeTz>(views);
        AddValue<PgTimestamp>(views);
        AddValue<DateTime>(views);
        AddValue<PgTimestampTz>(views);
        AddValue<DateTimeOffset>(views);
        AddValue<PgInterval>(views);
        AddValue<TimeSpan>(views);
        AddValue<PgPoint>(views);
        AddValue<PgLineSegment>(views);
        AddValue<PgLine>(views);
        AddValue<PgBox>(views);
        AddValue<PgCircle>(views);
        AddValue<PgInet>(views);
        AddValue<PgCidr>(views);
        AddValue<System.Net.IPNetwork>(views);
        AddValue<PgRelationIdentity>(views);
        Add<string>(views);
        Add<byte[]>(views);
        Add<PgTextView>(views);
        Add<PgByteaView>(views);
        Add<PgCString>(views);
        Add<PgCStringView>(views);
        Add<PgHeapTuple>(views);
        Add<PgRelation>(views);
        Add<PgPath>(views);
        Add<PgPolygon>(views);
        Add<System.Net.IPAddress>(views);
        Add<PgRange<int>>(views);
        Add<PgRange<long>>(views);
        Add<PgRange<PgNumeric>>(views);
        Add<PgRange<decimal>>(views);
        Add<PgRange<PgDate>>(views);
        Add<PgRange<DateOnly>>(views);
        Add<PgRange<PgTimestamp>>(views);
        Add<PgRange<DateTime>>(views);
        Add<PgRange<PgTimestampTz>>(views);
        Add<PgRange<DateTimeOffset>>(views);
        return views;
    }

    /// <summary>
    /// Adds the required and nullable forms of a built-in value type.
    /// </summary>
    private static void AddValue<T>(ConcurrentDictionary<Type, PgArrayViewMapping> views) where T : struct
    {
        Add<T>(views);
        Add<T?>(views);
    }

    /// <summary>
    /// Adds a statically closed factory to the initial table.
    /// </summary>
    private static void Add<T>(ConcurrentDictionary<Type, PgArrayViewMapping> views)
        => views[typeof(PgArrayView<T>)] = new PgArrayViewMapping<T>();
}

/// <summary>
/// Describes a closed collection independently of its element's managed type.
/// </summary>
internal abstract class PgArrayViewMapping
{
    /// <summary>
    /// Requires element read capability without invoking user factories or the backend.
    /// </summary>
    internal abstract void RequireRead();

    /// <summary>
    /// Gets whether the selected scalar reader requires nominal element identity.
    /// </summary>
    internal abstract bool ExactElementIdentity { get; }

    /// <summary>
    /// Resolves the current element identity without retaining catalog OIDs across backend calls.
    /// </summary>
    internal abstract uint GetElementOid();

    /// <summary>
    /// Resolves the current array identity for typed NULL parameters and caller-asserted native results.
    /// </summary>
    internal abstract uint GetOid();

    /// <summary>
    /// Validates the source type even for SQL NULL and otherwise opens a checked view.
    /// </summary>
    internal abstract object? Read(PgDatum value);
}

/// <summary>
/// Retains a statically closed constructor while leaving catalog identities backend-local.
/// </summary>
/// <typeparam name="T">The exact element representation.</typeparam>
internal sealed class PgArrayViewMapping<T> : PgArrayViewMapping
{
    /// <inheritdoc />
    internal override void RequireRead() => PgDatumRegistry.Find(typeof(T))?.RequireRead();

    /// <inheritdoc />
    internal override bool ExactElementIdentity => PgDatumRegistry.Find(typeof(T)) is not null;

    /// <inheritdoc />
    internal override uint GetElementOid() => SpiType.GetOid<T>();

    /// <inheritdoc />
    internal override uint GetOid() => NativeBackend.EnumArrayOid(GetElementOid());

    /// <inheritdoc />
    internal override object? Read(PgDatum value)
    {
        RequireRead();
        value.Lifetime.Validate();
        if (!value.IsNull)
        {
            return new PgArrayView<T>(value);
        }

        NativeBackend.ValidateArrayElement(value, GetElementOid(), ExactElementIdentity);
        return null;
    }
}

/// <summary>
/// Exposes checked raw transport and ownership for borrowed array representations.
/// </summary>
internal interface IPgArrayView : IDisposable
{
    /// <summary>
    /// Gets the live array datum without materializing its cells.
    /// </summary>
    PgDatum Datum { get; }
}
