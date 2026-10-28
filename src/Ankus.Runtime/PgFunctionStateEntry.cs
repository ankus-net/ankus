using System;

namespace Ankus;

/// <summary>
/// Owns one exactly typed, lazily initialized call-site value and its deterministic cleanup.
/// </summary>
internal sealed class PgFunctionStateEntry
{
    private Type? _type;
    private object? _value;
    private bool _initializing;
    private bool _released;

    /// <summary>
    /// Gets whether a successful factory value is available without further initialization.
    /// </summary>
    internal bool IsInitialized => _type is not null;

    /// <summary>
    /// Creates a value once, permits retries after failure, and rejects recursive or differently typed access.
    /// </summary>
    /// <typeparam name="T">The exact cached type.</typeparam>
    /// <param name="factory">The synchronous initializer.</param>
    /// <returns>The initialized value.</returns>
    internal T GetOrCreate<T>(Func<T> factory)
    {
        ObjectDisposedException.ThrowIf(_released, this);
        if (_initializing)
        {
            throw new InvalidOperationException("Function state cannot be initialized recursively at the same call site.");
        }

        if (_type is not null)
        {
            if (_type != typeof(T))
            {
                throw new InvalidCastException("The function call site already contains a different managed state type.");
            }

            return (T)_value!;
        }

        _initializing = true;
        try
        {
            T value = factory();
            if (_released)
            {
                (value as IDisposable)?.Dispose();
                throw new ObjectDisposedException(nameof(PgFunctionContext), "The function state owner ended during initialization.");
            }

            _value = value;
            _type = typeof(T);
            return value;
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>
    /// Releases all managed roots before running user cleanup, including when disposal throws or reenters.
    /// </summary>
    internal void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        object? value = _value;
        _value = null;
        _type = null;
        (value as IDisposable)?.Dispose();
    }
}
