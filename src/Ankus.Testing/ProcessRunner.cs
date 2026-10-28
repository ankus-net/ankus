using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ankus.Testing;

/// <summary>
/// Executes native PostgreSQL tools directly without a command shell and applies
/// a caller-provided process environment consistently across platforms.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Executes a process, captures both output streams, and terminates its process
    /// tree if cancellation interrupts the invocation.
    /// </summary>
    /// <param name="fileName">The executable to run.</param>
    /// <param name="arguments">Individual command-line arguments.</param>
    /// <param name="environment">Environment variables to add or replace.</param>
    /// <param name="cancellationToken">Cancels and terminates the process.</param>
    /// <param name="captureOutput">Whether to redirect output; otherwise the child inherits the host's handles.</param>
    /// <param name="workingDirectory">The child process directory, or the current directory when omitted.</param>
    /// <param name="terminateProcessTree">
    /// Whether cancellation terminates the child's descendants too. <c>sudo</c> children belong to another account,
    /// so cancellation terminates only <c>sudo</c> itself.
    /// </param>
    /// <returns>The captured process result.</returns>
    internal static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken,
        bool captureOutput = true,
        string? workingDirectory = null,
        bool terminateProcessTree = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach ((string key, string? value) in environment)
        {
            process.StartInfo.Environment[key] = value;
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }

        Task<string> standardOutput = Task.FromResult(string.Empty);
        Task<string> standardError = Task.FromResult(string.Empty);

        try
        {
            if (captureOutput)
            {
                standardOutput = ReadOutputAsync(process.StandardOutput);
                standardError = ReadOutputAsync(process.StandardError);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                process.Kill(terminateProcessTree);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between cancellation and termination.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            Task[] streams = [standardOutput, standardError];
            await Task.WhenAll(streams).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    /// <summary>
    /// Drains synchronous Windows process pipes without occupying the test host's thread-pool workers.
    /// The process owner kills and joins the child before joining these readers on cancellation.
    /// </summary>
    private static Task<string> ReadOutputAsync(StreamReader reader)
        => OperatingSystem.IsWindows()
            ? Task.Factory.StartNew(reader.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            : reader.ReadToEndAsync(CancellationToken.None);

    /// <summary>
    /// Executes a process and throws a detailed exception when it exits
    /// unsuccessfully.
    /// </summary>
    /// <param name="fileName">The executable to run.</param>
    /// <param name="arguments">Individual command-line arguments.</param>
    /// <param name="environment">Environment variables to add or replace.</param>
    /// <param name="cancellationToken">Cancels and terminates the process.</param>
    /// <param name="captureOutput">Whether to redirect output; otherwise the child inherits the host's handles.</param>
    /// <param name="workingDirectory">The child process directory, or the current directory when omitted.</param>
    /// <returns>The successful process result.</returns>
    internal static async Task<ProcessResult> RunCheckedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken,
        bool captureOutput = true,
        string? workingDirectory = null)
    {
        ProcessResult result = await RunAsync(
            fileName,
            arguments,
            environment,
            cancellationToken,
            captureOutput,
            workingDirectory).ConfigureAwait(false);
        result.EnsureSuccess(fileName, arguments);
        return result;
    }
}
