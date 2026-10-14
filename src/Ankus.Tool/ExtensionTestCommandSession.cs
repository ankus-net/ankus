using Ankus.PgConfig;

namespace Ankus.Tool;

/// <summary>
/// Owns one test process's fixture storage and stops surviving servers before deleting it.
/// </summary>
/// <param name="installation">The selected server tools used for shutdown.</param>
/// <param name="dataDirectoryBase">An optional parent for this invocation's cluster storage.</param>
/// <param name="runAs">The Unix account that owns the cluster storage and runs its servers, or null for the current account.</param>
internal sealed class ExtensionTestCommandSession(PostgresInstallation installation, string? dataDirectoryBase = null, string? runAs = null)
    : IAsyncDisposable
{
    private readonly (string Session, string Data) _directories = CreateDirectories(dataDirectoryBase, runAs);

    /// <summary>
    /// Gets the private fixture root. Unix paths stay short enough for PostgreSQL sockets.
    /// </summary>
    internal string DirectoryPath => _directories.Session;

    /// <summary>
    /// Gets the owned cluster root, separate from sockets when the caller selects a data directory.
    /// </summary>
    internal string DataDirectoryPath => _directories.Data;

    /// <summary>
    /// Stops servers left behind by an aborted host and removes only this invocation's storage.
    /// </summary>
    /// <returns>A task completing after server shutdown and directory removal.</returns>
    public async ValueTask DisposeAsync()
    {
        string dataRoot = DataDirectoryPath;
        if (Directory.Exists(dataRoot))
        {
            // Another account's data directories are private to it, so its pg_ctl inspects each candidate.
            IEnumerable<string> candidates = runAs is null
                ? Directory.EnumerateFiles(dataRoot, "PG_VERSION",
                    new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    .Select(static versionFile => Path.GetDirectoryName(versionFile)!)
                : Directory.EnumerateDirectories(dataRoot);
            foreach (string dataDirectory in candidates)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var output = new MemoryStream();
                int status = await RunServerToolAsync(["status", "-D", dataDirectory], output, timeout.Token);
                if (status == 0)
                {
                    int stopped = await RunServerToolAsync(["stop", "-D", dataDirectory, "-m", "immediate", "-w", "-t", "25"],
                        output, timeout.Token);
                    if (stopped != 0)
                    {
                        status = await RunServerToolAsync(["status", "-D", dataDirectory], output, timeout.Token);
                        if (status != 3)
                        {
                            throw new InvalidOperationException($"Test server cleanup failed. Storage retained at '{dataRoot}' and '{DirectoryPath}'.");
                        }
                    }
                }
                else if (status != 3 && !(runAs is not null && status == 4))
                {
                    throw new InvalidOperationException($"Test server status failed ({status}). Storage retained at '{dataRoot}' and '{DirectoryPath}'.");
                }
            }

            if (runAs is null)
            {
                PostgresServerStorage.Delete(dataRoot);
            }
            else if (await RunAsAsync(runAs, ["rm", "-rf", "--", dataRoot], CancellationToken.None) is int code and not 0)
            {
                throw new IOException($"sudo -u {runAs} could not delete '{dataRoot}' (exit {code}).");
            }
        }

        PostgresServerStorage.Delete(DirectoryPath);
    }

    private Task<int> RunServerToolAsync(string[] arguments, MemoryStream output, CancellationToken token)
        => runAs is null
            ? ToolProcess.RunAsync(installation.PgCtlPath, arguments, token, outputStream: output)
            : RunAsAsync(runAs, [installation.PgCtlPath, .. arguments], token, output);

    /// <summary>
    /// Runs a command as another Unix account through sudo, from a directory every account can enter.
    /// </summary>
    /// <param name="account">The account name.</param>
    /// <param name="arguments">The command and its arguments.</param>
    /// <param name="token">Cancels the command.</param>
    /// <param name="output">An optional destination for captured stdout.</param>
    /// <returns>The exit code.</returns>
    internal static Task<int> RunAsAsync(string account, string[] arguments, CancellationToken token, Stream? output = null)
        => ToolProcess.RunAsync("sudo", ["-u", account, "--", .. arguments], token, outputStream: output, workingDirectory: "/");

    private static (string Session, string Data) CreateDirectories(string? dataDirectoryBase, string? runAs)
    {
        string invocation = Guid.NewGuid().ToString("N");
        string? parent = dataDirectoryBase is null ? null
            : runAs is null ? Directory.CreateDirectory(Path.GetFullPath(dataDirectoryBase)).FullName
            : Path.GetFullPath(dataDirectoryBase);
        string session = Directory.CreateDirectory(Path.Join(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp",
            "ak-test-" + invocation)).FullName;
        try
        {
            if (runAs is null)
            {
                string data = Directory.CreateDirectory(parent is null ? Path.Join(session, "pgdata") :
                    Path.Join(parent, "ak-test-pgdata-" + invocation)).FullName;
                return (session, data);
            }

            // The account must create its own data root; the session directory belongs to the current account.
            string owned = Path.Join(parent ?? "/tmp", "ak-test-pgdata-" + invocation);
            int code = RunAsAsync(runAs, ["mkdir", "-p", "--", owned], CancellationToken.None).GetAwaiter().GetResult();
            if (code != 0)
            {
                throw new InvalidOperationException(
                    $"sudo -u {runAs} could not create the test data directory '{owned}' (exit {code}). " +
                    "With --runas, --pgdata must name a directory that account can write.");
            }

            return (session, owned);
        }
        catch
        {
            Directory.Delete(session, recursive: true);
            throw;
        }
    }
}
