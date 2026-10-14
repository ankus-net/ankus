using System.Text.RegularExpressions;
using System.Xml.Linq;
using NuGet.Configuration;

namespace Ankus.Tool;

/// <summary>
/// Discovers project-owned framework declarations and imported property files without restoring or building the project.
/// </summary>
/// <param name="path">The selected project path.</param>
/// <param name="documents">The shared manifest snapshots for this upgrade.</param>
internal sealed partial class UpgradeProject(string path, Dictionary<string, UpgradeDocument> documents)
{
    private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _conditionalProperties = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _visited = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private string[] _packageDirectories = [];
    private bool _requiresEvaluation;

    /// <summary>
    /// Gets the project directory used for package-source discovery.
    /// </summary>
    internal string DirectoryPath { get; } = Path.GetDirectoryName(path)!;

    /// <summary>
    /// Gets the selected project's source path for distinguishing local and imported property ownership.
    /// </summary>
    internal string ProjectPath { get; } = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;

    /// <summary>
    /// Gets the parsed project and shared manifests, in discovery order.
    /// </summary>
    internal List<(UpgradeDocument Document, XDocument Xml)> Files { get; } = [];

    /// <summary>
    /// Reads source declarations, using MSBuild when conditions or computed paths affect import selection.
    /// </summary>
    /// <param name="token">Cancels project evaluation.</param>
    /// <returns>A task representing source discovery.</returns>
    internal async Task ReadAsync(CancellationToken token)
    {
        ISettings settings = Settings.LoadDefaultSettings(DirectoryPath);
        _packageDirectories = [SettingsUtility.GetGlobalPackagesFolder(settings), .. SettingsUtility.GetFallbackPackageFolders(settings)];
        UpgradeDocument root = UpgradeDocument.Read(path);
        documents.TryAdd(root.Path, root);
        if (RequiresEvaluation(root.ReadXml()) || root.Path != path)
        {
            await ReadEvaluatedAsync(path, documents, token);
            return;
        }

        _properties["MSBuildProjectDirectory"] = DirectoryPath;
        _properties["MSBuildProjectFullPath"] = path;
        _properties["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(path);
        foreach (string name in new[] { "Directory.Build.props", "Directory.Packages.props" })
        {
            if (FindAbove(DirectoryPath, name) is not string shared)
            {
                continue;
            }

            ReadFile(shared);
        }

        ReadFile(path);
        if (FindAbove(DirectoryPath, "Directory.Build.targets") is string targets)
        {
            ReadFile(targets);
        }

        if (_requiresEvaluation)
        {
            Files.Clear();
            _visited.Clear();
            await ReadEvaluatedAsync(path, documents, token);
        }
    }

    /// <summary>
    /// Gets every source declaration of a property, preserving its configuration-specific alternatives.
    /// </summary>
    /// <param name="name">The case-insensitive MSBuild property name.</param>
    /// <returns>Matching scalar property declarations and their owning manifests.</returns>
    internal IEnumerable<(UpgradeDocument Document, XElement Element)> GetProperties(string name)
        => Files.SelectMany(file => file.Xml.Descendants()
            .Where(element => element.Parent?.Name.LocalName == "PropertyGroup" &&
                string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
            .Select(element => (file.Document, element)));

    /// <summary>
    /// Finds the nearest ancestor file using ordinary shared-project file discovery.
    /// </summary>
    /// <param name="directory">The starting directory.</param>
    /// <param name="name">The file name.</param>
    /// <returns>The nearest path, or null when no ancestor contains it.</returns>
    internal static string? FindAbove(string directory, string name)
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            string candidate = Path.Join(current.FullName, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private void ReadFile(string source)
    {
        if (_requiresEvaluation)
        {
            return;
        }

        source = Path.GetFullPath(source);
        if (_packageDirectories.Any(directory => IsWithin(source, directory)))
        {
            return;
        }

        if (File.ResolveLinkTarget(source, returnFinalTarget: true) is not null)
        {
            _requiresEvaluation = true;
            return;
        }

        if (!_visited.Add(source))
        {
            return;
        }

        if (!documents.TryGetValue(source, out UpgradeDocument? document))
        {
            document = UpgradeDocument.Read(source);
            documents.Add(source, document);
        }

        XDocument xml = document.ReadXml();
        Files.Add((document, xml));
        if (RequiresEvaluation(xml))
        {
            _requiresEvaluation = true;
            return;
        }

        foreach (XElement element in xml.Descendants())
        {
            if (element.Parent?.Name.LocalName == "PropertyGroup" && !element.HasElements)
            {
                if (element.AncestorsAndSelf().Any(static owner => owner.Attribute("Condition") is not null))
                {
                    _conditionalProperties.Add(element.Name.LocalName);
                }

                string value = Expand(element.Value, source);
                _properties[element.Name.LocalName] = value;
            }

            if (element.Name.LocalName != "Import" || element.Attribute("Sdk") is not null)
            {
                continue;
            }

            string import = Expand((string?)element.Attribute("Project") ?? "", source);
            if (_requiresEvaluation || import.Contains("$(", StringComparison.Ordinal) || import.IndexOfAny([';', '%']) >= 0)
            {
                _requiresEvaluation = true;
                return;
            }

            string imported = Path.GetFullPath(import.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(source)!);
            if (imported.IndexOfAny(['*', '?']) >= 0)
            {
                string directory = Path.GetDirectoryName(imported)!;
                if (directory.IndexOfAny(['*', '?']) >= 0)
                {
                    _requiresEvaluation = true;
                    return;
                }

                if (Directory.Exists(directory))
                {
                    foreach (string match in Directory.GetFiles(directory, Path.GetFileName(imported)).Order(StringComparer.Ordinal))
                    {
                        ReadFile(match);
                    }
                }
            }
            else if (File.Exists(imported))
            {
                ReadFile(imported);
            }
            else if (!element.AncestorsAndSelf().Any(static owner => owner.Attribute("Condition") is not null))
            {
                throw new FileNotFoundException("An imported project manifest was not found.", imported);
            }
        }
    }

    private static bool RequiresEvaluation(XDocument xml)
        => xml.Descendants().Any(static element =>
            element.Name.LocalName == "Choose" || IsEvaluatedImport(element) || IsImportLocation(element));

    /// <summary>
    /// Finds an import whose project depends on a condition or a property function, which only evaluation resolves.
    /// </summary>
    private static bool IsEvaluatedImport(XElement element)
        => element.Name.LocalName == "Import" && element.Attribute("Sdk") is null &&
            (element.AncestorsAndSelf().Any(static owner => owner.Attribute("Condition") is not null) ||
             ((string?)element.Attribute("Project") ?? "").Contains("$([", StringComparison.Ordinal));

    /// <summary>
    /// Finds a property that moves imported build files or the folders the upgrade must leave alone.
    /// </summary>
    private static bool IsImportLocation(XElement element)
    {
        string name = element.Name.LocalName;
        return element.Parent?.Name.LocalName == "PropertyGroup" &&
            (name.StartsWith("ImportDirectory", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("DirectoryBuildPropsPath", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("DirectoryBuildTargetsPath", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("DirectoryPackagesPropsPath", StringComparison.OrdinalIgnoreCase) ||
             s_excludedProperties.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    private string Expand(string value, string source)
        => PropertyUse().Replace(value, match =>
        {
            string name = match.Groups[1].Value;
            if (_conditionalProperties.Contains(name))
            {
                _requiresEvaluation = true;
            }

            return name.Equals("MSBuildThisFileDirectory", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(source) + Path.DirectorySeparatorChar
                : _properties.GetValueOrDefault(name, match.Value);
        });

    /// <summary>
    /// Matches a named MSBuild property reference without interpreting functions or expressions.
    /// </summary>
    /// <returns>The property-token matcher.</returns>
    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z_0-9.-]*)\)", RegexOptions.CultureInvariant)]
    internal static partial Regex PropertyUse();
}
