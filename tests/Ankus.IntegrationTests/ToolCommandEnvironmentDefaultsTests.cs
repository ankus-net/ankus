using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// An environment major overrides project defaults for a read-only server status lookup.
    /// </summary>
    /// <param name="prefix">Whether the environment uses a pgrx-style version label.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EnvironmentMajorPrecedesProjectDefault(bool prefix)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string home = Path.Join(directory, "home");
        await RegisterEnvironmentInstallationAsync(home, token);
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"),
                new XElement("UsingAnkusSdk", "true"), new XElement("AnkusPostgresMajor", DifferentMajor()))))
            .Save(Path.Join(directory, "Selection.csproj"));
        Dictionary<string, string?> environment = EnvironmentDefaultsConfiguration(home);
        environment["PG_VERSION"] = (prefix ? "pg" : "") + MajorText();
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, ["status"], environment, token, workingDirectory: directory);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(s_installation.Label + ": stopped" + Environment.NewLine, result.StandardOutput);
        Assert.IsEmpty(Directory.GetDirectories(directory, "obj", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetDirectories(directory, "bin", SearchOption.AllDirectories));
        Assert.HasCount(1, Directory.GetFileSystemEntries(home));
    }

    /// <summary>
    /// An absent or empty version default retains the project's explicit installation without consulting a broken registry.
    /// </summary>
    /// <param name="value">The absent or empty environment selection.</param>
    [TestMethod]
    [DataRow(default(string))]
    [DataRow("")]
    public async Task EmptyEnvironmentMajorRetainsProjectInstallation(string? value)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string home = Directory.CreateDirectory(Path.Join(directory, "home")).FullName;
        string configuration = Path.Join(home, "config.json");
        string original = JsonSerializer.Serialize(new Dictionary<string, string> { ["pg" + MajorText()] = "missing/bin/pg_config" });
        await File.WriteAllTextAsync(configuration, original, token);
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"),
                new XElement("UsingAnkusSdk", "true"), new XElement("AnkusPostgresMajor", MajorText()),
                new XElement("AnkusPgConfigPath", s_installation.PgConfigPath))))
            .Save(Path.Join(directory, "Selection.csproj"));
        Dictionary<string, string?> environment = EnvironmentDefaultsConfiguration(home);
        environment["PG_VERSION"] = value;
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, ["info", "version"], environment, token, workingDirectory: directory);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(s_installation.Version + Environment.NewLine, result.StandardOutput);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configuration, token));
        Assert.IsEmpty(Directory.GetDirectories(directory, "obj", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetDirectories(directory, "bin", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Invalid version defaults fail before a registry mutation or installation download.
    /// </summary>
    /// <param name="value">The invalid version label.</param>
    [TestMethod]
    [DataRow("12")]
    [DataRow("pg20")]
    [DataRow(" 18")]
    [DataRow("18.6")]
    [DataRow("banana")]
    [DataRow("pg")]
    [DataRow("18.6-1.pgdg13+1")]
    public async Task InvalidEnvironmentMajorPreservesRegistry(string value)
    {
        CancellationToken token = context.CancellationToken;
        string home = CreateDirectory();
        await RegisterEnvironmentInstallationAsync(home, token);
        string configuration = Path.Join(home, "config.json");
        string original = await File.ReadAllTextAsync(configuration, token);
        Dictionary<string, string?> environment = EnvironmentDefaultsConfiguration(home);
        environment["PG_VERSION"] = value;
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, ["status"], environment, token, workingDirectory: s_root);

        Assert.AreEqual(1, result.ExitCode);
        Assert.IsEmpty(result.StandardOutput);
        Assert.Contains("PG_VERSION must select PostgreSQL 13–19", result.StandardError);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configuration, token));
        Assert.HasCount(1, Directory.GetFileSystemEntries(home));
    }

    /// <summary>
    /// Explicit version options bypass an invalid environment default instead of treating it as a conflict.
    /// </summary>
    /// <param name="selection">The explicit selection form.</param>
    [TestMethod]
    [DataRow("option")]
    [DataRow("argument")]
    [DataRow("path")]
    public async Task ExplicitMajorPrecedesEnvironment(string selection)
    {
        CancellationToken token = context.CancellationToken;
        string home = CreateDirectory();
        await RegisterEnvironmentInstallationAsync(home, token);
        Dictionary<string, string?> environment = EnvironmentDefaultsConfiguration(home);
        environment["PG_VERSION"] = "invalid";
        string[] selected = selection switch
        {
            "option" => ["--pg", MajorText()],
            "argument" => ["pg" + MajorText()],
            "path" => ["--pg-config", s_installation.PgConfigPath],
            _ => throw new ArgumentException("Unknown selection.", nameof(selection)),
        };
        string[] command = selection == "argument" ? ["info", "version"] : ["status"];
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, [.. command, .. selected], environment, token, workingDirectory: s_root);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual((selection == "argument" ? s_installation.Version.ToString() : s_installation.Label + ": stopped") +
            Environment.NewLine, result.StandardOutput);
    }

    /// <summary>
    /// A valid unavailable environment version is rejected instead of falling back to a registered major.
    /// </summary>
    [TestMethod]
    public async Task UnregisteredEnvironmentMajorDoesNotFallBack()
    {
        CancellationToken token = context.CancellationToken;
        string home = CreateDirectory();
        await RegisterEnvironmentInstallationAsync(home, token);
        Dictionary<string, string?> environment = EnvironmentDefaultsConfiguration(home);
        int major = DifferentMajor();
        environment["PG_VERSION"] = "pg" + major.ToString(CultureInfo.InvariantCulture);
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, ["status"], environment, token, workingDirectory: s_root);

        Assert.AreEqual(1, result.ExitCode);
        Assert.IsEmpty(result.StandardOutput);
        Assert.Contains("PostgreSQL " + major.ToString(CultureInfo.InvariantCulture) + " is not registered", result.StandardError);
        Assert.HasCount(1, Directory.GetFileSystemEntries(home));
    }

    /// <summary>
    /// Information commands retain project selection even when Docker or another command supplies PG_VERSION.
    /// </summary>
    /// <param name="information">The scriptable installation value to read.</param>
    /// <param name="dockerValue">Whether the environment holds Docker's full version rather than another valid major.</param>
    [TestMethod]
    [DataRow("version", false)]
    [DataRow("version", true)]
    [DataRow("path", false)]
    [DataRow("path", true)]
    [DataRow("pg-config", false)]
    [DataRow("pg-config", true)]
    public async Task InformationIgnoresServerVersionEnvironment(string information, bool dockerValue)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string home = Path.Join(directory, "home");
        await RegisterEnvironmentInstallationAsync(home, token);
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"),
                new XElement("UsingAnkusSdk", "true"), new XElement("AnkusPostgresMajor", MajorText()),
                new XElement("AnkusPgConfigPath", s_installation.PgConfigPath))))
            .Save(Path.Join(directory, "Selection.csproj"));
        string configuration = Path.Join(home, "config.json");
        string original = await File.ReadAllTextAsync(configuration, token);
        Dictionary<string, string?> environment = EnvironmentDefaultsConfiguration(home);
        environment["PG_VERSION"] = dockerValue ? "18.6-1.pgdg13+1" : "pg" + DifferentMajor().ToString(CultureInfo.InvariantCulture);
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, ["info", information], environment, token, workingDirectory: directory);

        string expected = information switch
        {
            "version" => s_installation.Version.ToString(),
            "path" => Path.GetDirectoryName(Path.GetDirectoryName(s_installation.PgConfigPath)!)!,
            "pg-config" => s_installation.PgConfigPath,
            _ => throw new ArgumentException("Unknown information command.", nameof(information)),
        };
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(expected + Environment.NewLine, result.StandardOutput);
        Assert.AreEqual(original, await File.ReadAllTextAsync(configuration, token));
        Assert.HasCount(1, Directory.GetFileSystemEntries(home));
        Assert.IsEmpty(Directory.GetDirectories(directory, "obj", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetDirectories(directory, "bin", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Builds, publishes, extracts and stages a real project while Docker's full version is present.
    /// </summary>
    [TestMethod]
    public async Task ProjectCommandsIgnoreDockerServerVersion()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string home = Path.Join(directory, "home");
        await RegisterEnvironmentInstallationAsync(home, token);
        string configuration = Path.Join(home, "config.json");
        string original = await File.ReadAllTextAsync(configuration, token);
        string project = Path.Join(directory, "Environment.csproj");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Ankus.Sdk/" + s_version),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"),
                new XElement("Nullable", "enable"), new XElement("ImplicitUsings", "enable"),
                new XElement("AnkusExtensionName", "ankus_environment"), new XElement("AnkusExtensionVersion", "0.1.0"),
                new XElement("AnkusPostgresMajor", MajorText()), new XElement("AnkusPgConfigPath", s_installation.PgConfigPath))))
            .Save(project);
        File.Copy(Path.Join(Path.GetDirectoryName(s_project)!, "Hello.cs"), Path.Join(directory, "Hello.cs"));
        Dictionary<string, string?> environment = EnvironmentDefaultsConfiguration(home);
        environment["PG_VERSION"] = "18.6-1.pgdg13+1";
        string publication = Path.Join(directory, "bin", "ankus", "pg" + MajorText(),
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "Release");
        string stagedInstallation = Path.Join(directory, "staged installation");
        string stagedPackage = Path.Join(directory, "staged package");
        (string Name, string[] Arguments)[] commands =
        [
            ("build", ["build", "--project", project]),
            ("publish", ["publish", "--project", project]),
            ("schema", ["schema", "--project", project, "--no-build"]),
            ("get", ["get", "extname", "--project", project]),
            ("install", ["install", "--project", project, "--destdir", stagedInstallation]),
            ("package", ["package", "--project", project, "--output", stagedPackage, "--prefix-dir", "flat"]),
        ];
        foreach ((string name, string[] arguments) in commands)
        {
            ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, arguments, environment, token, workingDirectory: directory);
            Assert.AreEqual(0, result.ExitCode, name + ": " + result.StandardError);
            PublishedExtension manifest = PublishedExtension.Read(publication);
            Assert.AreEqual(s_installation.Version.Major, manifest.PostgresMajor, name);
            Assert.AreEqual(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, manifest.RuntimeIdentifier, name);
            Assert.AreEqual("ankus_environment.control", manifest.Control, name);
            ExtensionSchema schema = ExtensionSchema.Read(Path.Join(publication, manifest.Library), manifest.RuntimeIdentifier);
            Assert.AreEqual(s_installation.Version.Major, schema.Artifacts.PostgresMajor, name);
            if (name == "schema")
            {
                Assert.AreEqual(schema.Sql, result.StandardOutput);
            }
            else if (name == "get")
            {
                Assert.AreEqual("ankus_environment" + Environment.NewLine, result.StandardOutput);
            }

            if (name is "install" or "package")
            {
                string stagedLibrary = name == "install"
                    ? Path.Join(StagedPath(stagedInstallation, s_installation.LibraryDirectory), manifest.Library)
                    : Path.Join(stagedPackage, OperatingSystem.IsWindows() ? "lib" : "flat", manifest.Library);
                Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Join(publication, manifest.Library), token),
                    await File.ReadAllBytesAsync(stagedLibrary, token), name);
            }
        }

        Assert.AreEqual(original, await File.ReadAllTextAsync(configuration, token));
        Assert.HasCount(1, Directory.GetFileSystemEntries(home));
    }

    /// <summary>
    /// Connect transports DBNAME literally, and an explicit database option retains priority.
    /// </summary>
    /// <param name="explicitDatabase">Whether the command overrides the environment database.</param>
    /// <param name="explicitClient">Whether an explicit client option overrides an invalid environment default.</param>
    [RetryPortCollisionTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task ConnectUsesLiteralEnvironmentDatabase(bool explicitDatabase, bool explicitClient)
    {
        CancellationToken token = context.CancellationToken;
        string home = CreateDirectory();
        var cluster = new PostgresDevelopmentCluster(s_installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        const string EnvironmentDatabase = "host=elsewhere café'\\";
        const string ExplicitDatabase = "explicit café database";
        Dictionary<string, string?> environment = EnvironmentDefaultsConfiguration(home);
        environment["PG_VERSION"] = "invalid";
        environment["DBNAME"] = EnvironmentDatabase;
        environment["ANKUS_PGCLI"] = explicitClient ? "invalid" : "false";
        string expected = explicitDatabase ? ExplicitDatabase : EnvironmentDatabase;
        string[] selected = explicitDatabase ? ["--database", ExplicitDatabase] : [];
        string[] client = explicitClient ? ["--pgcli", "false"] : [];
        try
        {
            ProcessResult result = await PackageProcessRunner.RunAsync(s_tool,
                ["connect", "--pg-config", s_installation.PgConfigPath, "--port", port.ToString(CultureInfo.InvariantCulture),
                    .. selected, .. client, "--", "-X", "-A", "-t", "-c", "SELECT current_database()"],
                environment, token, workingDirectory: s_root);

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            Assert.EndsWith(expected + Environment.NewLine, result.StandardOutput);
            Assert.Contains("Created database " + expected, result.StandardOutput);
            Assert.IsTrue(await cluster.IsRunningAsync(token));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Invalid client defaults fail before creating or starting any development server.
    /// </summary>
    /// <param name="command">The interactive preparation command.</param>
    [TestMethod]
    [DataRow("connect")]
    [DataRow("run")]
    public async Task InvalidClientEnvironmentDoesNotStartServer(string command)
    {
        string directory = CreateDirectory();
        string home = Path.Join(directory, "unused home");
        Dictionary<string, string?> environment = EnvironmentDefaultsConfiguration(home);
        environment["ANKUS_PGCLI"] = "invalid";
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool,
            [command, "--pg-config", s_installation.PgConfigPath], environment,
            context.CancellationToken, workingDirectory: s_root);

        Assert.AreEqual(1, result.ExitCode);
        Assert.IsEmpty(result.StandardOutput);
        Assert.Contains("ANKUS_PGCLI must be true or false", result.StandardError);
        Assert.IsFalse(Directory.Exists(home));
    }

    /// <summary>
    /// The true client default selects pgcli, while an explicit false option selects the installation's psql.
    /// </summary>
    /// <param name="overrideClient">Whether the command explicitly selects psql.</param>
    [RetryPortCollisionTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EnvironmentClientSelectsPgcliUnlessOverridden(bool overrideClient)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string home = Path.Join(directory, "home");
        string clientDirectory = Directory.CreateDirectory(Path.Join(directory, "empty client path")).FullName;
        var cluster = new PostgresDevelopmentCluster(s_installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        Dictionary<string, string?> environment = EnvironmentDefaultsConfiguration(home);
        environment["ANKUS_PGCLI"] = "true";
        environment["PATH"] = clientDirectory;
        string[] client = overrideClient ? ["--pgcli", "false"] : [];
        try
        {
            ProcessResult result = await PackageProcessRunner.RunAsync(s_tool,
                ["connect", "--pg-config", s_installation.PgConfigPath, "--port", port.ToString(CultureInfo.InvariantCulture),
                    "--database", "client_selection", .. client, "--", "-X", "-A", "-t", "-c", "SELECT 42"],
                environment, token, workingDirectory: directory);

            if (overrideClient)
            {
                Assert.AreEqual(0, result.ExitCode, result.StandardError);
                Assert.EndsWith("42" + Environment.NewLine, result.StandardOutput);
            }
            else
            {
                Assert.AreEqual(1, result.ExitCode);
                Assert.Contains("pgcli", result.StandardError);
                Assert.Contains("Created database client_selection", result.StandardOutput);
            }

            Assert.IsTrue(await cluster.IsRunningAsync(token));
            Assert.IsEmpty(Directory.GetFileSystemEntries(clientDirectory));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Existing native publications select their compiled major before any new environment default.
    /// </summary>
    /// <param name="operation">The staged installation command.</param>
    /// <param name="invalid">Whether the environment default is malformed rather than unregistered.</param>
    [TestMethod]
    [DataRow("package", false)]
    [DataRow("package", true)]
    [DataRow("install", false)]
    [DataRow("install", true)]
    public async Task PublishedArtifactMajorPrecedesEnvironment(string operation, bool invalid)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string home = Path.Join(directory, "home");
        await RegisterEnvironmentInstallationAsync(home, token);
        string configuration = Path.Join(home, "config.json");
        string original = await File.ReadAllTextAsync(configuration, token);
        PublishedExtension manifest = PublishedExtension.Read(s_published);
        Assert.AreEqual(s_installation.Version.Major, manifest.PostgresMajor);
        string output = Path.Join(directory, "staged assets");
        Dictionary<string, string?> environment = EnvironmentDefaultsConfiguration(home);
        environment["PG_VERSION"] = invalid ? "invalid" : "pg" + DifferentMajor().ToString(CultureInfo.InvariantCulture);
        string[] staging = operation == "package" ? ["--output", output, "--prefix-dir", "flat"] : ["--destdir", output];
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool, [operation, "--from", s_published, .. staging],
            environment, token, workingDirectory: directory);

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        string libraries = operation == "install" ? StagedPath(output, s_installation.LibraryDirectory)
            : Path.Join(output, OperatingSystem.IsWindows() ? "lib" : "flat");
        string scripts = operation == "install" ? StagedPath(output, Path.Join(s_installation.SharedDirectory, "extension"))
            : OperatingSystem.IsWindows() ? Path.Join(output, "share", "extension") : Path.Join(output, "flat");
        (string Source, string Destination)[] assets =
        [
            (Path.Join(s_published, manifest.Library), Path.Join(libraries, manifest.Library)),
            (Path.Join(s_published, "extension", manifest.Control), Path.Join(scripts, manifest.Control)),
            (Path.Join(s_published, "extension", manifest.Sql), Path.Join(scripts, manifest.Sql)),
        ];
        foreach ((string source, string destination) in assets)
        {
            Assert.AreSequenceEqual(await File.ReadAllBytesAsync(source, token), await File.ReadAllBytesAsync(destination, token));
        }

        Assert.AreEqual(original, await File.ReadAllTextAsync(configuration, token));
        Assert.HasCount(1, Directory.GetFileSystemEntries(home));
        Assert.IsEmpty(Directory.GetDirectories(directory, "obj", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetDirectories(directory, "bin", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Removes fixture build overrides so each process observes the authored project or its explicit command selection.
    /// </summary>
    /// <param name="home">The case-owned registry directory.</param>
    /// <returns>Isolated tool defaults with ordinary consumer build properties.</returns>
    private static Dictionary<string, string?> EnvironmentDefaultsConfiguration(string home)
    {
        Dictionary<string, string?> environment = EnvironmentConfiguration(home);
        environment["AnkusPostgresMajor"] = null;
        environment["AnkusPgConfigPath"] = null;
        return environment;
    }

    /// <summary>
    /// Creates only the test-owned registry needed for selection without changing the user's home.
    /// </summary>
    /// <param name="home">The owned registry directory.</param>
    /// <param name="token">Cancels writing before invocation.</param>
    private static async Task RegisterEnvironmentInstallationAsync(string home, CancellationToken token)
    {
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(Path.Join(home, "config.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["pg" + MajorText()] = s_installation.PgConfigPath }), token);
    }
}
