using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

bool dismiss = args is ["dismiss", _, _];
bool filter = args is ["filter", _];
if (!dismiss && !filter && (args is not [string checkDirectory] || checkDirectory.StartsWith('-')))
{
    Console.Error.WriteLine("usage: Ankus.CodeQl.cs <sarif-directory>");
    Console.Error.WriteLine("       Ankus.CodeQl.cs filter <sarif-directory>");
    Console.Error.WriteLine("       Ankus.CodeQl.cs dismiss <sarif-directory> <category>");
    return 2;
}

string directory = dismiss || filter ? args[1] : args[0];
string[] files = Directory.Exists(directory)
    ? [.. Directory.EnumerateFiles(directory, "*.sarif", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
    : [];
if (files.Length == 0)
{
    Console.Error.WriteLine($"CodeQL left no SARIF file under {Path.GetFullPath(directory)}.");
    return 1;
}

if (filter)
{
    return GeneratedSources.Filter(files);
}

List<Finding> results = [];
foreach (string file in files)
{
    using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
    if (!document.RootElement.TryGetProperty("runs", out JsonElement runs) || runs.ValueKind != JsonValueKind.Array)
    {
        Console.Error.WriteLine($"{file} has no runs, so it is not a CodeQL result.");
        return 1;
    }

    foreach (JsonElement run in runs.EnumerateArray())
    {
        if (!run.TryGetProperty("results", out JsonElement found) || found.ValueKind != JsonValueKind.Array)
        {
            continue;
        }

        foreach (JsonElement result in found.EnumerateArray())
        {
            results.Add(Finding.Read(result));
        }
    }
}

if (dismiss)
{
    return await AlertSync.RunAsync(results, args[2]);
}

// Code scanning lists findings in the Security tab without failing a check, so a new one would go unseen. This reads the
// result files the scan uploads and fails for every finding in them.
foreach (Finding finding in results.Where(static finding => !finding.Suppressed).OrderBy(static finding => finding.ToString(), StringComparer.Ordinal))
{
    Console.Error.WriteLine(finding);
}

// A `// codeql[rule] reason` comment marks a finding the code must keep, such as a catch that reports every exception at
// the native boundary. These are listed so each one stays visible, and do not fail the check.
foreach (Finding finding in results.Where(static finding => finding.Suppressed).OrderBy(static finding => finding.ToString(), StringComparer.Ordinal))
{
    Console.WriteLine("Suppressed in source: " + finding);
}

int open = results.Count(static finding => !finding.Suppressed);
Console.WriteLine($"CodeQL reported {open} finding(s) and {results.Count - open} suppressed in source in {files.Length} result file(s).");
return open == 0 ? 0 : 1;

/// <summary>
/// One CodeQL result, located by its rule and its first location's start.
/// </summary>
/// <param name="Rule">The query's identifier, such as <c>cs/catch-of-all-exceptions</c>.</param>
/// <param name="Path">The file, relative to the checkout.</param>
/// <param name="Line">The first line, or 0 when the result has no location.</param>
/// <param name="Column">The first column; SARIF omits it when it is 1.</param>
/// <param name="Level">The SARIF level.</param>
/// <param name="Message">The result's message.</param>
/// <param name="Suppressed">Whether a <c>codeql[rule]</c> comment marks the result.</param>
internal sealed record Finding(string Rule, string Path, int Line, int Column, string Level, string Message, bool Suppressed)
{
    /// <summary>
    /// Reads a SARIF result.
    /// </summary>
    /// <param name="result">The result object.</param>
    /// <returns>The finding.</returns>
    internal static Finding Read(JsonElement result)
    {
        string path = "unknown location";
        int line = 0;
        int column = 1;
        if (result.TryGetProperty("locations", out JsonElement locations) && locations.ValueKind == JsonValueKind.Array &&
            locations.GetArrayLength() > 0 && locations[0].TryGetProperty("physicalLocation", out JsonElement place))
        {
            path = place.TryGetProperty("artifactLocation", out JsonElement artifact) ? Text(artifact, "uri", path) : path;
            if (place.TryGetProperty("region", out JsonElement region))
            {
                line = Number(region, "startLine", 0);
                column = Number(region, "startColumn", 1);
            }
        }

        string message = result.TryGetProperty("message", out JsonElement body) ? Text(body, "text", "") : "";

        // An accepted or unreviewed suppression marks the result; CodeQL's comment query writes one with no status.
        bool suppressed = result.TryGetProperty("suppressions", out JsonElement suppressions) &&
            suppressions.ValueKind == JsonValueKind.Array &&
            suppressions.EnumerateArray().Any(static suppression => !suppression.TryGetProperty("status", out JsonElement status) ||
                status.GetString() is not "rejected");
        return new Finding(Text(result, "ruleId", "unknown rule"), path, line, column, Text(result, "level", "warning"), message, suppressed);
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"{(Line == 0 ? Path : $"{Path}:{Line}")}: {Level} {Rule}: {Message}";

    private static string Text(JsonElement element, string name, string fallback)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static int Number(JsonElement element, string name, int fallback)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number) ? number : fallback;
}

/// <summary>
/// Brings the repository's code scanning alerts in line with the suppression comments of one analysis.
/// </summary>
/// <remarks>
/// Code scanning keeps a result open even when a <c>codeql[rule]</c> comment marks it. After the analysis is processed,
/// each open alert of a marked result is dismissed as won't fix, with the comment's reason, and an alert dismissed that
/// way whose result is reported again without its comment is reopened.
/// </remarks>
internal static class AlertSync
{
    private const string Marker = "Suppressed in source: ";

    /// <summary>
    /// Dismisses and reopens the alerts of one analysis category on the current branch.
    /// </summary>
    /// <param name="results">The analysis results.</param>
    /// <param name="category">The analysis category, such as <c>/language:csharp</c>.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunAsync(List<Finding> results, string category)
    {
        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        string? repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        string? reference = Environment.GetEnvironmentVariable("GITHUB_REF");
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(repository) || string.IsNullOrEmpty(reference))
        {
            Console.Error.WriteLine("Dismissing alerts needs GITHUB_TOKEN, GITHUB_REPOSITORY and GITHUB_REF.");
            return 2;
        }

        Dictionary<string, Finding> reported = [];
        foreach (Finding finding in results)
        {
            reported.TryAdd(Key(finding.Rule, finding.Path, finding.Line, finding.Column), finding);
        }

        using HttpClient client = new()
        {
            BaseAddress = new Uri((Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com").TrimEnd('/') + "/"),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ankus-codeql", "1"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        int dismissed = 0;
        int reopened = 0;
        string[] states = ["open", "dismissed"];
        foreach (string state in states)
        {
            for (int page = 1; ; page++)
            {
                using JsonDocument alerts = await GetAsync(client,
                    $"repos/{repository}/code-scanning/alerts?tool_name=CodeQL&ref={Uri.EscapeDataString(reference)}&state={state}&per_page=100&page={page}");
                foreach (JsonElement alert in alerts.RootElement.EnumerateArray())
                {
                    JsonElement instance = alert.GetProperty("most_recent_instance");
                    JsonElement location = instance.GetProperty("location");
                    if (instance.GetProperty("category").GetString()?.TrimEnd('/') != category.TrimEnd('/') ||
                        !reported.TryGetValue(Key(alert.GetProperty("rule").GetProperty("id").GetString(), location.GetProperty("path").GetString(),
                            location.GetProperty("start_line").GetInt32(), location.GetProperty("start_column").GetInt32()), out Finding? finding))
                    {
                        continue;
                    }

                    int number = alert.GetProperty("number").GetInt32();
                    if (state == "open" && finding.Suppressed)
                    {
                        await UpdateAsync(client, repository, number, "dismissed", Marker + Reason(finding));
                        dismissed++;
                    }
                    else if (state == "dismissed" && !finding.Suppressed &&
                        alert.TryGetProperty("dismissed_comment", out JsonElement comment) && comment.GetString()?.StartsWith(Marker, StringComparison.Ordinal) == true)
                    {
                        await UpdateAsync(client, repository, number, "open", null);
                        reopened++;
                    }
                }

                if (alerts.RootElement.GetArrayLength() < 100)
                {
                    break;
                }
            }
        }

        Console.WriteLine($"Dismissed {dismissed} alert(s) suppressed in source and reopened {reopened} in {category}.");
        return 0;
    }

    private static string Key(string? rule, string? path, int line, int column)
        => $"{rule}\n{path}\n{line}\n{column}";

    /// <summary>
    /// Reads the reason after the <c>codeql[rule]</c> comment on the line before the result.
    /// </summary>
    private static string Reason(Finding finding)
    {
        string[] lines = File.Exists(finding.Path) ? File.ReadAllLines(finding.Path) : [];
        string text = finding.Line >= 2 && finding.Line - 2 < lines.Length ? lines[finding.Line - 2] : "";
        int start = text.IndexOf("codeql[", StringComparison.Ordinal);
        int end = start < 0 ? -1 : text.IndexOf(']', start);
        string reason = end < 0 ? "" : text[(end + 1)..].Trim();
        if (reason.EndsWith("*/", StringComparison.Ordinal))
        {
            reason = reason[..^2].TrimEnd();
        }

        // Code scanning keeps at most 280 characters of a dismissal comment.
        reason = reason.Length == 0 ? "see the comment above the result." : reason;
        return reason.Length > 280 - Marker.Length ? reason[..(280 - Marker.Length - 1)] + "…" : reason;
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path);
        await EnsureSuccessAsync(response);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    private static async Task UpdateAsync(HttpClient client, string repository, int number, string state, string? comment)
    {
        using MemoryStream body = new();
        using (Utf8JsonWriter writer = new(body))
        {
            writer.WriteStartObject();
            writer.WriteString("state", state);
            if (comment is not null)
            {
                writer.WriteString("dismissed_reason", "won't fix");
                writer.WriteString("dismissed_comment", comment);
            }

            writer.WriteEndObject();
        }

        using ByteArrayContent content = new(body.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await client.PatchAsync($"repos/{repository}/code-scanning/alerts/{number}", content);
        await EnsureSuccessAsync(response);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} for {response.RequestMessage?.RequestUri}: {detail}");
        }
    }
}

/// <summary>
/// Leaves results in compiler-generated sources out of an analysis, as .NET analyzers do by default.
/// </summary>
/// <remarks>
/// A traced C# build lets CodeQL see the code that source generators add, so that calls into it resolve, but the
/// results it reports there belong to the generators rather than to the code that uses them. Ankus's own generator is
/// tested through its emitted source and the backend suites; the others ship with the SDK and test frameworks.
/// </remarks>
internal static class GeneratedSources
{
    /// <summary>
    /// Rewrites each SARIF file without the results whose first location is a generated source.
    /// </summary>
    /// <param name="files">The SARIF files.</param>
    /// <returns>The process exit code.</returns>
    internal static int Filter(string[] files)
    {
        Dictionary<string, bool> generated = new(StringComparer.Ordinal);
        foreach (string file in files)
        {
            JsonNode root = JsonNode.Parse(File.ReadAllBytes(file)) ?? throw new InvalidDataException($"{file} is empty.");
            int removed = 0;
            foreach (JsonArray results in (root["runs"]?.AsArray() ?? []).Select(static run => run?["results"]).OfType<JsonArray>())
            {
                for (int index = results.Count - 1; index >= 0; index--)
                {
                    string? path = results[index]?["locations"]?[0]?["physicalLocation"]?["artifactLocation"]?["uri"]?.GetValue<string>();
                    if (path is not null && IsGenerated(path, generated))
                    {
                        results.RemoveAt(index);
                        removed++;
                    }
                }
            }

            using (FileStream stream = File.Create(file))
            using (Utf8JsonWriter writer = new(stream))
            {
                root.WriteTo(writer);
            }

            Console.WriteLine($"Left {removed} result(s) in generated sources out of {Path.GetFileName(file)}.");
        }

        return 0;
    }

    /// <summary>
    /// Applies Roslyn's test for generated code: a generated file name, or an auto-generated comment at the top.
    /// </summary>
    private static bool IsGenerated(string path, Dictionary<string, bool> known)
    {
        if (known.TryGetValue(path, out bool generated))
        {
            return generated;
        }

        string name = Path.GetFileName(path);
        string[] suffixes = [".designer.cs", ".generated.cs", ".g.cs", ".g.i.cs"];
        generated = name.StartsWith("TemporaryGeneratedFile_", StringComparison.OrdinalIgnoreCase) ||
            suffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ||
            File.Exists(path) && File.ReadLines(path).Take(8).Any(static line =>
                line.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("<autogenerated", StringComparison.OrdinalIgnoreCase));
        known.Add(path, generated);
        return generated;
    }
}
