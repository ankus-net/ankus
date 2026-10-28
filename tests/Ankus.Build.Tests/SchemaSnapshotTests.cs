using System.Text.Json;
using System.Text.Json.Nodes;
using Ankus.PgConfig;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Ankus.Build.Tests;

/// <summary>
/// Verifies retained schema identity, graph agreement and replacement through real metadata and files.
/// </summary>
/// <param name="context">The active test context.</param>
[TestClass]
public sealed class SchemaSnapshotTests(TestContext context)
{
    private const string Graph = "QU5LVVNHMQABAAAABAAAAG5vZGUDAAAAc3FsCwAAAFNFTEVDVCA0MjsKAAAAAAEAAAAFAAAAcHJvYmUAAAAAAAAAAA==";
    private readonly string _root = Directory.CreateTempSubdirectory("ankus schema snapshot ").FullName;

    /// <summary>
    /// Removes only this test's owned files.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Reuse keeps SQL, graph and relocation policy while a new compilation supplies different SQL metadata.
    /// </summary>
    /// <param name="withGraph">Whether the generator supplies a graph or legacy SQL-only metadata.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReuseRetainsExactSqlGraphAndRelocation(bool withGraph)
    {
        string sql = withGraph ? "SELECT 42;\n" : "SELECT 'café 🐘', 'MODULE_PATHNAME';\r\n";
        string path = Path.Join(_root, "schema.json");
        ExtensionManifest first = Compile(sql, withGraph ? Graph : null, relocatable: false);
        Select(null, first).Write(path);
        byte[] before = File.ReadAllBytes(path);
        ExtensionManifest changed = Compile("SELECT 99;\n", null, relocatable: true);
        SchemaSnapshot reused = Select(path, changed);
        Assert.AreEqual(sql, reused.Sql);
        Assert.AreEqual(withGraph ? Graph : null, reused.Graph);
        Assert.IsFalse(reused.Relocatable);
        if (reused.Graph is not null)
        {
            ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(reused.Graph);
            Assert.AreEqual("node", Assert.ContainsSingle(graph.Items).Id);
            Assert.AreEqual("SELECT 42;\n", graph.Sql);
        }

        Assert.AreSequenceEqual(before, File.ReadAllBytes(path));
        SchemaSnapshot regenerated = Select(null, changed);
        Assert.AreEqual("SELECT 99;\n", regenerated.Sql);
        Assert.IsNull(regenerated.Graph);
        Assert.IsTrue(regenerated.Relocatable);
    }

    /// <summary>
    /// Every target identity component and both native wrapper inputs prevent incompatible reuse.
    /// </summary>
    /// <param name="property">The changed snapshot field.</param>
    [TestMethod]
    [DataRow("name")]
    [DataRow("version")]
    [DataRow("library")]
    [DataRow("postgresMajor")]
    [DataRow("runtimeIdentifier")]
    [DataRow("formatVersion")]
    [DataRow("nativeSource")]
    [DataRow("exports")]
    public void ReuseRejectsChangedIdentityAndNativeContract(string property)
    {
        ExtensionManifest manifest = Compile("SELECT 42;\n", Graph);
        string path = Path.Join(_root, "schema.json");
        Select(null, manifest).Write(path);
        if (property is "nativeSource" or "exports")
        {
            manifest = Compile("SELECT 42;\n", Graph,
                nativeSource: property == "nativeSource" ? "changed wrapper" : "stable wrapper",
                exports: property == "exports" ? "changed export" : "stable_export");
        }
        else
        {
            JsonNode document = JsonNode.Parse(File.ReadAllText(path))!;
            document[property] = property is "postgresMajor" or "formatVersion" ? JsonValue.Create(19) : JsonValue.Create("different");
            File.WriteAllText(path, document.ToJsonString());
        }

        byte[] before = File.ReadAllBytes(path);
        FormatException error = Assert.ThrowsExactly<FormatException>(() => Select(path, manifest));
        Assert.Contains(property is "nativeSource" or "exports" ? "Native declarations changed" : "different extension or build target", error.Message);
        Assert.AreSequenceEqual(before, File.ReadAllBytes(path));
    }

    /// <summary>
    /// Corrupt or incomplete snapshots fail explicitly rather than silently regenerating declarations.
    /// </summary>
    /// <param name="partition">The invalid serialized shape.</param>
    [TestMethod]
    [DataRow("empty")]
    [DataRow("oversized")]
    [DataRow("json")]
    [DataRow("array")]
    [DataRow("duplicate")]
    [DataRow("missing")]
    [DataRow("type")]
    [DataRow("null-sql")]
    [DataRow("empty-sql")]
    [DataRow("nul-sql")]
    [DataRow("graph")]
    [DataRow("graph-sql")]
    public void ReuseRejectsInvalidSnapshot(string partition)
    {
        ExtensionManifest manifest = Compile("SELECT 42;\n", Graph);
        string path = Path.Join(_root, "schema.json");
        Select(null, manifest).Write(path);
        JsonNode document = JsonNode.Parse(File.ReadAllText(path))!;
        switch (partition)
        {
            case "empty":
                File.WriteAllText(path, "");
                break;
            case "oversized":
                using (FileStream file = File.OpenWrite(path))
                {
                    file.SetLength(128L * 1024 * 1024 + 1);
                }

                break;
            case "json":
                File.WriteAllText(path, "{");
                break;
            case "array":
                File.WriteAllText(path, "[]");
                break;
            case "duplicate":
                File.WriteAllText(path, File.ReadAllText(path).Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1", StringComparison.Ordinal));
                break;
            default:
                switch (partition)
                {
                    case "missing":
                        document.AsObject().Remove("sql");
                        break;
                    case "type":
                        document["relocatable"] = "false";
                        break;
                    case "null-sql":
                        document["sql"] = null;
                        break;
                    case "empty-sql":
                        document["sql"] = "";
                        break;
                    case "nul-sql":
                        document["sql"] = "SELECT '\0';";
                        break;
                    case "graph":
                        document["graph"] = "not base64";
                        break;
                    default:
                        document["sql"] = "SELECT 99;\n";
                        break;
                }

                File.WriteAllText(path, document.ToJsonString());
                break;
        }

        Assert.ThrowsExactly<FormatException>(() => Select(path, manifest));
    }

    /// <summary>
    /// Missing saved input and failed replacement preserve the last successful snapshot and leave no staging files.
    /// </summary>
    [TestMethod]
    public void MissingInputAndFailedCommitPreserveSuccessfulSnapshot()
    {
        ExtensionManifest manifest = Compile("SELECT 42;\n", Graph);
        string path = Path.Join(_root, "schema.json");
        FileNotFoundException missing = Assert.ThrowsExactly<FileNotFoundException>(() => Select(path, manifest));
        Assert.Contains("Run without schema reuse first", missing.Message);
        Select(null, manifest).Write(path);
        byte[] before = File.ReadAllBytes(path);
        Assert.ThrowsExactly<FileNotFoundException>(() => SchemaSnapshot.Commit(Path.Join(_root, "missing"), path));
        Assert.AreSequenceEqual(before, File.ReadAllBytes(path));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp"));
        string prepared = Path.Join(_root, "prepared.json");
        Select(null, Compile("SELECT 99;\n", null)).Write(prepared);
        SchemaSnapshot.Commit(prepared, path);
        Assert.AreEqual("SELECT 99;\n", Select(path, manifest).Sql);
        Assert.AreSequenceEqual(File.ReadAllBytes(prepared), File.ReadAllBytes(path));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp"));
    }

    /// <summary>
    /// A versioned library resolves every module marker in the script and its graph together, preserving schema markers.
    /// </summary>
    [TestMethod]
    public void VersionedModulePathResolvesScriptAndGraphTogether()
    {
        const string Preamble = "-- MODULE_PATHNAME preamble\n";
        const string Declaration = "CREATE FUNCTION \0probe() RETURNS integer AS 'MODULE_PATHNAME', 'probe' LANGUAGE c;\n";
        string sql = Preamble + Declaration.Replace("\0", "", StringComparison.Ordinal);
        string graph = EncodeGraph(Preamble, Declaration, "FUNCTION \0probe()");
        Assert.AreEqual(sql, ExtensionSchemaGraph.Parse(graph).Sql);

        SchemaSnapshot versioned = Select(null, Compile(sql, graph)).WithModulePath("Probe-0.1.0");
        string expected = sql.Replace("MODULE_PATHNAME", "Probe-0.1.0", StringComparison.Ordinal);
        Assert.AreEqual(expected, versioned.Sql);
        ExtensionSchemaGraph parsed = ExtensionSchemaGraph.Parse(versioned.Graph!);
        Assert.AreEqual(expected, parsed.Sql);
        ExtensionSchemaItem item = Assert.ContainsSingle(parsed.Items);
        Assert.AreEqual(Declaration.Replace("MODULE_PATHNAME", "Probe-0.1.0", StringComparison.Ordinal), item.SqlTemplate);
        Assert.AreEqual("FUNCTION \0probe()", Assert.ContainsSingle(item.AttachmentTemplates));

        SchemaSnapshot repeated = versioned.WithModulePath("Probe-0.1.0");
        Assert.AreEqual(versioned.Sql, repeated.Sql);
        Assert.AreEqual(versioned.Graph, repeated.Graph);
        Assert.AreEqual(expected, Select(null, Compile(sql, null)).WithModulePath("Probe-0.1.0").Sql);
    }

    private static string EncodeGraph(string preamble, string sql, string attachment)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write("ANKUSG2\0"u8);
            Text(preamble);
            writer.Write(1);
            Text("node");
            Text("function");
            Text(sql);
            Text("");
            writer.Write(0);
            writer.Write(0);
            writer.Write(1);
            Text(attachment);

            void Text(string value)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }

        return Convert.ToBase64String(stream.ToArray());
    }

    private static SchemaSnapshot Select(string? path, ExtensionManifest manifest)
        => SchemaSnapshot.Select(path, manifest, "schema_probe", "0.1.0", "Probe.so", 18, "linux-x64");

    private ExtensionManifest Compile(string sql, string? graph, bool relocatable = true,
        string nativeSource = "stable wrapper", string exports = "stable_export")
    {
        string graphAttribute = graph is null ? "" : "[assembly: AssemblyMetadata(\"Ankus.SqlGraph\", " + JsonSerializer.Serialize(graph) + ")]";
        string source = $$"""
            using System.Reflection;
            [assembly: AssemblyMetadata("Ankus.NativeSource", {{JsonSerializer.Serialize(nativeSource)}})]
            [assembly: AssemblyMetadata("Ankus.Sql", {{JsonSerializer.Serialize(sql)}})]
            [assembly: AssemblyMetadata("Ankus.Exports", {{JsonSerializer.Serialize(exports)}})]
            [assembly: AssemblyMetadata("Ankus.Relocatable", "{{(relocatable ? "true" : "false")}}")]
            {{graphAttribute}}
            """;
        CSharpCompilation compilation = CSharpCompilation.Create("SchemaMetadataProbe",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: context.CancellationToken)],
            CompilationReferences.Platform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        string assembly = Path.Join(_root, Guid.NewGuid().ToString("N") + ".dll");
        using (FileStream stream = File.Create(assembly))
        {
            EmitResult emitted = compilation.Emit(stream, cancellationToken: context.CancellationToken);
            Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        }

        return ExtensionManifest.Read(assembly);
    }
}
