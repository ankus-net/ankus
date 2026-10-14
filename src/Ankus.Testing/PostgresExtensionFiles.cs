using Ankus.PgConfig;

namespace Ankus.Testing;

/// <summary>
/// Stages declared extension files while keeping author SQL directories inside an owned test location.
/// </summary>
internal static class PostgresExtensionFiles
{
    /// <summary>
    /// Copies a complete publication, remapping custom SQL directories only in the staged primary control.
    /// </summary>
    /// <param name="publishDirectory">The Ankus publication with its manifest.</param>
    /// <param name="baseDirectory">The owned PostgreSQL shared directory or extension search base.</param>
    internal static void Stage(string publishDirectory, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishDirectory);
        string source = Path.Join(Path.GetFullPath(publishDirectory), "extension");
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"The published extension directory was not found: {source}");
        }

        PublishedExtension manifest = PublishedExtension.Read(publishDirectory);
        _ = manifest.GetScriptDirectory(publishDirectory, baseDirectory);
        string[] scripts = [manifest.Sql, .. manifest.UpgradeScripts, .. manifest.VersionControlFiles];
        foreach (string path in scripts.Prepend(manifest.Control).Select(file => Path.Join(source, file)))
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The published extension is incomplete.", path);
            }
        }

        string controls = Path.Join(baseDirectory, "extension");
        string scriptDirectory = controls;
        string? stagedControl = null;
        if (manifest.ScriptDirectory is not null)
        {
            string relative = "ankus-test-scripts/" + Path.GetFileNameWithoutExtension(manifest.Control) + "-scripts";
            scriptDirectory = Path.Join(baseDirectory, relative);
            var settings = new Dictionary<string, string>(ExtensionControlFile.Read(Path.Join(source, manifest.Control)), StringComparer.Ordinal)
            {
                ["directory"] = relative,
            };
            stagedControl = ExtensionControlFile.Format(settings);
        }

        Directory.CreateDirectory(controls);
        Directory.CreateDirectory(scriptDirectory);
        foreach (string file in scripts)
        {
            File.Copy(Path.Join(source, file), Path.Join(scriptDirectory, file), overwrite: true);
        }

        string destination = Path.Join(controls, manifest.Control);
        if (stagedControl is null)
        {
            File.Copy(Path.Join(source, manifest.Control), destination, overwrite: true);
        }
        else
        {
            File.WriteAllText(destination, stagedControl);
        }
    }
}
