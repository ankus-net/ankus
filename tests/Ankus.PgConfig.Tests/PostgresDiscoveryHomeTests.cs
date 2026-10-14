using System.Text.Json;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Keeps registered and managed installation discovery inside the selected home without process-wide mutation.
/// </summary>
/// <param name="context">The current test's cancellation scope.</param>
[TestClass]
public sealed class PostgresDiscoveryHomeTests(TestContext context)
{
    /// <summary>
    /// An unavailable explicitly registered server cannot be replaced by a conventional installation.
    /// </summary>
    [TestMethod]
    public async Task MissingRegisteredServerDoesNotFallBackToAnotherHome()
    {
        string home = Directory.CreateTempSubdirectory("ankus discovery ").FullName;
        try
        {
            const string Missing = "missing/bin/pg_config";
            string configuration = JsonSerializer.Serialize(new Dictionary<string, string> { ["pg18"] = Missing });
            await File.WriteAllTextAsync(Path.Join(home, "config.json"), configuration, context.CancellationToken);
            FileNotFoundException error = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
                PostgresInstallation.DiscoverAsync(18, home, context.CancellationToken));
            Assert.AreEqual(Path.GetFullPath(Missing, home), error.FileName);
            Assert.Contains(Path.GetFullPath(Missing, home), error.Message);
            Assert.AreEqual(configuration, await File.ReadAllTextAsync(Path.Join(home, "config.json"), context.CancellationToken));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// Managed candidates precede conventional paths and come from the same home used by registration lookup.
    /// </summary>
    [TestMethod]
    public void ManagedCandidatesUseTheSelectedHome()
    {
        string home = Directory.CreateTempSubdirectory("ankus discovery ").FullName;
        try
        {
            string first = Path.Join(home, "postgres", "18.6");
            string second = Path.Join(home, "postgres", "18.5");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            string executable = OperatingSystem.IsWindows() ? "pg_config.exe" : "pg_config";
            string[] candidates = [.. PostgresDiscovery.GetCandidates(18, home)];
            Assert.AreSequenceEqual([Path.Join(first, "bin", executable), Path.Join(second, "bin", executable)], candidates.Take(2));
            Assert.IsFalse(File.Exists(Path.Join(home, "config.json")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
