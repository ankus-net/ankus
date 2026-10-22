using System.Text;
using Ankus.PgConfig;

namespace Ankus.Tool;

/// <summary>
/// Stages a publication's control and SQL files beside it, for servers that find them through PostgreSQL 18's
/// <c>extension_control_path</c> and the library through <c>dynamic_library_path</c>, so nothing is written into the
/// PostgreSQL installation.
/// </summary>
internal static class ExtensionStaging
{
    /// <summary>
    /// Determines whether a server can find files staged beneath a publication through its search-path settings.
    /// </summary>
    /// <param name="installation">The PostgreSQL installation that will run the server.</param>
    /// <param name="output">The publication directory.</param>
    /// <returns>True for PostgreSQL 18 and later, except on Windows when the publication path is not ASCII.</returns>
    /// <remarks>
    /// Ankus writes server settings as UTF-8, and PostgreSQL on Windows opens the paths they name in the ANSI code page,
    /// so only an ASCII path reaches the same directory there.
    /// </remarks>
    internal static bool CanStage(PostgresInstallation installation, string output)
        => installation.Version.Major >= 18 && (!OperatingSystem.IsWindows() || Ascii.IsValid(Path.GetFullPath(output)));

    /// <summary>
    /// Copies the control and SQL files into a directory beneath the publication, replacing any earlier copy.
    /// </summary>
    /// <param name="output">The publication directory.</param>
    /// <param name="directory">The staging directory's name beneath the publication.</param>
    /// <param name="token">Cancels staging before the new copy replaces the old one.</param>
    /// <param name="installed">The staged files.</param>
    /// <returns>The directory to add to <c>extension_control_path</c>.</returns>
    internal static string Stage(string output, string directory, CancellationToken token, out IReadOnlyList<string> installed)
    {
        PublishedExtension manifest = PublishedExtension.Read(output);
        string source = Path.Join(output, "extension");
        string stage = Path.Join(output, directory);
        string temporary = stage + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string controls = Path.Join(temporary, "extension");
        string scripts = manifest.ScriptDirectory is null ? controls : Path.Join(temporary, "scripts");
        string[] scriptNames = [manifest.Sql, .. manifest.UpgradeScripts, .. manifest.VersionControlFiles];
        string[] sourceFiles =
        [
            Path.Join(output, manifest.Library),
            Path.Join(source, manifest.Control),
            .. scriptNames.Select(name => Path.Join(source, name)),
        ];
        foreach (string path in sourceFiles)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The published extension is incomplete.", path);
            }
        }

        _ = manifest.GetScriptDirectory(output, temporary);
        IReadOnlyDictionary<string, string> authoredControl = ExtensionControlFile.Read(sourceFiles[1]);
        string? stagedControl = null;
        if (manifest.ScriptDirectory is not null)
        {
            var values = new Dictionary<string, string>(authoredControl, StringComparer.Ordinal)
            {
                ["directory"] = "scripts",
            };
            stagedControl = ExtensionControlFile.Format(values);
        }

        try
        {
            Directory.CreateDirectory(controls);
            Directory.CreateDirectory(scripts);
            var destinations = new List<string>(scriptNames.Length + 1);
            foreach (string name in scriptNames)
            {
                token.ThrowIfCancellationRequested();
                string destination = Path.Join(scripts, name);
                File.Copy(Path.Join(source, name), destination);
                destinations.Add(destination);
            }

            string control = Path.Join(controls, manifest.Control);
            if (stagedControl is null)
            {
                File.Copy(sourceFiles[1], control);
            }
            else
            {
                File.WriteAllText(control, stagedControl);
            }

            destinations.Add(control);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, recursive: true);
            }

            Directory.Move(temporary, stage);
            installed = [.. destinations.Select(path => Path.Join(stage, Path.GetRelativePath(temporary, path)))];
            return stage;
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }
}
