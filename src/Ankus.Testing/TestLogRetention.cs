using System.Collections.Concurrent;

namespace Ankus.Testing;

/// <summary>
/// Removes the fixture's own logs a week after their last write, so repeated test runs do not fill a log directory.
/// Every run adds a server log per cluster and a binary log per publish, and nothing else ever removes them.
/// </summary>
internal static class TestLogRetention
{
    /// <summary>
    /// Gets how long a log remains after its last write.
    /// </summary>
    internal static TimeSpan Retention { get; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Records the directories this process has already pruned, since a run starts many clusters in one directory.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> s_pruned = new(
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Removes expired logs from a directory the first time this process uses it.
    /// </summary>
    /// <param name="directory">The log directory.</param>
    internal static void Prune(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        if (s_pruned.TryAdd(fullPath, true))
        {
            Prune(fullPath, DateTime.UtcNow - Retention);
        }
    }

    /// <summary>
    /// Removes the logs this package named that were last written before a cutoff, leaving every other file.
    /// A log that cannot be removed, because it is open or another account owns it, is left for a later run.
    /// </summary>
    /// <param name="directory">The log directory.</param>
    /// <param name="cutoffUtc">The oldest last-write time to keep.</param>
    /// <returns>The number of logs removed.</returns>
    internal static int Prune(string directory, DateTime cutoffUtc)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        int removed = 0;
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            if (!IsOwned(Path.GetFileName(path)) || File.GetLastWriteTimeUtc(path) >= cutoffUtc)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Retention is best effort; the log stays until a later run can remove it.
            }
        }

        return removed;
    }

    /// <summary>
    /// Recognizes a cluster's server log (<c>major-process-id.log</c>), the files its collector keeps beside it, such
    /// as Windows' <c>.stderr.log</c> and event sidecars, and an extension publish's binary log (<c>id.binlog</c>),
    /// where each id is a 32-digit GUID that no other file shares.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <returns>Whether the fixture created a file of that name.</returns>
    internal static bool IsOwned(string name)
    {
        if (name.Length == 32 + ".binlog".Length && name.EndsWith(".binlog", StringComparison.Ordinal))
        {
            return IsId(name.AsSpan(0, 32));
        }

        int extension = name.IndexOf(".log", StringComparison.Ordinal);
        if (extension < 0 || !IsClusterInvocation(name.AsSpan(0, extension)))
        {
            return false;
        }

        ReadOnlySpan<char> sidecar = name.AsSpan(extension + ".log".Length);
        return sidecar.IsEmpty || sidecar[0] == '.';
    }

    /// <summary>
    /// Recognizes a cluster's invocation name: the PostgreSQL major, the test process ID and a 32-digit GUID.
    /// </summary>
    private static bool IsClusterInvocation(ReadOnlySpan<char> name)
    {
        int major = name.IndexOf('-');
        if (major <= 0 || !IsDigits(name[..major]))
        {
            return false;
        }

        ReadOnlySpan<char> rest = name[(major + 1)..];
        int process = rest.IndexOf('-');
        return process > 0 && IsDigits(rest[..process]) && IsId(rest[(process + 1)..]);
    }

    private static bool IsDigits(ReadOnlySpan<char> text) => !text.ContainsAnyExceptInRange('0', '9');

    private static bool IsId(ReadOnlySpan<char> text) => text.Length == 32 && Guid.TryParseExact(text, "N", out _);
}
