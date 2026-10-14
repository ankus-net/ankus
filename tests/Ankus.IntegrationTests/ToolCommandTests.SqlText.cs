using System.Text.Json;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Exact aliases in the required empty/whitespace installation chain.
    /// </summary>
    private static readonly string[] s_sqlTextDependencyNames =
        ["empty-inline", "inline", "whitespace-inline", "file", "empty-file", "whitespace-file", "default"];

    /// <summary>
    /// Independently expected PostgreSQL UTF-8 bytes for each authored text path.
    /// </summary>
    private static IReadOnlyList<(string Kind, string Hex)> SqlTextExpectedRows()
    {
        string physicalCrlf = OperatingSystem.IsWindows() && PostgresFixture.Cluster.Installation.Version.Major >= 18
            ? "410a42"
            : "410d0a42";
        return
        [
            ("default", physicalCrlf),
            ("enum-cr", "410d42"),
            ("enum-crlf", "410d0a42"),
            ("file-cr", "410d42"),
            ("file-crlf", physicalCrlf),
            ("inline-cr", "410d42"),
            ("inline-crlf", physicalCrlf),
            ("optional", "410d42"),
            ("replacement", physicalCrlf),
        ];
    }

    /// <summary>
    /// One published extension proves empty graph anchors, exact quoted values, generated defaults and enum conversion.
    /// </summary>
    [TestMethod]
    public async Task PublishedSqlPreservesTextValuesAndEmptyAnchors()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = await CreateControlProjectAsync(directory, token, "ankus_sql_text_probe");
        XDocument definition = XDocument.Load(project);
        definition.Root!.Add(new XElement("ItemGroup", new XElement("AdditionalFiles", new XAttribute("Include", "sql/*.sql"))));
        definition.Save(project);
        await File.WriteAllTextAsync(Path.Join(directory, "author settings.control"), "comment = 'Exact SQL text'\nrelocatable = true\n", token);
        string sqlDirectory = Path.Join(directory, "sql");
        Directory.CreateDirectory(sqlDirectory);
        await File.WriteAllTextAsync(Path.Join(sqlDirectory, "values.sql"),
            "INSERT INTO exact_sql_values VALUES ('file-cr', 'A\rB'), ('file-crlf', 'A\r\nB');", token);
        await File.WriteAllTextAsync(Path.Join(sqlDirectory, "empty.sql"), string.Empty, token);
        await File.WriteAllTextAsync(Path.Join(sqlDirectory, "whitespace.sql"), " \t", token);
        string inline = "CREATE TABLE exact_sql_values(kind text PRIMARY KEY, value text);\n" +
            "INSERT INTO exact_sql_values VALUES ('inline-cr', 'A\rB'), ('inline-crlf', 'A\r\nB');";
        string replacement = "CREATE FUNCTION literal_value() RETURNS text LANGUAGE sql IMMUTABLE AS $body$SELECT 'A\r\nB'$body$;";
        string source = $$"""
            using Ankus;
            [assembly: PgSql("empty-inline", "", Before = new[] { "inline" }, Relocatable = true)]
            [assembly: PgSql("inline", {{JsonSerializer.Serialize(inline)}}, Relocatable = true)]
            [assembly: PgSql("whitespace-inline", " \t", Requires = new[] { "inline" }, Before = new[] { "file" }, Relocatable = true)]
            [assembly: PgSqlFile("file", "sql/values.sql", Relocatable = true)]
            [assembly: PgSqlFile("empty-file", "sql/empty.sql", Requires = new[] { "file" }, Before = new[] { "whitespace-file" }, Relocatable = true)]
            [assembly: PgSqlFile("whitespace-file", "sql/whitespace.sql", Before = new[] { "default" }, Relocatable = true)]
            [PgEnum]
            public enum LineLabel
            {
                [PgEnumLabel("A\rB")] Cr,
                [PgEnumLabel("A\r\nB")] CrLf
            }
            public static class Functions
            {
                [PgFunction(Id = "default")]
                public static string DefaultValue([PgParameter(Default = "'A\r\nB'")] string value) => value;
                [PgFunction]
                public static string OptionalValue(string value = "A\rB") => value;
                [PgFunction]
                public static LineLabel Echo(LineLabel value) => value;
                [PgFunction(Sql = {{JsonSerializer.Serialize(replacement)}}, SqlRelocatable = true)]
                public static string LiteralValue() => "managed";
            }
            """;
        await File.WriteAllTextAsync(Path.Join(directory, "Functions.cs"), source, token);
        string output = Path.Join(directory, "published");
        PublishedExtension publication = await PublishControlProbeAsync(project, output, token);
        ExtensionSchema schema = ExtensionSchema.Read(Path.Join(output, publication.Library));
        Assert.IsTrue(schema.Relocatable);
        Assert.IsNotNull(schema.Graph);
        ExtensionSchemaItem[] chain = [.. s_sqlTextDependencyNames
            .Select(name => schema.Graph.Items.Single(item => item.Names.Contains(name, StringComparer.Ordinal)))];
        Assert.AreEqual(string.Empty, chain[0].Sql);
        Assert.AreEqual(string.Empty, chain[4].Sql);
        for (int index = 1; index < chain.Length; index++)
        {
            Assert.Contains(chain[index - 1].Id, chain[index].Dependencies);
        }

        Assert.Contains("'A\rB'", schema.Sql);
        Assert.Contains("'A\r\nB'", schema.Sql);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_sql_text_probe");
        await using var command = new NpgsqlCommand("""
            WITH actual(kind, value) AS (
                SELECT kind, value FROM exact_sql_values
                UNION ALL SELECT 'default', default_value()
                UNION ALL SELECT 'optional', optional_value()
                UNION ALL SELECT 'replacement', literal_value()
                UNION ALL SELECT 'enum-cr', echo(E'A\rB'::line_label)::text
                UNION ALL SELECT 'enum-crlf', echo(E'A\r\nB'::line_label)::text)
            SELECT kind, encode(convert_to(value, 'UTF8'), 'hex') FROM actual ORDER BY kind
            """, connection);
        List<(string Kind, string Hex)> values = [];
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                values.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        Assert.AreSequenceEqual(SqlTextExpectedRows(), values);
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection,
            "SELECT default_value(NULL) IS NULL AND optional_value(NULL) IS NULL AND echo(NULL) IS NULL"));
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "SELECT echo(E'A\nB'::line_label)"));
        Assert.AreEqual("22P02", error.SqlState);
        Assert.AreEqual("410d0a42", await SqlPackageScalarAsync<string>(connection,
            "SELECT encode(convert_to(echo(E'A\\r\\nB'::line_label)::text, 'UTF8'), 'hex')"));
        Assert.AreEqual(backend, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
    }
}
