using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Carries raw datum parameters with a native-validated context lifetime.
/// </summary>
public partial struct NativeValue
{
    /// <summary>
    /// Borrows an internal input, recovering managed state ownership when this extension created it.
    /// </summary>
    /// <returns>The present internal state, including a present zero native word.</returns>
    public readonly PgInternal ReadInternal()
        => PgInternal.DangerousCreate(unchecked((nuint)Integral), PgMemoryContext.Current);

    /// <summary>
    /// Writes internal state with its checked native lifetime without cloning its pointee.
    /// </summary>
    /// <param name="value">The present internal state.</param>
    /// <returns>The native-validated raw transport.</returns>
    public static NativeValue FromInternal(PgInternal value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToNative();
    }

    /// <summary>
    /// Copies a generated raw or polymorphic input into the active scalar or iterator memory owner.
    /// </summary>
    /// <returns>The datum with its resolved type and SQL NULL flag.</returns>
    public readonly PgDatum ReadPolymorphic()
    {
        PgMemoryContext context = PgMemoryContext.Current;
        PgDatum borrowed = PgDatum.DangerousCreate(unchecked((nuint)Integral), unchecked((uint)_auxiliary2), context, isNull: IsNull != 0);
        return borrowed.CopyTo(context);
    }

    /// <summary>
    /// Borrows a generated scalar array argument until its unique managed callback exits.
    /// </summary>
    /// <returns>The view with original storage or a private PostgreSQL detoast allocation.</returns>
    public readonly PgArrayView ReadBorrowedArray()
    {
        var lifetime = PgDatumLifetime.ForCurrentInput(NativeMemoryContext.BorrowScope);
        return new PgArrayView(new PgDatum(unchecked((nuint)Integral), unchecked((uint)_auxiliary2), IsNull != 0, lifetime));
    }

    /// <summary>
    /// Borrows a generated typed scalar array argument under the current callback's source lifetime.
    /// </summary>
    /// <typeparam name="T">The checked scalar element representation.</typeparam>
    /// <returns>A lazy typed view over the original native array storage.</returns>
    public readonly PgArrayView<T> ReadBorrowedArray<T>()
    {
        var lifetime = PgDatumLifetime.ForCurrentInput(NativeMemoryContext.BorrowScope);
        return new PgArrayView<T>(new PgDatum(unchecked((nuint)Integral), unchecked((uint)_auxiliary2), IsNull != 0, lifetime));
    }

    /// <summary>
    /// Copies an array argument before a set iterator or aggregate retains it across managed callbacks.
    /// </summary>
    /// <returns>A checked view over an independent snapshot in the callback result owner.</returns>
    public readonly PgArrayView ReadOwnedArrayView()
    {
        NativeValue value = this;
        return PgMemoryContext.Callback.Run(() => new PgArrayView(value.ReadPolymorphic()));
    }

    /// <summary>
    /// Copies a typed array argument before an iterator or aggregate retains it across managed callbacks.
    /// </summary>
    /// <typeparam name="T">The checked scalar element representation.</typeparam>
    /// <returns>A lazy typed view over an independent callback-owned snapshot.</returns>
    public readonly PgArrayView<T> ReadOwnedArrayView<T>()
    {
        NativeValue value = this;
        return PgMemoryContext.Callback.Run(() => new PgArrayView<T>(value.ReadPolymorphic()));
    }

    /// <summary>
    /// Writes a checked raw or polymorphic result with its exact type for native return validation.
    /// </summary>
    /// <param name="datum">The live native value.</param>
    /// <returns>The owned transport envelope.</returns>
    public static NativeValue FromPolymorphic(PgDatum datum)
    {
        ArgumentNullException.ThrowIfNull(datum);
        if (datum.Lifetime.Scope is not null)
        {
            datum = datum.CopyTo(PgMemoryContext.Callback);
        }

        NativeValue value = datum.ToNative();
        value._auxiliary1 = -6;
        value.Integral = datum.TypeOid;
        return value;
    }

    /// <summary>
    /// Copies a raw word and its lifetime into an owned parameter envelope.
    /// </summary>
    /// <param name="bits">The native Datum word.</param>
    /// <param name="context">The context identity.</param>
    /// <param name="generation">The captured context generation.</param>
    /// <param name="isNull">The SQL NULL flag.</param>
    /// <returns>The parameter transport.</returns>
    internal static NativeValue FromDatum(nuint bits, nint context, nuint generation, bool isNull)
    {
        NativeDatumReference reference = new()
        {
            _bits = bits,
            _context = context,
            _generation = generation
        };
        NativeValue value = FromBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in reference, 1)));
        value._auxiliary1 = -5;
        value.IsNull = isNull ? (byte)1 : (byte)0;
        return value;
    }
}
