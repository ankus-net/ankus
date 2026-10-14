using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.CompilerServices;

/// <summary>
/// Calls PostgreSQL's quoting functions through the memory bridge when no SQL callback is active, as in a background
/// worker between transactions.
/// </summary>
internal static unsafe class NativeQuote
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Quotes text.
    /// </summary>
    /// <param name="kind">One for an identifier, two for a qualified identifier and three for a literal.</param>
    /// <param name="text">The identifier or literal.</param>
    /// <param name="qualifier">The qualifier of a qualified identifier, or null.</param>
    /// <returns>The quoted SQL fragment.</returns>
    internal static string Quote(int kind, string text, string? qualifier)
    {
        if (!NativeMemoryContext.IsActive)
        {
            // Off the backend thread, report the same error as the callback path.
            NativeBackend.CheckAccess();
        }

        byte[] first = s_utf8.GetBytes(text);
        byte[]? second = qualifier is null ? null : s_utf8.GetBytes(qualifier);

        // The native side needs a valid address even for zero bytes. An array's data reference pins it even when it is
        // empty, where pinning the array itself gives null; a missing qualifier is the null address.
        ref byte secondData = ref second is null ? ref Unsafe.NullRef<byte>() : ref MemoryMarshal.GetArrayDataReference(second);
        fixed (byte* firstAddress = &MemoryMarshal.GetArrayDataReference(first))
        fixed (byte* secondAddress = &secondData)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.Quote,
                _flags = kind,
                _pointer = (nint)firstAddress,
                _length = (nuint)first.Length,
                _data = (nint)secondAddress,
                _value = second?.Length ?? 0,
            };
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            return s_utf8.GetString((byte*)result._data, checked((int)result._length));
        }
    }
}
