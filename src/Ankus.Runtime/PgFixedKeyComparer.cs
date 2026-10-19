using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus;

/// <summary>
/// Creates deterministic equality comparers for keys in fixed shared-memory dictionaries.
/// </summary>
public static class PgFixedKeyComparer
{
    /// <summary>
    /// Creates a comparer with .NET value equality and process-independent FNV-1a hashes.
    /// </summary>
    /// <typeparam name="T">A supported unmanaged key type.</typeparam>
    /// <returns>A comparer suitable for independently attached PostgreSQL processes.</returns>
    /// <remarks>
    /// Supported types are Boolean, character, integer primitives, enums, Int128, UInt128,
    /// Guid, Half, Single, Double and Decimal. Equivalent NaNs, signed zeros and decimal scales
    /// hash identically. Other structs require an explicit stable comparer. Hashes are an
    /// implementation detail; don't persist them, send them over the wire or expect them to match
    /// Rust's Hash encoding.
    /// </remarks>
    public static IEqualityComparer<T> Create<T>() where T : unmanaged
    {
        Validate<T>();
        return new FixedKeyComparer<T>();
    }

    /// <summary>
    /// Checks the scalar key contract without allocating a comparer when constructing a borrowed view.
    /// </summary>
    internal static void Validate<T>() where T : unmanaged
    {
        if ((!typeof(T).IsPrimitive && !typeof(T).IsEnum && typeof(T) != typeof(Int128) &&
            typeof(T) != typeof(UInt128) && typeof(T) != typeof(Guid) && typeof(T) != typeof(Half) &&
            typeof(T) != typeof(decimal)) || Unsafe.SizeOf<T>() > 16)
        {
            throw new NotSupportedException($"The key type '{typeof(T)}' requires an explicit process-stable equality comparer.");
        }
    }

    /// <summary>
    /// Hashes supported scalar values using a canonical representation compatible with value equality.
    /// </summary>
    internal static int Hash<T>(T value) where T : unmanaged
    {
        Span<byte> bytes = stackalloc byte[16];
        int length = Unsafe.SizeOf<T>();
        MemoryMarshal.Write(bytes, in value);
        if (!BitConverter.IsLittleEndian)
        {
            bytes[..length].Reverse();
        }

        if (typeof(T) == typeof(bool))
        {
            bytes[0] = Unsafe.As<T, bool>(ref value) ? (byte)1 : (byte)0;
        }
        else if (typeof(T) == typeof(Half))
        {
            Half number = Unsafe.As<T, Half>(ref value);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, Half.IsNaN(number) ? (ushort)0x7E00 :
                number == (Half)0 ? (ushort)0 : BitConverter.HalfToUInt16Bits(number));
        }
        else if (typeof(T) == typeof(float))
        {
            float number = Unsafe.As<T, float>(ref value);
            BinaryPrimitives.WriteInt32LittleEndian(bytes, float.IsNaN(number) ? 0x7FC00000 :
                number is 0 ? 0 : BitConverter.SingleToInt32Bits(number));
        }
        else if (typeof(T) == typeof(double))
        {
            double number = Unsafe.As<T, double>(ref value);
            BinaryPrimitives.WriteInt64LittleEndian(bytes, double.IsNaN(number) ? 0x7FF8000000000000 :
                number is 0 ? 0 : BitConverter.DoubleToInt64Bits(number));
        }
        else if (typeof(T) == typeof(decimal))
        {
            Span<int> parts = stackalloc int[4];
            decimal.GetBits(Unsafe.As<T, decimal>(ref value), parts);
            UInt128 magnitude = (uint)parts[0] | ((UInt128)(uint)parts[1] << 32) | ((UInt128)(uint)parts[2] << 64);
            int scale = (parts[3] >> 16) & 0xFF;
            while (scale > 0 && magnitude % 10 == 0)
            {
                magnitude /= 10;
                scale--;
            }

            BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)magnitude);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], (uint)(magnitude >> 64));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], magnitude == 0 ? 0 : (parts[3] & int.MinValue) | (scale << 16));
        }
        else if (typeof(T) == typeof(Guid))
        {
            Unsafe.As<T, Guid>(ref value).TryWriteBytes(bytes);
        }

        uint hash = 2166136261;
        foreach (byte part in bytes[..length])
        {
            hash = unchecked((hash ^ part) * 16777619);
        }

        return unchecked((int)hash);
    }
}
