using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Retains a PostgreSQL datum, its exact type OID, and its SQL NULL flag under a checked memory-context lifetime.
/// </summary>
/// <remarks>
/// Raw SPI results own their native storage. Copy a value into another context to keep it beyond result disposal.
/// Reading an ordinary managed type returns an independent copy; polymorphic wrappers share this datum's lifetime.
/// Native reads, output formatting, and copies access existing storage without reapplying domain constraints.
/// Explicit parameter and result assignments still validate those constraints; selected converters and output functions retain their own behavior.
/// Native access requires the owning backend thread.
/// </remarks>
public sealed class PgDatum
{
    private readonly nuint _bits;
    private readonly PgDatumLifetime _lifetime;

    /// <summary>
    /// Captures a native value whose storage belongs to the supplied checked lifetime.
    /// </summary>
    /// <param name="bits">The native Datum word.</param>
    /// <param name="typeOid">The declared PostgreSQL type.</param>
    /// <param name="isNull">Whether the value is SQL NULL.</param>
    /// <param name="lifetime">The storage lifetime.</param>
    internal PgDatum(nuint bits, uint typeOid, bool isNull, PgDatumLifetime lifetime)
    {
        _bits = bits;
        TypeOid = typeOid;
        IsNull = isNull;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Gets the declared PostgreSQL type OID, including domain identity and typed NULLs.
    /// </summary>
    public uint TypeOid { get; }

    /// <summary>
    /// Gets whether this value is SQL NULL. A zero Datum word alone does not indicate SQL NULL.
    /// </summary>
    public bool IsNull { get; }

    /// <summary>
    /// Gets the checked native owner shared by derived raw values.
    /// </summary>
    internal PgDatumLifetime Lifetime => _lifetime;

    /// <summary>
    /// Reads a supported managed type with the ordinary SPI checks, or invokes registered scalar or array element readers.
    /// </summary>
    /// <typeparam name="T">The desired managed type.</typeparam>
    /// <returns>An independent managed value, or a polymorphic wrapper sharing this datum's lifetime.</returns>
    /// <remarks>
    /// Registered datum readers require the exact current mapped type and a live owner, including for SQL NULL.
    /// Their user-supplied conversion must return independent managed data for a present value.
    /// Mapped array elements use a temporary native owner that ends after conversion; this source datum remains owned by its original context.
    /// PgArrayView&lt;T&gt; instead retains this source lifetime and converts only accessed cells. It validates element identity
    /// even for whole-array SQL NULL, which returns a null view. Dispose the view while the source backend remains active.
    /// </remarks>
    public T Read<[PgConvertible] T>() => NativeBackend.ReadDatum<T>(this);

    /// <summary>
    /// Converts this datum using an explicit converter without runtime code generation or reflection.
    /// </summary>
    /// <typeparam name="T">The converter's managed result type.</typeparam>
    /// <param name="converter">The converter, which must copy any data that will outlive this datum.</param>
    /// <returns>The converter's result.</returns>
    public T Read<T>(Func<PgDatum, T> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        _lifetime.Validate();
        return converter(this);
    }

    /// <summary>
    /// Uses the type's PostgreSQL output function and copies its text into a managed string.
    /// </summary>
    /// <returns>The server-formatted value, or null for SQL NULL.</returns>
    public string? ToPostgresString() => NativeBackend.FormatDatum(this);

    /// <summary>
    /// Copies the native value into a specified context, independently of this datum's current owner.
    /// </summary>
    /// <param name="context">The destination context whose reset or deletion ends the new value's lifetime.</param>
    /// <returns>The copied datum with its exact type and NULL state.</returns>
    public PgDatum CopyTo(PgMemoryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return NativeBackend.CopyDatum(this, new PgDatumLifetime(context));
    }

    /// <summary>
    /// Converts a managed value to a datum of its PostgreSQL type, allocated in the destination context, as pgrx's
    /// <c>IntoDatum</c> does. The value's type selects the PostgreSQL type, as for <see cref="SpiParameter.Create{T}(T)"/>,
    /// including a <see cref="PgDatumTypeAttribute"/> mapping. A null value gives SQL NULL of that type.
    /// </summary>
    /// <typeparam name="T">The managed type, which selects the PostgreSQL type when the value is null.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="context">The destination context whose reset or deletion ends the datum's lifetime.</param>
    /// <returns>The converted datum.</returns>
    public static PgDatum Create<[PgConvertible] T>(T value, PgMemoryContext context) => Create(SpiParameter.Create(value), context);

    /// <summary>
    /// Converts a typed value to a datum of its declared PostgreSQL type, allocated in the destination context. A domain's
    /// constraints apply as they do to an assignment, including to SQL NULL.
    /// </summary>
    /// <param name="value">The typed value, which can be a typed SQL NULL.</param>
    /// <param name="context">The destination context whose reset or deletion ends the datum's lifetime.</param>
    /// <returns>The converted datum.</returns>
    public static PgDatum Create(SpiParameter value, PgMemoryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (value.TypeOid == 0)
        {
            throw new ArgumentException("The value must be created with an explicit managed or PostgreSQL type.", nameof(value));
        }

        return value.Value is PgDatum datum && datum.TypeOid == value.TypeOid
            ? datum.CopyTo(context)
            : NativeBackend.CreateDatum(value, new PgDatumLifetime(context));
    }

    /// <summary>
    /// Borrows a caller-supplied native Datum word using an explicit context lifetime anchor.
    /// </summary>
    /// <param name="bits">A correctly represented by-value Datum or a valid by-reference address.</param>
    /// <param name="typeOid">The exact PostgreSQL type OID.</param>
    /// <param name="context">The lifetime anchor; it is not inferred to own the supplied address.</param>
    /// <param name="isNull">Whether the datum is SQL NULL.</param>
    /// <returns>A checked lifetime handle for the supplied datum.</returns>
    /// <remarks>
    /// The caller must prove the representation matches the type and that referenced storage remains valid
    /// until the anchor resets or is deleted. The handle cannot detect an earlier free of that storage.
    /// </remarks>
    [NativeUnsafeAccess]
    public static PgDatum DangerousCreate(nuint bits, uint typeOid, PgMemoryContext context, bool isNull = false)
    {
        ArgumentOutOfRangeException.ThrowIfZero(typeOid);
        ArgumentNullException.ThrowIfNull(context);
        return new PgDatum(bits, typeOid, isNull, new PgDatumLifetime(context));
    }

    /// <summary>
    /// Returns the native Datum word after checking its memory owner and backend thread.
    /// </summary>
    /// <returns>The raw bits, which may represent a value or a native address according to the type.</returns>
    /// <remarks>
    /// This does not extend native storage lifetime. Check IsNull separately before interpreting the bits.
    /// </remarks>
    public nuint DangerousGetBits()
    {
        _lifetime.Validate();
        return _bits;
    }

    /// <summary>
    /// Gets the native Datum word without checking its owner, for a caller that has just validated the lifetime.
    /// </summary>
    internal nuint UncheckedBits => _bits;

    /// <summary>
    /// Copies the lifetime identity and raw word into an owned parameter envelope.
    /// </summary>
    /// <returns>The raw native parameter with its SQL NULL flag.</returns>
    internal NativeValue ToNative()
    {
        _lifetime.Validate();
        return NativeValue.FromDatum(_bits, _lifetime.ContextId, _lifetime.Generation, IsNull);
    }
}
