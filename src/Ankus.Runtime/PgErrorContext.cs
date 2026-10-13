using System.Runtime.CompilerServices;
using System.Text;

namespace Ankus;

/// <summary>
/// Adds a line of context to the errors and messages reported while it is active, as a C extension's error context
/// callback does, such as which remote transaction an apply worker is applying.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL adds the text to the context field of every error, warning and log message raised by a backend call
/// made while the context is active, after any inner lines, and to errors that a managed exception thrown while it is
/// active becomes. Change <see cref="Text"/> as the work moves on; PostgreSQL never calls back into .NET to build it.
/// </para>
/// <para>
/// Contexts nest, and disposing one removes it. A callback's contexts are removed when it returns, so a forgotten one
/// cannot describe later errors; a background worker's contexts last until it disposes them or exits. A context
/// belongs to the thread that pushed it.
/// </para>
/// </remarks>
public sealed unsafe class PgErrorContext : IDisposable
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly ConditionalWeakTable<Exception, string> s_thrown = [];

    [ThreadStatic]
    private static List<PgErrorContext>? s_active;

    private static int s_observing;

    private readonly int _thread = Environment.CurrentManagedThreadId;

    private readonly NativeBorrowScope _scope;

    private nint _entry;

    private string _text;

    private PgErrorContext(nint entry, string text, NativeBorrowScope scope)
    {
        _entry = entry;
        _text = text;
        _scope = scope;
    }

    /// <summary>
    /// Gets or sets the context's text.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The context was removed.</exception>
    public string Text
    {
        get => _text;
        set
        {
            byte[] encoded = Encode(value);
            fixed (byte* address = encoded)
            {
                Invoke(2, Entry, address, encoded.Length);
            }

            _text = value;
        }
    }

    private nint Entry
    {
        get
        {
            ObjectDisposedException.ThrowIf(_entry == 0, this);
            if (Environment.CurrentManagedThreadId != _thread)
            {
                throw new InvalidOperationException("An error context belongs to the backend thread that pushed it.");
            }

            return _entry;
        }
    }

    /// <summary>
    /// Makes a context active until it is disposed.
    /// </summary>
    /// <param name="text">The line to add, such as <c>applying remote transaction 742</c>.</param>
    /// <returns>The active context.</returns>
    public static PgErrorContext Push(string text)
    {
        byte[] encoded = Encode(text);
        NativeBorrowScope scope = NativeMemoryContext.BorrowScope;
        if (Interlocked.Exchange(ref s_observing, 1) == 0)
        {
            AppDomain.CurrentDomain.FirstChanceException += static (_, arguments) => Capture(arguments.Exception);
        }

        nint entry;
        fixed (byte* address = encoded)
        {
            entry = Invoke(1, 0, address, encoded.Length)._value;
        }

        var context = new PgErrorContext(entry, text, scope);
        scope.Register(context);
        (s_active ??= []).Add(context);
        return context;
    }

    /// <summary>
    /// Removes the context.
    /// </summary>
    public void Dispose()
    {
        if (_entry == 0)
        {
            return;
        }

        nint entry = Entry;
        _entry = 0;
        s_active?.Remove(this);
        _scope.Unregister(this);
        Invoke(3, entry, null, 0);
    }

    /// <summary>
    /// Gets the contexts that were active when a managed exception was first thrown, innermost first.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The context lines, or null when none were active.</returns>
    internal static string? Captured(Exception exception) => s_thrown.TryGetValue(exception, out string? text) ? text : null;

    private static void Capture(Exception exception)
    {
        if (s_active is not { Count: > 0 } active || s_thrown.TryGetValue(exception, out _))
        {
            return;
        }

        StringBuilder lines = new();
        for (int index = active.Count - 1; index >= 0; index--)
        {
            lines.Append(active[index]._text);
            if (index > 0)
            {
                lines.Append('\n');
            }
        }

        s_thrown.AddOrUpdate(exception, lines.ToString());
    }

    private static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Error context text cannot contain a zero character.", nameof(text));
        }

        return s_utf8.GetBytes(text);
    }

    private static NativeMemoryResult Invoke(int operation, nint entry, byte* text, int length)
    {
        // An empty array pins as null; the native side needs a valid address even for zero bytes.
        byte empty = 0;
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.ErrorContext,
            _flags = operation,
            _context = entry,
            _pointer = text == null && operation != 3 ? (nint)(&empty) : (nint)text,
            _length = (nuint)length,
        };
        NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
        return result;
    }
}
