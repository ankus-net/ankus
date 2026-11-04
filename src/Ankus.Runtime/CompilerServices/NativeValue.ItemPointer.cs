namespace Ankus.CompilerServices;

public partial struct NativeValue
{
    /// <summary>
    /// Reads exact item-pointer fields without depending on native struct packing.
    /// </summary>
    /// <returns>The copied location, including invalid offset-zero values.</returns>
    public readonly PgItemPointer ReadItemPointer() => new(checked((uint)Integral), checked((ushort)_auxiliary1));

    /// <summary>
    /// Writes a non-NULL item pointer as independent block and offset fields.
    /// </summary>
    /// <param name="value">The location; offset zero remains non-NULL.</param>
    /// <returns>The exact scalar transport.</returns>
    public static NativeValue FromItemPointer(PgItemPointer value) => new() { Integral = value.BlockNumber, _auxiliary1 = value.OffsetNumber };
}
