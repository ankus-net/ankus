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
        fixed (byte* firstAddress = first, secondAddress = second)
        {
            // An empty array pins as null; the native side needs a valid address even for zero bytes.
            byte empty = 0;
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.Quote,
                _flags = kind,
                _pointer = firstAddress == null ? (nint)(&empty) : (nint)firstAddress,
                _length = (nuint)first.Length,
                _data = qualifier is null ? 0 : secondAddress == null ? (nint)(&empty) : (nint)secondAddress,
                _value = second?.Length ?? 0,
            };
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            return s_utf8.GetString((byte*)result._data, checked((int)result._length));
        }
    }
}
