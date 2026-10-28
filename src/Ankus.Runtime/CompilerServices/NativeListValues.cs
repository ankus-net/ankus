using System;
using System.Runtime.CompilerServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Converts the four supported list cell types without assuming native union layout.
/// </summary>
internal static class NativeListValues
{
    /// <summary>
    /// Selects a finite cell kind independently of PostgreSQL's version-specific NodeTag values.
    /// </summary>
    internal static int GetKind<T>() where T : unmanaged
    {
        if (typeof(T) == typeof(nint))
        {
            return 1;
        }

        if (typeof(T) == typeof(int))
        {
            return 2;
        }

        if (typeof(T) == typeof(uint))
        {
            return 3;
        }

        if (typeof(T) == typeof(PgTransactionId))
        {
            return 4;
        }

        throw new NotSupportedException("PostgreSQL list cells support only nint, int, uint (OID), and PgTransactionId.");
    }

    /// <summary>
    /// Encodes one cell as exact pointer bits or a zero-extended 32-bit value.
    /// </summary>
    internal static ulong ToBits<T>(T value) where T : unmanaged
    {
        if (typeof(T) == typeof(nint))
        {
            return (nuint)Unsafe.As<T, nint>(ref value);
        }

        if (typeof(T) == typeof(int))
        {
            return unchecked((uint)Unsafe.As<T, int>(ref value));
        }

        if (typeof(T) == typeof(uint))
        {
            return Unsafe.As<T, uint>(ref value);
        }

        if (typeof(T) == typeof(PgTransactionId))
        {
            return Unsafe.As<T, PgTransactionId>(ref value).Value;
        }

        throw new NotSupportedException("Unsupported PostgreSQL list cell type.");
    }

    /// <summary>
    /// Decodes a checked cell envelope, rejecting high bits that would otherwise be lost.
    /// </summary>
    internal static T FromBits<T>(ulong bits) where T : unmanaged
    {
        if (typeof(T) == typeof(nint))
        {
            nint value = unchecked((nint)checked((nuint)bits));
            return Unsafe.As<nint, T>(ref value);
        }

        uint narrow = checked((uint)bits);
        if (typeof(T) == typeof(int))
        {
            int value = unchecked((int)narrow);
            return Unsafe.As<int, T>(ref value);
        }

        if (typeof(T) == typeof(uint))
        {
            return Unsafe.As<uint, T>(ref narrow);
        }

        if (typeof(T) == typeof(PgTransactionId))
        {
            var value = new PgTransactionId(narrow);
            return Unsafe.As<PgTransactionId, T>(ref value);
        }

        throw new NotSupportedException("Unsupported PostgreSQL list cell type.");
    }
}
