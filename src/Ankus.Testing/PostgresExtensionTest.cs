using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ankus.PgConfig;
using Npgsql;

namespace Ankus.Testing;

/// <summary>
/// Publishes an extension project, loads it into an isolated PostgreSQL cluster, and owns both lifetimes.
/// Ordinary test initialization can use this fixture without environment variables or wrapper commands.
/// </summary>
public sealed class PostgresExtensionTest : IAsyncDisposable
{
    /// <summary>
    /// Lets the publication select its SDK from the extension's directory, as a command-line publish there would. The
    /// SDK that ran the tests passes its MSBuild locations to the test host, and another SDK must not load them.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string?> s_publishEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["MSBuildSDKsPath"] = null,
        ["MSBuildExtensionsPath"] = null,
    };

    /// <summary>
    /// Maps each cached publication directory to the stamp of the publish this process made there. Later fixtures that
    /// select the same publication copy it, unless the stamp beside it shows that another publish replaced it.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> s_publications = new(StringComparer.Ordinal);

    private readonly string _publishDirectory;
    private readonly string _dataDirectoryBase;
    private readonly PostgresServerAccount _account;
    private readonly PostgresTestInstallation? _stagedInstallation;
    private readonly ExtensionSchema _schema;
    private readonly string _installedSchema;
    private readonly bool _includeTests;
    private readonly Lock _disposeLock = new();
    private Task? _disposeTask;

    private PostgresExtensionTest(
        PostgresTestCluster cluster,
        string publishDirectory,
        string dataDirectoryBase,
        PostgresServerAccount account,
        PostgresTestInstallation? stagedInstallation,
        ExtensionSchema schema,
        string installedSchema,
        bool includeTests)
    {
        Cluster = cluster;
        _publishDirectory = publishDirectory;
        _dataDirectoryBase = dataDirectoryBase;
        _account = account;
        _stagedInstallation = stagedInstallation;
        _schema = schema;
        _installedSchema = installedSchema;
        _includeTests = includeTests;
    }

    /// <summary>
    /// Gets the running cluster, with the published extension installed.
    /// </summary>
    public PostgresTestCluster Cluster { get; }

    /// <summary>
    /// Publishes for the host architecture using the selected installation's headers, starts a fresh cluster,
    /// and executes CREATE EXTENSION. Build and startup failures fail initialization, so tests never skip silently.
    /// </summary>
    /// <param name="projectPath">The extension project file.</param>
    /// <param name="installation">
    /// The PostgreSQL installation, or null to use <c>ANKUS_TEST_PG_CONFIG</c> when set and otherwise the build or project selection.
    /// Projects without a selection default to PostgreSQL 18.
    /// </param>
    /// <param name="cancellationToken">Cancels discovery, publication, or startup.</param>
    /// <returns>The fixture to dispose after all tests finish.</returns>
    /// <remarks>
    /// PostgreSQL 18 and later use a per-cluster extension search path. Earlier versions run from an isolated,
    /// relocatable copy of the selected installation.
    /// Custom SQL directories are remapped into owned test storage without changing the authored control file.
    /// Server logs and build logs remain in the project's bin/ankus-test-logs directory for seven days after their last write.
    /// The publication uses the SDK that the extension's directory selects, even when another SDK runs the tests.
    /// Fixtures in one test run that select the same project, configuration, installation and build properties publish
    /// once, and each installs its own copy; changes to the extension take effect in the next run.
    /// </remarks>
    public static Task<PostgresExtensionTest> StartAsync(string projectPath,
        PostgresInstallation? installation = null, CancellationToken cancellationToken = default)
        => StartAsync(projectPath, sharedPreload: false, installation, cancellationToken);

    /// <summary>
    /// Publishes and installs an extension in an isolated cluster, optionally loading its library during shared preload.
    /// </summary>
    /// <param name="projectPath">The extension project file.</param>
    /// <param name="sharedPreload">Whether PostgreSQL must load the published library before starting backends.</param>
    /// <param name="installation">The selected installation, or null to use the ordinary fixture discovery.</param>
    /// <param name="cancellationToken">Cancels discovery, publication or startup.</param>
    /// <returns>The fixture that owns the cluster and temporary published library.</returns>
    public static Task<PostgresExtensionTest> StartAsync(string projectPath, bool sharedPreload,
        PostgresInstallation? installation = null, CancellationToken cancellationToken = default)
        => StartAsync(new PostgresExtensionTestOptions { ProjectPath = projectPath, SharedPreload = sharedPreload, Installation = installation }, cancellationToken);

    /// <summary>
    /// Publishes and installs an extension in an isolated cluster on an exact requested TCP port.
    /// The port is validated before publishing and is never replaced with an automatic port.
    /// </summary>
    /// <param name="projectPath">The extension project file.</param>
    /// <param name="sharedPreload">Whether to load the library during shared preload.</param>
    /// <param name="port">The requested TCP port, from 1 through 65535.</param>
    /// <param name="installation">The selected installation, or null for ordinary fixture discovery.</param>
    /// <param name="cancellationToken">Cancels discovery, publication or startup.</param>
    /// <returns>The fixture that owns the cluster and temporary published library.</returns>
    public static Task<PostgresExtensionTest> StartAsync(string projectPath, bool sharedPreload, int port,
        PostgresInstallation? installation = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        return StartAsync(new PostgresExtensionTestOptions
        {
            ProjectPath = projectPath,
            SharedPreload = sharedPreload,
            Port = port,
            Installation = installation,
        }, cancellationToken);
    }

    /// <summary>
    /// Publishes and starts an isolated extension with explicit test inclusion, build configuration and server settings.
    /// </summary>
    /// <param name="options">The publication and cluster options. Native tests are excluded unless explicitly enabled.</param>
    /// <param name="cancellationToken">Cancels discovery, publication or startup.</param>
    /// <returns>The fixture that owns the published extension and isolated server.</returns>
    public static Task<PostgresExtensionTest> StartAsync(PostgresExtensionTestOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Configuration);
        ArgumentNullException.ThrowIfNull(options.BuildProperties);
        ArgumentNullException.ThrowIfNull(options.PostgreSqlConfiguration);
        if (options.Port is int port)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        }

        var properties = new Dictionary<string, string>(options.BuildProperties, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in properties)
        {
            ArgumentNullException.ThrowIfNull(value);
            try
            {
                System.Xml.XmlConvert.VerifyNCName(name);
            }
            catch (System.Xml.XmlException error)
            {
                throw new ArgumentException($"MSBuild property name '{name}' is invalid.", nameof(options), error);
            }
        }

        if (properties.TryGetValue("Configuration", out string? configuration) &&
            !string.Equals(configuration, options.Configuration, StringComparison.Ordinal))
        {
            throw new ArgumentException("Select the same configuration in BuildProperties and Configuration.", nameof(options));
        }

        if (properties.TryGetValue("RuntimeIdentifier", out string? runtime) &&
            !string.Equals(runtime, RuntimeInformation.RuntimeIdentifier, StringComparison.Ordinal))
        {
            throw new ArgumentException("Extension fixtures publish for the host RuntimeIdentifier.", nameof(options));
        }

        properties["Configuration"] = options.Configuration;
        properties["RuntimeIdentifier"] = RuntimeInformation.RuntimeIdentifier;
        SetBooleanProperty(properties, "SelfContained", true, options);
        SetBooleanProperty(properties, "AnkusIncludeTests", options.IncludeTests, options);
        SetBooleanProperty(properties, "AnkusReuseSchema", options.ReuseSchema, options);

        return StartCoreAsync(options, properties, [.. options.PostgreSqlConfiguration], cancellationToken);
    }

    /// <summary>
    /// Executes a discovered native test in its own rollback-only transaction, preserving exact expected-error matching.
    /// </summary>
    /// <param name="test">A case from the extension's generated PostgresTests catalog.</param>
    /// <param name="cancellationToken">Cancels connection and test execution.</param>
    /// <returns>A task completing after the test and rollback.</returns>
    /// <remarks>
    /// Report ignored cases through the host framework. Invoking an ignored case directly fails, so it never reports false success.
    /// </remarks>
    public Task RunTestAsync(PgTestCase test, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (!_includeTests)
        {
            throw new InvalidOperationException("Enable IncludeTests when starting the extension fixture to run generated backend tests.");
        }

        if (test.IgnoreReason is not null)
        {
            throw new InvalidOperationException($"Report ignored backend test '{test.Name}' through the host framework: {test.IgnoreReason}");
        }

        string identity = "FUNCTION " + (test.Schema is null ? string.Empty : QuoteIdentifier(test.Schema) + ".") +
            QuoteIdentifier(test.FunctionName) + "()";
        if (_schema.Graph?.Items.Any(item => item.Kind == "function" && item.Names.Contains(test.Name) &&
            item.Names.Contains(test.FunctionName) && item.Attachments.Contains(identity)) != true)
        {
            throw new InvalidOperationException($"The published extension does not contain backend test '{test.Name}'. Rebuild the test project and extension together.");
        }

        return RunTestCoreAsync(test, cancellationToken);
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private async Task RunTestCoreAsync(PgTestCase test, CancellationToken cancellationToken)
    {
        try
        {
            await Cluster.RunTestAsync(test.Schema ?? _installedSchema, test.FunctionName, test.ExpectedError, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresTestException error)
        {
            throw new PostgresTestException(test.Name, error.ServerLog, error.InnerException ?? error);
        }
    }

    private static async Task<PostgresExtensionTest> StartCoreAsync(PostgresExtensionTestOptions options,
        Dictionary<string, string> properties, string[] serverConfiguration, CancellationToken cancellationToken)
    {
        string projectPath = Path.GetFullPath(options.ProjectPath);
        if (!File.Exists(projectPath) || !projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("The extension project was not found.", projectPath);
        }

        PostgresInstallation installation = await PostgresTestSelection.ResolveAsync(options, properties, cancellationToken).ConfigureAwait(false);
        properties["AnkusPostgresMajor"] = installation.Version.Major.ToString(CultureInfo.InvariantCulture);
        properties["AnkusPgConfigPath"] = installation.PgConfigPath;
        TestCommandContext.ValidateInstallation(installation);
        string? session = TestCommandContext.SessionDirectory;
        string? commandData = TestCommandContext.DataDirectory;
        string root = Path.GetDirectoryName(projectPath)!;
        // The test host may have loaded this project's ordinary build. Publishing
        // must not replace its assemblies, symbols or incremental-clean inventory.
        // The SDK artifacts layout also separates every referenced project's outputs.
        string buildKey = PublicationBuildKey(properties);
        string buildArtifacts = Path.Join(root, "obj", "ankus-test-build");
        string buildOutput = Path.Join(buildArtifacts, "publish-cache", buildKey);
        string invocation = Guid.NewGuid().ToString("N");
        string output = session is null
            ? Path.Join(root, "bin", "ankus-test-publish", invocation)
            : Path.Join(session, "publish", invocation);
        string logs = Path.Join(root, "bin", "ankus-test-logs");
        string dataDirectoryBase = session is null
            ? Path.Join(Path.GetFullPath(options.DataDirectoryBase ?? Path.GetTempPath()), "ankus-test-pgdata-" + invocation)
            : Path.Join(commandData!, invocation);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(logs);
        TestLogRetention.Prune(logs);
        PostgresServerAccount account = PostgresServerAccount.Create(TestCommandContext.RunAs);
        PostgresTestCluster? cluster = null;
        PostgresTestInstallation? stagedInstallation = null;
        try
        {
            await account.CreateDirectoryAsync(dataDirectoryBase, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(buildArtifacts);
            await using (await LockBuildAsync(
                Path.Join(buildArtifacts, "publish.lock"), cancellationToken).ConfigureAwait(false))
            {
                // Fixtures in one test run that select the same publication share it. A publish removes the stamp
                // first and writes its own after it succeeds, so a stamp this process did not write means the
                // publication is another process's or incomplete.
                string stamp = buildOutput + ".stamp";
                if (!s_publications.TryGetValue(buildOutput, out string? published) || !File.Exists(stamp) ||
                    !string.Equals(await File.ReadAllTextAsync(stamp, cancellationToken).ConfigureAwait(false), published, StringComparison.Ordinal))
                {
                    if (File.Exists(stamp))
                    {
                        File.Delete(stamp);
                    }

                    if (Directory.Exists(buildOutput))
                    {
                        Directory.Delete(buildOutput, recursive: true);
                    }

                    Directory.CreateDirectory(buildOutput);
                    await ProcessRunner.RunCheckedAsync("dotnet",
                        ["publish", projectPath, .. PropertyArguments(properties), "--artifacts-path", buildArtifacts,
                            "-o", buildOutput,
                            "-bl:" + Path.Join(logs, invocation + ".binlog")],
                        s_publishEnvironment, cancellationToken, workingDirectory: root).ConfigureAwait(false);
                    await File.WriteAllTextAsync(stamp, invocation, cancellationToken).ConfigureAwait(false);
                    s_publications[buildOutput] = invocation;
                }

                CopyDirectory(buildOutput, output);
            }

            PublishedExtension manifest = PublishedExtension.Read(output);
            ExtensionSchema schema = ExtensionSchema.Read(Path.Join(output, manifest.Library));
            if (manifest.PostgresMajor != installation.Version.Major || manifest.RuntimeIdentifier != RuntimeInformation.RuntimeIdentifier)
            {
                throw new InvalidOperationException("The published extension does not match the selected PostgreSQL target.");
            }

            string searchPath = output.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);
            char separator = OperatingSystem.IsWindows() ? ';' : ':';
            PostgresInstallation clusterInstallation = installation;
            List<string> configuration = [$"dynamic_library_path = '{searchPath}{separator}$libdir'"];
            if (installation.Version.Major >= 18)
            {
                // Keep these paths short: PostgreSQL on Windows cannot open files beyond MAX_PATH. Another account owns
                // the data directories, so its staging goes in the command session, which only ankus test --runas uses.
                string scriptBase = account.Name is null
                    ? Path.Join(dataDirectoryBase, "share")
                    : Path.Join(session!, "share", invocation);
                PostgresExtensionFiles.Stage(output, scriptBase);
                string controlPath = scriptBase.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);
                configuration.Insert(0, $"extension_control_path = '{controlPath}{separator}$system'");
            }
            else
            {
                string stageRoot = session is null
                    ? Path.Join(Path.GetTempPath(), "ankus-test-postgresql-" + invocation)
                    : Path.Join(session, "postgresql", invocation);
                stagedInstallation = await PostgresTestInstallation.StageAsync(installation, stageRoot, cancellationToken)
                    .ConfigureAwait(false);
                stagedInstallation.InstallExtensionFiles(output);
                clusterInstallation = stagedInstallation.Installation;
            }

            if (options.SharedPreload)
            {
                string library = Path.GetFileNameWithoutExtension(manifest.Library).Replace("'", "''", StringComparison.Ordinal);
                configuration.Add($"shared_preload_libraries = '{library}'");
            }

            configuration.AddRange(serverConfiguration);
            cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
            {
                Installation = clusterInstallation,
                Port = options.Port,
                DataDirectoryBase = dataDirectoryBase,
                LogDirectory = logs,
                PostgreSqlConfiguration = configuration,
                UseValgrind = options.UseValgrind,
            }, cancellationToken).ConfigureAwait(false);
            await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            string name = Path.GetFileNameWithoutExtension(manifest.Control);
            // CASCADE installs the extensions that the control file requires, as pgrx's test framework does.
            await using var command = new NpgsqlCommand(
                "CREATE EXTENSION \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" CASCADE", connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "SELECT namespace.nspname FROM pg_extension extension JOIN pg_namespace namespace ON namespace.oid=extension.extnamespace WHERE extension.extname=$1";
            command.Parameters.AddWithValue(name);
            string installedSchema = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The test extension was not installed."));
            return new PostgresExtensionTest(cluster, output, dataDirectoryBase, account, stagedInstallation, schema, installedSchema,
                options.IncludeTests);
        }
        catch
        {
            if (cluster is not null)
            {
                await cluster.DisposeAsync().ConfigureAwait(false);
            }

            if (stagedInstallation is not null)
            {
                await stagedInstallation.DisposeAsync().ConfigureAwait(false);
            }

            account.DeleteDirectory(dataDirectoryBase);
            PostgresServerStorage.Delete(output);
            throw;
        }
    }

    /// <summary>
    /// Stops the backend before deleting the temporary published library. Logs remain available.
    /// </summary>
    /// <returns>A task completing after shutdown and file cleanup.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposeTask is null || _disposeTask.IsFaulted)
            {
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Cluster.DisposeAsync().ConfigureAwait(false);
        if (_stagedInstallation is not null)
        {
            await _stagedInstallation.DisposeAsync().ConfigureAwait(false);
        }

        _account.DeleteDirectory(_dataDirectoryBase);
        PostgresServerStorage.Delete(_publishDirectory);
    }

    private static string PublicationBuildKey(IReadOnlyDictionary<string, string> properties)
    {
        string value = string.Join('\n', properties.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Key + "=" + pair.Value));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private static async Task<FileStream> LockBuildAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 ||
                error.HResult == (OperatingSystem.IsMacOS() ? 35 : 11))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Join(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destinationFile = Path.Join(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: true);
        }
    }

    private static string EscapeProperty(string value)
        => string.Concat(value.Select(static c => c is '%' or ';' or ',' or '$' or '@' or '(' or ')' or '\'' or '*' or '?' or '"'
            ? "%" + ((int)c).ToString("X2", CultureInfo.InvariantCulture) : c.ToString()));

    private static IEnumerable<string> PropertyArguments(IReadOnlyDictionary<string, string> properties)
        => properties.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => "-p:" + pair.Key + "=" + EscapeProperty(pair.Value));

    private static void SetBooleanProperty(Dictionary<string, string> properties, string name, bool value,
        PostgresExtensionTestOptions options)
    {
        string required = value ? "true" : "false";
        if (properties.TryGetValue(name, out string? selected) &&
            (!bool.TryParse(selected, out bool parsed) || parsed != value))
        {
            throw new ArgumentException($"BuildProperties must select {name}={required}.", nameof(options));
        }

        properties[name] = required;
    }
}
