using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ankus.Tool;

/// <summary>
/// Plans framework package and project SDK upgrades while preserving unrelated project configuration.
/// </summary>
internal static partial class FrameworkUpgrade
{
    private static readonly HashSet<string> s_packages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ankus.Runtime", "Ankus.Generators", "Ankus.PgConfig", "Ankus.Testing", "Ankus.Sdk",
    };

    /// <summary>
    /// Resolves all selected version declarations before allowing any file replacement.
    /// </summary>
    /// <param name="projects">The selected project paths.</param>
    /// <param name="versions">The explicit or discovered target-version policy.</param>
    /// <param name="token">Cancels discovery and version resolution.</param>
    /// <returns>The captured manifests with their planned edits.</returns>
    internal static async Task<UpgradeDocument[]> PlanAsync(string[] projects, FrameworkVersionResolver versions, CancellationToken token)
    {
        versions.Validate();

        // A first pass finds every Ankus package whose version comes from the package sources, so the second can move
        // them all to one version that each of them lists.
        versions.Recording = true;
        _ = await PlanProjectsAsync(projects, versions, token);
        versions.Recording = false;
        return await PlanProjectsAsync(projects, versions, token);
    }

    private static async Task<UpgradeDocument[]> PlanProjectsAsync(string[] projects, FrameworkVersionResolver versions, CancellationToken token)
    {
        var documents = new Dictionary<string, UpgradeDocument>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        int count = 0;
        foreach (string path in projects)
        {
            token.ThrowIfCancellationRequested();
            var project = new UpgradeProject(path, documents);
            await project.ReadAsync(token);
            (List<UpgradeReference> references, int globals) = await CollectAsync(project, documents, versions, token);
            count += references.Count + globals;
            if (references.Count + globals != 0)
            {
                await PlanToolManifestAsync(project, documents, versions, token);
            }

            var owned = new HashSet<XObject>(references.Where(static reference => reference.Item is null).Select(static reference => reference.Node));
            foreach (UpgradeReference reference in references.Where(static reference => reference.Item is null))
            {
                CollectProperties(project, reference.Version, owned, []);
            }

            foreach (UpgradeReference reference in references)
            {
                await ApplyAsync(project, reference, reference.Document, reference.Node, reference.Version, versions, owned, [], reference.Item is not null, token);
            }
        }

        if (count == 0)
        {
            throw new InvalidOperationException("The selected projects contain no Ankus package or SDK version declarations.");
        }

        foreach (UpgradeDocument document in documents.Values.Where(static document => document.Changed))
        {
            if (document.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using JsonDocument validated = JsonDocument.Parse(document.Render(),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            else
            {
                _ = XDocument.Parse(document.Render());
            }
        }

        return [.. documents.Values.DistinctBy(static document => document.Path)
            .OrderBy(static document => document.Path, StringComparer.Ordinal)];
    }

    private static async Task<(List<UpgradeReference> References, int Globals)> CollectAsync(UpgradeProject project,
        Dictionary<string, UpgradeDocument> documents, FrameworkVersionResolver versions, CancellationToken token)
    {
        var references = new List<UpgradeReference>();
        int globals = 0;
        foreach ((UpgradeDocument document, XDocument xml) in project.Files)
        {
            if (xml.Root?.Attribute("Sdk") is XAttribute sdks)
            {
                int offset = 0;
                foreach (string part in sdks.Value.Split(';'))
                {
                    string value = part.Trim();
                    int separator = value.IndexOf('/');
                    string name = separator < 0 ? value : value[..separator];
                    if (name.Equals("Ankus.Sdk", StringComparison.OrdinalIgnoreCase))
                    {
                        if (separator < 0 && await PlanGlobalSdkAsync(project, documents, versions, token))
                        {
                            globals++;
                        }
                        else
                        {
                            int beginning = offset + part.IndexOf(value, StringComparison.Ordinal);
                            int version = beginning + name.Length + (separator < 0 ? 0 : 1);
                            references.Add(new UpgradeReference(document, sdks, "Ankus.Sdk", separator < 0 ? "" : value[(separator + 1)..], true,
                                sdks.Value[..version] + (separator < 0 ? "/" : ""), sdks.Value[(beginning + value.Length)..]));
                        }
                    }

                    offset += part.Length + 1;
                }
            }

            foreach (XElement element in xml.Descendants())
            {
                string kind = element.Name.LocalName;
                if (kind is "PackageReference" or "PackageVersion" or "GlobalPackageReference")
                {
                    string identity = (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? "";
                    string[] packages = [.. project.GetIdentities(identity).Where(s_packages.Contains)];
                    if (packages.Length == 0)
                    {
                        continue;
                    }

                    foreach (string package in packages)
                    {
                        foreach (string metadata in new[] { "Version", "VersionOverride" })
                        {
                            if (element.Attribute(metadata) is XAttribute attribute)
                            {
                                AddPackageReference(references, document, element, attribute, package, !s_packages.Contains(identity));
                            }

                            foreach (XElement child in element.Elements().Where(child => child.Name.LocalName == metadata))
                            {
                                AddPackageReference(references, document, element, child, package, !s_packages.Contains(identity));
                            }
                        }
                    }
                }
                else if (kind is "Sdk" or "Import")
                {
                    string? name = (string?)element.Attribute(kind == "Sdk" ? "Name" : "Sdk");
                    if (!string.Equals(name, "Ankus.Sdk", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (element.Attribute("Version") is XAttribute version)
                    {
                        references.Add(new UpgradeReference(document, version, "Ankus.Sdk", version.Value, true));
                    }
                    else if (await PlanGlobalSdkAsync(project, documents, versions, token))
                    {
                        globals++;
                    }
                    else
                    {
                        throw new InvalidOperationException($"The Ankus.Sdk declaration in '{document.Path}' has no Version or global.json SDK version.");
                    }
                }
            }
        }

        return (references, globals);
    }

    private static void CollectProperties(UpgradeProject project, string expression, HashSet<XObject> owned, HashSet<string> active)
    {
        if (PropertyName(expression) is not string name)
        {
            return;
        }

        if (!active.Add(name))
        {
            throw new InvalidOperationException($"The framework version property '{name}' has a circular reference.");
        }

        foreach ((_, XElement element) in project.GetProperties(name))
        {
            owned.Add(element);
            CollectProperties(project, element.Value, owned, active);
        }

        active.Remove(name);
    }

    private static async Task ApplyAsync(UpgradeProject project, UpgradeReference reference, UpgradeDocument document,
        XObject node, string expression, FrameworkVersionResolver versions, HashSet<XObject> owned, HashSet<string> active,
        bool shared, CancellationToken token)
    {
        string? name = PropertyName(expression);
        if (name is not null)
        {
            if (!active.Add(name))
            {
                throw new InvalidOperationException($"The framework version property '{name}' has a circular reference.");
            }

            (UpgradeDocument Document, XElement Element)[] definitions = [.. project.GetProperties(name)];
            // An imported property can also be consumed by projects outside the selected solution.
            // Update the framework reference itself rather than changing those unknown consumers.
            bool outside = shared || definitions.Any(definition => definition.Document.Path != project.ProjectPath) ||
                project.Files.SelectMany(static file => file.Xml.Descendants())
                .SelectMany(static element => element.Attributes().Cast<XObject>().Concat(element.HasElements ? [] : [element]))
                .Any(value => !owned.Contains(value) && UpgradeProject.PropertyUse()
                    .Matches(value is XAttribute attribute ? attribute.Value : ((XElement)value).Value)
                    .Any(match => name.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)));
            if (definitions.Length != 0)
            {
                foreach ((UpgradeDocument owner, XElement element) in definitions)
                {
                    await ApplyAsync(project, reference, owner, element, element.Value, versions, owned, active, outside, token);
                }

                active.Remove(name);
                return;
            }

            active.Remove(name);
        }

        string updated = await versions.ResolveAsync(reference.Package, expression.Trim(), reference.IsSdk, project.DirectoryPath, token);
        if (reference.Item is not null)
        {
            string metadata = reference.Node is XAttribute attribute ? attribute.Name.LocalName : ((XElement)reference.Node).Name.LocalName;
            reference.Document.AddItemMetadata(reference.Item, metadata, reference.Condition!, updated);
        }
        else if (shared || ReferenceEquals(node, reference.Node))
        {
            reference.Document.Replace(reference.Node, reference.Prefix + PreserveWhitespace(reference.Version, updated) + reference.Suffix);
        }
        else
        {
            document.Replace(node, PreserveWhitespace(expression, updated));
        }
    }

    private static string? PropertyName(string value)
    {
        value = value.Trim();
        Match match = UpgradeProject.PropertyUse().Match(value);
        return match.Success && match.Length == value.Length ? match.Groups[1].Value : null;
    }

    private static string PreserveWhitespace(string original, string value)
    {
        int leading = original.Length - original.TrimStart().Length;
        int trailing = original.Length - original.TrimEnd().Length;
        return original[..leading] + value + (trailing == 0 || leading == original.Length ? "" : original[^trailing..]);
    }

    private static async Task<bool> PlanGlobalSdkAsync(UpgradeProject project, Dictionary<string, UpgradeDocument> documents,
        FrameworkVersionResolver versions, CancellationToken token)
    {
        if (UpgradeProject.FindAbove(project.DirectoryPath, "global.json") is not string path)
        {
            return false;
        }

        path = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        if (!documents.TryGetValue(path, out UpgradeDocument? document))
        {
            document = UpgradeDocument.Read(path);
            documents.Add(path, document);
        }

        (int Start, int Length, string Version)? declaration = ReadGlobalSdk(document.Text);
        if (declaration is not (int start, int length, string current))
        {
            return false;
        }

        string updated = await versions.ResolveAsync("Ankus.Sdk", current, true, project.DirectoryPath, token);
        if (updated != current)
        {
            document.Replace(start, length, "\"" + JsonEncodedText.Encode(updated) + "\"");
        }

        return true;
    }

    /// <summary>
    /// Updates Ankus.Tool in the local tool manifest that <c>dotnet tool restore</c> uses from the project directory, so
    /// <c>dotnet ankus</c> runs the tool released with the upgraded framework. Like the .NET SDK, the search checks each
    /// directory's <c>.config/dotnet-tools.json</c> and then <c>dotnet-tools.json</c>, from the project upward, and stops
    /// at the first manifest that lists the tool or is marked as the root.
    /// </summary>
    private static async Task PlanToolManifestAsync(UpgradeProject project, Dictionary<string, UpgradeDocument> documents,
        FrameworkVersionResolver versions, CancellationToken token)
    {
        for (DirectoryInfo? directory = new(project.DirectoryPath); directory is not null; directory = directory.Parent)
        {
            foreach (string candidate in new[] { Path.Join(directory.FullName, ".config", "dotnet-tools.json"), Path.Join(directory.FullName, "dotnet-tools.json") })
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                string path = File.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName ?? candidate;
                UpgradeDocument document = documents.TryGetValue(path, out UpgradeDocument? existing) ? existing : UpgradeDocument.Read(path);
                ((int Start, int Length, string Version)? declaration, bool root) = ReadToolManifest(document.Text);
                if (declaration is (int start, int length, string current))
                {
                    documents.TryAdd(path, document);
                    string updated = await versions.ResolveAsync("Ankus.Tool", current, true, project.DirectoryPath, token);
                    document.Replace(start, length, "\"" + JsonEncodedText.Encode(updated) + "\"");
                    return;
                }

                if (root)
                {
                    return;
                }
            }
        }
    }

    private static ((int Start, int Length, string Version)? Declaration, bool Root) ReadToolManifest(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        (int Start, int Length, string Version)? declaration = null;
        bool root = false;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
            {
                continue;
            }

            bool isRoot = reader.ValueTextEquals("isRoot");
            bool tools = reader.ValueTextEquals("tools");
            reader.Read();
            if (isRoot)
            {
                root = reader.TokenType == JsonTokenType.True;
            }
            else if (tools && reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    bool ankus = string.Equals(reader.GetString(), "Ankus.Tool", StringComparison.OrdinalIgnoreCase);
                    reader.Read();
                    if (!ankus || reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        continue;
                    }

                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        bool version = reader.ValueTextEquals("version");
                        reader.Read();
                        if (version && reader.TokenType == JsonTokenType.String)
                        {
                            int offset = checked((int)reader.TokenStartIndex);
                            declaration = (Encoding.UTF8.GetCharCount(utf8.AsSpan(0, offset)),
                                Encoding.UTF8.GetCharCount(utf8.AsSpan(offset, checked((int)reader.BytesConsumed) - offset)), reader.GetString()!);
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                }
            }
            else
            {
                reader.Skip();
            }
        }

        return (declaration, root);
    }

    private static (int Start, int Length, string Version)? ReadGlobalSdk(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        (int Start, int Length, string Version)? result = null;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("msbuild-sdks"))
            {
                continue;
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidOperationException("global.json msbuild-sdks must be an object.");
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new InvalidOperationException("Invalid global.json SDK declaration.");
                }

                bool ankus = string.Equals(reader.GetString(), "Ankus.Sdk", StringComparison.OrdinalIgnoreCase);
                reader.Read();
                if (ankus)
                {
                    if (result is not null || reader.TokenType != JsonTokenType.String)
                    {
                        throw new InvalidOperationException("global.json must have one string version for Ankus.Sdk.");
                    }

                    int offset = checked((int)reader.TokenStartIndex);
                    result = (Encoding.UTF8.GetCharCount(utf8.AsSpan(0, offset)),
                        Encoding.UTF8.GetCharCount(utf8.AsSpan(offset, checked((int)reader.BytesConsumed) - offset)), reader.GetString()!);
                }
                else
                {
                    reader.Skip();
                }
            }
        }

        return result;
    }
}
