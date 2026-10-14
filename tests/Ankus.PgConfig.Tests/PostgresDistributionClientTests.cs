using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Exercises actual HTTP response handling and archive filesystem boundaries without remote dependencies.
/// </summary>
/// <param name="context">The current test's cancellation scope.</param>
[TestClass]
public sealed class PostgresDistributionClientTests(TestContext context)
{
    private static readonly TimeSpan[] s_firstTwoWaits = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];
    private static readonly TimeSpan[] s_allWaits = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly string _root = Path.Join(Path.GetTempPath(), "ankus-download-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Removes only this test's files, including after a failed assertion.
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
    /// Selects numeric versions independently of directory ordering, including stable preference and prerelease progression.
    /// </summary>
    [TestMethod]
    public async Task SelectsLatestAcrossEverySupportedMajor()
    {
        const string Index = """
            <a href="v13.9/"></a><a href="v13.23/"></a>
            <a href="v14.24/"></a><a href="v14.8/"></a>
            <a href="v15.19/"></a><a href="v15rc9/"></a>
            <a href="v16beta9/"></a><a href="v16rc1/"></a>
            <a href="v17beta10/"></a><a href="v17beta2/"></a>
            <a href="v18.0/"></a><a href="v18beta9/"></a>
            <a href="v19rc2/"></a><a href="v19rc1/"></a>
            <a href="https://untrusted.invalid/v18.99/"></a><a href="../v18.999/"></a>
            <a href="v12.99/"></a><a href="v20.0/"></a><a href="v18.2.3/"></a>
            """;
        using var handler = new DistributionHandler([], Index);
        using var client = new HttpClient(handler);
        IReadOnlyDictionary<int, PostgresVersion> versions = await new PostgresDistributionClient(client)
            .GetLatestAsync([13, 14, 15, 16, 17, 18, 19], context.CancellationToken);
        Assert.HasCount(7, versions);
        string[] expected = ["13.23", "14.24", "15.19", "16rc1", "17beta10", "18.0", "19rc2"];
        string[] actual = [.. versions.OrderBy(static pair => pair.Key).Select(static pair => pair.Value.ToString())];
        Assert.AreSequenceEqual(expected, actual);
        Assert.AreEqual("https://ftp.postgresql.org/pub/source/", Assert.ContainsSingle(handler.Requests));
    }

    /// <summary>
    /// Rejects unsupported requests before issuing HTTP requests.
    /// </summary>
    /// <param name="major">An out-of-contract major.</param>
    [TestMethod]
    [DataRow(12)]
    [DataRow(20)]
    public async Task RejectsUnsupportedMajorsBeforeHttp(int major)
    {
        using var handler = new DistributionHandler([]);
        using var client = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            new PostgresDistributionClient(client).GetLatestAsync([major], context.CancellationToken));
        Assert.IsEmpty(handler.Requests);
    }

    /// <summary>
    /// A missing requested major never falls back to another server or an invented version.
    /// </summary>
    [TestMethod]
    public async Task RejectsMissingRelease()
    {
        using var handler = new DistributionHandler([], "<a href=\"v18.6/\"></a>");
        using var client = new HttpClient(handler);
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new PostgresDistributionClient(client).GetLatestAsync([19], context.CancellationToken));
        Assert.Contains("PostgreSQL 19", error.Message);
    }

    /// <summary>
    /// Archive addresses contain only the independently parsed version and fixed upstream hosts.
    /// </summary>
    /// <param name="windows">Whether EDB Windows binaries are selected.</param>
    /// <param name="expected">The complete upstream URL.</param>
    [TestMethod]
    [DataRow(false, "https://ftp.postgresql.org/pub/source/v18.6/postgresql-18.6.tar.gz")]
    [DataRow(true, "https://get.enterprisedb.com/postgresql/postgresql-18.6-1-windows-x64-binaries.zip")]
    public void ConstructsTrustedArchiveUrls(bool windows, string expected)
    {
        Assert.AreEqual(expected, PostgresDistributionClient.GetArchiveUri(Version(), windows).AbsoluteUri);
    }

    /// <summary>
    /// Downloads real archive bytes, strips the one expected root, and leaves only the completed tree.
    /// </summary>
    /// <param name="zip">Whether to use the Windows ZIP representation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExtractsExactBytesAndCleansScratch(bool zip)
    {
        byte[] payload = [0, 255, 13, 10, 42];
        string root = zip ? "pgsql" : "postgresql-18.6";
        byte[] archive = Archive(zip, root + "/bin/probe", payload);
        using var handler = new DistributionHandler(archive);
        using var client = new HttpClient(handler);
        string destination = Path.Join(_root, "installed");
        await new PostgresDistributionClient(client).DownloadAsync(Version(), destination, zip, context.CancellationToken);
        Assert.AreSequenceEqual(payload, await File.ReadAllBytesAsync(Path.Join(destination, "bin", "probe"), context.CancellationToken));
        Assert.AreEqual(destination, Assert.ContainsSingle(Directory.GetFileSystemEntries(_root)));
        Assert.HasCount(zip ? 1 : 2, handler.Requests);
        Assert.AreEqual(zip ? "https://get.enterprisedb.com/postgresql/postgresql-18.6-1-windows-x64-binaries.zip"
            : "https://ftp.postgresql.org/pub/source/v18.6/postgresql-18.6.tar.gz", handler.Requests[0]);
    }

    /// <summary>
    /// Rejects archive paths that could escape the owned destination or change their meaning on Windows.
    /// </summary>
    /// <param name="suffix">The invalid member path.</param>
    /// <param name="zip">The archive representation.</param>
    [TestMethod]
    [DataRow("../outside", false)]
    [DataRow("../outside", true)]
    [DataRow("dir/../../outside", false)]
    [DataRow("dir/../../outside", true)]
    [DataRow("bin\\outside", false)]
    [DataRow("bin\\outside", true)]
    [DataRow("bin:stream", false)]
    [DataRow("bin:stream", true)]
    [DataRow("./bin/probe", false)]
    [DataRow("./bin/probe", true)]
    [DataRow("bin./probe", false)]
    [DataRow("bin./probe", true)]
    public async Task RejectsUnsafePathsAndRemovesPartialExtraction(string suffix, bool zip)
    {
        string root = zip ? "pgsql" : "postgresql-18.6";
        using var handler = new DistributionHandler(Archive(zip, root + "/" + suffix, [1]));
        using var client = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new PostgresDistributionClient(client)
            .DownloadAsync(Version(), Path.Join(_root, "installed"), zip, context.CancellationToken));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// Tar and ZIP links cannot redirect later file writes outside the installation.
    /// </summary>
    /// <param name="zip">The archive representation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RejectsLinks(bool zip)
    {
        using var handler = new DistributionHandler(Archive(zip, (zip ? "pgsql" : "postgresql-18.6") + "/link", [], link: true));
        using var client = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new PostgresDistributionClient(client)
            .DownloadAsync(Version(), Path.Join(_root, "installed"), zip, context.CancellationToken));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// A matching source archive must have an authentic, correctly named SHA-256 document before extraction.
    /// </summary>
    /// <param name="checksum">The bad checksum document.</param>
    [TestMethod]
    [DataRow("bad")]
    [DataRow("0000000000000000000000000000000000000000000000000000000000000000  postgresql-18.6.tar.gz")]
    [DataRow("0000000000000000000000000000000000000000000000000000000000000000  wrong.tar.gz")]
    public async Task RejectsChecksumFailuresWithoutPublishing(string checksum)
    {
        using var handler = new DistributionHandler(Archive(false, "postgresql-18.6/file", [1]), checksum: checksum);
        using var client = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new PostgresDistributionClient(client)
            .DownloadAsync(Version(), Path.Join(_root, "installed"), false, context.CancellationToken));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// Failed HTTP requests and cancellation never publish a partial destination.
    /// </summary>
    [TestMethod]
    public async Task HttpFailureAndCancellationLeaveNoInstallation()
    {
        using var handler = new DistributionHandler([], status: HttpStatusCode.NotFound);
        using var client = new HttpClient(handler);
        var distributions = new PostgresDistributionClient(client);
        string destination = Path.Join(_root, "installed");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => distributions.DownloadAsync(Version(), destination, false, context.CancellationToken));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        await canceled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => distributions.DownloadAsync(Version(), destination, false, canceled.Token));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// A missing Windows release has an actionable diagnostic and never substitutes an older server.
    /// </summary>
    [TestMethod]
    public async Task MissingWindowsArchivePreservesLatestVersionAndExplainsRegistration()
    {
        using var handler = new DistributionHandler([], status: HttpStatusCode.Forbidden);
        using var client = new HttpClient(handler);
        var version = new PostgresVersion(19, 0, PostgresReleaseStage.Beta, 4);
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new PostgresDistributionClient(client).DownloadAsync(version, Path.Join(_root, "installed"), true, context.CancellationToken));
        Assert.Contains("PostgreSQL 19beta4", error.Message);
        Assert.Contains("ankus init --pg19", error.Message);
        Assert.AreEqual("https://get.enterprisedb.com/postgresql/postgresql-19beta4-1-windows-x64-binaries.zip",
            Assert.ContainsSingle(handler.Requests));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// An existing destination is never overwritten or removed, even if it is empty or not a distribution.
    /// </summary>
    [TestMethod]
    public async Task PreservesExistingDestination()
    {
        string destination = Path.Join(_root, "installed");
        Directory.CreateDirectory(destination);
        string existing = Path.Join(destination, "keep");
        await File.WriteAllTextAsync(existing, "user data", context.CancellationToken);
        using var handler = new DistributionHandler([]);
        using var client = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<IOException>(() => new PostgresDistributionClient(client)
            .DownloadAsync(Version(), destination, false, context.CancellationToken));
        Assert.AreEqual("user data", await File.ReadAllTextAsync(existing, context.CancellationToken));
        Assert.IsEmpty(handler.Requests);
    }

    /// <summary>
    /// Truncated, wrong-root and duplicate-member archives leave no published or temporary files.
    /// </summary>
    /// <param name="zip">The archive representation.</param>
    /// <param name="failure">The independently corrupted archive contract.</param>
    [TestMethod]
    [DataRow(false, "root")]
    [DataRow(true, "root")]
    [DataRow(false, "truncated")]
    [DataRow(true, "truncated")]
    [DataRow(false, "duplicate")]
    [DataRow(true, "duplicate")]
    public async Task RejectsIncompleteOrAmbiguousArchives(bool zip, string failure)
    {
        string root = failure == "root" ? "unexpected" : zip ? "pgsql" : "postgresql-18.6";
        byte[] archive = Archive(zip, root + "/file", [7, 8, 9], duplicate: failure == "duplicate");
        if (failure == "truncated")
        {
            archive = archive[..20];
        }

        using var handler = new DistributionHandler(archive);
        using var client = new HttpClient(handler);
        Task Download() => new PostgresDistributionClient(client).DownloadAsync(Version(), Path.Join(_root, "installed"),
            zip, context.CancellationToken);
        if (failure == "duplicate")
        {
            await Assert.ThrowsAsync<IOException>(Download);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(Download);
        }

        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    private static PostgresVersion Version() => new(18, 6, PostgresReleaseStage.Stable, 0);

    private static byte[] Archive(bool zip, string name, byte[] payload, bool link = false, bool duplicate = false)
    {
        using var stream = new MemoryStream();
        if (zip)
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            ZipArchiveEntry entry = archive.CreateEntry(name);
            if (link)
            {
                entry.ExternalAttributes = 0xA1FF << 16;
            }

            using (Stream output = entry.Open())
            {
                output.Write(payload);
            }

            if (duplicate)
            {
                using Stream second = archive.CreateEntry(name).Open();
                second.Write(payload);
            }
        }
        else
        {
            using var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
            using var writer = new TarWriter(gzip, leaveOpen: true);
            writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry([new KeyValuePair<string, string>("comment", "upstream metadata")]));
            var entry = new PaxTarEntry(link ? TarEntryType.SymbolicLink : TarEntryType.RegularFile, name);
            using var body = new MemoryStream(payload);
            if (link)
            {
                entry.LinkName = "../../outside";
            }
            else
            {
                entry.DataStream = body;
            }

            writer.WriteEntry(entry);
            if (duplicate)
            {
                body.Position = 0;
                writer.WriteEntry(entry);
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Supplies deterministic upstream bytes while recording the actual requested URLs.
    /// </summary>
    /// <summary>
    /// Failed downloads are retried four times in all, waiting 1, 2 and 4 seconds and warning on one line before
    /// each retry, as cargo-pgrx's <c>download_postgres_archive_with_retries</c> does; a later success installs.
    /// </summary>
    [TestMethod]
    public async Task TransientDownloadFailuresAreRetriedWithBackoff()
    {
        byte[] archive = Archive(false, "postgresql-18.6/README", "ready"u8.ToArray());
        using var handler = new FlakyHandler(new DistributionHandler(archive), failures: 2);
        using var client = new HttpClient(handler);
        var waits = new List<TimeSpan>();
        var warnings = new List<string>();
        string destination = Path.Join(_root, "installed");
        await new PostgresDistributionClient(client, (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        }).DownloadAsync(Version(), destination, false, context.CancellationToken, new SynchronousProgress(warnings));
        Assert.AreEqual("ready", await File.ReadAllTextAsync(Path.Join(destination, "README"), context.CancellationToken));
        Assert.AreSequenceEqual(s_firstTwoWaits, waits);
        Assert.HasCount(2, warnings);
        Assert.StartsWith("Warning: PostgreSQL 18.6 download attempt 1/4 failed; retrying in 1s: ", warnings[0]);
        Assert.StartsWith("Warning: PostgreSQL 18.6 download attempt 2/4 failed; retrying in 2s: ", warnings[1]);
        Assert.DoesNotContain("\n", string.Concat(warnings), StringComparison.Ordinal);

        using var broken = new FlakyHandler(new DistributionHandler(archive), failures: int.MaxValue);
        using var failing = new HttpClient(broken);
        waits.Clear();
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new PostgresDistributionClient(failing, (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        }).DownloadAsync(Version(), Path.Join(_root, "never"), false, context.CancellationToken));
        Assert.AreSequenceEqual(s_allWaits, waits);
        Assert.AreEqual(PostgresDistributionClient.DownloadAttempts, broken.ArchiveRequests);
        Assert.IsFalse(Path.Exists(Path.Join(_root, "never")));
    }

    /// <summary>
    /// A retry warning keeps only the first line of the failure, as cargo-pgrx's
    /// <c>download_error_summary_keeps_retry_warning_single_line</c> checks.
    /// </summary>
    [TestMethod]
    public void DownloadErrorSummaryKeepsOneLine()
    {
        Assert.AreEqual("first line", PostgresDistributionClient.Summarize(new HttpRequestException("first line\nsecond line")));
        Assert.AreEqual("first line", PostgresDistributionClient.Summarize(new HttpRequestException("first line\r\nsecond line")));
        Assert.AreEqual("unknown download error", PostgresDistributionClient.Summarize(new IOException("\nsecond line")));
    }

    private sealed class FlakyHandler(HttpMessageHandler inner, int failures) : DelegatingHandler(inner)
    {
        internal int ArchiveRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.EndsWith(".tar.gz", StringComparison.Ordinal) && ArchiveRequests++ < failures)
            {
                return Task.FromResult(Respond(HttpStatusCode.ServiceUnavailable, new StringContent("upstream busy\nretry later")));
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Creates a response that the client receiving it disposes.
    /// </summary>
    private static HttpResponseMessage Respond(HttpStatusCode status, HttpContent content) => new(status) { Content = content };

    private sealed class SynchronousProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }

    private sealed class DistributionHandler(byte[] archive, string index = "", string? checksum = null,
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string uri = request.RequestUri!.AbsoluteUri;
            Requests.Add(uri);
            byte[] content = uri.EndsWith("/source/", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(index)
                : uri.EndsWith(".sha256", StringComparison.Ordinal)
                    ? Encoding.UTF8.GetBytes(checksum ?? Convert.ToHexStringLower(SHA256.HashData(archive)) + "  postgresql-18.6.tar.gz\n")
                    : archive;
            return Task.FromResult(Respond(status, new ByteArrayContent(content)));
        }
    }
}
