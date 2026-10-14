using System.Text;

namespace Ankus.IntegrationTests;

/// <summary>
/// Keeps test-controller sockets within the portable Unix pathname limit independently of build directories.
/// </summary>
internal static class PackagePipeDirectory
{
    /// <summary>
    /// Reuses the temporary directory when it fits, otherwise creates a separately owned short socket directory.
    /// </summary>
    /// <param name="temporaryDirectory">The physical directory used for compiler temporary files.</param>
    /// <param name="fallbackDirectory">An existing short parent for an isolated socket directory.</param>
    /// <returns>The existing temporary directory or a new directory that the caller must remove.</returns>
    internal static string Create(string temporaryDirectory, string fallbackDirectory)
    {
        if (OperatingSystem.IsWindows() || Fits(temporaryDirectory))
        {
            return temporaryDirectory;
        }

        string parent = IntegrationEnvironment.PhysicalDirectory(new DirectoryInfo(fallbackDirectory));
        string directory = Path.Join(parent, ".ap-" + Guid.NewGuid().ToString("N")[..12]);
        if (!Fits(directory))
        {
            throw new PathTooLongException("The test socket directory exceeds the Unix pathname limit. Set TESTINGPLATFORM_PIPE_DIRECTORY to a shorter existing directory.");
        }

        return Directory.CreateDirectory(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
    }

    /// <summary>
    /// Checks the complete socket pathname in bytes, including the controller-generated name.
    /// </summary>
    private static bool Fits(string directory)
        // MTP's monitoring controller prefixes its GUID; reserve the complete 46-byte name.
        // macOS permits 103 UTF-8 pathname bytes, excluding the terminating null byte.
        => Encoding.UTF8.GetByteCount(Path.Join(directory, "MONITORTOHOST_" + new string('p', 32))) <= 103;
}
