using System;
using System.IO;
using System.Linq;

namespace Ankus.PgConfig;

/// <summary>
/// Locates native executables through explicit paths and the host process's
/// platform-specific search path.
/// </summary>
internal static class ExecutableLocator
{
    /// <summary>
    /// Finds an executable using the same search-path semantics on Windows,
    /// Linux, and macOS.
    /// </summary>
    /// <param name="name">An executable name or explicit path.</param>
    /// <returns>The absolute path when found; otherwise, <see langword="null"/>.</returns>
    internal static string? Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (Path.IsPathFullyQualified(name) || name.Contains(Path.DirectorySeparatorChar))
        {
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        }

        string executableName = OperatingSystem.IsWindows() && Path.GetExtension(name).Length == 0
            ? $"{name}.exe"
            : name;
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string candidate in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Join(directory.Trim('"'), executableName)))
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
