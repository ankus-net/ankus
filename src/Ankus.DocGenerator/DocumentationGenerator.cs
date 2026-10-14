using System.Text.Json.Nodes;
using Docfx.Dotnet;

namespace Ankus.DocGenerator;

/// <summary>
/// Extracts compiled XML documentation and emits deterministic Starlight reference pages.
/// </summary>
internal static class DocumentationGenerator
{
    /// <summary>
    /// Generates the reference or verifies that the checked-in pages match the current public API.
    /// </summary>
    /// <param name="arguments">An optional --check flag.</param>
    /// <returns>Zero on success, or one if generation fails or pages are stale.</returns>
    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            if (arguments.Length > 1 || (arguments.Length == 1 && arguments[0] != "--check"))
            {
                throw new ArgumentException("Usage: dotnet run --project src/Ankus.DocGenerator -- [--check]");
            }

            string root = FindRepository();
            string work = Path.Join(root, "artifacts", "api-metadata", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                foreach (string assembly in new[] { "Ankus.Runtime", "Ankus.PgConfig", "Ankus.Testing" })
                {
                    if (!File.Exists(Path.Join(AppContext.BaseDirectory, assembly + ".xml")))
                    {
                        throw new FileNotFoundException($"XML documentation for {assembly} is missing.");
                    }
                }

                var config = new JsonObject
                {
                    ["metadata"] = new JsonArray(new JsonObject
                    {
                        ["src"] = new JsonArray(new JsonObject
                        {
                            ["src"] = AppContext.BaseDirectory,
                            ["files"] = new JsonArray("Ankus.Runtime.dll", "Ankus.PgConfig.dll", "Ankus.Testing.dll"),
                        }),
                        ["dest"] = Path.Join(work, "metadata"),
                        ["filter"] = Path.Join(AppContext.BaseDirectory, "filter.yml"),
                        ["disableGitFeatures"] = true,
                    }),
                };
                string configPath = Path.Join(work, "docfx.json");
                await File.WriteAllTextAsync(configPath, config.ToJsonString());
                await DotnetApiCatalog.GenerateManagedReferenceYamlFiles(configPath);
                var renderer = new ApiReferenceRenderer(Path.Join(work, "metadata"));
                IReadOnlyDictionary<string, string> pages = renderer.Render();
                string destination = Path.Join(root, "docs", "src", "content", "docs", "api", "generated");
                string[] stale = Directory.Exists(destination)
                    ? [.. Directory.GetFiles(destination, "*.md").Where(file => !pages.ContainsKey(Path.GetFileName(file)))] : [];
                foreach (string file in stale.Concat(pages.Keys.Select(name => Path.Join(destination, name))))
                {
                    if (File.Exists(file) && !File.ReadAllText(file).Contains(ApiReferenceRenderer.Marker, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Refusing to replace an unrecognized file: {file}");
                    }
                }

                string[] changed = [.. pages.Keys.Where(name => !File.Exists(Path.Join(destination, name)) ||
                    File.ReadAllText(Path.Join(destination, name)) != pages[name])];
                if (arguments.Length == 1)
                {
                    if (changed.Length != 0 || stale.Length != 0)
                    {
                        Console.Error.WriteLine($"API reference is stale: {changed.Length} changed, {stale.Length} removed pages.");
                        return 1;
                    }
                }
                else
                {
                    Directory.CreateDirectory(destination);
                    foreach (string name in changed)
                    {
                        await File.WriteAllTextAsync(Path.Join(destination, name), pages[name]);
                    }

                    foreach (string file in stale)
                    {
                        File.Delete(file);
                    }
                }

                Console.WriteLine($"API reference: {pages.Count} pages, {renderer.MemberCount} members.");
                return 0;
            }
            finally
            {
                Directory.Delete(work, recursive: true);
            }
        }
        // codeql[cs/catch-of-all-exceptions] The command reports any failure as a message and a nonzero exit code.
        catch (Exception error)
        {
            Console.Error.WriteLine($"API generation failed: {error.Message}");
            return 1;
        }
    }

    private static string FindRepository()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "Ankus.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The Ankus repository root was not found.");
    }
}
