using System.Diagnostics;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies stopped-server storage removal, including Windows handles that outlive their processes.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
public sealed class PostgresServerStorageTests(TestContext context)
{
    /// <summary>
    /// Contains only files created by this test instance.
    /// </summary>
    private readonly string _root = Directory.CreateTempSubdirectory("ankus-server-storage-").FullName;

    /// <summary>
    /// Removes only the test's owned files.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// A missing directory is already deleted.
    /// </summary>
    [TestMethod]
    public void DeleteAcceptsMissingDirectory()
    {
        string path = Path.Join(_root, "missing");
        PostgresServerStorage.Delete(path);
        Assert.IsFalse(Directory.Exists(path));
    }

    /// <summary>
    /// Deletion removes a nested tree and leaves its parent's other entries intact.
    /// </summary>
    [TestMethod]
    public void DeleteRemovesOnlyTheTree()
    {
        string path = CreateTree("data");
        string sibling = Path.Join(_root, "sibling.txt");
        File.WriteAllText(sibling, "keep");
        PostgresServerStorage.Delete(path);
        Assert.IsFalse(Directory.Exists(path));
        Assert.AreEqual("keep", File.ReadAllText(sibling));
    }

    /// <summary>
    /// On Windows, a file still open without delete sharing blocks deletion until its handle closes; deletion then completes.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task DeleteWaitsForWindowsHandlesToClose()
    {
        string path = CreateTree("held");
        await using var held = new FileStream(Path.Join(path, "base", "1", "1259"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.ThrowsExactly<IOException>(() => Directory.Delete(path, recursive: true));
        Task release = Task.Run(async () =>
        {
            await Task.Delay(500, context.CancellationToken);
            await held.DisposeAsync();
        }, context.CancellationToken);
        var elapsed = Stopwatch.StartNew();
        PostgresServerStorage.Delete(path);
        await release;
        Assert.IsFalse(Directory.Exists(path));
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(400), elapsed.Elapsed);
    }

    /// <summary>
    /// On Windows, a handle held beyond PostgreSQL's ten-second unlink limit is reported instead of waited on forever.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void DeleteReportsHandlesHeldBeyondTheLimit()
    {
        string path = CreateTree("stuck");
        using var held = new FileStream(Path.Join(path, "base", "1", "1259"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var elapsed = Stopwatch.StartNew();
        Assert.ThrowsExactly<IOException>(() => PostgresServerStorage.Delete(path));
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(10), elapsed.Elapsed);
        Assert.IsLessThan(TimeSpan.FromSeconds(20), elapsed.Elapsed);
        Assert.IsTrue(File.Exists(Path.Join(path, "base", "1", "1259")));
    }

    /// <summary>
    /// Creates a small data-directory-shaped tree under the test root.
    /// </summary>
    private string CreateTree(string name)
    {
        string path = Path.Join(_root, name);
        Directory.CreateDirectory(Path.Join(path, "base", "1"));
        File.WriteAllText(Path.Join(path, "PG_VERSION"), "17\n");
        File.WriteAllText(Path.Join(path, "base", "1", "1259"), "relation");
        return path;
    }
}
