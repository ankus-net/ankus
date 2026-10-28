using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ankus.PgConfig;

/// <summary>
/// Adapts PostgreSQL's recorded build flags to the selected native development toolchain.
/// </summary>
internal static class NativeCompilerArguments
{
    /// <summary>
    /// Keeps PostgreSQL's definitions and search paths while selecting the current macOS development SDK.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> CreateAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return arguments;
        }

        string sdk = await ResolveMacOsSdkAsync(Environment.GetEnvironmentVariable("SDKROOT"), cancellationToken).ConfigureAwait(false);
        return WithMacOsSdk(arguments, sdk);
    }

    /// <summary>
    /// Honors an explicit SDKROOT or asks the active Apple developer tools for their macOS SDK.
    /// </summary>
    internal static async Task<string> ResolveMacOsSdkAsync(string? sdk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(sdk))
        {
            sdk = await PostgresInstallation.QueryAsync("/usr/bin/xcrun", ["--sdk", "macosx", "--show-sdk-path"],
                cancellationToken).ConfigureAwait(false);
        }

        if (!Path.IsPathFullyQualified(sdk) || !Directory.Exists(sdk))
        {
            throw new DirectoryNotFoundException("The selected macOS SDK does not exist. Select an installed SDK with SDKROOT or xcode-select.");
        }

        return sdk;
    }

    /// <summary>
    /// Replaces historical SDK roots without changing other argument boundaries or caller-owned flags.
    /// </summary>
    internal static IReadOnlyList<string> WithMacOsSdk(IReadOnlyList<string> arguments, string sdk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdk);
        var result = new List<string>();
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument is "-isysroot" or "--sysroot")
            {
                if (++index == arguments.Count || string.IsNullOrWhiteSpace(arguments[index]) || arguments[index].StartsWith('-'))
                {
                    throw new FormatException("PostgreSQL preprocessor flags contain a sysroot option without a path.");
                }
            }
            else if (argument == "--sysroot=" || argument == "-isysroot=")
            {
                throw new FormatException("PostgreSQL preprocessor flags contain a sysroot option without a path.");
            }
            else if (!argument.StartsWith("-isysroot", StringComparison.Ordinal) && !argument.StartsWith("--sysroot=", StringComparison.Ordinal))
            {
                result.Add(argument);
            }
        }

        result.AddRange(["-isysroot", sdk]);
        return result.AsReadOnly();
    }
}
