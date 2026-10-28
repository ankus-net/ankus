using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Ankus.PgConfig;

/// <summary>
/// Removes storage that belonged to stopped PostgreSQL servers.
/// </summary>
public static class PostgresServerStorage
{
    /// <summary>
    /// Gets the interval between Windows deletion attempts, matching PostgreSQL's <c>pgunlink</c>.
    /// </summary>
    private static readonly TimeSpan s_retryInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets the longest time that Windows deletion waits for handles to close, matching PostgreSQL's <c>pgunlink</c>.
    /// </summary>
    private static readonly TimeSpan s_retryLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Deletes a directory tree after the servers using it have stopped.
    /// </summary>
    /// <param name="path">The directory to delete. A missing directory is already deleted.</param>
    /// <remarks>
    /// On Windows, a stopped server's processes can still hold handles to its files for a short time after
    /// <c>pg_ctl stop</c> returns. Like PostgreSQL's own Windows <c>unlink</c>, deletion retries access and sharing
    /// failures every 100 milliseconds for up to 10 seconds, then reports the last failure. Other platforms delete once.
    /// </remarks>
    /// <exception cref="IOException">A file remained in use or the directory could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to a file or directory remained denied.</exception>
    public static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (DirectoryNotFoundException) when (!Directory.Exists(path))
            {
                return;
            }
            catch (Exception error) when (OperatingSystem.IsWindows() && error is IOException or UnauthorizedAccessException &&
                Stopwatch.GetElapsedTime(started) < s_retryLimit)
            {
                Thread.Sleep(s_retryInterval);
            }
        }
    }
}
