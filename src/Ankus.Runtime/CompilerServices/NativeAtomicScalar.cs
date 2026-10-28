using System;
using System.Runtime.CompilerServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Validates the unmanaged primitive and enum types supported by scalar Interlocked operations.
/// </summary>
internal static class NativeAtomicScalar
{
    /// <summary>
    /// Rejects unsupported types before their storage is accessed.
    /// </summary>
    /// <typeparam name="T">The requested unmanaged scalar.</typeparam>
    internal static void Validate<T>() where T : unmanaged
    {
        if ((!typeof(T).IsPrimitive && !typeof(T).IsEnum) || Unsafe.SizeOf<T>() is not (1 or 2 or 4 or 8))
        {
            throw new NotSupportedException($"The type '{typeof(T)}' is not supported by .NET scalar atomic operations.");
        }
    }
}
