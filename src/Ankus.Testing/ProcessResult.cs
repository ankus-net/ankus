using System;
using System.Collections.Generic;

namespace Ankus.Testing;

/// <summary>
/// Captures the complete result of one native process invocation.
/// </summary>
internal sealed class ProcessResult
{
    /// <summary>
    /// Initializes a captured process result.
    /// </summary>
    /// <param name="exitCode">The native process exit code.</param>
    /// <param name="standardOutput">The captured standard output.</param>
    /// <param name="standardError">The captured standard error.</param>
    internal ProcessResult(int exitCode, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    /// <summary>
    /// Gets the native process exit code.
    /// </summary>
    internal int ExitCode { get; }

    /// <summary>
    /// Gets the complete standard output.
    /// </summary>
    internal string StandardOutput { get; }

    /// <summary>
    /// Gets the complete standard error.
    /// </summary>
    internal string StandardError { get; }

    /// <summary>
    /// Throws when the process failed and includes both captured output streams
    /// in the diagnostic message.
    /// </summary>
    /// <param name="fileName">The executable that was invoked.</param>
    /// <param name="arguments">The arguments supplied to the executable.</param>
    internal void EnsureSuccess(string fileName, IEnumerable<string> arguments)
    {
        if (ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"'{fileName} {string.Join(' ', arguments)}' exited with code {ExitCode}." +
            $"{Environment.NewLine}stdout:{Environment.NewLine}{StandardOutput}" +
            $"{Environment.NewLine}stderr:{Environment.NewLine}{StandardError}");
    }
}
