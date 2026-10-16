using System.Text.Json;
using NuGet.Configuration;
using NuGet.Versioning;

namespace Ankus.Tool;

/// <summary>
/// Resolves framework requirements through NuGet's version rules and the installed SDK's configured package sources.
/// </summary>
/// <param name="target">An explicit NuGet requirement, or null to discover updates.</param>
/// <param name="includePrereleases">Whether discovery includes prerelease packages.</param>
/// <param name="configFile">An optional explicit NuGet configuration file.</param>
/// <param name="sources">Optional package source overrides.</param>
internal sealed class FrameworkVersionResolver(string? target, bool includePrereleases, string? configFile, string[] sources)
{
    private readonly Dictionary<(string Directory, string Package), NuGetVersion[]> _versions = [];

    /// <summary>
    /// Validates an explicit requirement before any project is edited or feed is queried.
    /// </summary>
    internal void Validate()
    {
        if (target is not null && (string.IsNullOrWhiteSpace(target) || !VersionRange.TryParse(target, out _)))
        {
            throw new ArgumentException($"'{target}' is not a NuGet version or version range.");
        }

        if (configFile is not null && !File.Exists(configFile))
        {
            throw new FileNotFoundException("The NuGet configuration file was not found.", configFile);
        }
    }

    /// <summary>
    /// Resolves one SDK version or package requirement without changing the project or restoring its packages.
    /// </summary>
    /// <param name="package">The exact framework package identity.</param>
    /// <param name="current">The current literal requirement, or an empty unversioned SDK reference.</param>
    /// <param name="sdk">Whether this declaration requires a concrete MSBuild SDK version.</param>
    /// <param name="directory">The project directory used for NuGet configuration discovery.</param>
    /// <param name="token">Cancels package discovery.</param>
    /// <returns>The new version text.</returns>
    internal async Task<string> ResolveAsync(string package, string current, bool sdk, string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (target is not null)
        {
            if (!sdk)
            {
                return target;
            }

            if (NuGetVersion.TryParse(target, out NuGetVersion? exact))
            {
                return exact.ToNormalizedString();
            }

            VersionRange requested = VersionRange.Parse(target);
            NuGetVersion[] available = await GetVersionsAsync(package, directory,
                includePrereleases || requested.MinVersion?.IsPrerelease == true || requested.Float?.IncludePrerelease == true, token);
            return Select(available.Where(requested.Satisfies), package, target).ToNormalizedString();
        }

        VersionRange? existing = null;
        if (current.Length != 0 && !VersionRange.TryParse(current, out existing))
        {
            throw new InvalidOperationException($"The {package} requirement '{current}' is not a literal NuGet version or range.");
        }

        // Floating requirements already select updates within their declared band.
        // Changing that band requires an explicit --to requirement.
        if (!sdk && existing?.IsFloating == true)
        {
            return current;
        }

        NuGetVersion[] versions = await GetVersionsAsync(package, directory, includePrereleases, token);
        bool pinned = existing?.MinVersion is not null && existing.MinVersion.Equals(existing.MaxVersion);
        IEnumerable<NuGetVersion> candidates = !sdk && existing?.MaxVersion is not null && !pinned
            ? versions.Where(existing.Satisfies) : versions;
        NuGetVersion selected = Select(candidates, package, current);
        if (sdk || existing is null || NuGetVersion.TryParse(current, out _))
        {
            return selected.ToNormalizedString();
        }

        if (pinned)
        {
            return "[" + selected.ToNormalizedString() + "]";
        }

        return new VersionRange(selected, includeMinVersion: true, existing.MaxVersion,
            includeMaxVersion: existing.IsMaxInclusive).ToNormalizedString();
    }

    private async Task<NuGetVersion[]> GetVersionsAsync(string package, string directory, bool prerelease, CancellationToken token)
    {
        (string Directory, string Package) key = (directory, package.ToUpperInvariant() + (prerelease ? ":preview" : ":stable"));
        if (_versions.TryGetValue(key, out NuGetVersion[]? cached))
        {
            return cached;
        }

        List<string> arguments = ["package", "search", package, "--exact-match", "--format", "json"];
        if (prerelease)
        {
            arguments.Add("--prerelease");
        }

        if (configFile is not null)
        {
            arguments.AddRange(["--configfile", configFile]);
        }

        foreach (string source in GetSources(package, directory))
        {
            arguments.AddRange(["--source", source]);
        }

        // NuGet reuses a feed's saved package listing for up to 30 minutes, so a release published after an earlier
        // check stayed invisible to upgrades until that copy expired. An empty HTTP cache reads the current listing.
        using var output = new MemoryStream();
        DirectoryInfo cache = Directory.CreateTempSubdirectory("ankus-nuget-http-");
        int code;
        try
        {
            code = await ToolProcess.RunAsync("dotnet", arguments, token, outputStream: output, workingDirectory: directory,
                environment: new Dictionary<string, string?> { ["NUGET_HTTP_CACHE_PATH"] = cache.FullName });
        }
        finally
        {
            cache.Delete(recursive: true);
        }

        if (code != 0)
        {
            throw new InvalidOperationException($"NuGet version discovery for {package} failed ({code}). No project files were changed.");
        }

        using JsonDocument result = JsonDocument.Parse(output.ToArray());
        JsonElement root = result.RootElement;
        if (root.GetProperty("version").GetInt32() != 2)
        {
            throw new InvalidOperationException("The installed SDK returned an unsupported NuGet search format.");
        }

        RequireCompleteResult(root, package);
        var found = new HashSet<NuGetVersion>(VersionComparer.VersionRelease);
        foreach (JsonElement source in root.GetProperty("searchResult").EnumerateArray())
        {
            RequireCompleteResult(source, package);
            foreach (JsonElement entry in source.GetProperty("packages").EnumerateArray())
            {
                if (!string.Equals(package, entry.GetProperty("id").GetString(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"NuGet returned a different package while resolving {package}.");
                }

                NuGetVersion version = NuGetVersion.Parse(entry.GetProperty("version").GetString()!);
                if (prerelease || !version.IsPrerelease)
                {
                    found.Add(version);
                }
            }
        }

        NuGetVersion[] values = [.. found.OrderBy(static version => version, VersionComparer.VersionRelease)];
        _versions.Add(key, values);
        return values;
    }

    private string[] GetSources(string package, string directory)
    {
        ISettings settings = configFile is null ? Settings.LoadDefaultSettings(directory)
            : Settings.LoadSpecificSettings(Path.GetDirectoryName(configFile)!, Path.GetFileName(configFile));
        PackageSourceMapping mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        var provider = new PackageSourceProvider(settings);
        PackageSource[] configured = [.. provider.LoadPackageSources()];
        PackageSource[] selected = sources.Length == 0 ? [.. configured.Where(static source => source.IsEnabled)]
            : [.. sources.Select(source => ResolveSource(source, configured))];
        if (!mapping.IsEnabled)
        {
            return sources.Length == 0 ? [] : [.. selected.Select(static source => source.Source)];
        }

        IReadOnlyList<string> permitted = mapping.GetConfiguredPackageSources(package);
        string[] result = [.. selected.Where(source => permitted.Contains(source.Name, StringComparer.OrdinalIgnoreCase))
            .Select(static source => source.Name)];
        if (result.Length == 0)
        {
            throw new InvalidOperationException($"No selected NuGet package source mapping allows {package}.");
        }

        return result;
    }

    private static PackageSource ResolveSource(string source, PackageSource[] configured)
    {
        PackageSource? named = configured.FirstOrDefault(candidate => candidate.Name.Equals(source, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
        {
            return named;
        }

        string location = Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
            ? uri.IsFile ? Path.GetFullPath(uri.LocalPath) : source : Path.GetFullPath(source);
        return configured.FirstOrDefault(candidate => SameSource(candidate.Source, location))
            ?? new PackageSource(location);
    }

    private static bool SameSource(string configured, string selected)
    {
        bool configuredUri = Uri.TryCreate(configured, UriKind.Absolute, out Uri? left);
        bool selectedUri = Uri.TryCreate(selected, UriKind.Absolute, out Uri? right);
        if (configuredUri && !left!.IsFile || selectedUri && !right!.IsFile)
        {
            return configuredUri && selectedUri && left!.Equals(right);
        }

        string configuredPath = ResolveDirectory(configuredUri ? left!.LocalPath : configured);
        string selectedPath = ResolveDirectory(selectedUri ? right!.LocalPath : selected);
        return configuredPath.Equals(selectedPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string ResolveDirectory(string path)
    {
        path = Path.GetFullPath(path);
        string resolved = Path.GetPathRoot(path)!;
        foreach (string component in path[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Join(resolved, component);
            if (Directory.Exists(resolved) && Directory.ResolveLinkTarget(resolved, returnFinalTarget: true) is FileSystemInfo target)
            {
                resolved = ResolveDirectory(target.FullName);
            }
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static void RequireCompleteResult(JsonElement result, string package)
    {
        if (result.TryGetProperty("problems", out JsonElement problems) && problems.GetArrayLength() != 0)
        {
            string messages = string.Join(Environment.NewLine, problems.EnumerateArray()
                .Select(static problem => problem.GetProperty("text").GetString()));
            throw new InvalidOperationException($"NuGet version discovery for {package} was incomplete: {messages}");
        }
    }

    private static NuGetVersion Select(IEnumerable<NuGetVersion> versions, string package, string requirement)
        => versions.LastOrDefault() ?? throw new InvalidOperationException(
            $"No eligible version of {package} was found for '{requirement}'. No project files were changed.");
}
