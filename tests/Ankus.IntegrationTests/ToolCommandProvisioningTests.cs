using Ankus.Testing;

namespace Ankus.IntegrationTests;

/// <summary>
/// Exercises PostgreSQL provisioning options through the installed tool package.
/// </summary>
public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Every pgrx PostgreSQL major and source-build option is exposed by the installed tool.
    /// </summary>
    [TestMethod]
    public async Task InitHelpDescribesDownloadsAndAllPostgresMajors()
    {
        ProcessResult result = await InvokeAsync(["init", "--help"], context.CancellationToken);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        foreach (string option in new[] { "--pg13", "--pg14", "--pg15", "--pg16", "--pg17", "--pg18", "--pg19",
            "--jobs", "--configure-flag", "--valgrind" })
        {
            Assert.Contains(option, result.StandardOutput);
        }

        Assert.Contains("download", result.StandardOutput);
        Assert.IsEmpty(result.StandardError);
    }

    /// <summary>
    /// Invalid build parallelism fails before downloading and leaves the registry and filesystem untouched.
    /// </summary>
    /// <param name="jobs">The invalid parallelism setting.</param>
    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    public async Task InitRejectsInvalidDownloadJobsBeforeFilesystemChanges(string jobs)
    {
        string home = CreateDirectory();
        string configuration = Path.Join(home, "config.json");
        const string Original = "{\"pg13\":\"preserve\",\"port\":30000}";
        await File.WriteAllTextAsync(configuration, Original, context.CancellationToken);
        ProcessResult result = await InvokeAsync(["init", "--pg18", "download", "--jobs", jobs, "--home", home], context.CancellationToken);
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("Jobs", result.StandardError);
        Assert.AreEqual(Original, await File.ReadAllTextAsync(configuration, context.CancellationToken));
        Assert.AreEqual(configuration, Assert.ContainsSingle(Directory.GetFileSystemEntries(home)));
    }
}
