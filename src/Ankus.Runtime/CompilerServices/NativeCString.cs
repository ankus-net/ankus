using System;

namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    /// <summary>
    /// Copies a present C-string payload without decoding or transcoding its bytes.
    /// </summary>
    /// <returns>An independent managed C string with its own terminator.</returns>
    public readonly PgCString ReadCString()
    {
        if (IsNull != 0 || _length < 0 || (_length != 0 && _data == null))
        {
            throw new InvalidOperationException("A present C string requires a valid byte transport.");
        }

        return new PgCString(new ReadOnlySpan<byte>(_data, _length));
    }

    /// <summary>
    /// Copies exact C-string payload bytes into an allocator-matched output transport.
    /// </summary>
    /// <param name="value">The present managed C string.</param>
    /// <returns>The transport whose native writer appends one zero byte.</returns>
    public static NativeValue FromCString(PgCString value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return FromBytes(value.AsSpan());
    }

    /// <summary>
    /// Borrows a generated scalar C-string input until its managed callback exits.
    /// </summary>
    /// <returns>The view over the original terminated native bytes.</returns>
    public readonly PgCStringView ReadBorrowedCString()
    {
        RequirePresentCString();
        var lifetime = PgDatumLifetime.ForCurrentInput(NativeMemoryContext.BorrowScope);
        return new PgCStringView(new PgDatum(unchecked((nuint)Integral), unchecked((uint)_auxiliary2), false, lifetime));
    }

    /// <summary>
    /// Copies a C-string input before a set iterator or aggregate retains it across callbacks.
    /// </summary>
    /// <returns>A checked view over an independent snapshot in the callback result owner.</returns>
    public readonly PgCStringView ReadOwnedCStringView()
    {
        RequirePresentCString();
        NativeValue value = this;
        return PgMemoryContext.Callback.Run(() => new PgCStringView(value.ReadPolymorphic()));
    }

    /// <summary>
    /// Rejects both SQL NULL and PostgreSQL's null C-string input address before native copying.
    /// </summary>
    private readonly void RequirePresentCString()
    {
        if (IsNull != 0 || Integral == 0)
        {
            throw new InvalidOperationException("A present C string requires native storage.");
        }
    }
}
