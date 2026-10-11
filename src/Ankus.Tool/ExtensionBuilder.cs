using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Tool;

/// <summary>
/// Drives Native AOT publishing with an exact PostgreSQL installation selection.
/// </summary>
internal static class ExtensionBuilder
{
    /// <summary>
    /// Resolves a single extension project without choosing arbitrarily among multiple projects.
    /// </summary>
    /// <param name="path">An optional project, solution or directory.</param>
    /// <param name="configuration">The effective build configuration.</param>
    /// <param name="token">Cancels candidate evaluation and joins its child process.</param>
    /// <param name="properties">Literal global properties shared with the eventual build.</param>
    /// <returns>The absolute project path.</returns>
    internal static async Task<string> ResolveProjectAsync(string? path, string configuration, CancellationToken token,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        token.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path ?? Environment.CurrentDirectory);
        if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        string[] projects;
        if (Directory.Exists(path))
        {
            string[] solutions = [.. Directory.GetFiles(path, "*.sln"), .. Directory.GetFiles(path, "*.slnx")];
            if (solutions.Length == 0)
            {
                projects = Directory.GetFiles(path, "*.csproj");
                if (projects.Length == 1)
                {
                    return projects[0];
                }
            }
            else
            {
                projects = FrameworkUpgrade.SelectProjects(path, package: null);
            }
        }
        else
        {
            projects = FrameworkUpgrade.SelectProjects(path, package: null);
        }

        var extensions = new List<string>();
        var failures = new List<(string Project, string Output)>();
        foreach (string project in projects)
        {
            using var output = new MemoryStream();
            using var errors = new MemoryStream();
            int code = await ToolProcess.RunAsync("dotnet",
                ["msbuild", project, "-nologo", "-verbosity:quiet", "-getProperty:UsingAnkusSdk",
                    .. PropertyArguments(properties), "-p:Configuration=" + EscapeProperty(configuration)], token,
                outputStream: output, errorStream: errors);
            string evaluated = System.Text.Encoding.UTF8.GetString(output.ToArray());
            if (code != 0)
            {
                string error = System.Text.Encoding.UTF8.GetString(errors.ToArray());
                failures.Add((project, evaluated + error));
                continue;
            }

            if (string.Equals(evaluated.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            {
                extensions.Add(project);
            }
        }

        if (extensions.Count == 1)
        {
            return extensions[0];
        }

        foreach ((string project, string output) in failures)
        {
            Console.Error.WriteLine($"Could not evaluate '{project}':");
            Console.Error.Write(output);
        }

        throw new ArgumentException("Specify --project with one extension .csproj file; the selection must identify exactly one Ankus.Sdk project.");
    }

    /// <summary>
    /// Publishes for the host runtime and returns the original dotnet exit code.
    /// </summary>
    /// <param name="project">The extension project or directory.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="installation">The exact PostgreSQL installation.</param>
    /// <param name="output">The output directory.</param>
    /// <param name="token">Cancels publishing and terminates its process tree.</param>
    /// <param name="diagnosticsToStandardError">Whether build output belongs on stderr, leaving stdout available for SQL.</param>
    /// <param name="properties">Literal global properties used by evaluation and publication.</param>
    /// <returns>The dotnet publish exit code.</returns>
    internal static async Task<int> PublishAsync(string? project, string configuration, PostgresInstallation installation,
        string output, CancellationToken token, bool diagnosticsToStandardError = false, IReadOnlyDictionary<string, string>? properties = null)
    {
        string path = await ResolveProjectAsync(project, configuration, token, properties);
        Directory.CreateDirectory(output);
        // A failed build must not leave an earlier manifest that looks installable.
        PublishedExtension.Invalidate(output);
        // Target information from another machine selects its runtime and stands in for its pg_config.
        string runtime = installation.TargetInfo?.RuntimeIdentifier ?? RuntimeInformation.RuntimeIdentifier;
        string selection = installation.TargetInfo is null ? "-p:AnkusPgConfigPath=" + EscapeProperty(installation.PgConfigPath)
            : "-p:AnkusTargetInfo=" + EscapeProperty(installation.TargetInfo.ManifestPath);
        int exitCode = await ToolProcess.RunAsync("dotnet",
        [
            "publish", path, .. PropertyArguments(properties), "-p:Configuration=" + EscapeProperty(configuration), "--runtime", runtime,
            "--self-contained", "true", "--output", Path.GetFullPath(output),
            "-p:AnkusPostgresMajor=" + installation.Version.Major.ToString(CultureInfo.InvariantCulture), selection,
        ], token, diagnosticsToStandardError);
        if (exitCode == 0)
        {
            PublishedExtension manifest = PublishedExtension.Read(output);
            if (manifest.PostgresMajor != installation.Version.Major || manifest.RuntimeIdentifier != runtime)
            {
                throw new InvalidOperationException("The published extension does not match the selected PostgreSQL target.");
            }
        }

        return exitCode;
    }

    /// <summary>
    /// Measures the installation's native bindings with the project's toolchain and archives them for other machines.
    /// </summary>
    /// <param name="project">The extension project or directory, whose Ankus version builds will need.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="installation">The PostgreSQL installation on this machine.</param>
    /// <param name="archive">The archive to write.</param>
    /// <param name="token">Cancels the export and its process tree.</param>
    /// <param name="properties">Literal global properties for the export.</param>
    /// <returns>The MSBuild exit code.</returns>
    internal static async Task<int> ExportTargetAsync(string project, string configuration, PostgresInstallation installation,
        string archive, CancellationToken token, IReadOnlyDictionary<string, string>? properties = null)
    {
        if (installation.TargetInfo is not null)
        {
            throw new ArgumentException("Export target information from the target machine's own pg_config.");
        }

        string path = await ResolveProjectAsync(project, configuration, token, properties);
        return await ToolProcess.RunAsync("dotnet",
        [
            "msbuild", path, "-restore", "-nologo", "-t:AnkusExportTargetInfo", .. PropertyArguments(properties),
            "-p:Configuration=" + EscapeProperty(configuration), "-p:RuntimeIdentifier=" + RuntimeInformation.RuntimeIdentifier,
            "-p:AnkusPostgresMajor=" + installation.Version.Major.ToString(CultureInfo.InvariantCulture),
            "-p:AnkusPgConfigPath=" + EscapeProperty(installation.PgConfigPath),
            "-p:AnkusTargetInfoOutput=" + EscapeProperty(archive),
        ], token);
    }

    /// <summary>
    /// Escapes a literal value for one MSBuild command-line property.
    /// </summary>
    /// <param name="value">The literal property value.</param>
    /// <returns>The escaped property value.</returns>
    internal static string EscapeProperty(string value)
    {
        // MSBuild treats these characters as property-list separators or expansion syntax.
        return string.Concat(value.Select(static c => c is '%' or ';' or ',' or '$' or '@' or '(' or ')' or '\'' or '*' or '?' or '"'
            ? "%" + ((int)c).ToString("X2", CultureInfo.InvariantCulture) : c.ToString()));
    }

    /// <summary>
    /// Evaluates the extension's database name with MSBuild, including imported and configuration-specific properties.
    /// </summary>
    /// <param name="project">The extension project or directory.</param>
    /// <param name="configuration">The selected build configuration.</param>
    /// <param name="installation">The selected PostgreSQL installation.</param>
    /// <param name="token">Cancels project evaluation.</param>
    /// <param name="properties">Literal global properties for evaluating extension identity.</param>
    /// <returns>The explicit extension name or the SDK's normalized target name.</returns>
    internal static Task<string> GetExtensionNameAsync(string? project, string configuration,
        PostgresInstallation installation, CancellationToken token, IReadOnlyDictionary<string, string>? properties = null)
        => GetExtensionNameAsync(project, configuration, installation.Version.Major, installation.PgConfigPath, token, properties);

    /// <summary>
    /// Evaluates extension identity without requiring PostgreSQL discovery or managed compilation.
    /// </summary>
    /// <param name="project">The extension project or directory.</param>
    /// <param name="configuration">The selected build configuration.</param>
    /// <param name="postgresMajor">The selected PostgreSQL major.</param>
    /// <param name="pgConfigPath">An optional explicit PostgreSQL configuration path.</param>
    /// <param name="token">Cancels project evaluation.</param>
    /// <param name="properties">Literal global properties for evaluating extension identity.</param>
    /// <returns>The explicit extension name or the SDK's normalized target name.</returns>
    internal static async Task<string> GetExtensionNameAsync(string? project, string configuration,
        int postgresMajor, string? pgConfigPath, CancellationToken token, IReadOnlyDictionary<string, string>? properties = null)
    {
        string path = await ResolveProjectAsync(project, configuration, token, properties);
        using var output = new MemoryStream();
        List<string> arguments = ["msbuild", path, "-nologo", "-verbosity:quiet", "-getProperty:AnkusExtensionName,TargetName", .. PropertyArguments(properties),
            "-p:Configuration=" + EscapeProperty(configuration),
            "-p:AnkusPostgresMajor=" + postgresMajor.ToString(CultureInfo.InvariantCulture)];
        if (pgConfigPath is not null)
        {
            arguments.Add("-p:AnkusPgConfigPath=" + EscapeProperty(pgConfigPath));
        }

        int code = await ToolProcess.RunAsync("dotnet", arguments, token, outputStream: output);
        if (code != 0)
        {
            Console.Error.Write(System.Text.Encoding.UTF8.GetString(output.ToArray()));
            throw new InvalidOperationException($"Project evaluation failed ({code}).");
        }

        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        JsonElement evaluated = document.RootElement.GetProperty("Properties");
        string name = evaluated.GetProperty("AnkusExtensionName").GetString()!;
        if (name.Length == 0)
        {
            name = evaluated.GetProperty("TargetName").GetString()!.ToLowerInvariant().Replace('.', '_');
        }

        ArgumentException.ThrowIfNullOrEmpty(name);
        return name;
    }

    /// <summary>
    /// Generates validated primary control metadata through managed compilation without native publication.
    /// </summary>
    /// <param name="project">The extension project or directory.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="installation">The PostgreSQL development installation.</param>
    /// <param name="output">The control output file owned by the caller.</param>
    /// <param name="token">Cancels compilation and its child process tree.</param>
    /// <param name="properties">Literal global properties for compiling control metadata.</param>
    /// <returns>The original build exit code.</returns>
    internal static async Task<int> GenerateControlAsync(string project, string configuration,
        PostgresInstallation installation, string output, CancellationToken token, IReadOnlyDictionary<string, string>? properties = null)
    {
        string path = await ResolveProjectAsync(project, configuration, token, properties);
        return await ToolProcess.RunAsync("dotnet",
            ["build", path, "-t:AnkusGenerateControlFile", .. PropertyArguments(properties),
                "-p:Configuration=" + EscapeProperty(configuration), "--runtime", RuntimeInformation.RuntimeIdentifier,
                "-p:AnkusPostgresMajor=" + installation.Version.Major.ToString(CultureInfo.InvariantCulture),
                "-p:AnkusPgConfigPath=" + EscapeProperty(installation.PgConfigPath),
                "-p:AnkusControlOutput=" + EscapeProperty(Path.GetFullPath(output))], token, diagnosticsToStandardError: true);
    }

    /// <summary>
    /// Escapes each literal property separately so separators and expansion syntax stay in the value.
    /// </summary>
    /// <param name="properties">The literal property assignments.</param>
    /// <returns>Individual MSBuild command arguments.</returns>
    private static IEnumerable<string> PropertyArguments(IReadOnlyDictionary<string, string>? properties)
        => properties is null ? [] : properties.Select(static pair => "-p:" + pair.Key + "=" + EscapeProperty(pair.Value));
}
