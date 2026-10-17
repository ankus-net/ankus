namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies development-build choices and installation isolation before native compilation begins.
/// </summary>
/// <param name="context">The current test's cancellation scope.</param>
[TestClass]
public sealed class PostgresProvisionerTests(TestContext context)
{
    /// <summary>
    /// Invalid public installation requests do not contact an upstream host or create an Ankus home.
    /// </summary>
    /// <param name="empty">Whether the major selection is empty instead of the job count being invalid.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidRequestsDoNotCreateFilesOrContactUpstream(bool empty)
    {
        string home = Path.Join(Path.GetTempPath(), "ankus-provision-test-" + Guid.NewGuid().ToString("N"));
        using var handler = new UnexpectedRequestHandler();
        using var client = new HttpClient(handler);
        var provisioner = new PostgresProvisioner(client, home);
        try
        {
            if (empty)
            {
                await Assert.ThrowsExactlyAsync<ArgumentException>(() => provisioner.InstallAsync([],
                    cancellationToken: context.CancellationToken));
            }
            else
            {
                await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => provisioner.InstallAsync([18],
                    new PostgresProvisionOptions { Jobs = 0 }, cancellationToken: context.CancellationToken));
            }

            Assert.IsFalse(Directory.Exists(home));
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>
    /// CPU concurrency changes do not create a different binary installation, while semantic build options do.
    /// </summary>
    [TestMethod]
    public void BuildIdentitySeparatesOptionsButNotJobCounts()
    {
        string baseline = PostgresProvisioner.GetBuildIdentity(new PostgresProvisionOptions { Jobs = 1 });
        Assert.AreEqual(baseline, PostgresProvisioner.GetBuildIdentity(new PostgresProvisionOptions { Jobs = 8 }));
        Assert.AreNotEqual(baseline, PostgresProvisioner.GetBuildIdentity(new PostgresProvisionOptions { EnableValgrind = true }));
        Assert.AreNotEqual(baseline, PostgresProvisioner.GetBuildIdentity(new PostgresProvisionOptions { Release = true }));
        Assert.AreNotEqual(baseline, PostgresProvisioner.GetBuildIdentity(new PostgresProvisionOptions { ConfigureFlags = ["--with-icu"] }));
        Assert.AreNotEqual(PostgresProvisioner.GetBuildIdentity(new PostgresProvisionOptions { ConfigureFlags = ["--with-icu"] }),
            PostgresProvisioner.GetBuildIdentity(new PostgresProvisionOptions { ConfigureFlags = ["--without-icu"] }));
    }

    /// <summary>
    /// Development builds check assertions and randomize allocated memory as <c>cargo pgrx init</c>'s do, and release builds
    /// leave both out for benchmarking. Both keep debug symbols, and Valgrind instrumentation is independent.
    /// </summary>
    [TestMethod]
    public void ReleaseBuildsLeaveOutAssertionsAndMemoryRandomization()
    {
        var development = new PostgresProvisionOptions { ConfigureFlags = ["--with-icu"] };
        var release = new PostgresProvisionOptions { ConfigureFlags = ["--with-icu"], Release = true, EnableValgrind = true };
        Assert.AreSequenceEqual(["--prefix=/opt/pg", "--enable-debug", "--enable-cassert", "--with-icu"],
            PostgresProvisioner.GetConfigureArguments("/opt/pg", development));
        Assert.AreSequenceEqual(["--prefix=/opt/pg", "--enable-debug", "--with-icu"], PostgresProvisioner.GetConfigureArguments("/opt/pg", release));
        Assert.AreEqual("-O2 -DUSE_ASSERT_CHECKING=1 -DRANDOMIZE_ALLOCATED_MEMORY=1", PostgresProvisioner.GetPreprocessorFlags("-O2", development));
        Assert.AreEqual("-O2 -DUSE_VALGRIND=1", PostgresProvisioner.GetPreprocessorFlags("-O2", release));
    }

    /// <summary>
    /// Unlimited or negative native-build parallelism must not be selected accidentally.
    /// </summary>
    /// <param name="jobs">An invalid parallelism limit.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void RejectsInvalidJobs(int jobs)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PostgresProvisioner.ValidateOptions(new PostgresProvisionOptions { Jobs = jobs }, windows: false));
    }

    /// <summary>
    /// Unix feature options retain exact arguments while installation-directory overrides and shell fragments are rejected.
    /// </summary>
    /// <param name="flag">An argument outside the supported feature-option surface.</param>
    [TestMethod]
    [DataRow("--prefix=/system")]
    [DataRow("--pre=/system")]
    [DataRow("--exec-prefix=/system")]
    [DataRow("--bindir=/system")]
    [DataRow("--help")]
    [DataRow("CPPFLAGS=changed")]
    [DataRow(";touch elsewhere")]
    public void RejectsArgumentsThatChangeInstallationLocations(string flag)
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() =>
            PostgresProvisioner.ValidateOptions(new PostgresProvisionOptions { ConfigureFlags = [flag] }, windows: false));
        Assert.Contains(flag, error.Message);
    }

    /// <summary>
    /// Source-only options cannot be silently ignored by a Windows binary installation.
    /// </summary>
    [TestMethod]
    public void RejectsSourceOptionsForWindows()
    {
        Assert.ThrowsExactly<ArgumentException>(() => PostgresProvisioner.ValidateOptions(
            new PostgresProvisionOptions { EnableValgrind = true }, windows: true));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresProvisioner.ValidateOptions(
            new PostgresProvisionOptions { ConfigureFlags = ["--with-icu"] }, windows: true));
    }

    /// <summary>
    /// Turns any unexpected upstream access into a distinct failure.
    /// </summary>
    private sealed class UnexpectedRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Invalid requests must not access the network.");
    }
}
