using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.CompilerServices;

/// <summary>
/// Borrows the native error destination and backend capabilities for one generated callback entry.
/// </summary>
/// <param name="Error">The native diagnostic destination, owned by the entering native dispatcher.</param>
/// <param name="Execute">The callback-scoped SPI binding, or zero when no transaction is available.</param>
/// <param name="Memory">The callback-scoped native memory and raw-call binding.</param>
/// <param name="Read">The extension's native configuration reader, or zero when none is registered.</param>
/// <param name="Log">The callback-scoped native logging capability.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeCallbackContext(nint Error, nint Execute, nint Memory, nint Read, nint Log);

/// <summary>
/// Validates and copies native callback frames without changing their values or retaining borrowed storage.
/// </summary>
/// <remarks>
/// These are generated-code contracts. Native addresses must designate live storage supplied by the
/// selected callback wrapper. The generated dispatcher catches exceptions before returning to native
/// code; PostgreSQL errors may be raised only after that managed dispatcher has returned.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class NativeRawCallback
{
    /// <summary>
    /// Validates the complete callback type's binding against the current native capability before registration or dispatch.
    /// </summary>
    /// <typeparam name="T">The generated function-pointer representation.</typeparam>
    public static void ValidateBinding<T>() where T : unmanaged, IPgNativeType
    {
        if (T.NativeSize != sizeof(nint) || Unsafe.SizeOf<T>() != sizeof(nint) ||
            T.RuntimeIdentifier != RuntimeInformation.RuntimeIdentifier || T.AbiIdentity is not { Length: 64 } identity)
        {
            throw new PlatformNotSupportedException("The native callback does not match its generated binding representation.");
        }

        ValidateIdentity(identity, T.PostgresMajor);
    }

    /// <summary>
    /// Copies a native record address after validating the record's measured binding and the address's alignment.
    /// </summary>
    /// <typeparam name="T">The generated native record that the address designates.</typeparam>
    /// <param name="argument">The live native pointer descriptor.</param>
    /// <returns>The nonnull, aligned record address. The native caller retains ownership of its storage.</returns>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context. The checks cannot prove that the address
    /// designates a live record; use only argument storage supplied by the matching native dispatcher.
    /// </remarks>
    [NativeUnsafeAccess]
    public static T* ReadRecordPointer<T>(NativeCallArgument argument) where T : unmanaged, IPgNativeType
    {
        int size = NativeSize<T>();
        int alignment = T.NativeAlignment;
        if (size == 0 || alignment <= 0 || (alignment & (alignment - 1)) != 0 ||
            T.RuntimeIdentifier != RuntimeInformation.RuntimeIdentifier || T.AbiIdentity is not { Length: 64 } identity)
        {
            throw new PlatformNotSupportedException("The native callback record does not match its generated binding representation.");
        }

        ValidateIdentity(identity, T.PostgresMajor);
        nint address = Read<nint>(argument);
        if (address == 0)
        {
            throw new InvalidOperationException("The native callback received a null record address.");
        }

        if ((nuint)address % (nuint)alignment != 0)
        {
            throw new InvalidOperationException("The native callback record address does not satisfy its native alignment.");
        }

        return (T*)address;
    }

    /// <summary>
    /// Checks a generated binding identity against the active native capability.
    /// </summary>
    private static void ValidateIdentity(string identity, int postgresMajor)
    {
        Span<byte> bytes = stackalloc byte[64];
        if (Encoding.UTF8.GetByteCount(identity) != bytes.Length)
        {
            throw new PlatformNotSupportedException("The native callback has an invalid binding identity.");
        }

        Encoding.UTF8.GetBytes(identity, bytes);
        NativeRawCall.ValidateBinding(bytes, postgresMajor);
    }

    /// <summary>
    /// Checks argument and result envelopes before a managed handler can observe arguments or cause side effects.
    /// </summary>
    /// <param name="arguments">The native argument descriptor array.</param>
    /// <param name="count">The number of descriptors supplied by the native entry point.</param>
    /// <param name="expectedCount">The exact number required by the generated signature.</param>
    /// <param name="result">The native result destination.</param>
    /// <param name="resultSize">The exact destination size.</param>
    /// <param name="expectedResultSize">The native result size, or minus one for void.</param>
    public static void ValidateFrame(NativeCallArgument* arguments, nuint count, int expectedCount,
        nint result, nuint resultSize, int expectedResultSize)
    {
        if (expectedCount < 0 || count != (nuint)expectedCount || count != 0 && arguments is null)
        {
            throw new InvalidOperationException("The native callback has an invalid argument frame.");
        }

        if (expectedResultSize < -1 || (expectedResultSize == -1 ? result != 0 || resultSize != 0
            : result == 0 || resultSize != (nuint)expectedResultSize))
        {
            throw new InvalidOperationException("The native callback has invalid result storage.");
        }
    }

    /// <summary>
    /// Gets an exact generated native value size, retaining zero-byte native types without copying CLR placeholder bytes.
    /// </summary>
    /// <typeparam name="T">The generated native value representation.</typeparam>
    /// <returns>The complete native value size.</returns>
    public static int NativeSize<T>() where T : unmanaged, IPgNativeType
    {
        int size = T.NativeSize;
        if (size < 0 || size != 0 && size != Unsafe.SizeOf<T>())
        {
            throw new PlatformNotSupportedException("The native callback value does not match its generated storage size.");
        }

        return size;
    }

    /// <summary>
    /// Copies a primitive native argument from validated storage, including an unaligned value address.
    /// </summary>
    /// <typeparam name="T">The primitive unmanaged value type.</typeparam>
    /// <param name="argument">The live native value descriptor.</param>
    /// <returns>The exact native argument value.</returns>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// Use only live argument and result storage supplied by the matching native dispatcher.
    /// </remarks>
    [NativeUnsafeAccess]
    public static T Read<T>(NativeCallArgument argument) where T : unmanaged
        => Read<T>(argument, Unsafe.SizeOf<T>());

    /// <summary>
    /// Copies a generated native value while preserving empty native representations.
    /// </summary>
    /// <typeparam name="T">The generated native value type.</typeparam>
    /// <param name="argument">The live native value descriptor.</param>
    /// <returns>The exact value, or the logical default of a zero-byte native type.</returns>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// Use only live argument and result storage supplied by the matching native dispatcher.
    /// </remarks>
    [NativeUnsafeAccess]
    public static T ReadNative<T>(NativeCallArgument argument) where T : unmanaged, IPgNativeType
        => Read<T>(argument, NativeSize<T>());

    /// <summary>
    /// Writes a primitive callback result without assuming native destination alignment.
    /// </summary>
    /// <typeparam name="T">The primitive unmanaged result type.</typeparam>
    /// <param name="result">The live native destination.</param>
    /// <param name="resultSize">The supplied destination size.</param>
    /// <param name="value">The exact result value.</param>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// Use only live argument and result storage supplied by the matching native dispatcher.
    /// </remarks>
    [NativeUnsafeAccess]
    public static void Write<T>(nint result, nuint resultSize, T value) where T : unmanaged
        => Write(result, resultSize, value, Unsafe.SizeOf<T>());

    /// <summary>
    /// Writes a generated callback result without publishing bytes for an empty native representation.
    /// </summary>
    /// <typeparam name="T">The generated unmanaged result type.</typeparam>
    /// <param name="result">The live native destination.</param>
    /// <param name="resultSize">The supplied destination size.</param>
    /// <param name="value">The exact result value.</param>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// Use only live argument and result storage supplied by the matching native dispatcher.
    /// </remarks>
    [NativeUnsafeAccess]
    public static void WriteNative<T>(nint result, nuint resultSize, T value) where T : unmanaged, IPgNativeType
        => Write(result, resultSize, value, NativeSize<T>());

    private static T Read<T>(NativeCallArgument argument, int size) where T : unmanaged
    {
        if (argument.Address == 0 || argument.Size != (nuint)size)
        {
            throw new InvalidOperationException("The native callback has invalid argument storage.");
        }

        return size == 0 ? default : Unsafe.ReadUnaligned<T>((void*)argument.Address);
    }

    private static void Write<T>(nint result, nuint resultSize, T value, int size) where T : unmanaged
    {
        ValidateFrame(null, 0, 0, result, resultSize, size);
        if (size != 0)
        {
            Unsafe.WriteUnaligned((void*)result, value);
        }
    }
}
