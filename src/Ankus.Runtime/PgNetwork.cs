using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Shares backend input-error handling for network values.
/// </summary>
internal static class PgNetwork
{
    /// <summary>
    /// Parses an allowlisted network result, catching only malformed input and encoding errors.
    /// </summary>
    /// <typeparam name="T">The network value type.</typeparam>
    /// <param name="text">The PostgreSQL input text.</param>
    /// <param name="value">The parsed value, or default.</param>
    /// <returns>Whether parsing succeeded.</returns>
    internal static bool TryParse<T>(string? text, out T value) where T : struct
    {
        value = default;
        if (text is null || text.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            value = NativeInputRecovery.Run(() => NativeBackend.Network<T>([PgTemporal.Text(text)]));
            return true;
        }
        catch (PgException error) when ((error.NativeFlags & NativeErrorFlags.Unrecovered) == 0 && (error.SqlState == "22P02"))
        {
            return false;
        }
        catch (System.Text.EncoderFallbackException)
        {
            return false;
        }
    }
}
