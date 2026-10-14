using System.Text.Json;

if (args is not [string directory] || directory.StartsWith('-'))
{
    Console.Error.WriteLine("usage: Ankus.CodeQl.cs <sarif-directory>");
    return 2;
}

// Code scanning lists findings in the Security tab without failing a check, so a new one would go unseen. This reads the
// result files the scan uploads and fails for every finding in them.
string[] files = Directory.Exists(directory)
    ? [.. Directory.EnumerateFiles(directory, "*.sarif", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
    : [];
if (files.Length == 0)
{
    Console.Error.WriteLine($"CodeQL left no SARIF file under {Path.GetFullPath(directory)}.");
    return 1;
}

List<string> findings = [];
List<string> suppressed = [];
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
        if (!run.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
        {
            continue;
        }

        foreach (JsonElement result in results.EnumerateArray())
        {
            string message = result.TryGetProperty("message", out JsonElement body) ? Text(body, "text", "") : "";
            string finding = $"{Location(result)}: {Text(result, "level", "warning")} {Text(result, "ruleId", "unknown rule")}: {message}";
            (Suppressed(result) ? suppressed : findings).Add(finding);
        }
    }
}

foreach (string finding in findings.Order(StringComparer.Ordinal))
{
    Console.Error.WriteLine(finding);
}

// A `// codeql[rule] reason` comment marks a finding the code must keep, such as a catch that reports every exception at
// the native boundary. These are listed so each one stays visible, and do not fail the check.
foreach (string finding in suppressed.Order(StringComparer.Ordinal))
{
    Console.WriteLine("Suppressed in source: " + finding);
}

Console.WriteLine($"CodeQL reported {findings.Count} finding(s) and {suppressed.Count} suppressed in source in {files.Length} result file(s).");
return findings.Count == 0 ? 0 : 1;

static bool Suppressed(JsonElement result)
    => result.TryGetProperty("suppressions", out JsonElement suppressions) && suppressions.ValueKind == JsonValueKind.Array &&
        suppressions.EnumerateArray().Any(static suppression => !suppression.TryGetProperty("status", out JsonElement status) ||
            status.GetString() is not "rejected");

static string Text(JsonElement element, string name, string fallback)
    => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

static string Location(JsonElement result)
{
    if (!result.TryGetProperty("locations", out JsonElement locations) || locations.ValueKind != JsonValueKind.Array
        || locations.GetArrayLength() == 0 || !locations[0].TryGetProperty("physicalLocation", out JsonElement place))
    {
        return "unknown location";
    }

    string path = place.TryGetProperty("artifactLocation", out JsonElement artifact) ? Text(artifact, "uri", "unknown location") : "unknown location";
    return place.TryGetProperty("region", out JsonElement region) && region.TryGetProperty("startLine", out JsonElement line)
        && line.TryGetInt32(out int number) ? $"{path}:{number}" : path;
}
