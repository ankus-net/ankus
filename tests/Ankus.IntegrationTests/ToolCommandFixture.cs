using System.Collections.Concurrent;
using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

/// <summary>
/// Bounds package-consumer concurrency and isolates pre-18 extension control files for each active test.
/// </summary>
public sealed partial class ToolCommandTests
{
    private static readonly int s_concurrentCases = IntegrationEnvironment.PackageTestConcurrency;

    private static readonly SemaphoreSlim s_caseSlots = new(s_concurrentCases, s_concurrentCases);
    private static readonly ConcurrentQueue<PostgresTestInstallation> s_caseInstallations = new();

    private readonly ConcurrentQueue<string> _caseDirectories = new();
    private PostgresTestInstallation? _caseInstallation;
    private bool _ownsCaseSlot;

    /// <summary>
    /// Removes completed consumers and returns the installation after their clusters and child processes stop.
    /// </summary>
    [TestCleanup]
    public void ReleaseCase()
    {
        if (_ownsCaseSlot)
        {
            if (_caseInstallation is not null)
            {
                s_caseInstallations.Enqueue(_caseInstallation);
                _caseInstallation = null;
            }

            _ownsCaseSlot = false;
            s_caseSlots.Release();
        }

        while (_caseDirectories.TryDequeue(out string? directory))
        {
            DeleteCaseDirectory(directory);
        }
    }

    /// <summary>
    /// Deletes a completed case, allowing Windows to release handles that outlive a stopped server's processes.
    /// </summary>
    /// <param name="directory">The case directory.</param>
    /// <remarks>
    /// The case's clusters have already stopped. A handle still held after ten seconds remains a test failure.
    /// </remarks>
    private static void DeleteCaseDirectory(string directory)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception error) when (OperatingSystem.IsWindows() && error is IOException or UnauthorizedAccessException &&
                elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(100);
            }
        }
    }

    private async Task<PostgresInstallation> PrepareCaseInstallationAsync(string output, CancellationToken token)
    {
        PostgresInstallation installation = await ReserveCaseInstallationAsync(token);
        _caseInstallation?.InstallExtensionFiles(output);
        return installation;
    }

    /// <summary>
    /// Reserves a writable installation only for cases that need PostgreSQL's pre-18 control-file layout.
    /// </summary>
    private async Task<PostgresInstallation> ReserveCaseInstallationAsync(CancellationToken token)
    {
        if (s_installation.Version.Major >= 18)
        {
            return s_installation;
        }

        if (!_ownsCaseSlot)
        {
            await s_caseSlots.WaitAsync(token);
            try
            {
                if (!s_caseInstallations.TryDequeue(out _caseInstallation))
                {
                    string root = Path.Join(s_root, "postgres " + Guid.NewGuid().ToString("N"));
                    _caseInstallation = await PostgresTestInstallation.StageAsync(s_installation, root, token);
                }

                _ownsCaseSlot = true;
            }
            catch
            {
                s_caseSlots.Release();
                throw;
            }
        }

        PostgresTestInstallation installation = _caseInstallation
            ?? throw new InvalidOperationException("The package-consumer test has no reserved PostgreSQL installation.");
        return installation.Installation;
    }
}
