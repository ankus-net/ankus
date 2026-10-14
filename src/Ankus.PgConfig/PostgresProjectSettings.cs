using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Ankus.PgConfig;

/// <summary>
/// Reads a project's evaluated PostgreSQL selection without building the extension.
/// </summary>
public sealed class PostgresProjectSettings
{
    /// <summary>
    /// Describes an invalid selection without treating it as an absent project default.
    /// </summary>
    private const string ConflictMessage = "Selected or referenced projects select different PostgreSQL installations. Set AnkusPostgresMajor and AnkusPgConfigPath explicitly, or use --pg/--pg-config.";

    private PostgresProjectSettings(int postgresMajor, string? pgConfigPath, bool hasExplicitPostgresMajor)
    {
        PostgresMajor = postgresMajor;
        PgConfigPath = pgConfigPath;
        HasExplicitPostgresMajor = hasExplicitPostgresMajor;
    }

    /// <summary>
    /// Gets the PostgreSQL major selected by the evaluated project, or 18 when none is declared.
    /// </summary>
    public int PostgresMajor { get; }

    /// <summary>
    /// Gets whether the evaluated project explicitly selected <see cref="PostgresMajor"/>.
    /// When false and <see cref="PgConfigPath"/> is set, callers use the executable's reported major.
    /// </summary>
    public bool HasExplicitPostgresMajor { get; }

    /// <summary>
    /// Gets the selected pg_config executable, or null when ordinary installation discovery applies.
    /// Relative file paths are resolved against the project directory; executable names retain PATH lookup.
    /// </summary>
    public string? PgConfigPath { get; }

    /// <summary>
    /// Evaluates the project's PostgreSQL properties with the installed .NET SDK and selected configuration.
    /// Imports, conditions and automatic response files participate; build targets run only when requested by the response file.
    /// A test project without its own selection inherits an unambiguous selection from its project references.
    /// </summary>
    /// <param name="projectPath">The extension project file.</param>
    /// <param name="configuration">The MSBuild configuration used for the eventual publication.</param>
    /// <param name="postgresMajor">An explicit major supplied by the calling build, or null to use project defaults.</param>
    /// <param name="cancellationToken">Cancels evaluation and joins the query process before returning.</param>
    /// <returns>The evaluated PostgreSQL selection.</returns>
    public static Task<PostgresProjectSettings> ReadAsync(string projectPath, string configuration,
        int? postgresMajor = null, CancellationToken cancellationToken = default)
        => ReadAsync(projectPath, configuration, new Dictionary<string, string>(), postgresMajor, cancellationToken);

    /// <summary>
    /// Evaluates project selection with the same global properties as the eventual build.
    /// </summary>
    /// <param name="projectPath">The project files to evaluate.</param>
    /// <param name="configuration">The effective build configuration.</param>
    /// <param name="globalProperties">Literal global properties inherited by evaluated project references.</param>
    /// <param name="postgresMajor">An explicit PostgreSQL major, or null for evaluated defaults.</param>
    /// <param name="cancellationToken">Cancels evaluation and joins the query processes.</param>
    /// <returns>The evaluated PostgreSQL selection.</returns>
    public static async Task<PostgresProjectSettings> ReadAsync(string projectPath, string configuration,
        IReadOnlyDictionary<string, string> globalProperties, int? postgresMajor = null,
        CancellationToken cancellationToken = default)
    {
        SelectionResult result = await EvaluateAsync(projectPath, configuration, postgresMajor, globalProperties, NewCache(),
            cancellationToken).ConfigureAwait(false);
        if (result.Conflict)
        {
            throw new InvalidOperationException(ConflictMessage);
        }

        return result.Settings ?? new(18, null, false);
    }

    /// <summary>
    /// Reads an unambiguous evaluated selection without assigning defaults to unrelated projects.
    /// </summary>
    /// <param name="projectPath">The project file whose imports and references participate.</param>
    /// <param name="configuration">The effective MSBuild configuration.</param>
    /// <param name="postgresMajor">An explicit global major, or null for declared defaults.</param>
    /// <param name="cancellationToken">Cancels evaluation and joins the query process.</param>
    /// <returns>The selected installation, or null when no selection is declared.</returns>
    /// <exception cref="InvalidOperationException">Referenced projects select conflicting PostgreSQL installations.</exception>
    /// <remarks>
    /// Invalid property values, conflicting selections, broken imports and circular references remain errors.
    /// A caller can use an absent declaration to apply its documented default.
    /// </remarks>
    public static Task<PostgresProjectSettings?> TryReadAsync(string projectPath, string configuration,
        int? postgresMajor = null, CancellationToken cancellationToken = default)
        => TryReadAsync(projectPath, configuration, new Dictionary<string, string>(), postgresMajor, cancellationToken);

    /// <summary>
    /// Evaluates project selection with the same global properties as the eventual build.
    /// </summary>
    /// <param name="projectPath">The project files to evaluate.</param>
    /// <param name="configuration">The effective build configuration.</param>
    /// <param name="globalProperties">Literal global properties inherited by evaluated project references.</param>
    /// <param name="postgresMajor">An explicit PostgreSQL major, or null for evaluated defaults.</param>
    /// <param name="cancellationToken">Cancels evaluation and joins the query processes.</param>
    /// <returns>The evaluated PostgreSQL selection.</returns>
    /// <exception cref="InvalidOperationException">Referenced projects select conflicting PostgreSQL installations.</exception>
    public static async Task<PostgresProjectSettings?> TryReadAsync(string projectPath, string configuration,
        IReadOnlyDictionary<string, string> globalProperties, int? postgresMajor = null,
        CancellationToken cancellationToken = default)
    {
        SelectionResult result = await EvaluateAsync(projectPath, configuration, postgresMajor, globalProperties, NewCache(),
            cancellationToken).ConfigureAwait(false);
        if (result.Conflict)
        {
            throw new InvalidOperationException(ConflictMessage);
        }

        return result.Settings;
    }

    /// <summary>
    /// Reads a shared selection across evaluated projects without allowing an ambiguous reference chain to select a server.
    /// </summary>
    /// <param name="projectPaths">The project files in the selected solution.</param>
    /// <param name="configuration">The effective MSBuild configuration.</param>
    /// <param name="postgresMajor">An explicit global major, or null for declared defaults.</param>
    /// <param name="cancellationToken">Cancels evaluation and joins the query processes.</param>
    /// <returns>The shared declared selection, or null for an empty or unrelated group.</returns>
    /// <exception cref="InvalidOperationException">Selected projects or their references select conflicting PostgreSQL installations.</exception>
    public static Task<PostgresProjectSettings?> TryReadAsync(IEnumerable<string> projectPaths, string configuration,
        int? postgresMajor = null, CancellationToken cancellationToken = default)
        => TryReadAsync(projectPaths, configuration, new Dictionary<string, string>(), postgresMajor, cancellationToken);

    /// <summary>
    /// Evaluates project selection with the same global properties as the eventual build.
    /// </summary>
    /// <param name="projectPaths">The project files to evaluate.</param>
    /// <param name="configuration">The effective build configuration.</param>
    /// <param name="globalProperties">Literal global properties inherited by evaluated project references.</param>
    /// <param name="postgresMajor">An explicit PostgreSQL major, or null for evaluated defaults.</param>
    /// <param name="cancellationToken">Cancels evaluation and joins the query processes.</param>
    /// <returns>The evaluated PostgreSQL selection.</returns>
    /// <exception cref="InvalidOperationException">Selected projects or their references select conflicting PostgreSQL installations.</exception>
    public static async Task<PostgresProjectSettings?> TryReadAsync(IEnumerable<string> projectPaths, string configuration,
        IReadOnlyDictionary<string, string> globalProperties, int? postgresMajor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        // Each evaluation is a separate MSBuild process, so a solution's projects are evaluated side by side, and a
        // project that several of them reference is evaluated once. Results are still read in solution order, so the
        // selection and the first reported problem do not depend on which evaluation finishes first.
        ConcurrentDictionary<string, SelectionResult> evaluated = NewCache();
        using var remaining = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount));
        Task<SelectionResult>[] evaluations = [.. projectPaths.Select(async project =>
        {
            await slots.WaitAsync(remaining.Token).ConfigureAwait(false);
            try
            {
                return await EvaluateAsync(project, configuration, postgresMajor, globalProperties, evaluated, remaining.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        })];
        Task[] pending = evaluations;
        try
        {
            PostgresProjectSettings? selected = null;
            foreach (Task<SelectionResult> evaluation in evaluations)
            {
                SelectionResult result = await evaluation.ConfigureAwait(false);
                if (result.Conflict)
                {
                    throw new InvalidOperationException(ConflictMessage);
                }

                if (result.Settings is not { } current)
                {
                    continue;
                }

                if (!TryMerge(selected, current, out PostgresProjectSettings? merged))
                {
                    throw new InvalidOperationException(ConflictMessage);
                }

                selected = merged;
            }

            return selected;
        }
        finally
        {
            // A problem leaves later evaluations running; they stop, and their MSBuild processes exit, before this returns.
            await remaining.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Validates inputs and queries the evaluated project selection.
    /// </summary>
    private static async Task<SelectionResult> EvaluateAsync(string projectPath, string configuration,
        int? postgresMajor, IReadOnlyDictionary<string, string> globalProperties,
        ConcurrentDictionary<string, SelectionResult> evaluated, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        string project = Path.GetFullPath(projectPath);
        if (!File.Exists(project) || !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("The extension project was not found.", project);
        }

        ArgumentNullException.ThrowIfNull(globalProperties);
        var properties = new Dictionary<string, string>(globalProperties, StringComparer.OrdinalIgnoreCase);
        foreach (string name in properties.Keys)
        {
            System.Xml.XmlConvert.VerifyNCName(name);
        }

        if (properties.TryGetValue("Configuration", out string? supplied) && supplied != configuration)
        {
            throw new ArgumentException("Select the same configuration for evaluation and global properties.");
        }

        properties["Configuration"] = configuration;
        if (postgresMajor is int selected)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(selected, 13, nameof(postgresMajor));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(selected, 19, nameof(postgresMajor));
            string major = selected.ToString(CultureInfo.InvariantCulture);
            if (properties.TryGetValue("AnkusPostgresMajor", out string? forwarded) && forwarded != major)
            {
                throw new ArgumentException("Select the same PostgreSQL major for evaluation and global properties.");
            }

            properties["AnkusPostgresMajor"] = major;
        }

        return await ReadCoreAsync(project, properties, new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal), evaluated, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the per-selection record of evaluated projects, keyed by path and global properties.
    /// </summary>
    private static ConcurrentDictionary<string, SelectionResult> NewCache()
        => new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Evaluates declared references only when the current project does not select PostgreSQL itself.
    /// </summary>
    /// <param name="project">The absolute project path.</param>
    /// <param name="globalProperties">The build properties inherited by this project evaluation.</param>
    /// <param name="visiting">The current reference chain, used to reject cycles.</param>
    /// <param name="evaluated">Projects already evaluated for this selection, with their global properties.</param>
    /// <param name="cancellationToken">Cancels project evaluation.</param>
    /// <returns>The declared or inherited selection, or null for projects unrelated to PostgreSQL.</returns>
    private static async Task<SelectionResult> ReadCoreAsync(string project, Dictionary<string, string> globalProperties,
        HashSet<string> visiting, ConcurrentDictionary<string, SelectionResult> evaluated, CancellationToken cancellationToken)
    {
        if (!visiting.Add(project))
        {
            throw new InvalidOperationException("A circular project reference prevents PostgreSQL selection.");
        }

        // The same project evaluates differently under other global properties, so both identify a result.
        string key = project + "\n" + string.Join("\n", globalProperties.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => pair.Key + "=" + pair.Value));
        if (evaluated.TryGetValue(key, out SelectionResult? known))
        {
            visiting.Remove(project);
            return known;
        }

        SelectionResult result = await ReadProjectAsync(project, globalProperties, visiting, evaluated, cancellationToken).ConfigureAwait(false);
        evaluated.TryAdd(key, result);
        visiting.Remove(project);
        return result;
    }

    /// <summary>
    /// Queries one project's selection, then its references when it selects nothing itself.
    /// </summary>
    private static async Task<SelectionResult> ReadProjectAsync(string project, Dictionary<string, string> globalProperties,
        HashSet<string> visiting, ConcurrentDictionary<string, SelectionResult> evaluated, CancellationToken cancellationToken)
    {
        List<string> arguments = ["msbuild", project, "-nologo", "-verbosity:quiet",
            "-getProperty:AnkusPostgresMajor,AnkusPgConfigPath,_AnkusPostgresMajorWasSpecified", "-getItem:ProjectReference"];
        arguments.AddRange(globalProperties.Select(static pair => "-p:" + pair.Key + "=" + EscapeProperty(pair.Value)));
        string output = await PostgresInstallation.QueryAsync("dotnet", arguments, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement properties = document.RootElement.GetProperty("Properties");
        string value = properties.GetProperty("AnkusPostgresMajor").GetString() ?? string.Empty;
        int major = 18;
        if (value.Length != 0 && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out major) || major is < 13 or > 19))
        {
            throw new FormatException("AnkusPostgresMajor must select PostgreSQL 13–19.");
        }

        string? path = properties.GetProperty("AnkusPgConfigPath").GetString();
        if (string.IsNullOrWhiteSpace(path))
        {
            path = null;
        }
        else if (path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar))
        {
            path = Path.GetFullPath(path, Path.GetDirectoryName(project)!);
        }

        if (value.Length != 0 || path is not null)
        {
            string marker = properties.GetProperty("_AnkusPostgresMajorWasSpecified").GetString() ?? string.Empty;
            bool explicitlySelected = marker.Length == 0
                ? value.Length != 0
                : bool.TryParse(marker, out bool specified) && specified;
            return new(new(major, path, explicitlySelected), false);
        }

        PostgresProjectSettings? inherited = null;
        foreach (JsonElement reference in document.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray())
        {
            string referencePath = reference.GetProperty("FullPath").GetString()!;
            if (!referencePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SelectionResult child = await ReadCoreAsync(referencePath,
                ReferenceProperties(reference, globalProperties), visiting, evaluated, cancellationToken).ConfigureAwait(false);
            if (child.Conflict)
            {
                return child;
            }

            PostgresProjectSettings? selection = child.Settings;
            if (selection is null)
            {
                continue;
            }

            if (!TryMerge(inherited, selection, out PostgresProjectSettings? merged))
            {
                return new(null, true);
            }

            inherited = merged;
        }

        return new(inherited, false);
    }

    /// <summary>
    /// Merges compatible project selections without turning an unspecified major into PostgreSQL 18.
    /// </summary>
    private static bool TryMerge(PostgresProjectSettings? first, PostgresProjectSettings second,
        out PostgresProjectSettings? merged)
    {
        if (first is null)
        {
            merged = second;
            return true;
        }

        bool samePath = string.Equals(first.PgConfigPath, second.PgConfigPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        bool sameMajor = !first.HasExplicitPostgresMajor || !second.HasExplicitPostgresMajor ||
            first.PostgresMajor == second.PostgresMajor;
        if (!samePath || !sameMajor)
        {
            merged = null;
            return false;
        }

        merged = first.HasExplicitPostgresMajor ? first : second.HasExplicitPostgresMajor ? second : first;
        return true;
    }

    /// <summary>
    /// Distinguishes an unrelated project from conflicting transitive selections.
    /// </summary>
    /// <param name="Settings">The declared or inherited unambiguous settings.</param>
    /// <param name="Conflict">Whether any selected reference chain disagrees.</param>
    private sealed record SelectionResult(PostgresProjectSettings? Settings, bool Conflict);

    /// <summary>
    /// Preserves the reference's explicit MSBuild property overrides and removals during evaluation.
    /// </summary>
    /// <param name="reference">The evaluated ProjectReference item and metadata.</param>
    /// <param name="parent">The parent's explicit global properties.</param>
    /// <returns>The global properties for the referenced project.</returns>
    private static Dictionary<string, string> ReferenceProperties(JsonElement reference, Dictionary<string, string> parent)
    {
        var properties = new Dictionary<string, string>(parent, StringComparer.OrdinalIgnoreCase);
        string[] removals = ["GlobalPropertiesToRemove", "UndefineProperties"];
        foreach (string metadata in removals)
        {
            if (!(reference.TryGetProperty(metadata, out JsonElement names)))
            {
                continue;
            }

            foreach (string name in names.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                properties.Remove(name);
            }
        }

        string[] overrides = reference.TryGetProperty("Properties", out JsonElement replacement) && !string.IsNullOrEmpty(replacement.GetString())
            ? ["Properties", "AdditionalProperties"]
            : ["SetConfiguration", "SetPlatform", "SetTargetFramework", "AdditionalProperties"];
        foreach (string metadata in overrides)
        {
            if (!reference.TryGetProperty(metadata, out JsonElement values))
            {
                continue;
            }

            foreach (string property in values.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int separator = property.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    throw new FormatException($"ProjectReference {metadata} must contain name=value properties.");
                }

                properties[property[..separator].Trim()] = Uri.UnescapeDataString(property[(separator + 1)..]);
            }
        }

        return properties;
    }

    /// <summary>
    /// Keeps literal property values separate from MSBuild's property-list and expansion syntax.
    /// </summary>
    /// <param name="value">The literal property value.</param>
    /// <returns>The escaped MSBuild argument value.</returns>
    private static string EscapeProperty(string value)
        => string.Concat(value.Select(static character => character is '%' or ';' or ',' or '$' or '@' or '(' or ')' or '\'' or '*' or '?' or '"'
            ? "%" + ((int)character).ToString("X2", CultureInfo.InvariantCulture) : character.ToString()));
}
