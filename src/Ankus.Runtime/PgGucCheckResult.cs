using System;

namespace Ankus;

/// <summary>
/// Contains either an accepted and possibly normalized configuration value with owned extra bytes, or rejection diagnostics.
/// </summary>
/// <typeparam name="T">The declared setting type.</typeparam>
public sealed class PgGucCheckResult<T>
{
    private readonly T? _value;
    private readonly PgGucExtra? _extra;
    private readonly PgGucCheckError? _error;

    /// <summary>
    /// Accepts the value, which may differ from the proposed value, with optional immutable extra data.
    /// </summary>
    /// <param name="value">The accepted value.</param>
    /// <param name="extra">The owned extra bytes, or null when absent.</param>
    public PgGucCheckResult(T value, PgGucExtra? extra = null)
    {
        _value = value;
        _extra = extra;
        IsAccepted = true;
    }

    /// <summary>
    /// Rejects the proposed value with owned diagnostics.
    /// </summary>
    /// <param name="error">The rejection diagnostics.</param>
    public PgGucCheckResult(PgGucCheckError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _error = error;
    }

    /// <summary>
    /// Gets whether the check accepted its value.
    /// </summary>
    public bool IsAccepted { get; }

    /// <summary>
    /// Gets the accepted value; rejected results throw InvalidOperationException.
    /// </summary>
    public T Value => IsAccepted ? _value! : throw new InvalidOperationException("A rejected configuration check has no accepted value.");

    /// <summary>
    /// Gets the accepted extra bytes, possibly null; rejected results throw InvalidOperationException.
    /// </summary>
    public PgGucExtra? Extra => IsAccepted ? _extra : throw new InvalidOperationException("A rejected configuration check has no accepted extra data.");

    /// <summary>
    /// Gets rejection diagnostics; accepted results throw InvalidOperationException.
    /// </summary>
    public PgGucCheckError Error => !IsAccepted ? _error! : throw new InvalidOperationException("An accepted configuration check has no rejection diagnostics.");
}
