using System;
using System.Linq;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Owns optional diagnostics for a rejected configuration value while leaving reporting severity to PostgreSQL.
/// </summary>
/// <param name="message">The rejection message, or null to retain PostgreSQL's standard invalid-value message.</param>
/// <param name="detail">Optional error detail.</param>
/// <param name="hint">An optional corrective hint.</param>
/// <param name="sqlState">An optional five-character uppercase SQLSTATE; PostgreSQL's default is 22023.</param>
public sealed class PgGucCheckError(string? message = null, string? detail = null, string? hint = null, string? sqlState = null)
{
    /// <summary>
    /// Gets the custom message, or null to use PostgreSQL's standard message.
    /// </summary>
    public string? Message { get; } = NativeGuc.ValidateText(message, nameof(message));

    /// <summary>
    /// Gets optional error detail, retaining the distinction between null and empty text.
    /// </summary>
    public string? Detail { get; } = NativeGuc.ValidateText(detail, nameof(detail));

    /// <summary>
    /// Gets an optional corrective hint.
    /// </summary>
    public string? Hint { get; } = NativeGuc.ValidateText(hint, nameof(hint));

    /// <summary>
    /// Gets the explicit SQLSTATE, or null to use PostgreSQL's default invalid-parameter code.
    /// </summary>
    public string? SqlState { get; } = ValidateSqlState(sqlState);

    /// <summary>
    /// Validates an optional error code using the same syntax as PostgreSQL exceptions.
    /// </summary>
    private static string? ValidateSqlState(string? sqlState)
    {
        if (sqlState is not null && (sqlState.Length != 5 || sqlState == "00000" ||
            sqlState.Any(static value => value is not (>= '0' and <= '9' or >= 'A' and <= 'Z'))))
        {
            throw new ArgumentException("An error SQLSTATE must contain five uppercase ASCII letters or digits and not be 00000.",
                nameof(sqlState));
        }

        return sqlState;
    }
}
