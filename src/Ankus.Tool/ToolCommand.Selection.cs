using System.CommandLine;
using Ankus.PgConfig;

namespace Ankus.Tool;

internal static partial class ToolCommand
{
    /// <summary>
    /// Selects an installation using explicit command options before evaluated project defaults.
    /// </summary>
    /// <param name="result">The parsed command.</param>
    /// <param name="home">The registry home option.</param>
    /// <param name="token">Cancels project evaluation and installation discovery.</param>
    /// <param name="testProject">An optional project selected in forwarded test arguments.</param>
    /// <param name="testConfiguration">The effective forwarded test configuration.</param>
    /// <param name="testProperties">Explicit MSBuild properties forwarded to the test build.</param>
    /// <param name="positionalMajor">An optional major selected by an information subcommand's argument.</param>
    /// <param name="resolvedProject">An extension already resolved by the command.</param>
    /// <returns>The selected and version-checked installation.</returns>
    private static async Task<PostgresInstallation> SelectAsync(ParseResult result, Option<string?> home, CancellationToken token,
        string? testProject = null, string? testConfiguration = null, IReadOnlyDictionary<string, string>? testProperties = null,
        int? positionalMajor = null, string? resolvedProject = null)
        => (await SelectWithProjectAsync(result, home, token, testProject, testConfiguration, testProperties, positionalMajor, resolvedProject)).Installation;

    /// <summary>
    /// Retains the evaluated extension project for the remainder of this invocation.
    /// </summary>
    /// <param name="result">The parsed command.</param>
    /// <param name="home">The registry home option.</param>
    /// <param name="token">Cancels evaluation and installation discovery.</param>
    /// <param name="testProject">An optional forwarded test project.</param>
    /// <param name="testConfiguration">The effective test configuration.</param>
    /// <param name="testProperties">The effective global properties.</param>
    /// <param name="positionalMajor">An optional explicit positional major.</param>
    /// <param name="resolvedProject">An extension already resolved by the command.</param>
    /// <returns>The installation and the project already resolved during default selection.</returns>
    private static async Task<PostgresSelection> SelectWithProjectAsync(ParseResult result, Option<string?> home, CancellationToken token,
        string? testProject = null, string? testConfiguration = null, IReadOnlyDictionary<string, string>? testProperties = null,
        int? positionalMajor = null, string? resolvedProject = null)
    {
        testProperties ??= BuildProperties(result);
        if (testConfiguration is null && result.CommandResult.Command.Options.Any(static option => option.Name == "--configuration"))
        {
            testConfiguration = GetConfiguration(result);
        }

        int? major = ExplicitMajor(result);
        if (major is not null && positionalMajor is not null && major != positionalMajor)
        {
            throw new ArgumentException("Select the same PostgreSQL major for the version argument and --pg.");
        }

        major ??= positionalMajor;
        string? selectedProject = resolvedProject;
        if (result.CommandResult.Command.Options.Any(static option => option.Name == "--target-info") &&
            result.GetValue<string?>("--target-info") is string targetInfo)
        {
            if (result.GetValue<string?>("--pg-config") is not null || testProperties?.ContainsKey("AnkusPgConfigPath") == true)
            {
                throw new ArgumentException("Use either --target-info or a pg_config.");
            }

            PostgresInstallation target = await OpenTargetAsync(targetInfo, testProperties, token);
            if (major is int requested && target.Version.Major != requested)
            {
                throw new ArgumentException($"Expected PostgreSQL {requested}, but '{targetInfo}' describes {target.Label}.");
            }

            return new(target, selectedProject);
        }

        string? path = result.GetValue<string?>("--pg-config");
        string? forwardedPath = testProperties?.GetValueOrDefault("AnkusPgConfigPath");
        if (testProperties?.GetValueOrDefault("AnkusPostgresMajor") is string value)
        {
            if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int forwarded) ||
                forwarded is < 13 or > 19)
            {
                throw new ArgumentException("The forwarded AnkusPostgresMajor property must select PostgreSQL 13–19.");
            }

            if (major is not null && major != forwarded)
            {
                throw new ArgumentException("Select the same PostgreSQL major for --pg and forwarded AnkusPostgresMajor.");
            }

            major = forwarded;
        }

        if (forwardedPath is not null)
        {
            (string? normalized, ProjectPostgresSelection? forwardedProject) = await ResolveForwardedPgConfigPathAsync(
                result, forwardedPath, token, testProject, testConfiguration, testProperties, resolvedProject);
            if (path is not null && !string.Equals(Path.GetFullPath(path), normalized is null ? null : Path.GetFullPath(normalized),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new ArgumentException("Select the same pg_config for --pg-config and forwarded AnkusPgConfigPath.");
            }

            path ??= normalized;
            if (forwardedProject is not null)
            {
                selectedProject ??= forwardedProject.ProjectPath;
            }
        }

        if (major is null && path is null)
        {
            if (result.CommandResult.Command.Name is "install" or "package" && result.GetValue<string?>("--from") is string publication)
            {
                major = PublishedExtension.Read(publication).PostgresMajor;
            }
            else
            {
                major = EnvironmentMajor(result);
                if (major is null && await ProjectSelectionAsync(result, token, testProject, testConfiguration, testProperties, resolvedProject) is { } project)
                {
                    major = project.Settings.HasExplicitPostgresMajor ? project.Settings.PostgresMajor : null;
                    path = forwardedPath is null ? project.Settings.PgConfigPath : null;
                    selectedProject = project.ProjectPath;
                }
            }
        }

        if (path is null)
        {
            return new(await new PostgresRegistry(result.GetValue(home)).GetAsync(major ?? 18, token), selectedProject);
        }

        PostgresInstallation installation = await PostgresInstallation.CreateAsync(path, token);
        if (major is int expected && installation.Version.Major != expected)
        {
            throw new ArgumentException($"Expected PostgreSQL {expected}, but '{path}' is {installation.Label}.");
        }

        return new(installation, selectedProject);
    }

    /// <summary>
    /// Opens exported target information, extracting an archive where builds of the same archive extract it.
    /// </summary>
    /// <param name="path">An archive, an extracted directory or its manifest.</param>
    /// <param name="properties">Forwarded properties, which may select the binding cache.</param>
    /// <param name="token">Cancels extraction and verification.</param>
    /// <returns>An installation that describes the target machine.</returns>
    private static async Task<PostgresInstallation> OpenTargetAsync(string path, IReadOnlyDictionary<string, string>? properties,
        CancellationToken token)
    {
        PostgresTargetInfo target;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            string cache = properties?.GetValueOrDefault("AnkusBindingCacheDirectory") is string bindings && bindings.Length != 0
                ? Path.Combine(Path.GetFullPath(bindings), "targets")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ankus", "targets");
            target = await PostgresTargetInfo.ExtractAsync(path, cache, token);
        }
        else
        {
            target = PostgresTargetInfo.Open(path);
            await target.VerifyAsync(token);
        }

        return await PostgresInstallation.CreateAsync(target.ManifestPath, token);
    }

    /// <summary>
    /// Resolves build metadata without requiring a registered or discovered server installation.
    /// </summary>
    /// <param name="result">The parsed command.</param>
    /// <param name="token">Cancels evaluation.</param>
    /// <param name="resolvedProject">An extension already resolved by the command.</param>
    /// <returns>The explicit or project-selected PostgreSQL major.</returns>
    private static async Task<int> SelectMajorAsync(ParseResult result, CancellationToken token, string? resolvedProject = null)
    {
        int? major = ExplicitMajor(result);
        IReadOnlyDictionary<string, string> properties = BuildProperties(result);
        string? explicitPath = result.GetValue<string?>("--pg-config");
        string? forwardedPath = properties.GetValueOrDefault("AnkusPgConfigPath");
        string? normalized = null;
        if (forwardedPath is not null)
        {
            (normalized, _) = await ResolveForwardedPgConfigPathAsync(result, forwardedPath, token,
                testProperties: properties, resolvedProject: resolvedProject);
        }

        if (explicitPath is not null && normalized is not null &&
            !string.Equals(Path.GetFullPath(explicitPath), Path.GetFullPath(normalized),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("Select the same pg_config for --pg-config and AnkusPgConfigPath.");
        }

        if ((explicitPath ?? normalized) is string path)
        {
            int selected = (await PostgresInstallation.CreateAsync(path, token)).Version.Major;
            if (major is int expected && selected != expected)
            {
                throw new ArgumentException($"Expected PostgreSQL {expected}, but '{path}' is pg{selected}.");
            }

            return selected;
        }

        if (major is int selectedMajor)
        {
            return selectedMajor;
        }

        if (EnvironmentMajor(result) is int environmentMajor)
        {
            return environmentMajor;
        }

        ProjectPostgresSelection? project = await ProjectSelectionAsync(result, token,
            properties: properties, resolvedProject: resolvedProject);
        if (project?.Settings is { HasExplicitPostgresMajor: true } settings)
        {
            return settings.PostgresMajor;
        }

        return project?.Settings.PgConfigPath is string projectPath
            ? (await PostgresInstallation.CreateAsync(projectPath, token)).Version.Major : 18;
    }

    /// <summary>
    /// Resolves a forwarded relative pg_config path using the selected project's evaluation directory.
    /// </summary>
    private static async Task<(string? Path, ProjectPostgresSelection? Project)> ResolveForwardedPgConfigPathAsync(
        ParseResult result, string forwardedPath, CancellationToken token, string? testProject = null,
        string? testConfiguration = null, IReadOnlyDictionary<string, string>? testProperties = null,
        string? resolvedProject = null)
    {
        if (string.IsNullOrWhiteSpace(forwardedPath))
        {
            return (null, null);
        }

        bool projectRelative = !Path.IsPathFullyQualified(forwardedPath) &&
            (forwardedPath.Contains(Path.DirectorySeparatorChar) || forwardedPath.Contains(Path.AltDirectorySeparatorChar));
        if (!projectRelative)
        {
            return (forwardedPath, null);
        }

        ProjectPostgresSelection? project = await ProjectSelectionAsync(result, token, testProject, testConfiguration,
            testProperties, resolvedProject);
        return (project?.Settings.PgConfigPath ?? Path.GetFullPath(forwardedPath), project);
    }

    /// <summary>
    /// Rejects an invalid explicit major instead of silently using project defaults.
    /// </summary>
    /// <param name="result">The parsed command.</param>
    /// <returns>The explicit major, or null when the option was omitted.</returns>
    private static int? ExplicitMajor(ParseResult result)
    {
        int? major = result.GetValue<int?>("--pg");
        if (BuildProperties(result).GetValueOrDefault("AnkusPostgresMajor") is string value)
        {
            if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int forwarded) ||
                forwarded is < 13 or > 19)
            {
                throw new ArgumentException("AnkusPostgresMajor must select PostgreSQL 13–19.");
            }

            if (major is not null && major != forwarded)
            {
                throw new ArgumentException("Select the same PostgreSQL major for --pg and AnkusPostgresMajor.");
            }

            major = forwarded;
        }

        if (major is int selected)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(selected, 13, nameof(major));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(selected, 19, nameof(major));
        }

        return major;
    }

    /// <summary>
    /// Reads pgrx-compatible version selection for the same server commands as cargo-pgrx.
    /// </summary>
    /// <param name="result">The parsed command, after explicit command and property overrides.</param>
    /// <returns>The environment-selected major, or no selection when the variable is absent or empty.</returns>
    private static int? EnvironmentMajor(ParseResult result)
    {
        if (result.CommandResult.Command.Name is not ("run" or "start" or "stop" or "status" or "connect" or "test" or "regress" or "bench"))
        {
            return null;
        }

        string? value = Environment.GetEnvironmentVariable("PG_VERSION");
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        string number = value.StartsWith("pg", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (!int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major) ||
            major is < 13 or > 19)
        {
            throw new ArgumentException("PG_VERSION must select PostgreSQL 13–19, such as 18 or pg18.");
        }

        return major;
    }

    /// <summary>
    /// Evaluates project commands with their effective configuration; standalone cluster commands retain the default.
    /// </summary>
    /// <param name="result">The parsed command.</param>
    /// <param name="token">Cancels evaluation.</param>
    /// <param name="testProject">The project selected by forwarded test arguments.</param>
    /// <param name="testConfiguration">The effective test configuration.</param>
    /// <param name="properties">Literal global properties shared with the eventual build.</param>
    /// <param name="resolvedProject">An extension already resolved by the command.</param>
    /// <returns>The evaluated project settings, or null for commands without a project.</returns>
    private static async Task<ProjectPostgresSelection?> ProjectSelectionAsync(ParseResult result, CancellationToken token,
        string? testProject = null, string? testConfiguration = null, IReadOnlyDictionary<string, string>? properties = null,
        string? resolvedProject = null)
    {
        properties ??= BuildProperties(result);
        bool test = result.CommandResult.Command.Name == "test";
        bool cluster = result.CommandResult.Command.Name is "start" or "stop" or "status" or "info" ||
            result.CommandResult.Parent is System.CommandLine.Parsing.CommandResult { Command.Name: "info" };
        if (!test && !cluster && !result.CommandResult.Command.Options.Any(static option => option.Name == "--project"))
        {
            return null;
        }

        if (!test && !cluster)
        {
            string project = resolvedProject ?? await ExtensionBuilder.ResolveProjectAsync(result.GetValue<string?>("--project"),
                GetConfiguration(result), token, properties);
            return new(await PostgresProjectSettings.ReadAsync(project, GetConfiguration(result), properties, cancellationToken: token), project);
        }

        string input = Path.GetFullPath(testProject ?? Environment.CurrentDirectory);
        string[] projects;
        try
        {
            projects = FrameworkUpgrade.SelectProjects(input, package: null);
        }
        catch (ArgumentException) when (Directory.Exists(input))
        {
            return null;
        }

        PostgresProjectSettings? settings = await PostgresProjectSettings.TryReadAsync(projects,
            testConfiguration ?? (cluster ? "Debug" : GetConfiguration(result)), properties, cancellationToken: token);
        return settings is null ? null : new(settings, null);
    }

    /// <summary>
    /// Resolves an extension only once and retains its build context for publication.
    /// </summary>
    /// <param name="result">The parsed extension command.</param>
    /// <param name="home">The registry home option.</param>
    /// <param name="token">Cancels project and installation selection.</param>
    /// <param name="resolvedProject">An extension already resolved before a dry-run decision.</param>
    /// <param name="properties">The complete property set, including command-owned mode properties.</param>
    /// <returns>The exact extension, installation and build properties for this invocation.</returns>
    private static async Task<ExtensionSelection> SelectExtensionAsync(ParseResult result, Option<string?> home, CancellationToken token,
        string? resolvedProject = null, IReadOnlyDictionary<string, string>? properties = null)
    {
        string configuration = GetConfiguration(result);
        properties ??= BuildProperties(result);
        PostgresSelection selection = await SelectWithProjectAsync(result, home, token, testConfiguration: configuration,
            testProperties: properties, resolvedProject: resolvedProject);
        string project = selection.ProjectPath ?? await ExtensionBuilder.ResolveProjectAsync(result.GetValue<string?>("--project"),
            configuration, token, properties);
        return new(selection.Installation, project, configuration, properties);
    }

    /// <summary>
    /// Captures project defaults without treating a multi-project test selection as one extension.
    /// </summary>
    /// <param name="Settings">The evaluated PostgreSQL settings.</param>
    /// <param name="ProjectPath">The single extension path, or null for a test or cluster selection.</param>
    private sealed record ProjectPostgresSelection(PostgresProjectSettings Settings, string? ProjectPath);

    /// <summary>
    /// Carries an installation and any extension resolved while selecting its defaults.
    /// </summary>
    /// <param name="Installation">The checked native installation.</param>
    /// <param name="ProjectPath">The already resolved extension, when available.</param>
    private sealed record PostgresSelection(PostgresInstallation Installation, string? ProjectPath);

    /// <summary>
    /// Captures a complete build selection without process-wide caches or repeated solution scans.
    /// </summary>
    /// <param name="Installation">The checked native installation.</param>
    /// <param name="Project">The resolved extension project.</param>
    /// <param name="Configuration">The effective build configuration.</param>
    /// <param name="Properties">The literal properties shared by evaluation and publication.</param>
    private sealed record ExtensionSelection(PostgresInstallation Installation, string Project, string Configuration,
        IReadOnlyDictionary<string, string> Properties);
}
