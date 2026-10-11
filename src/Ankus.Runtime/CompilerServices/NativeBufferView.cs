namespace Ankus.CompilerServices;

public partial struct NativeValue
{
    /// <summary>
    /// Borrows a generated scalar bytea argument until its unique managed callback exits.
    /// </summary>
    /// <returns>The checked view over original bytes or a private detoast allocation.</returns>
    public readonly PgByteaView ReadBorrowedBytea()
    {
        var lifetime = PgDatumLifetime.ForCurrentInput(NativeMemoryContext.BorrowScope);
        return new PgByteaView(new PgDatum(unchecked((nuint)Integral), unchecked((uint)_auxiliary2), IsNull != 0, lifetime));
    }

    /// <summary>
    /// Borrows a generated scalar text argument until its unique managed callback exits.
    /// </summary>
    /// <returns>The checked UTF-8 view retaining the original server-encoded datum.</returns>
    public readonly PgTextView ReadBorrowedText()
    {
        var lifetime = PgDatumLifetime.ForCurrentInput(NativeMemoryContext.BorrowScope);
        return new PgTextView(new PgDatum(unchecked((nuint)Integral), unchecked((uint)_auxiliary2), IsNull != 0, lifetime));
    }

    /// <summary>
    /// Copies a bytea argument before a set iterator or aggregate retains it across callbacks.
    /// </summary>
    /// <returns>A checked view over an independent snapshot in the callback result owner.</returns>
    public readonly PgByteaView ReadOwnedByteaView()
    {
        NativeValue value = this;
        return PgMemoryContext.Callback.Run(() => new PgByteaView(value.ReadPolymorphic()));
    }

    /// <summary>
    /// Copies a text argument before a set iterator or aggregate retains it across callbacks.
    /// </summary>
    /// <returns>A checked UTF-8 view over an independent snapshot in the callback result owner.</returns>
    public readonly PgTextView ReadOwnedTextView()
    {
        NativeValue value = this;
        return PgMemoryContext.Callback.Run(() => new PgTextView(value.ReadPolymorphic()));
    }
}
