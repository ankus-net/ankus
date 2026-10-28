using System;

namespace Ankus;

/// <summary>
/// Shares one lazy instance between a mapped type's independently declared read and write directions.
/// </summary>
internal sealed class DatumTypeMapping<T>(string name, string? schema, PgTypeOrigin origin, Type converterType,
    Func<object> createConverter, bool canRead, bool canWrite, DatumTypeMapping? rangeBound = null)
    : DatumTypeMapping(name, schema, origin, converterType, canRead, canWrite, rangeBound)
{
    private readonly Lazy<object> _converter = new(() => createConverter() ??
        throw new InvalidOperationException("A datum converter factory returned null."));

    /// <inheritdoc />
    protected override object ReadPresent(PgDatum value) => ((IPgDatumReader<T>)_converter.Value).Read(value) ??
        throw new InvalidOperationException("A datum reader returned null for a present PostgreSQL value.");

    /// <inheritdoc />
    protected override PgDatum WritePresent(object value, uint oid, PgMemoryContext destination)
        => ((IPgDatumWriter<T>)_converter.Value).Write((T)value, oid, destination) ??
            throw new InvalidOperationException("A datum writer returned a null handle.");
}
