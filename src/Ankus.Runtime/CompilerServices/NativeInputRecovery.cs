using System;

namespace Ankus.CompilerServices;

/// <summary>
/// Requests real rollback for deliberately recoverable native input conversions.
/// </summary>
internal static class NativeInputRecovery
{
    [ThreadStatic]
    private static int s_depth;

    /// <summary>
    /// Gets whether native input requests should use a recovery subtransaction where permitted.
    /// </summary>
    internal static bool IsActive => s_depth != 0;

    /// <summary>
    /// Runs an input conversion while preserving native errors when PostgreSQL forbids independent rollback.
    /// </summary>
    /// <typeparam name="T">The detached conversion result.</typeparam>
    /// <param name="parse">The synchronous input conversion.</param>
    /// <returns>The converted value.</returns>
    internal static T Run<T>(Func<T> parse)
    {
        ArgumentNullException.ThrowIfNull(parse);
        NativeBackend.CheckAccess();
        s_depth++;
        try
        {
            return parse();
        }
        finally
        {
            s_depth--;
        }
    }
}
