using System.Net;

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
    /// An installation keeps the caller's build options, so a release build uses its own directory instead of the
    /// development build's.
    /// </summary>
    [TestMethod]
    public async Task InstallationsKeepTheReleaseBuildOption()
    {
        string home = Path.Join(Path.GetTempPath(), "ankus-provision-test-" + Guid.NewGuid().ToString("N"));
        using var handler = new IndexHandler("<a href=\"v18.6/\"></a>");
        using var client = new HttpClient(handler);
        var provisioner = new PostgresProvisioner(client, home);
        var options = new PostgresProvisionOptions { Release = true };
        string destination = Path.Join(home, "postgres", "18.6-" + PostgresProvisioner.GetBuildIdentity(options));
        Directory.CreateDirectory(destination);
        try
        {
            IOException refused = await Assert.ThrowsExactlyAsync<IOException>(() => provisioner.InstallAsync([18], options,
                cancellationToken: context.CancellationToken));
            Assert.AreEqual("Refusing to replace an existing PostgreSQL directory: " + destination, refused.Message);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
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
    /// A Windows source build keeps debug information, and development builds check assertions and randomize
    /// allocated memory as Unix builds do.
    /// </summary>
    [TestMethod]
    public void WindowsSourceBuildsMatchUnixBuildChoices()
    {
        Assert.AreSequenceEqual(["setup", "b", "s", "--prefix=p", "--vsenv", "--buildtype=debugoptimized", "-Dcassert=true",
            "-Dc_args=-DRANDOMIZE_ALLOCATED_MEMORY=1"], PostgresProvisioner.GetMesonSetupArguments("s", "b", "p", new PostgresProvisionOptions()));
        Assert.AreSequenceEqual(["setup", "b", "s", "--prefix=p", "--vsenv", "--buildtype=debugoptimized", "-Dcassert=false"],
            PostgresProvisioner.GetMesonSetupArguments("s", "b", "p", new PostgresProvisionOptions { Release = true }));
    }

    /// <summary>
    /// A Windows source build names every missing program and how to register a build instead, and accepts either
    /// name for Bison and Flex.
    /// </summary>
    [TestMethod]
    public void WindowsSourceBuildsNameMissingPrograms()
    {
        var version = new PostgresVersion(19, 0, PostgresReleaseStage.ReleaseCandidate, 1);
        PostgresProvisioner.RequireWindowsBuildTools("No archive.", version,
            static name => name is "meson" or "perl" or "bison" or "win_flex" ? name + ".exe" : null);
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PostgresProvisioner.RequireWindowsBuildTools("No archive.", version, static name => name == "meson" ? "meson.exe" : null));
        Assert.AreEqual("No archive. Building it from source needs Visual Studio's C++ tools and these programs on PATH: " +
            "perl (Strawberry Perl), win_bison or bison (winflexbison), win_flex or flex (winflexbison). Install them, or register " +
            "an existing build with 'ankus init --pg19 /path/to/pg_config.exe'.", error.Message);
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
    /// An installation reached through a symbolic link, as macOS's /tmp is, owns the resolved paths its pg_config reports,
    /// while a sibling directory or a link inside the installation that leads out of it does not.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public void OwnedPathsAreComparedWithLinksResolved()
    {
        string root = Directory.CreateTempSubdirectory("ankus-owned-").FullName;
        try
        {
            string physical = Directory.CreateDirectory(Path.Join(root, "private", "tmp", "postgres")).FullName;
            _ = Directory.CreateDirectory(Path.Join(root, "private", "tmp", "other"));
            _ = Directory.CreateSymbolicLink(Path.Join(root, "tmp"), Path.Join("private", "tmp"));
            string linked = Path.Join(root, "tmp", "postgres");
            PostgresProvisioner.RequireOwnedPath(linked, Path.Join(PostgresProvisioner.ResolveLinks(physical), "lib"));
            PostgresProvisioner.RequireOwnedPath(linked, Path.Join(linked, "lib"));
            Assert.ThrowsExactly<InvalidDataException>(() => PostgresProvisioner.RequireOwnedPath(linked, Path.Join(root, "private", "tmp", "other")));
            _ = Directory.CreateSymbolicLink(Path.Join(physical, "escape"), Path.Join(root, "private", "tmp", "other"));
            Assert.ThrowsExactly<InvalidDataException>(() => PostgresProvisioner.RequireOwnedPath(linked, Path.Join(linked, "escape", "lib")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Answers PostgreSQL's source index with fixed release links and refuses any other request.
    /// </summary>
    /// <param name="index">The index page.</param>
    private sealed class IndexHandler(string index) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsoluteUri.EndsWith("/source/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Only the source index may be requested: " + request.RequestUri);
            }

            return Task.FromResult(Respond(new StringContent(index)));
        }

        /// <summary>
        /// Creates a successful response that the HTTP client owns and disposes.
        /// </summary>
        private static HttpResponseMessage Respond(HttpContent content) => new(HttpStatusCode.OK) { Content = content };
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
