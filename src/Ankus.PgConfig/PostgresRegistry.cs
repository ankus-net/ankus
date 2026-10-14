using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ankus.PgConfig;

/// <summary>
/// Persists explicitly registered PostgreSQL installations in the Ankus home directory.
/// </summary>
/// <remarks>
/// Uses the specified home, then ANKUS_HOME, then the current user's ~/.ankus directory.
/// Relative home paths are resolved against the caller's current directory.
/// </remarks>
/// <param name="homeDirectory">An optional Ankus home directory.</param>
public sealed class PostgresRegistry(string? homeDirectory = null)
{
    /// <summary>
    /// Gets the directory that contains configuration and managed installations.
    /// </summary>
    public string HomeDirectory { get; } = Path.GetFullPath(homeDirectory ?? Environment.GetEnvironmentVariable("ANKUS_HOME") ??
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ankus"));

    /// <summary>
    /// Gets the persisted configuration path.
    /// </summary>
    public string ConfigurationPath => Path.Join(HomeDirectory, "config.json");

    /// <summary>
    /// Gets the configured development port base plus the selected major, defaulting to 28800 plus the major.
    /// </summary>
    /// <param name="major">A supported PostgreSQL major, from 13 through 19.</param>
    /// <returns>The development TCP port.</returns>
    public int GetPort(int major) => ReadPort(ReadConfiguration(), "basePort", 28800, major);

    /// <summary>
    /// Gets the configured test port base plus the selected major, defaulting to 32200 plus the major.
    /// Test fixtures use this port only when explicitly selected; their default ports remain automatic.
    /// </summary>
    /// <param name="major">A supported PostgreSQL major, from 13 through 19.</param>
    /// <returns>The TCP port to request for a test fixture.</returns>
    public int GetTestPort(int major) => ReadPort(ReadConfiguration(), "baseTestingPort", 32200, major);

    /// <summary>
    /// Gets a registered pg_config path, resolving relative entries against the Ankus home.
    /// </summary>
    /// <param name="major">The PostgreSQL major version.</param>
    /// <returns>The registered path, or null if this version has not been registered.</returns>
    public string? GetPath(int major)
    {
        JsonObject configuration = ReadConfiguration();
        if (!configuration.TryGetPropertyValue($"pg{major}", out JsonNode? node))
        {
            return null;
        }

        string? path = node?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FormatException($"Missing pg{major} path in {ConfigurationPath}.");
        }

        return Path.GetFullPath(path, HomeDirectory);
    }

    /// <summary>
    /// Loads an explicitly registered installation, rejecting missing or mismatched registrations.
    /// </summary>
    /// <param name="major">The PostgreSQL major version.</param>
    /// <param name="cancellationToken">Cancels installation queries.</param>
    /// <returns>The registered installation.</returns>
    public async Task<PostgresInstallation> GetAsync(int major, CancellationToken cancellationToken = default)
    {
        string path = GetPath(major) ?? throw new InvalidOperationException(
            $"PostgreSQL {major} is not registered. Run 'ankus init --pg{major} /path/to/pg_config'.");
        PostgresInstallation installation = await PostgresInstallation.CreateAsync(path, cancellationToken).ConfigureAwait(false);
        RequireVersion(installation, major);
        return installation;
    }

    /// <summary>
    /// Validates all requested installations before atomically updating the registry, preserving other settings.
    /// </summary>
    /// <param name="paths">PostgreSQL majors and their pg_config executable paths.</param>
    /// <param name="cancellationToken">Cancels validation or writing before the commit.</param>
    /// <returns>The validated registrations.</returns>
    public Task<IReadOnlyList<PostgresInstallation>> RegisterAsync(
        IReadOnlyDictionary<int, string> paths, CancellationToken cancellationToken = default)
        => RegisterAsync(paths, new PostgresPortOptions(), cancellationToken);

    /// <summary>
    /// Atomically registers validated installations and selected port bases, preserving unspecified settings.
    /// </summary>
    /// <param name="paths">PostgreSQL majors and their pg_config executable paths.</param>
    /// <param name="ports">Port bases to update; null properties preserve their existing values.</param>
    /// <param name="cancellationToken">Cancels validation or writing before the commit.</param>
    /// <returns>The validated registrations.</returns>
    public async Task<IReadOnlyList<PostgresInstallation>> RegisterAsync(
        IReadOnlyDictionary<int, string> paths, PostgresPortOptions ports, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(ports);
        if (paths.Count == 0)
        {
            throw new ArgumentException("Specify an installation, for example 'ankus init --pg18 /path/to/pg_config'.", nameof(paths));
        }

        var installations = new List<PostgresInstallation>();
        foreach ((int major, string path) in paths.OrderBy(static pair => pair.Key))
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(major, 13);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(major, 19);
            PostgresInstallation installation = await PostgresInstallation.CreateAsync(path, cancellationToken).ConfigureAwait(false);
            RequireVersion(installation, major);
            RequireFile(installation.InitDbPath);
            RequireFile(installation.PgCtlPath);
            RequireFile(installation.PsqlPath);
            RequireFile(Path.Join(installation.ServerIncludeDirectory, "postgres.h"));
            installations.Add(installation);
        }

        Directory.CreateDirectory(HomeDirectory);
        // Keep the lock file: deleting it would let another process lock a different inode.
        using var configurationLock = new FileStream(Path.Join(HomeDirectory, "config.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        JsonObject configuration = ReadConfiguration();
        if (ports.BasePort is int basePort)
        {
            configuration["basePort"] = basePort;
        }

        if (ports.BaseTestingPort is int baseTestingPort)
        {
            configuration["baseTestingPort"] = baseTestingPort;
        }

        ReadPort(configuration, "basePort", 28800, 19);
        ReadPort(configuration, "baseTestingPort", 32200, 19);
        foreach (PostgresInstallation installation in installations)
        {
            configuration[installation.Label] = installation.PgConfigPath;
        }

        string temporary = ConfigurationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                configuration.WriteTo(writer);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, ConfigurationPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        return installations;
    }

    private int ReadPort(JsonObject configuration, string key, int defaultBase, int major)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(major, 13);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(major, 19);
        if (!configuration.TryGetPropertyValue(key, out JsonNode? node))
        {
            return defaultBase + major;
        }

        if (node is not JsonValue value || !value.TryGetValue(out int portBase) || portBase is < 0 or > 65516)
        {
            throw new FormatException($"'{key}' must be an integer from 0 through 65516 in {ConfigurationPath}.");
        }

        return portBase + major;
    }

    private JsonObject ReadConfiguration()
    {
        if (!File.Exists(ConfigurationPath))
        {
            return [];
        }

        return JsonNode.Parse(File.ReadAllText(ConfigurationPath)) as JsonObject ??
            throw new FormatException($"Expected a JSON object in {ConfigurationPath}.");
    }

    private static void RequireVersion(PostgresInstallation installation, int major)
    {
        if (installation.Version.Major != major)
        {
            throw new InvalidOperationException(
                $"'{installation.PgConfigPath}' is PostgreSQL {installation.Version.Major}, not PostgreSQL {major}.");
        }
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The PostgreSQL installation is missing a required development file.", path);
        }
    }
}
