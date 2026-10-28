using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.CompilerServices;

/// <summary>
/// Validates generated node representations against the current native capability without requiring SPI.
/// </summary>
internal static unsafe class NativeNode
{
    /// <summary>
    /// Checks the complete unmanaged layout contract and active selected-header ABI before accessing node storage.
    /// </summary>
    /// <typeparam name="T">The generated node representation.</typeparam>
    /// <returns>The representation's required native alignment.</returns>
    internal static nuint Validate<T>() where T : unmanaged, IPgNativeNode
    {
        _ = NativeMemoryContext.Provider;
        int size = T.NativeSize;
        int alignment = T.NativeAlignment;
        string identity = T.AbiIdentity;
        if (size < sizeof(uint) || size != Unsafe.SizeOf<T>() || alignment <= 0 ||
            (alignment & (alignment - 1)) != 0 || size % alignment != 0 ||
            identity is null || identity.Length != 64 ||
            T.RuntimeIdentifier != RuntimeInformation.RuntimeIdentifier)
        {
            throw new PlatformNotSupportedException("The node representation does not match its declared native layout or runtime ABI.");
        }

        NativeBindingContract.Validate(Encoding.UTF8.GetBytes(identity), T.PostgresMajor);
        return (nuint)alignment;
    }

    /// <summary>
    /// Rejects a native address that cannot satisfy the declared node's alignment.
    /// </summary>
    /// <param name="address">The checked current storage address.</param>
    /// <param name="alignment">The validated native alignment.</param>
    internal static void CheckAlignment(void* address, nuint alignment)
    {
        if ((nuint)address % alignment != 0)
        {
            throw new InvalidOperationException("The borrowed node address does not satisfy its native alignment.");
        }
    }
}
