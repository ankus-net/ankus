namespace Ankus.Build;

/// <summary>
/// Resolves owned native build staging and removes it after tools and diagnostic streams have finished.
/// </summary>
internal static class NativeBuildDirectory
{
    /// <summary>
    /// Resolves directory aliases in every ancestor so native tools and managed input snapshots share one staging location.
    /// </summary>
    /// <param name="directory">The directory whose physical path is required.</param>
    /// <returns>The directory path after resolving its symbolic-link ancestors.</returns>
    internal static string PhysicalPath(DirectoryInfo directory)
    {
        // Windows link resolution uses file enumeration, which cannot query a drive root.
        if (directory.Parent is null)
        {
            return directory.FullName;
        }

        DirectoryInfo resolved = (DirectoryInfo?)directory.ResolveLinkTarget(returnFinalTarget: true) ?? directory;
        return resolved.Parent is DirectoryInfo parent
            ? Path.Join(PhysicalPath(parent), resolved.Name)
            : resolved.FullName;
    }

    /// <summary>
    /// Allows bounded Windows file-release retries while preserving persistent access and ownership failures.
    /// </summary>
    /// <param name="directory">The caller-owned directory whose child processes have already been joined.</param>
    internal static async Task DeleteAsync(string directory)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception error) when (OperatingSystem.IsWindows() && attempt < 19 &&
                error is IOException or UnauthorizedAccessException && (error.HResult & 0xFFFF) is 5 or 32 or 33 or 145)
            {
                // Access denied, sharing/lock violation, or a directory containing a locked file.
                // Owned cleanup must finish even after the operation's cancellation token expires.
                await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
            }
        }
    }
}
