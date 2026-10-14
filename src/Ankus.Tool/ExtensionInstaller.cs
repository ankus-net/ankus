using System.Runtime.InteropServices;
using Ankus.PgConfig;

namespace Ankus.Tool;

/// <summary>
/// Installs the manifest's native library and control files, placing SQL in the declared PostgreSQL directory.
/// </summary>
internal static class ExtensionInstaller
{
    /// <summary>
    /// Validates the full artifact set and target before copying files, publishing the control file last.
    /// </summary>
    /// <param name="source">The publish directory.</param>
    /// <param name="installation">The target PostgreSQL installation.</param>
    /// <param name="destinationRoot">An optional staging root, analogous to DESTDIR.</param>
    /// <param name="token">Cancels copying between artifacts.</param>
    /// <param name="packageLayout">Whether Windows output uses lib and share/extension relative to the package root.</param>
    /// <param name="prefixDirectory">An optional flat Unix package asset directory; Windows retains its portable layout.</param>
    /// <returns>The installed file paths.</returns>
    internal static IReadOnlyList<string> Install(string source, PostgresInstallation installation, string? destinationRoot,
        CancellationToken token, bool packageLayout = false, string? prefixDirectory = null)
    {
        source = Path.GetFullPath(source);
        PublishedExtension manifest = PublishedExtension.Read(source);
        if (manifest.PostgresMajor != installation.Version.Major)
        {
            throw new InvalidOperationException(
                $"The extension was built for PostgreSQL {manifest.PostgresMajor}, not {installation.Label}.");
        }

        if (installation.TargetInfo is not null && !packageLayout)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {installation.Version} is installed on another machine; package the extension and install it there.");
        }

        string runtime = installation.TargetInfo?.RuntimeIdentifier ?? RuntimeInformation.RuntimeIdentifier;
        if (manifest.RuntimeIdentifier != runtime)
        {
            throw new InvalidOperationException(
                $"The extension targets {manifest.RuntimeIdentifier}, not {runtime}.");
        }

        string libraryDirectory = StagePath(installation.LibraryDirectory, destinationRoot);
        string extensionDirectory = StagePath(Path.Join(installation.SharedDirectory, "extension"), destinationRoot);
        string scriptBase = installation.SharedDirectory;
        string? prefix = null;
        if (prefixDirectory is not null)
        {
            if (!packageLayout)
            {
                throw new ArgumentException("An asset prefix requires a package layout.", nameof(prefixDirectory));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
            prefix = GetPackagePrefix(destinationRoot, prefixDirectory);
        }

        if (packageLayout && OperatingSystem.IsWindows())
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
            string root = Path.GetFullPath(destinationRoot);
            libraryDirectory = Path.Join(root, "lib");
            extensionDirectory = Path.Join(root, "share", "extension");
            scriptBase = Path.Join(root, "share");
        }
        else if (prefix is not null)
        {
            libraryDirectory = prefix;
            extensionDirectory = prefix;
            if (installation.Version.Major >= 18)
            {
                string logical = Path.Join(Path.GetPathRoot(installation.SharedDirectory)!,
                    Path.GetRelativePath(Path.GetFullPath(destinationRoot!), prefix));
                scriptBase = Path.GetDirectoryName(logical) ?? Path.GetPathRoot(logical)!;
            }
        }

        IReadOnlyList<string> scriptDirectories = GetScriptDirectoryTraversal(manifest.GetScriptDirectory(source, scriptBase));
        string scriptDirectory = scriptDirectories[^1];
        if (prefix is not null && !OperatingSystem.IsWindows() && manifest.ScriptDirectory is null)
        {
            scriptDirectories = [extensionDirectory];
            scriptDirectory = extensionDirectory;
        }
        else if (packageLayout && OperatingSystem.IsWindows() && !Path.IsPathRooted(manifest.ScriptDirectory ?? "extension"))
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(destinationRoot!), scriptDirectory);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                throw new InvalidOperationException("The SQL directory escapes the portable Windows package root; use install --destdir for a full filesystem layout.");
            }
        }
        else
        {
            scriptDirectories = [.. scriptDirectories.Select(directory => StagePath(directory, destinationRoot))];
            scriptDirectory = scriptDirectories[^1];
        }

        (string Source, string Destination)[] files =
        [
            (Path.Join(source, manifest.Library), Path.Join(libraryDirectory, manifest.Library)),
            .. manifest.PluginLibraries.Select(module => (Path.Join(source, module), Path.Join(libraryDirectory, module))),
            (Path.Join(source, "extension", manifest.Sql), Path.Join(scriptDirectory, manifest.Sql)),
            .. manifest.UpgradeScripts.Select(script =>
                (Path.Join(source, "extension", script), Path.Join(scriptDirectory, script))),
            .. manifest.VersionControlFiles.Select(control =>
                (Path.Join(source, "extension", control), Path.Join(scriptDirectory, control))),
            (Path.Join(source, "extension", manifest.Control), Path.Join(extensionDirectory, manifest.Control)),
        ];
        foreach ((string input, _) in files)
        {
            if (!File.Exists(input))
            {
                throw new FileNotFoundException("The published extension is incomplete.", input);
            }
        }

        foreach (string directory in scriptDirectories)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
        }

        foreach ((string input, string destination) in files)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(input, temporary);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        return [.. files.Select(static file => file.Destination)];
    }

    /// <summary>
    /// Normalizes a package asset prefix and rejects paths that lexically escape the selected output root.
    /// </summary>
    /// <param name="outputRoot">The package's output directory.</param>
    /// <param name="prefixDirectory">A relative prefix or a fully qualified target directory whose filesystem root is stripped.</param>
    /// <returns>The normalized asset directory inside the output root.</returns>
    /// <exception cref="ArgumentException">The prefix is blank, partially rooted or escapes the output root.</exception>
    internal static string GetPackagePrefix(string outputRoot, string prefixDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefixDirectory);
        if (Path.IsPathRooted(prefixDirectory) && !Path.IsPathFullyQualified(prefixDirectory))
        {
            throw new ArgumentException("Package prefixes must be relative or fully qualified, not partially rooted.", nameof(prefixDirectory));
        }

        string relative = Path.IsPathFullyQualified(prefixDirectory)
            ? prefixDirectory[Path.GetPathRoot(prefixDirectory)!.Length..] : prefixDirectory;
        string root = Path.GetFullPath(outputRoot);
        string destination = Path.GetFullPath(Path.Join(root, relative));
        string resolved = Path.GetRelativePath(root, destination);
        if (resolved == ".." || resolved.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(resolved))
        {
            throw new ArgumentException("The package prefix escapes the output directory.", nameof(prefixDirectory));
        }

        return destination;
    }

    private static List<string> GetScriptDirectoryTraversal(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return [Path.GetFullPath(path)];
        }

        string current = Path.GetPathRoot(path)!;
        var directories = new List<string> { current };
        foreach (string component in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                var directory = new DirectoryInfo(current);
                if (directory.LinkTarget is not null)
                {
                    current = Path.TrimEndingDirectorySeparator(directory.ResolveLinkTarget(returnFinalTarget: true)!.FullName);
                }

                current = Path.GetDirectoryName(current) ?? Path.GetPathRoot(current)!;
            }
            else
            {
                current = Path.Join(current, component);
            }

            directories.Add(current);
        }

        return directories;
    }

    private static string StagePath(string absolutePath, string? root)
        => root is null ? absolutePath : Path.Join(Path.GetFullPath(root), absolutePath[Path.GetPathRoot(absolutePath)!.Length..]);
}
