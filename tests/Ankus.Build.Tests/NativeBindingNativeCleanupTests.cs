namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Releases an open file before Windows cleanup retries, and removes the entire owned directory.
    /// </summary>
    [TestMethod]
    public async Task NativeProbeCleanupWaitsForReleasedFile()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-probe-cleanup-").FullName;
        string file = Path.Join(directory, "held.bin");
        Task? cleanup = null;
        try
        {
            using (File.Open(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
                cleanup = DeleteDirectoryAsync(directory);
                if (OperatingSystem.IsWindows())
                {
                    Assert.IsFalse(cleanup.IsCompleted, "Windows cannot delete a file whose handle denies delete sharing.");
                }
            }

            await cleanup;
            Assert.IsFalse(Directory.Exists(directory));
        }
        finally
        {
            if (cleanup is not null)
            {
                await cleanup;
            }
            else
            {
                await DeleteDirectoryAsync(directory);
            }
        }
    }

    /// <summary>
    /// Persistent access denial remains a failure, with the file intact for explicit cleanup.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task NativeProbeCleanupDoesNotHideAccessDenial()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-probe-cleanup-").FullName;
        string file = Path.Join(directory, "readonly.bin");
        try
        {
            await File.WriteAllTextAsync(file, "retained", context.CancellationToken);
            File.SetAttributes(file, FileAttributes.ReadOnly);
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => DeleteDirectoryAsync(directory));
            Assert.AreEqual("retained", await File.ReadAllTextAsync(file, context.CancellationToken));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
            await DeleteDirectoryAsync(directory);
        }

        Assert.IsFalse(Directory.Exists(directory));
    }

    /// <summary>
    /// Missing directories are programming errors rather than transient file locks.
    /// </summary>
    [TestMethod]
    public async Task NativeProbeCleanupRejectsMissingDirectory()
    {
        string directory = Path.Join(Path.GetTempPath(), "ankus-probe-missing-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() => DeleteDirectoryAsync(directory));
    }

    /// <summary>
    /// Removes owned probe artifacts after exit, allowing bounded retries for Windows file-release races.
    /// </summary>
    private static Task DeleteDirectoryAsync(string directory) => NativeBuildDirectory.DeleteAsync(directory);
}
