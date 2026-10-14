using System.Diagnostics;
using System.Text;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Publishes native-build control snapshots and declared upgrades after validating names and expanding SQL tokens.
/// </summary>
internal static class UpgradeSqlCommand
{
    /// <summary>
    /// Publishes the installation SQL and selected upgrades, then commits the complete manifest.
    /// </summary>
    /// <param name="arguments">
    /// Artifact directory, publish directory, script list, project directory, extension version, optional schema snapshot
    /// destination, optional versioned-library mode, and an optional list of the output plugin modules built beside the
    /// library.
    /// </param>
    /// <param name="cancellationToken">Cancels script reads and Git inspection.</param>
    /// <returns>A task that completes after the publication is installable.</returns>
    /// <remarks>
    /// Versioned libraries have no primary <c>module_pathname</c>. An upgrade script to version <c>new</c> therefore
    /// resolves MODULE_PATHNAME to that version's library, as PostgreSQL would with a control file naming it, unless the
    /// published control for <c>new</c> declares its own <c>module_pathname</c> for PostgreSQL to substitute.
    /// </remarks>
    internal static async Task RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length is not (5 or 6 or 7 or 8))
        {
            throw new ArgumentException("Expected artifact directory, publish directory, upgrade list, project directory, extension version, " +
                "optional schema snapshot destination, optional versioned-library mode, and an optional plugin module list.", nameof(arguments));
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool versionedLibrary = arguments.Length >= 7 && VersionedLibrary.ParseMode(arguments[6]);
        string[] plugins = arguments.Length == 8 && File.Exists(arguments[7])
            ? [.. File.ReadAllLines(arguments[7]).Where(static line => line.Length > 0)] : [];
        string artifacts = Path.GetFullPath(arguments[0]);
        string output = Path.GetFullPath(arguments[1]);
        PublishedExtension.Invalidate(output);
        PublishedExtension original = PublishedExtension.Read(artifacts);
        string[] scripts = File.ReadAllLines(arguments[2]);
        var manifest = new PublishedExtension(original.PostgresMajor, original.RuntimeIdentifier, original.Library,
            original.Control, original.Sql, [.. scripts.Select(static path => Path.GetFileName(path))], original.VersionControlFiles,
            original.ScriptDirectory, plugins);
        string? libraryBase = versionedLibrary ? VersionedLibrary.GetBaseName(original.Library, arguments[4]) : null;
        string versionPrefix = original.Control[..^".control".Length] + "--";
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string? gitHash = null;
        foreach (string script in scripts)
        {
            string content = encoding.GetString(await File.ReadAllBytesAsync(script, cancellationToken));
            if (content.Contains("@GIT_HASH@", StringComparison.Ordinal))
            {
                gitHash ??= await ReadGitHashAsync(arguments[3], cancellationToken);
                content = content.Replace("@GIT_HASH@", gitHash, StringComparison.Ordinal);
            }

            content = content.Replace("@EXTENSION_VERSION@", arguments[4], StringComparison.Ordinal);
            string name = Path.GetFileName(script);
            if (libraryBase is not null)
            {
                // The manifest above validated the extension--old--new.sql form.
                string target = name[versionPrefix.Length..^".sql".Length].Split("--")[1];
                string control = versionPrefix + target + ".control";
                if (!original.VersionControlFiles.Contains(control, StringComparer.Ordinal) ||
                    !ExtensionControlFile.Read(Path.Join(artifacts, "extension", control)).ContainsKey("module_pathname"))
                {
                    content = VersionedLibrary.Substitute(content, VersionedLibrary.GetModulePath(libraryBase, target));
                }
            }

            contents.Add(name, encoding.GetBytes(content));
        }

        // Read the entire SQL and control payload before changing any published files.
        string[] artifactFiles = [original.Control, original.Sql, .. original.VersionControlFiles];
        foreach (string name in artifactFiles)
        {
            contents.Add(name, await File.ReadAllBytesAsync(Path.Join(artifacts, "extension", name), cancellationToken));
        }

        string extensionDirectory = Path.Join(output, "extension");
        Directory.CreateDirectory(extensionDirectory);
        foreach ((string name, byte[] content) in contents)
        {
            await File.WriteAllBytesAsync(Path.Join(extensionDirectory, name), content, cancellationToken);
        }

        manifest.CompletePublish(output);
        if (arguments.Length >= 6 && arguments[5].Length != 0)
        {
            try
            {
                SchemaSnapshot.Commit(Path.Join(artifacts, "schema.generated.json"), arguments[5]);
            }
            catch
            {
                PublishedExtension.Invalidate(output);
                throw;
            }
        }
    }

    private static async Task<string> ReadGitHashAsync(string projectDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-C", projectDirectory, "rev-parse", "HEAD" },
            },
        };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        string hash = (await output).Trim();
        string diagnostic = await error;
        if (process.ExitCode != 0 || hash.Length is not (40 or 64) || !hash.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException("Cannot expand @GIT_HASH@ without a committed Git repository: " + diagnostic);
        }

        return hash;
    }
}
