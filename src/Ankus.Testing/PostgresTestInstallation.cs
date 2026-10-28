using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ankus.PgConfig;

namespace Ankus.Testing;

/// <summary>
/// Owns a relocatable copy of a PostgreSQL installation for isolated extension tests.
/// </summary>
public sealed class PostgresTestInstallation : IAsyncDisposable
{
    private int _disposed;

    private PostgresTestInstallation(PostgresInstallation installation, string rootDirectory)
    {
        Installation = installation;
        RootDirectory = rootDirectory;
    }

    /// <summary>
    /// Gets the relocated PostgreSQL installation.
    /// </summary>
    public PostgresInstallation Installation { get; }

    /// <summary>
    /// Gets the staging root owned by this instance.
    /// </summary>
    public string RootDirectory { get; }

    /// <summary>
    /// Copies PostgreSQL into a relocatable staging root without changing the source installation.
    /// </summary>
    /// <param name="installation">The source PostgreSQL installation.</param>
    /// <param name="rootDirectory">An empty directory to own as the staging root.</param>
    /// <param name="cancellationToken">Cancels file staging and PostgreSQL inspection.</param>
    /// <returns>The staged installation owner.</returns>
    public static async Task<PostgresTestInstallation> StageAsync(
        PostgresInstallation installation,
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string root = Path.GetFullPath(rootDirectory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException($"The PostgreSQL staging directory is not empty: {root}");
        }

        Directory.CreateDirectory(root);
        try
        {
            string[] directories =
            [
                installation.BinDirectory,
                installation.LibraryDirectory,
                installation.SharedDirectory,
                installation.IncludeDirectory,
                installation.ServerIncludeDirectory,
            ];
            StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            foreach (string source in directories.Distinct(comparer))
            {
                CopyDirectory(source, StagePath(source, root), cancellationToken);
            }

            // pg_config may be reached through an alias outside the authoritative binary directory,
            // such as Homebrew's opt symlink. Query the executable in the directory we actually copied.
            string stagedPgConfig = Path.Join(StagePath(installation.BinDirectory, root),
                OperatingSystem.IsWindows() ? "pg_config.exe" : "pg_config");
            PostgresInstallation staged = await PostgresInstallation.CreateAsync(stagedPgConfig, cancellationToken)
                .ConfigureAwait(false);
            if (staged.Version.Major != installation.Version.Major)
            {
                throw new InvalidOperationException("The staged PostgreSQL installation changed major version.");
            }

            return new PostgresTestInstallation(staged, root);
        }
        catch
        {
            PostgresServerStorage.Delete(root);
            throw;
        }
    }

    /// <summary>
    /// Copies the publication manifest's declared control and SQL files into this isolated installation.
    /// Native libraries can remain in the publish directory and be selected with <c>dynamic_library_path</c>.
    /// Custom SQL directories are remapped inside this installation without changing the publication.
    /// </summary>
    /// <param name="publishDirectory">The Ankus publish directory.</param>
    public void InstallExtensionFiles(string publishDirectory)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        PostgresExtensionFiles.Stage(publishDirectory, Installation.SharedDirectory);
    }

    /// <summary>
    /// Deletes the staged installation after the servers using it have stopped.
    /// </summary>
    /// <remarks>
    /// On Windows, a stopped server's exiting processes can briefly keep the staged executables and libraries open;
    /// deletion retries as <see cref="PostgresServerStorage.Delete(string)"/> describes.
    /// </remarks>
    /// <returns>A completed task after synchronous file cleanup.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            PostgresServerStorage.Delete(RootDirectory);
        }

        return ValueTask.CompletedTask;
    }

    private static string StagePath(string absolutePath, string rootDirectory)
    {
        string fullPath = Path.GetFullPath(absolutePath);
        string pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"PostgreSQL returned a path without a root: {fullPath}");
        return Path.Join(rootDirectory, fullPath[pathRoot.Length..]);
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"The PostgreSQL installation directory was not found: {source}");
        }

        Directory.CreateDirectory(destination);
        foreach (string sourceDirectory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(source, sourceDirectory);
            Directory.CreateDirectory(Path.Join(destination, relativePath));
        }

        foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(source, sourceFile);
            string destinationFile = Path.Join(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destinationFile, File.GetUnixFileMode(sourceFile));
            }
        }
    }
}
