using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Ankus.Tool;

internal sealed partial class UpgradeProject
{
    private static readonly string[] s_excludedProperties = ["MSBuildBinPath", "NuGetPackageRoot", "MSBuildProjectExtensionsPath"];

    private async Task ReadEvaluatedAsync(string project, Dictionary<string, UpgradeDocument> documents, CancellationToken token)
    {
        // Query and preprocess only: no restore or build targets execute, including authored targets.
        string properties = await EvaluateAsync(project,
            ["-getProperty:Configurations,TargetFrameworks,TargetFramework,MSBuildBinPath,NuGetPackageRoot,MSBuildProjectExtensionsPath"], token);
        using JsonDocument result = JsonDocument.Parse(properties);
        JsonElement values = result.RootElement.GetProperty("Properties");
        string[] configurations = Split(values.GetProperty("Configurations").GetString());
        string[] frameworks = Split(values.GetProperty("TargetFrameworks").GetString());
        if (frameworks.Length == 0)
        {
            frameworks = Split(values.GetProperty("TargetFramework").GetString());
        }

        string[] excluded = [.. s_excludedProperties
            .Select(name => values.GetProperty(name).GetString())
            .OfType<string>().Where(static value => value.Length != 0)
            .Select(value => Path.GetFullPath(value, DirectoryPath))];
        AddEvaluatedFile(project, documents);
        // Include the default outer evaluation as well as each declared inner build.
        foreach ((string? configuration, string? framework) in new (string?, string?)[] { (null, null) }
            .Concat((configurations.Length == 0 ? new string?[] { null } : configurations)
                .SelectMany(configuration => (frameworks.Length == 0 ? new string?[] { null } : frameworks)
                    .Select(framework => (configuration, framework)))).Distinct())
        {
            List<string> arguments = ["-preprocess"];
            if (configuration is not null)
            {
                arguments.Add("-property:Configuration=" + EscapeProperty(configuration));
            }

            if (framework is not null)
            {
                arguments.Add("-property:TargetFramework=" + EscapeProperty(framework));
            }

            string preprocessed = await EvaluateAsync(project, arguments, token);
            XDocument logical = XDocument.Parse(preprocessed);
            foreach (XComment comment in logical.DescendantNodes().OfType<XComment>())
            {
                string[] lines = comment.Value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                // MSBuild surrounds an expanded import with a source-path boundary comment.
                // False imports remain ordinary commented XML and have no such boundary.
                if (lines.Length < 4 || !lines[0].StartsWith("==========", StringComparison.Ordinal) ||
                    !lines[1].StartsWith("<Import ", StringComparison.Ordinal) ||
                    !lines[^1].StartsWith("==========", StringComparison.Ordinal))
                {
                    continue;
                }

                string source = lines[^2];
                if (!Path.IsPathFullyQualified(source) || !File.Exists(source))
                {
                    throw new InvalidOperationException($"MSBuild returned an unrecognized import boundary for '{project}'. No project files were changed.");
                }

                string resolved = File.ResolveLinkTarget(source, returnFinalTarget: true)?.FullName ?? source;
                if (!excluded.Any(directory => IsWithin(source, directory) || IsWithin(resolved, directory)))
                {
                    AddEvaluatedFile(source, documents);
                }
            }
        }
    }

    private void AddEvaluatedFile(string source, Dictionary<string, UpgradeDocument> documents)
    {
        source = File.ResolveLinkTarget(source, returnFinalTarget: true)?.FullName ?? source;
        if (_packageDirectories.Any(directory => IsWithin(source, directory)))
        {
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

        Files.Add((document, document.ReadXml()));
    }

    private async Task<string> EvaluateAsync(string project, IEnumerable<string> arguments, CancellationToken token)
    {
        using var output = new MemoryStream();
        int code = await ToolProcess.RunAsync("dotnet", ["msbuild", project, "-nologo", "-noAutoResponse", .. arguments], token,
            outputStream: output, workingDirectory: DirectoryPath);
        string text = Encoding.UTF8.GetString(output.ToArray());
        if (code != 0)
        {
            throw new InvalidOperationException($"MSBuild could not discover imports for '{project}' ({code}). " +
                "Conditional and computed imports require a resolvable project SDK. No project files were changed." + Environment.NewLine + text);
        }

        return text;
    }

    private static string[] Split(string? value)
        => value?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];

    private static string EscapeProperty(string value)
        => value.Replace("%", "%25", StringComparison.Ordinal).Replace(";", "%3B", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal);

    private static bool IsWithin(string path, string directory)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
