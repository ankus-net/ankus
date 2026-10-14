using Ankus.PgConfig;

namespace Ankus.Tool;

/// <summary>
/// Installs an extension into directories the current account cannot write, as <c>cargo pgrx install --sudo</c> does.
/// </summary>
internal static class ExtensionSudoInstaller
{
    /// <summary>
    /// Stages the complete artifact set as the current account, then copies each file into place with <c>sudo</c>,
    /// control file last.
    /// </summary>
    /// <remarks>
    /// Validation, directory resolution and ordering are those of an ordinary installation. Each file is copied beside
    /// its destination and renamed over it, so a running server never maps a partially written library. Missing
    /// destination directories are created with <c>sudo mkdir -p</c>. The staging directory is always removed.
    /// </remarks>
    /// <param name="source">The publish directory.</param>
    /// <param name="installation">The target PostgreSQL installation.</param>
    /// <param name="destinationRoot">An optional root, analogous to DESTDIR, beneath which PostgreSQL's paths are preserved.</param>
    /// <param name="token">Cancels installation between commands.</param>
    /// <returns>Zero after every file is installed, otherwise the exit code of the failed <c>sudo</c> command.</returns>
    internal static async Task<int> InstallAsync(string source, PostgresInstallation installation, string? destinationRoot,
        CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("--sudo is not supported on Windows; install from an elevated terminal instead.");
        }

        string root = destinationRoot is null ? Path.GetPathRoot(installation.SharedDirectory)! : Path.GetFullPath(destinationRoot);
        string staging = Directory.CreateTempSubdirectory("ankus-install-").FullName;
        try
        {
            IReadOnlyList<string> staged = ExtensionInstaller.Install(source, installation, staging, token);
            Console.Error.WriteLine($"Using sudo to copy extension files from {staging}");
            foreach (string file in staged)
            {
                string destination = Path.Join(root, Path.GetRelativePath(staging, file));
                string directory = Path.GetDirectoryName(destination)!;
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                int code;
                try
                {
                    code = Directory.Exists(directory) ? 0 : await RunAsync(["mkdir", "-p", "--", directory], token);
                    if (code == 0)
                    {
                        code = await RunAsync(["cp", "--", file, temporary], token);
                    }

                    if (code == 0)
                    {
                        code = await RunAsync(["mv", "-f", "--", temporary, destination], token);
                    }
                }
                catch (OperationCanceledException)
                {
                    await RemoveAsync(temporary);
                    throw;
                }

                if (code != 0)
                {
                    await RemoveAsync(temporary);
                    Console.Error.WriteLine($"Ankus: sudo command failed with exit code {code}.");
                    return code;
                }

                Console.WriteLine($"Installed {destination}");
            }

            return 0;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task RemoveAsync(string temporary)
    {
        if (File.Exists(temporary))
        {
            await RunAsync(["rm", "-f", "--", temporary], CancellationToken.None);
        }
    }

    private static Task<int> RunAsync(string[] arguments, CancellationToken token)
    {
        Console.Error.WriteLine("Running sudo " + string.Join(' ', arguments));
        return ToolProcess.RunAsync("sudo", arguments, token);
    }
}
