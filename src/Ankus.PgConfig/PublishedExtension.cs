using System.Text.Json;

namespace Ankus.PgConfig;

/// <summary>
/// Describes the native library, SQL scripts and control files produced by one extension publish.
/// </summary>
public sealed class PublishedExtension
{
    private static readonly StringComparer s_fileComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Gets the manifest filename placed alongside a published native library.
    /// </summary>
    public const string FileName = "ankus.extension.json";

    /// <summary>
    /// Creates a manifest with filename-only artifact names.
    /// </summary>
    /// <param name="postgresMajor">The PostgreSQL header major used to compile the extension.</param>
    /// <param name="runtimeIdentifier">The Native AOT runtime identifier.</param>
    /// <param name="library">The native library filename.</param>
    /// <param name="control">The control filename under extension/.</param>
    /// <param name="sql">The versioned SQL filename under extension/.</param>
    public PublishedExtension(int postgresMajor, string runtimeIdentifier, string library, string control, string sql)
        : this(postgresMajor, runtimeIdentifier, library, control, sql, [])
    {
    }

    /// <summary>
    /// Creates a manifest with an owned, ordered collection of extension upgrade scripts.
    /// </summary>
    /// <param name="postgresMajor">The PostgreSQL header major used to compile the extension.</param>
    /// <param name="runtimeIdentifier">The Native AOT runtime identifier.</param>
    /// <param name="library">The native library filename.</param>
    /// <param name="control">The control filename under extension/.</param>
    /// <param name="sql">The installation SQL filename under extension/.</param>
    /// <param name="upgradeScripts">Upgrade filenames in extension--old--new.sql form.</param>
    public PublishedExtension(int postgresMajor, string runtimeIdentifier, string library, string control, string sql,
        IReadOnlyList<string> upgradeScripts)
        : this(postgresMajor, runtimeIdentifier, library, control, sql, upgradeScripts, [])
    {
    }

    /// <summary>
    /// Creates a manifest with owned, ordered upgrade scripts and version-specific control files.
    /// </summary>
    /// <param name="postgresMajor">The PostgreSQL header major used to compile the extension.</param>
    /// <param name="runtimeIdentifier">The Native AOT runtime identifier.</param>
    /// <param name="library">The native library filename.</param>
    /// <param name="control">The primary control filename under extension/.</param>
    /// <param name="sql">The installation SQL filename under extension/.</param>
    /// <param name="upgradeScripts">Upgrade filenames in extension--old--new.sql form.</param>
    /// <param name="versionControlFiles">Secondary control filenames in extension--version.control form.</param>
    public PublishedExtension(int postgresMajor, string runtimeIdentifier, string library, string control, string sql,
        IReadOnlyList<string> upgradeScripts, IReadOnlyList<string> versionControlFiles)
        : this(postgresMajor, runtimeIdentifier, library, control, sql, upgradeScripts, versionControlFiles, null)
    {
    }

    /// <summary>
    /// Creates a manifest with a declared PostgreSQL SQL directory and a flat, owned publication payload.
    /// </summary>
    /// <param name="postgresMajor">The PostgreSQL header major used to compile the extension.</param>
    /// <param name="runtimeIdentifier">The Native AOT runtime identifier.</param>
    /// <param name="library">The native library filename.</param>
    /// <param name="control">The primary control filename under extension/.</param>
    /// <param name="sql">The installation SQL filename under extension/.</param>
    /// <param name="upgradeScripts">Upgrade filenames in extension--old--new.sql form.</param>
    /// <param name="versionControlFiles">Secondary control filenames in extension--version.control form.</param>
    /// <param name="scriptDirectory">The literal primary control directory setting, or null for PostgreSQL's default.</param>
    public PublishedExtension(int postgresMajor, string runtimeIdentifier, string library, string control, string sql,
        IReadOnlyList<string> upgradeScripts, IReadOnlyList<string> versionControlFiles, string? scriptDirectory)
        : this(postgresMajor, runtimeIdentifier, library, control, sql, upgradeScripts, versionControlFiles, scriptDirectory, [])
    {
    }

    /// <summary>
    /// Creates a manifest whose publication also contains modules that load the extension's output plugin under other
    /// names.
    /// </summary>
    /// <param name="postgresMajor">The PostgreSQL header major used to compile the extension.</param>
    /// <param name="runtimeIdentifier">The Native AOT runtime identifier.</param>
    /// <param name="library">The native library filename.</param>
    /// <param name="control">The primary control filename under extension/.</param>
    /// <param name="sql">The installation SQL filename under extension/.</param>
    /// <param name="upgradeScripts">Upgrade filenames in extension--old--new.sql form.</param>
    /// <param name="versionControlFiles">Secondary control filenames in extension--version.control form.</param>
    /// <param name="scriptDirectory">The literal primary control directory setting, or null for PostgreSQL's default.</param>
    /// <param name="pluginLibraries">Output plugin alias module filenames, installed beside the library.</param>
    public PublishedExtension(int postgresMajor, string runtimeIdentifier, string library, string control, string sql,
        IReadOnlyList<string> upgradeScripts, IReadOnlyList<string> versionControlFiles, string? scriptDirectory,
        IReadOnlyList<string> pluginLibraries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(postgresMajor, 13);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        ValidateFileName(library);
        ValidateFileName(control);
        ValidateFileName(sql);
        if (!control.EndsWith(".control", StringComparison.Ordinal) || !sql.EndsWith(".sql", StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected a .control file and a versioned .sql file.");
        }

        ArgumentNullException.ThrowIfNull(upgradeScripts);
        string[] upgrades = [.. upgradeScripts];
        var names = new HashSet<string>(s_fileComparer) { control, sql };
        string prefix = control[..^".control".Length] + "--";
        foreach (string script in upgrades)
        {
            ValidateFileName(script);
            if (!script.StartsWith(prefix, StringComparison.Ordinal) || !script.EndsWith(".sql", StringComparison.Ordinal))
            {
                throw new ArgumentException("Upgrade scripts must belong to the extension and end in .sql.", nameof(upgradeScripts));
            }

            string[] versions = script[prefix.Length..^4].Split("--", StringSplitOptions.None);
            if (versions.Length != 2 || versions.Any(static version => version.Length == 0 ||
                    version[0] == '-' || version[^1] == '-') || !names.Add(script))
            {
                throw new ArgumentException("Upgrade scripts require unique filenames with two nonempty PostgreSQL versions.", nameof(upgradeScripts));
            }
        }

        Array.Sort(upgrades, StringComparer.Ordinal);
        UpgradeScripts = Array.AsReadOnly(upgrades);
        ArgumentNullException.ThrowIfNull(versionControlFiles);
        string[] controls = [.. versionControlFiles];
        foreach (string file in controls)
        {
            ValidateFileName(file);
            if (!file.StartsWith(prefix, StringComparison.Ordinal) || !file.EndsWith(".control", StringComparison.Ordinal))
            {
                throw new ArgumentException("Secondary control files must belong to the extension and end in .control.", nameof(versionControlFiles));
            }

            string version = file[prefix.Length..^".control".Length];
            if (version.Length == 0 || version[0] == '-' || version[^1] == '-' ||
                version.Contains("--", StringComparison.Ordinal) || !names.Add(file))
            {
                throw new ArgumentException("Secondary control files require unique filenames with one nonempty PostgreSQL version.", nameof(versionControlFiles));
            }
        }

        Array.Sort(controls, StringComparer.Ordinal);
        VersionControlFiles = Array.AsReadOnly(controls);
        if (scriptDirectory is not null && scriptDirectory.Any(static character => character is '\0' or > '\x7f'))
        {
            throw new ArgumentException("SQL directories must be ASCII without NUL, like PostgreSQL control values.", nameof(scriptDirectory));
        }

        ScriptDirectory = scriptDirectory;
        ArgumentNullException.ThrowIfNull(pluginLibraries);
        string[] plugins = [.. pluginLibraries];
        var libraries = new HashSet<string>(s_fileComparer) { library };
        foreach (string plugin in plugins)
        {
            ValidateFileName(plugin);
            if (!string.Equals(Path.GetExtension(plugin), Path.GetExtension(library), StringComparison.Ordinal) || !libraries.Add(plugin))
            {
                throw new ArgumentException("Output plugin modules need unique filenames with the library's suffix.", nameof(pluginLibraries));
            }
        }

        Array.Sort(plugins, StringComparer.Ordinal);
        PluginLibraries = Array.AsReadOnly(plugins);
        PostgresMajor = postgresMajor;
        RuntimeIdentifier = runtimeIdentifier;
        Library = library;
        Control = control;
        Sql = sql;
    }

    /// <summary>
    /// Gets the PostgreSQL major whose headers were used to compile this library.
    /// </summary>
    public int PostgresMajor { get; }

    /// <summary>
    /// Gets the runtime identifier used by Native AOT.
    /// </summary>
    public string RuntimeIdentifier { get; }

    /// <summary>
    /// Gets the native library filename.
    /// </summary>
    public string Library { get; }

    /// <summary>
    /// Gets the control filename within the extension directory.
    /// </summary>
    public string Control { get; }

    /// <summary>
    /// Gets the versioned SQL filename within the extension directory.
    /// </summary>
    public string Sql { get; }

    /// <summary>
    /// Gets the ordered upgrade filenames within the extension directory.
    /// </summary>
    public IReadOnlyList<string> UpgradeScripts { get; }

    /// <summary>
    /// Gets the ordered version-specific control filenames within the extension directory.
    /// </summary>
    public IReadOnlyList<string> VersionControlFiles { get; }

    /// <summary>
    /// Gets the literal SQL directory setting, or null when PostgreSQL uses its control file directory.
    /// </summary>
    public string? ScriptDirectory { get; }

    /// <summary>
    /// Gets the filenames of modules that load the extension's output plugin under other names, installed beside the
    /// library.
    /// </summary>
    public IReadOnlyList<string> PluginLibraries { get; }

    /// <summary>
    /// Verifies the published control's directory and returns its fully qualified PostgreSQL SQL path.
    /// </summary>
    /// <param name="publishDirectory">The publication containing the primary control under extension/.</param>
    /// <param name="baseDirectory">The PostgreSQL shared directory, or a PostgreSQL 18+ extension search base.</param>
    /// <returns>The directory for SQL scripts and secondary controls, preserving Unix traversal components.</returns>
    /// <remarks>
    /// PostgreSQL traverses Unix paths through the filesystem. Removing a directory before a parent component
    /// can change its meaning, including when that directory is a symbolic link.
    /// </remarks>
    /// <exception cref="FormatException">The control disagrees with the manifest or uses an ambiguous rooted path.</exception>
    public string GetScriptDirectory(string publishDirectory, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        IReadOnlyDictionary<string, string> settings = ExtensionControlFile.Read(Path.Join(publishDirectory, "extension", Control));
        settings.TryGetValue("directory", out string? authored);
        if (authored != ScriptDirectory)
        {
            throw new FormatException("The control SQL directory does not match the published manifest; republish the extension.");
        }

        string directory = ScriptDirectory ?? "extension";
        if (Path.IsPathRooted(directory) && !Path.IsPathFullyQualified(directory))
        {
            throw new FormatException("SQL directories must be relative or fully qualified; drive-relative paths and paths rooted without a drive are rejected.");
        }

        // A fully qualified directory stands alone; PostgreSQL resolves any other one under the shared directory.
        string path = Path.IsPathFullyQualified(directory) ? directory : Path.Join(Path.GetFullPath(baseDirectory), directory);
        return OperatingSystem.IsWindows() ? Path.GetFullPath(path) : path;
    }

    /// <summary>
    /// Reads a published manifest without reflection-based deserialization.
    /// </summary>
    /// <param name="directory">The publish directory.</param>
    /// <returns>The validated manifest.</returns>
    public static PublishedExtension Read(string directory)
        => ReadFile(Path.Join(directory, FileName));

    private static PublishedExtension ReadFile(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                {
                    throw new FormatException("Duplicate extension manifest property.");
                }
            }

            int format = root.GetProperty("formatVersion").GetInt32();
            if (format is not (1 or 2 or 3 or 4 or 5))
            {
                throw new FormatException("Unsupported Ankus extension manifest version.");
            }

            if (format == 1 && root.TryGetProperty("upgradeScripts", out _))
            {
                throw new FormatException("Upgrade scripts require extension manifest version 2.");
            }

            if (format < 3 && root.TryGetProperty("versionControlFiles", out _))
            {
                throw new FormatException("Secondary control files require extension manifest version 3.");
            }

            string[] upgrades = format >= 2
                ? [.. root.GetProperty("upgradeScripts").EnumerateArray().Select(static item => item.GetString()!)] : [];
            if (format < 4 && root.TryGetProperty("scriptDirectory", out _))
            {
                throw new FormatException("Custom SQL directories require extension manifest version 4.");
            }

            string[] controls = format >= 3
                ? [.. root.GetProperty("versionControlFiles").EnumerateArray().Select(static item => item.GetString()!)] : [];
            string? directory = format == 4
                ? root.GetProperty("scriptDirectory").GetString() ?? throw new FormatException("A version-four manifest requires a SQL directory string.")
                : format == 5 && root.TryGetProperty("scriptDirectory", out JsonElement optional)
                    ? optional.GetString() ?? throw new FormatException("A SQL directory must be a string.") : null;
            if (format < 5 && root.TryGetProperty("pluginLibraries", out _))
            {
                throw new FormatException("Output plugin modules require extension manifest version 5.");
            }

            string[] plugins = format == 5
                ? [.. root.GetProperty("pluginLibraries").EnumerateArray().Select(static item => item.GetString()!)] : [];
            return new PublishedExtension(root.GetProperty("postgresMajor").GetInt32(),
                root.GetProperty("runtimeIdentifier").GetString()!, root.GetProperty("library").GetString()!,
                root.GetProperty("control").GetString()!, root.GetProperty("sql").GetString()!, upgrades, controls, directory, plugins);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or
            ArgumentException or OverflowException)
        {
            throw new FormatException("Invalid Ankus extension manifest.", error);
        }
    }

    /// <summary>
    /// Writes the manifest to an existing artifact directory.
    /// </summary>
    /// <param name="directory">The artifact directory.</param>
    public void Write(string directory)
    {
        using FileStream stream = File.Create(Path.Join(directory, FileName));
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", PluginLibraries.Count > 0 ? 5 : ScriptDirectory is not null ? 4 :
            VersionControlFiles.Count > 0 ? 3 : UpgradeScripts.Count > 0 ? 2 : 1);
        writer.WriteNumber("postgresMajor", PostgresMajor);
        writer.WriteString("runtimeIdentifier", RuntimeIdentifier);
        writer.WriteString("library", Library);
        writer.WriteString("control", Control);
        writer.WriteString("sql", Sql);
        if (UpgradeScripts.Count > 0 || VersionControlFiles.Count > 0 || ScriptDirectory is not null || PluginLibraries.Count > 0)
        {
            writer.WriteStartArray("upgradeScripts");
            foreach (string script in UpgradeScripts)
            {
                writer.WriteStringValue(script);
            }

            writer.WriteEndArray();
        }

        if (VersionControlFiles.Count > 0 || ScriptDirectory is not null || PluginLibraries.Count > 0)
        {
            writer.WriteStartArray("versionControlFiles");
            foreach (string control in VersionControlFiles)
            {
                writer.WriteStringValue(control);
            }

            writer.WriteEndArray();
        }

        if (ScriptDirectory is not null)
        {
            writer.WriteString("scriptDirectory", ScriptDirectory);
        }

        if (PluginLibraries.Count > 0)
        {
            writer.WriteStartArray("pluginLibraries");
            foreach (string plugin in PluginLibraries)
            {
                writer.WriteStringValue(plugin);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Invalidates a previous publication while retaining its file inventory for the next successful publish.
    /// </summary>
    /// <param name="directory">The publish directory.</param>
    public static void Invalidate(string directory)
    {
        string manifest = Path.Join(directory, FileName);
        if (File.Exists(manifest))
        {
            File.Move(manifest, Path.Join(directory, "ankus.extension.previous.json"), overwrite: true);
        }
    }

    /// <summary>
    /// Validates the complete payload, removes obsolete owned SQL, control and output plugin files, and makes the publication
    /// installable.
    /// </summary>
    /// <param name="directory">The directory containing the native library and extension files.</param>
    public void CompletePublish(string directory)
    {
        Invalidate(directory);
        string[] files = [Control, Sql, .. UpgradeScripts, .. VersionControlFiles];
        foreach (string path in files.Select(file => Path.Join(directory, "extension", file))
                     .Concat(PluginLibraries.Select(file => Path.Join(directory, file)))
                     .Prepend(Path.Join(directory, Library)))
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The published extension is incomplete.", path);
            }
        }

        if (ScriptDirectory is not null)
        {
            _ = GetScriptDirectory(directory, directory);
        }

        string previousPath = Path.Join(directory, "ankus.extension.previous.json");
        if (File.Exists(previousPath))
        {
            PublishedExtension previous = ReadFile(previousPath);
            string[] previousFiles = [previous.Control, previous.Sql, .. previous.UpgradeScripts, .. previous.VersionControlFiles];
            foreach (string file in previousFiles.Except(files, s_fileComparer))
            {
                File.Delete(Path.Join(directory, "extension", file));
            }

            // A module that became the library's own name stays.
            foreach (string file in previous.PluginLibraries.Except([.. PluginLibraries, Library], s_fileComparer))
            {
                File.Delete(Path.Join(directory, file));
            }
        }

        Write(directory);
        File.Delete(previousPath);
    }

    private static void ValidateFileName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is "." or ".." || !value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+'))
        {
            throw new ArgumentException("Extension artifacts must have filenames without directory components.", nameof(value));
        }
    }
}
