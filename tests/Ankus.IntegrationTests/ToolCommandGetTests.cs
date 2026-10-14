using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Published properties retain exact parsed values without requiring native files, PostgreSQL registration or mutation.
    /// </summary>
    [TestMethod]
    public async Task GetPublishedControlPreservesExactValues()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string extension = Directory.CreateDirectory(Path.Join(directory, "extension")).FullName;
        PublishedExtension original = PublishedExtension.Read(s_published);
        original.Write(directory);
        string control = Path.Join(extension, original.Control);
        const string Text = "default_version='0.1.0'\ncomment='old'\ncomment='  exact = ''quote'' # value  '\nrequires=''\nrelocatable=true\n";
        await File.WriteAllTextAsync(control, Text, token);
        string home = Path.Join(directory, "unregistered home");
        (string Property, string? Expected)[] cases =
        [
            ("extname", "ankus_tool_probe"), ("default_version", "0.1.0"),
            ("comment", "  exact = 'quote' # value  "), ("requires", ""),
            ("relocatable", "true"), ("missing", null), ("COMMENT", null),
        ];
        foreach ((string property, string? expected) in cases)
        {
            ProcessResult result = await InvokeAsync(["get", property, "--from", directory, "--home", home], token);
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            Assert.AreEqual(expected is null ? "" : expected + Environment.NewLine, result.StandardOutput, property);
            Assert.IsEmpty(result.StandardError, property);
        }

        Assert.AreEqual(Text, await File.ReadAllTextAsync(control, token));
        Assert.IsFalse(Directory.Exists(home));
        Assert.HasCount(2, Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Imported configuration and author settings agree across managed queries, native publication and PostgreSQL catalogs.
    /// </summary>
    [TestMethod]
    public async Task GetProjectControlMatchesPublicationAndPostgres()
    {
        CancellationToken token = context.CancellationToken;
        const string Configuration = "Query+Checked";
        const string Comment = "query = exact 'comment'";
        string directory = CreateDirectory();
        string project = await CreateControlProjectAsync(directory, token, "ankus_query_probe");
        XDocument definition = XDocument.Load(project);
        definition.Descendants("AnkusExtensionVersion").Remove();
        definition.Root!.Add(new XElement("Import", new XAttribute("Project", "query.props")));
        var guard = new XElement("Target", new XAttribute("Name", "RejectNativeQuery"),
            new XAttribute("BeforeTargets", "IlcCompile;LinkNative"),
            new XElement("Error", new XAttribute("Text", "Metadata queries must not publish native code.")));
        definition.Root.Add(guard);
        definition.Save(project);
        new XDocument(new XElement("Project", new XElement("PropertyGroup",
            new XAttribute("Condition", "'$(Configuration)' == 'Query+Checked'"),
            new XElement("AssemblyName", "Ankus.Control.Query"),
            new XElement("AnkusExtensionName", "ankus_query_configured"),
            new XElement("Version", "3.2.1")))).Save(Path.Join(directory, "query.props"));
        string author = Path.Join(directory, "author settings.control");
        await File.WriteAllTextAsync(author, "comment='query = exact ''comment'''\nschema='Query Schema'\nrequires=''", token);
        string[] options = ["--home", s_home, "--pg", MajorText(), "--project", project, "-c", Configuration];
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["extname"] = "ankus_query_configured",
            ["default_version"] = "3.2.1",
            ["module_pathname"] = "Ankus.Control.Query" + Path.GetExtension(PublishedExtension.Read(s_published).Library),
            ["relocatable"] = "false",
            ["comment"] = Comment,
            ["requires"] = "",
        };
        foreach ((string property, string value) in expected)
        {
            ProcessResult query = await InvokeAsync(["get", property, .. options], token);
            Assert.AreEqual(0, query.ExitCode, query.StandardOutput + query.StandardError);
            Assert.AreEqual(value + Environment.NewLine, query.StandardOutput, property);
        }

        Assert.IsEmpty(Directory.GetFiles(directory, PublishedExtension.FileName, SearchOption.AllDirectories));
        Assert.IsFalse(Directory.Exists(Path.Join(directory, "bin", "ankus")));
        guard.Remove();
        definition.Save(project);
        string output = CreateDirectory();
        ProcessResult published = await InvokeAsync(["publish", .. options, "--output", output], token);
        Assert.AreEqual(0, published.ExitCode, published.StandardOutput + published.StandardError);
        PublishedExtension manifest = PublishedExtension.Read(output);
        IReadOnlyDictionary<string, string> control = ExtensionControlFile.Read(Path.Join(output, "extension", manifest.Control));
        foreach ((string property, string value) in expected.Where(static item => item.Key != "extname"))
        {
            Assert.AreEqual(value, control[property], property);
        }

        Assert.AreEqual(expected["extname"] + ".control", manifest.Control);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_query_configured");
        Assert.AreEqual("3.2.1|false|Query Schema", await SqlPackageScalarAsync<string>(connection,
            "SELECT e.extversion || '|' || e.extrelocatable::text || '|' || n.nspname FROM pg_extension e " +
            "JOIN pg_namespace n ON n.oid=e.extnamespace WHERE e.extname='ankus_query_configured'"));
        Assert.AreEqual(Comment, await SqlPackageScalarAsync<string>(connection,
            "SELECT obj_description(oid, 'pg_extension') FROM pg_extension WHERE extname='ankus_query_configured'"));
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, "SELECT \"Query Schema\".control_value()"));
    }

    /// <summary>
    /// Source and author edits invalidate query results, and build errors cannot reuse an earlier control file.
    /// </summary>
    [TestMethod]
    public async Task GetProjectControlTracksSourceChangesAndBuildErrors()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = await CreateControlProjectAsync(directory, token, "ankus_query_edits");
        string authored = Path.Join(directory, "author settings.control");
        await File.WriteAllTextAsync(authored, "comment='first'", token);
        string[] options = ["--home", s_home, "--pg", MajorText(), "--project", project];
        ProcessResult original = await InvokeAsync(["get", "relocatable", .. options], token);
        Assert.AreEqual(0, original.ExitCode, original.StandardError);
        Assert.AreEqual("true" + Environment.NewLine, original.StandardOutput);
        string source = Path.Join(directory, "Functions.cs");
        string text = await File.ReadAllTextAsync(source, token);
        await File.WriteAllTextAsync(source, text.Replace("public static class Functions", "[PgSchema(\"query_fixed\")]\npublic static class Functions", StringComparison.Ordinal), token);
        ProcessResult changed = await InvokeAsync(["get", "relocatable", .. options], token);
        Assert.AreEqual(0, changed.ExitCode, changed.StandardError);
        Assert.AreEqual("false" + Environment.NewLine, changed.StandardOutput);
        await File.WriteAllTextAsync(authored, "relocatable=true", token);
        ProcessResult invalid = await InvokeAsync(["get", "relocatable", .. options], token);
        Assert.AreEqual(1, invalid.ExitCode);
        Assert.IsEmpty(invalid.StandardOutput);
        Assert.Contains("relocatable", invalid.StandardError);
        await File.WriteAllTextAsync(authored, "comment='recovered'", token);
        ProcessResult recovered = await InvokeAsync(["get", "comment", .. options], token);
        Assert.AreEqual(0, recovered.ExitCode, recovered.StandardError);
        Assert.AreEqual("recovered" + Environment.NewLine, recovered.StandardOutput);
        ProcessResult absent = await InvokeAsync(["get", "unknown_property", .. options], token);
        Assert.AreEqual(0, absent.ExitCode, absent.StandardError);
        Assert.IsEmpty(absent.StandardOutput);
        await File.WriteAllTextAsync(source, "invalid C# source", token);
        ProcessResult broken = await InvokeAsync(["get", "comment", .. options], token);
        Assert.AreEqual(1, broken.ExitCode);
        Assert.IsEmpty(broken.StandardOutput);
        Assert.Contains("error CS", broken.StandardError);
        Assert.IsEmpty(Directory.GetFiles(directory, PublishedExtension.FileName, SearchOption.AllDirectories));
    }

    /// <summary>
    /// Identity evaluation honors the selected project and solution while avoiding compilation and PostgreSQL discovery.
    /// </summary>
    [TestMethod]
    public async Task GetIdentityResolvesProjectsWithoutCompilation()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string projectDirectory = Directory.CreateDirectory(Path.Join(directory, "src")).FullName;
        string project = await CreateControlProjectAsync(projectDirectory, token);
        XDocument definition = XDocument.Load(project);
        definition.Descendants("AnkusExtensionName").Remove();
        definition.Root!.Add(new XElement("PropertyGroup", new XElement("AssemblyName", "Query.Identity.With.Dots")));
        definition.Save(project);
        await File.WriteAllTextAsync(Path.Join(projectDirectory, "Functions.cs"), "invalid C# source", token);
        string home = Path.Join(directory, "unregistered");
        ProcessResult explicitProject = await InvokeAsync(["get", "extname", "--project", project, "--home", home], token);
        Assert.AreEqual(0, explicitProject.ExitCode, explicitProject.StandardError);
        Assert.AreEqual("query_identity_with_dots" + Environment.NewLine, explicitProject.StandardOutput);
        Assert.IsEmpty(explicitProject.StandardError);
        new XDocument(new XElement("Solution", new XElement("Project", new XAttribute("Path", "src/ControlProbe.csproj"))))
            .Save(Path.Join(directory, "Query.slnx"));
        ProcessResult solution = await PackageProcessRunner.RunAsync(s_tool, ["get", "extname", "--home", home],
            s_environment, token, workingDirectory: directory);
        Assert.AreEqual(0, solution.ExitCode, solution.StandardError);
        Assert.AreEqual(explicitProject.StandardOutput, solution.StandardOutput);
        File.Copy(project, Path.Join(projectDirectory, "Other.csproj"));
        ProcessResult ambiguous = await InvokeAsync(["get", "extname", "--project", projectDirectory, "--home", home], token);
        Assert.AreNotEqual(0, ambiguous.ExitCode);
        Assert.IsEmpty(ambiguous.StandardOutput);
        Assert.Contains("Specify --project", ambiguous.StandardError);
        Assert.IsFalse(Directory.Exists(Path.Join(projectDirectory, "obj")));
        Assert.IsFalse(Directory.Exists(home));
    }

    /// <summary>
    /// Git queries use the selected project repository, preserve Git errors and track later commits without compiling.
    /// </summary>
    [TestMethod]
    public async Task GetGitHashUsesSelectedProjectRepository()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "Query.csproj");
        await File.WriteAllTextAsync(project, "<Project />", token);
        string[] query = ["get", "git_hash", "--project", project, "--home", Path.Join(directory, "no registration")];
        try
        {
            ProcessResult missingGit = await PackageProcessRunner.RunAsync("git", ["-C", directory, "rev-parse", "--verify", "HEAD"], s_environment, token);
            ProcessResult missing = await InvokeAsync(query, token);
            Assert.AreNotEqual(0, missing.ExitCode);
            Assert.AreEqual(missingGit.ExitCode, missing.ExitCode);
            Assert.AreEqual(missingGit.StandardError, missing.StandardError);
            Assert.IsEmpty(missing.StandardOutput);
            (await PackageProcessRunner.RunAsync("git", ["init", "--quiet", directory], s_environment, token)).EnsureSuccess("git", ["init"]);
            ProcessResult unbornGit = await PackageProcessRunner.RunAsync("git", ["-C", directory, "rev-parse", "--verify", "HEAD"], s_environment, token);
            ProcessResult unborn = await InvokeAsync(query, token);
            Assert.AreNotEqual(0, unborn.ExitCode);
            Assert.AreEqual(unbornGit.ExitCode, unborn.ExitCode);
            Assert.AreEqual(unbornGit.StandardError, unborn.StandardError);
            Assert.IsEmpty(unborn.StandardOutput);
            string? previous = null;
            foreach (string message in new[] { "first", "second" })
            {
                (await PackageProcessRunner.RunAsync("git", ["-C", directory, "-c", "user.name=Ankus Tests",
                    "-c", "user.email=tests@example.invalid", "-c", "commit.gpgsign=false", "commit", "--allow-empty", "--quiet", "-m", message],
                    s_environment, token)).EnsureSuccess("git", ["commit"]);
                ProcessResult expected = await PackageProcessRunner.RunAsync("git", ["-C", directory, "rev-parse", "HEAD"], s_environment, token);
                expected.EnsureSuccess("git", ["rev-parse"]);
                ProcessResult actual = await InvokeAsync(query, token);
                Assert.AreEqual(0, actual.ExitCode, actual.StandardError);
                Assert.AreEqual(expected.StandardOutput, actual.StandardOutput);
                Assert.AreNotEqual(previous, actual.StandardOutput);
                Assert.IsEmpty(actual.StandardError);
                previous = actual.StandardOutput;
            }

            Assert.IsFalse(Directory.Exists(Path.Join(directory, "obj")));
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }
        }
    }

    /// <summary>
    /// Conflicting selectors and malformed metadata fail without printing a property or mutating the publication.
    /// </summary>
    /// <param name="failure">The invalid query partition.</param>
    [TestMethod]
    [DataRow("project")]
    [DataRow("configuration")]
    [DataRow("pg")]
    [DataRow("pg-config")]
    [DataRow("git_hash")]
    [DataRow("manifest")]
    [DataRow("control")]
    [DataRow("missing-control")]
    public async Task GetRejectsConflictsAndMalformedMetadata(string failure)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        PublishedExtension original = PublishedExtension.Read(s_published);
        original.Write(directory);
        string control = Path.Join(Directory.CreateDirectory(Path.Join(directory, "extension")).FullName, original.Control);
        await File.WriteAllTextAsync(control, "comment='retained'", token);
        List<string> arguments = ["get", failure == "git_hash" ? "git_hash" : "comment", "--from", directory];
        switch (failure)
        {
            case "project":
            case "configuration":
            case "pg-config":
                arguments.AddRange(["--" + failure, "unused"]);
                break;
            case "pg":
                arguments.AddRange(["--pg", MajorText()]);
                break;
            case "manifest":
                await File.WriteAllTextAsync(Path.Join(directory, PublishedExtension.FileName), "invalid manifest", token);
                break;
            case "control":
                await File.WriteAllTextAsync(control, "comment='unterminated", token);
                break;
            case "missing-control":
                File.Delete(control);
                break;
        }

        byte[]? before = File.Exists(control) ? await File.ReadAllBytesAsync(control, token) : null;
        ProcessResult result = await InvokeAsync([.. arguments], token);
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsEmpty(result.StandardOutput);
        Assert.IsNotEmpty(result.StandardError);
        if (before is not null)
        {
            Assert.AreSequenceEqual(before, await File.ReadAllBytesAsync(control, token));
        }
        else
        {
            Assert.IsFalse(File.Exists(control));
        }
    }
}
