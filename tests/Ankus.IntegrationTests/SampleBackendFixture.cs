using Ankus.Testing;

namespace Ankus.IntegrationTests;

/// <summary>
/// Starts one sample's test publication on first use, so its generated <c>[PgTest]</c> cases run inside PostgreSQL.
/// </summary>
/// <param name="sample">The sample project's directory and file name beneath <c>samples/</c>.</param>
/// <remarks>
/// The test publication installs the sample's backend test functions in its own cluster. Filtering out every backend case
/// avoids the publication entirely; the owning test class disposes the fixture during class cleanup.
/// </remarks>
internal sealed class SampleBackendFixture(string sample) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private PostgresExtensionTest? _extension;

    /// <summary>
    /// Gets the started fixture, publishing and installing the sample with its tests on the first call.
    /// </summary>
    /// <param name="cancellationToken">Cancels publication and startup.</param>
    /// <returns>The shared running fixture.</returns>
    internal async Task<PostgresExtensionTest> GetAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _extension ??= await PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions
            {
                ProjectPath = Path.Join(IntegrationEnvironment.RepositoryRoot, "samples", sample, sample + ".csproj"),
                Installation = await IntegrationEnvironment.GetInstallationAsync(cancellationToken),
                IncludeTests = true,
                DataDirectoryBase = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-pgdata"),
            }, cancellationToken);
            return _extension;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Stops the cluster and removes its data if the fixture was started.
    /// </summary>
    /// <returns>A task that completes after shutdown.</returns>
    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_extension is not null)
            {
                await _extension.DisposeAsync();
                _extension = null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
