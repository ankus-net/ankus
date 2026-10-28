using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ankus.Testing;

/// <summary>
/// Selects deterministic <c>initdb</c> locale arguments using pgrx's operating
/// system rules.
/// </summary>
internal static class PostgresLocale
{
    /// <summary>
    /// Returns <c>C.UTF-8</c> on Unix when available, the macOS C/UTF-8 pairing,
    /// and the portable C locale on Windows or as a fallback.
    /// </summary>
    /// <param name="environment">Environment variables passed to locale discovery.</param>
    /// <param name="cancellationToken">Cancels locale discovery.</param>
    /// <returns>The locale arguments for <c>initdb</c>.</returns>
    internal static async Task<IReadOnlyList<string>> GetInitDbArgumentsAsync(
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return ["--locale=C"];
        }

        if (OperatingSystem.IsMacOS())
        {
            return ["--locale=C", "--lc-ctype=UTF-8"];
        }

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync("locale", ["-a"], environment, cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ["--locale=C"];
        }

        bool hasUtf8 = result.ExitCode == 0 &&
            result.StandardOutput.Split('\n').Any(
                static value => value.Trim() is "C.UTF-8" or "C.utf8");
        return hasUtf8 ? ["--locale=C.UTF-8"] : ["--locale=C"];
    }
}
