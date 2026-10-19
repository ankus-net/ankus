using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ankus.PgConfig;

/// <summary>
/// Describes a PostgreSQL installation captured on the machine an extension will run on: its <c>pg_config</c> values,
/// headers and the native bindings measured there. A machine with the same operating system and another architecture
/// can then build the extension for that runtime identifier, as a pgrx target bundle from
/// <c>cargo pgrx cross pgrx-target</c> allows.
/// </summary>
/// <remarks>
/// Native bindings come from compiling and running probe programs against the installation's headers, which a build
/// machine cannot do for another architecture. The target machine runs them once when the information is exported.
/// Files keep their absolute target paths beneath the <c>files</c> directory, so header search paths and relative
/// includes resolve as they did on the target.
/// </remarks>
public sealed class PostgresTargetInfo
{
    /// <summary>
    /// Gets the manifest's file name at the root of an archive or extracted directory.
    /// </summary>
    public const string ManifestFileName = "ankus-target.json";

    private const int Format = 1;
    private const string FilesDirectory = "files";
    private const string BindingsDirectory = "bindings";
    // MSBuild reads these plain copies of manifest values before any Ankus tool has been built.
    private const string MajorFileName = "postgres-major";
    private const string RuntimeFileName = "runtime-identifier";

    private static readonly string[] s_includeOptions = ["-isystem", "-idirafter", "-iquote", "-I"];

    private readonly Dictionary<string, string> _files;

    private PostgresTargetInfo(string rootDirectory, string ankusVersion, string runtimeIdentifier, string targetTriple,
        string versionText, string binDirectory, string libraryDirectory, string sharedDirectory, string includeDirectory,
        string serverIncludeDirectory, string preprocessorFlags, Dictionary<string, string> files)
    {
        RootDirectory = rootDirectory;
        AnkusVersion = ankusVersion;
        RuntimeIdentifier = runtimeIdentifier;
        TargetTriple = targetTriple;
        VersionText = versionText;
        Version = PostgresVersion.Parse(versionText);
        BinDirectory = binDirectory;
        LibraryDirectory = libraryDirectory;
        SharedDirectory = sharedDirectory;
        IncludeDirectory = includeDirectory;
        ServerIncludeDirectory = serverIncludeDirectory;
        PreprocessorFlags = preprocessorFlags;
        _files = files;
    }

    /// <summary>
    /// Gets the directory that contains the manifest, the target's files and its bindings.
    /// </summary>
    public string RootDirectory { get; }

    /// <summary>
    /// Gets the manifest path, which build settings accept in place of a <c>pg_config</c> path.
    /// </summary>
    public string ManifestPath => Path.Join(RootDirectory, ManifestFileName);

    /// <summary>
    /// Gets the version of Ankus that exported the information; only the same version can consume its bindings.
    /// </summary>
    public string AnkusVersion { get; }

    /// <summary>
    /// Gets the .NET runtime identifier of the target machine, such as <c>osx-x64</c>.
    /// </summary>
    public string RuntimeIdentifier { get; }

    /// <summary>
    /// Gets the native compiler target triple used for the target's bindings, or an empty string for the compiler default.
    /// </summary>
    public string TargetTriple { get; }

    /// <summary>
    /// Gets the target installation's PostgreSQL version.
    /// </summary>
    public PostgresVersion Version { get; }

    /// <summary>
    /// Gets the target's <c>pg_config --version</c> output.
    /// </summary>
    internal string VersionText { get; }

    /// <summary>
    /// Gets the target's executable directory.
    /// </summary>
    internal string BinDirectory { get; }

    /// <summary>
    /// Gets the target's extension library directory, which packages mirror.
    /// </summary>
    internal string LibraryDirectory { get; }

    /// <summary>
    /// Gets the target's shared-data directory, which packages mirror.
    /// </summary>
    internal string SharedDirectory { get; }

    /// <summary>
    /// Gets the target's public header directory.
    /// </summary>
    internal string IncludeDirectory { get; }

    /// <summary>
    /// Gets the target's server header directory.
    /// </summary>
    internal string ServerIncludeDirectory { get; }

    /// <summary>
    /// Gets the target's preprocessor flags as <c>pg_config --cppflags</c> reported them.
    /// </summary>
    internal string PreprocessorFlags { get; }

    /// <summary>
    /// Gets the conventional archive name for a target, such as <c>ankus-target.pg18.osx-x64.zip</c>.
    /// </summary>
    /// <param name="postgresMajor">The target's PostgreSQL major.</param>
    /// <param name="runtimeIdentifier">The target's runtime identifier.</param>
    /// <returns>The archive file name.</returns>
    public static string GetArchiveFileName(int postgresMajor, string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        return $"ankus-target.pg{postgresMajor}.{runtimeIdentifier}.zip";
    }

    /// <summary>
    /// Maps an absolute path on the target machine to its copy in this information.
    /// </summary>
    /// <param name="targetPath">An absolute path as the target reported it.</param>
    /// <returns>The local path, which exists only for files and directories the export captured.</returns>
    public string MapPath(string targetPath) => Path.Join(RootDirectory, FilesDirectory, ToArchivePath(targetPath).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Gets the local path of one native binding artifact measured on the target.
    /// </summary>
    /// <param name="name">The artifact file name.</param>
    /// <returns>The artifact path.</returns>
    public string GetBindingPath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Path.GetFileName(name) != name)
        {
            throw new ArgumentException("A binding artifact is named by its file name.", nameof(name));
        }

        return Path.Join(RootDirectory, BindingsDirectory, name);
    }

    /// <summary>
    /// Reads extracted target information without checking its file contents.
    /// </summary>
    /// <param name="path">The manifest or the directory that contains it.</param>
    /// <returns>The described target.</returns>
    /// <exception cref="InvalidDataException">The manifest is malformed or has an unsupported format.</exception>
    public static PostgresTargetInfo Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string manifest = Path.GetFullPath(Directory.Exists(path) ? Path.Join(path, ManifestFileName) : path);
        if (!File.Exists(manifest))
        {
            throw new FileNotFoundException("The target information manifest does not exist.", manifest);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifest));
            JsonElement root = document.RootElement;
            if (root.GetProperty("format").GetInt32() != Format)
            {
                throw new InvalidDataException($"Target information format {root.GetProperty("format").GetInt32()} is not supported; export it again with this version of Ankus.");
            }

            JsonElement postgres = root.GetProperty("postgres");
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty file in root.GetProperty("files").EnumerateObject())
            {
                ValidateArchivePath(file.Name);
                string hash = file.Value.GetString() ?? throw new InvalidDataException("A target file has no hash.");
                files.Add(file.Name, hash);
            }

            string directory = Path.GetDirectoryName(manifest)!;
            var target = new PostgresTargetInfo(directory, Read(root, "ankusVersion"), Read(root, "runtimeIdentifier"),
                root.GetProperty("targetTriple").GetString() ?? "", Read(postgres, "version"), Read(postgres, "bindir"),
                Read(postgres, "pkglibdir"), Read(postgres, "sharedir"), Read(postgres, "includedir"), Read(postgres, "includedirServer"),
                postgres.GetProperty("cppflags").GetString() ?? "", files);
            if (root.GetProperty("postgresMajor").GetInt32() != target.Version.Major ||
                ReadValue(directory, MajorFileName) != target.Version.Major.ToString(CultureInfo.InvariantCulture) ||
                ReadValue(directory, RuntimeFileName) != target.RuntimeIdentifier)
            {
                throw new InvalidDataException("The target information's PostgreSQL major or runtime identifier is inconsistent.");
            }

            return target;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"The target information manifest is malformed: {error.Message}", error);
        }
    }

    /// <summary>
    /// Checks that every captured file is present and unchanged.
    /// </summary>
    /// <param name="cancellationToken">Cancels hashing.</param>
    /// <returns>The verification operation.</returns>
    /// <exception cref="InvalidDataException">A file is missing or differs from the manifest.</exception>
    public async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        foreach ((string name, string expected) in _files)
        {
            string path = Path.Join(RootDirectory, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"Target information is missing '{name}'.");
            }

            if (!string.Equals(await HashAsync(path, cancellationToken).ConfigureAwait(false), expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Target information file '{name}' does not match its manifest.");
            }
        }
    }

    /// <summary>
    /// Extracts an archive into a directory named by its content hash and verifies it, reusing an earlier extraction.
    /// </summary>
    /// <remarks>
    /// Builds that set <c>AnkusTargetInfo</c> to an archive extract it to the same location.
    /// </remarks>
    /// <param name="archive">The exported archive.</param>
    /// <param name="cacheDirectory">The directory that holds extracted targets.</param>
    /// <param name="cancellationToken">Cancels hashing and extraction.</param>
    /// <returns>The verified target.</returns>
    public static async Task<PostgresTargetInfo> ExtractAsync(string archive, string cacheDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        string destination = Path.Join(Path.GetFullPath(cacheDirectory), await HashAsync(archive, cancellationToken).ConfigureAwait(false));
        if (File.Exists(Path.Join(destination, ManifestFileName)))
        {
            PostgresTargetInfo existing = Open(destination);
            await existing.VerifyAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        Directory.CreateDirectory(cacheDirectory);
        string stage = Path.Join(Path.GetFullPath(cacheDirectory), "." + Path.GetRandomFileName());
        bool moved = false;
        try
        {
            // ExtractToDirectory rejects entries that would leave the stage.
            await ZipFile.ExtractToDirectoryAsync(archive, stage, cancellationToken).ConfigureAwait(false);
            PostgresTargetInfo staged = Open(stage);
            await staged.VerifyAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.Move(stage, destination);
                moved = true;
            }
            catch (IOException) when (File.Exists(Path.Join(destination, ManifestFileName)))
            {
                // Another process extracted the same archive first; its copy is verified below.
            }
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, recursive: true);
            }
        }

        PostgresTargetInfo extracted = Open(destination);
        if (!moved)
        {
            await extracted.VerifyAsync(cancellationToken).ConfigureAwait(false);
        }

        return extracted;
    }

    /// <summary>
    /// Writes an archive of an installation's headers, its build values and the native bindings measured for it.
    /// </summary>
    /// <param name="installation">The target installation, queried on the target machine.</param>
    /// <param name="runtimeIdentifier">The target runtime identifier.</param>
    /// <param name="targetTriple">The native compiler target triple, or an empty string.</param>
    /// <param name="ankusVersion">The exporting Ankus version.</param>
    /// <param name="bindings">Binding artifacts by file name and local path.</param>
    /// <param name="headers">
    /// Absolute paths of the headers compilation reads. Those beneath the public header directory or a search directory
    /// in the installation's preprocessor flags are captured; the build machine's toolchain supplies system headers.
    /// </param>
    /// <param name="archive">The archive to create or replace.</param>
    /// <param name="cancellationToken">Cancels copying and compression.</param>
    /// <returns>The export operation.</returns>
    public static async Task CreateArchiveAsync(PostgresInstallation installation, string runtimeIdentifier, string targetTriple,
        string ankusVersion, IReadOnlyDictionary<string, string> bindings, IEnumerable<string> headers, string archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        ArgumentNullException.ThrowIfNull(targetTriple);
        ArgumentException.ThrowIfNullOrWhiteSpace(ankusVersion);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentException.ThrowIfNullOrWhiteSpace(archive);
        if (installation.TargetInfo is not null)
        {
            throw new ArgumentException("Export target information from a PostgreSQL installation; this installation already comes from exported target information.", nameof(installation));
        }

        string stage = Directory.CreateTempSubdirectory("ankus-target-").FullName;
        try
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            // The server headers are copied whole: bridges and native calls may include any of them.
            CopyTree(installation.ServerIncludeDirectory, stage, files);
            if (OperatingSystem.IsWindows())
            {
                // Windows installations keep their library dependencies' headers beside PostgreSQL's own, and link
                // extensions against the server's import library.
                CopyTree(installation.IncludeDirectory, stage, files);
                CopyFile(Path.Join(installation.LibraryDirectory, "postgres.lib"), stage, files);
            }

            string[] searched = [.. GetIncludeDirectories(installation.PreprocessorArguments).Prepend(installation.IncludeDirectory)
                .Select(static directory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar)];
            foreach (string header in headers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (searched.Any(directory => Path.GetFullPath(header).StartsWith(directory, StringComparison.Ordinal)))
                {
                    CopyFile(header, stage, files);
                }
            }

            // Keep every search directory so no flag falls back to a build-machine directory of the same name.
            Directory.CreateDirectory(Path.Join(stage, FilesDirectory, ToArchivePath(installation.IncludeDirectory)));
            foreach (string directory in GetIncludeDirectories(installation.PreprocessorArguments))
            {
                Directory.CreateDirectory(Path.Join(stage, FilesDirectory, ToArchivePath(directory)));
            }

            Directory.CreateDirectory(Path.Join(stage, BindingsDirectory));
            foreach ((string name, string source) in bindings)
            {
                if (Path.GetFileName(name) != name)
                {
                    throw new ArgumentException("Binding artifacts are named by their file names.", nameof(bindings));
                }

                File.Copy(source, Path.Join(stage, BindingsDirectory, name));
                files[BindingsDirectory + "/" + name] = "";
            }

            foreach (string name in files.Keys.ToArray())
            {
                files[name] = await HashAsync(Path.Join(stage, name.Replace('/', Path.DirectorySeparatorChar)), cancellationToken).ConfigureAwait(false);
            }

            await using (FileStream stream = File.Create(Path.Join(stage, ManifestFileName)))
            await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("format", Format);
                writer.WriteString("ankusVersion", ankusVersion);
                writer.WriteString("runtimeIdentifier", runtimeIdentifier);
                writer.WriteString("targetTriple", targetTriple);
                writer.WriteNumber("postgresMajor", installation.Version.Major);
                writer.WriteStartObject("postgres");
                writer.WriteString("version", installation.VersionText);
                writer.WriteString("bindir", installation.BinDirectory);
                writer.WriteString("pkglibdir", installation.LibraryDirectory);
                writer.WriteString("sharedir", installation.SharedDirectory);
                writer.WriteString("includedir", installation.IncludeDirectory);
                writer.WriteString("includedirServer", installation.ServerIncludeDirectory);
                writer.WriteString("cppflags", installation.PreprocessorFlags);
                writer.WriteEndObject();
                writer.WriteStartObject("files");
                foreach ((string name, string hash) in files)
                {
                    writer.WriteString(name, hash);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            await File.WriteAllTextAsync(Path.Join(stage, MajorFileName),
                installation.Version.Major.ToString(CultureInfo.InvariantCulture) + "\n", cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Join(stage, RuntimeFileName), runtimeIdentifier + "\n", cancellationToken).ConfigureAwait(false);
            string output = Path.GetFullPath(archive);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            string partial = output + ".partial";
            File.Delete(partial);
            await ZipFile.CreateFromDirectoryAsync(stage, partial, CompressionLevel.Optimal, includeBaseDirectory: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            File.Move(partial, output, overwrite: true);
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    /// <summary>
    /// Replaces absolute header search paths in preprocessor arguments with their captured copies.
    /// </summary>
    /// <param name="arguments">The target's split preprocessor arguments.</param>
    /// <returns>Arguments that search only captured directories.</returns>
    internal IReadOnlyList<string> MapIncludeArguments(IReadOnlyList<string> arguments)
    {
        var result = new List<string>(arguments.Count);
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (IsIncludeOption(argument) && index + 1 < arguments.Count && Path.IsPathFullyQualified(arguments[index + 1]))
            {
                result.Add(argument);
                result.Add(MapPath(arguments[++index]));
            }
            else if (SplitIncludeOption(argument) is (string option, string path) && Path.IsPathFullyQualified(path))
            {
                result.Add(option + MapPath(path));
            }
            else
            {
                result.Add(argument);
            }
        }

        return result;
    }

    /// <summary>
    /// Lists the absolute header search directories that preprocessor arguments name.
    /// </summary>
    /// <param name="arguments">Split preprocessor arguments.</param>
    /// <returns>The directories, in argument order.</returns>
    internal static IEnumerable<string> GetIncludeDirectories(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (IsIncludeOption(arguments[index]) && index + 1 < arguments.Count)
            {
                if (Path.IsPathFullyQualified(arguments[++index]))
                {
                    yield return arguments[index];
                }
            }
            else if (SplitIncludeOption(arguments[index]) is (_, string path) && Path.IsPathFullyQualified(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>
    /// Converts an absolute target path to its location beneath the files directory, using forward slashes.
    /// </summary>
    /// <param name="path">An absolute path.</param>
    /// <returns>The relative archive path.</returns>
    internal static string ToArchivePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Target paths are absolute.", nameof(path));
        }

        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        string relative = full[root.Length..].Replace('\\', '/').TrimEnd('/');
        if (OperatingSystem.IsWindows())
        {
            // A drive becomes a directory; UNC shares have no stable local equivalent.
            if (root.Length < 2 || root[1] != ':')
            {
                throw new ArgumentException("Target paths on Windows must be on a drive.", nameof(path));
            }

            relative = char.ToUpperInvariant(root[0]) + (relative.Length == 0 ? "" : "/" + relative);
        }

        return relative;
    }

    private static bool IsIncludeOption(string argument) => s_includeOptions.Contains(argument, StringComparer.Ordinal);

    private static (string Option, string Path)? SplitIncludeOption(string argument)
    {
        foreach (string option in s_includeOptions)
        {
            if (argument.Length > option.Length && argument.StartsWith(option, StringComparison.Ordinal))
            {
                return (option, argument[option.Length..]);
            }
        }

        return null;
    }

    private static void CopyTree(string directory, string stage, IDictionary<string, string> files)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            CopyFile(file, stage, files);
        }
    }

    private static void CopyFile(string file, string stage, IDictionary<string, string> files)
    {
        string name = FilesDirectory + "/" + ToArchivePath(file);
        if (files.ContainsKey(name))
        {
            return;
        }

        string destination = Path.Join(stage, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination);
        files.Add(name, "");
    }

    private static void ValidateArchivePath(string name)
    {
        if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\', StringComparison.Ordinal) ||
            name.Split('/').Any(static segment => segment is "" or "." or ".." || segment.Contains(':', StringComparison.Ordinal)) ||
            !(name.StartsWith(FilesDirectory + "/", StringComparison.Ordinal) || name.StartsWith(BindingsDirectory + "/", StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"The target information names an invalid file '{name}'.");
        }
    }

    private static string ReadValue(string directory, string name)
    {
        string path = Path.Join(directory, name);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : throw new InvalidDataException($"The target information has no {name} file.");
    }

    private static string Read(JsonElement element, string name)
    {
        string? value = element.GetProperty(name).GetString();
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"The target information has no {name}.") : value;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
