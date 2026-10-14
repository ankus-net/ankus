using System.Text.Json;
using System.Text.RegularExpressions;
using Ankus.Tool;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies scaffolded SQL names an extension exactly as PostgreSQL parses identifiers.
/// </summary>
/// <param name="context">The active test's cancellation context.</param>
[TestClass]
public sealed class ProjectScaffolderTests(TestContext context)
{
    /// <summary>
    /// Identifiers follow quote_identifier: only lowercase names that are not reserved, column-name or
    /// type-or-function-name keywords on a supported major remain bare.
    /// </summary>
    /// <param name="identifier">The catalog identifier.</param>
    /// <param name="expected">The SQL spelling PostgreSQL's quote_identifier produces.</param>
    [TestMethod]
    [DataRow("acme_regress_probe", "acme_regress_probe")]
    [DataRow("_1_ext", "_1_ext")]
    [DataRow("users", "users")]
    [DataRow("abort", "abort")]
    [DataRow("json_exists_probe", "json_exists_probe")]
    [DataRow("user", "\"user\"")]
    [DataRow("select", "\"select\"")]
    [DataRow("int", "\"int\"")]
    [DataRow("left", "\"left\"")]
    [DataRow("system_user", "\"system_user\"")]
    [DataRow("json_table", "\"json_table\"")]
    [DataRow("merge_action", "\"merge_action\"")]
    [DataRow("Acme", "\"Acme\"")]
    [DataRow("1ext", "\"1ext\"")]
    [DataRow("café", "\"café\"")]
    [DataRow("a\"b", "\"a\"\"b\"")]
    [DataRow("", "\"\"")]
    public void QuoteIdentifierMatchesPostgres(string identifier, string expected)
        => Assert.AreEqual(expected, ProjectScaffolder.QuoteIdentifier(identifier));

    /// <summary>
    /// A derived or explicit extension name that is a SQL keyword is quoted in every scaffolded SQL statement,
    /// while project metadata keeps the exact extension name.
    /// </summary>
    /// <param name="name">The project name passed to ankus new.</param>
    /// <param name="extension">The explicit extension name, or null to derive it.</param>
    /// <param name="worker">Whether to scaffold a background worker.</param>
    /// <param name="expected">The extension's SQL identifier.</param>
    [TestMethod]
    [DataRow("User", null, false, "\"user\"")]
    [DataRow("User", null, true, "\"user\"")]
    [DataRow("Probe", "select", false, "\"select\"")]
    [DataRow("Acme.RegressProbe", null, false, "acme_regress_probe")]
    public async Task ScaffoldedSqlQuotesKeywordExtensionNames(string name, string? extension, bool worker, string expected)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-scaffold-").FullName;
        try
        {
            string destination = await ProjectScaffolder.CreateAsync(name, Path.Join(directory, "solution"), extension, worker, "mstest",
                context.CancellationToken);
            string project = Path.Join(destination, "src", name);
            string catalogName = extension ?? expected.Trim('"');
            string setup = "-- This setup file runs when the regression database is created or recreated.\n" +
                "-- Create the extension before running the ordinary SQL tests.\nCREATE EXTENSION " + expected + ";\n";

            // Scaffolding preserves each template's checked-out line endings, which are CRLF in a Windows checkout.
            Assert.AreEqual(setup, (await File.ReadAllTextAsync(Path.Join(project, "pg_regress", "sql", "setup.sql"),
                context.CancellationToken)).ReplaceLineEndings("\n"));
            Assert.AreEqual(setup, (await File.ReadAllTextAsync(Path.Join(project, "pg_regress", "expected", "setup.out"),
                context.CancellationToken)).ReplaceLineEndings("\n"));
            Assert.Contains("\nCREATE EXTENSION " + expected + ";\n",
                (await File.ReadAllTextAsync(Path.Join(destination, "README.md"), context.CancellationToken)).ReplaceLineEndings("\n"));
            string projectFile = await File.ReadAllTextAsync(Path.Join(project, name + ".csproj"), context.CancellationToken);
            Assert.Contains("<AnkusExtensionName>" + catalogName + "</AnkusExtensionName>", projectFile);
            Assert.Contains("<AssemblyName>" + catalogName + "</AssemblyName>", projectFile);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The dotnet new template quotes exactly the same keywords as ankus new, using the same generated extension name.
    /// </summary>
    [TestMethod]
    public void TemplateIdentifierSymbolQuotesTheSameKeywords()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Join(AppContext.BaseDirectory, "TemplateConfiguration", "template.json")));
        JsonElement symbol = document.RootElement.GetProperty("symbols").GetProperty("extensionIdentifier");
        JsonElement parameters = symbol.GetProperty("parameters");
        JsonElement step = Assert.ContainsSingle(parameters.GetProperty("steps").EnumerateArray());
        string pattern = step.GetProperty("regex").GetString()!;
        string replacement = step.GetProperty("replacement").GetString()!;

        Assert.AreEqual("__EXTENSION_IDENTIFIER__", symbol.GetProperty("replaces").GetString());
        Assert.AreEqual("extensionName", parameters.GetProperty("source").GetString());
        Assert.StartsWith("^(", pattern);
        Assert.EndsWith(")$", pattern);
        string[] alternatives = pattern[2..^2].Split('|');
        Assert.HasCount(alternatives.Length, alternatives.Distinct(StringComparer.Ordinal));
        Assert.IsTrue(ProjectScaffolder.PostgresKeywords.SetEquals(alternatives));
        foreach (string identifier in ProjectScaffolder.PostgresKeywords.Concat(["users", "abort", "_user", "user_", "json_tables"]))
        {
            Assert.AreEqual(ProjectScaffolder.QuoteIdentifier(identifier), Regex.Replace(identifier, pattern, replacement), identifier);
        }
    }
}
