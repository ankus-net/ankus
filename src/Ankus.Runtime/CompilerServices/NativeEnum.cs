using System;

namespace Ankus.CompilerServices;

public partial struct NativeValue
{
    /// <summary>
    /// Copies the enum catalog lookup fields without retaining a native tuple or memory context.
    /// </summary>
    internal readonly PgEnumInfo ReadEnumInfo(uint valueOid)
        => new(ReadString(), checked((uint)_integer), valueOid, BitConverter.Int32BitsToSingle(_auxiliary1));
}
