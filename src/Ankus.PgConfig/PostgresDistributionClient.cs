using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Ankus.PgConfig;

/// <summary>
/// Retrieves PostgreSQL distributions from the upstream source archive or EDB's Windows archive.
/// </summary>
/// <param name="client">The caller-owned HTTP client.</param>
/// <param name="delay">Waits between download attempts; tests replace the real delay.</param>
internal sealed partial class PostgresDistributionClient(HttpClient client, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>
    /// The download attempts before failing, as cargo-pgrx makes, waiting 1, 2 and 4 seconds between them.
    /// </summary>
    internal const int DownloadAttempts = 4;

    private const long DownloadLimit = 2L * 1024 * 1024 * 1024;
    private const long ExpandedLimit = 8L * 1024 * 1024 * 1024;
    private static readonly Uri s_sourceIndex = new("https://ftp.postgresql.org/pub/source/");

    /// <summary>
    /// Discovers the newest published release in each requested major, preferring stable releases.
    /// </summary>
    internal async Task<IReadOnlyDictionary<int, PostgresVersion>> GetLatestAsync(
        IReadOnlyCollection<int> majors, CancellationToken cancellationToken)
    {
        foreach (int major in majors)
        {
            ValidateMajor(major);
        }

        using HttpResponseMessage response = await client.GetAsync(s_sourceIndex,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken).ConfigureAwait(false);
        string index = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var releases = new Dictionary<int, PostgresVersion>();
        foreach (Match match in ReleaseLink().Matches(index))
        {
            PostgresVersion version = PostgresVersion.Parse("PostgreSQL " + match.Groups[1].Value);
            if (majors.Contains(version.Major) &&
                (!releases.TryGetValue(version.Major, out PostgresVersion previous) || Compare(version, previous) > 0))
            {
                releases[version.Major] = version;
            }
        }

        foreach (int major in majors)
        {
            if (!releases.ContainsKey(major))
            {
                throw new InvalidOperationException($"No published PostgreSQL {major} release was found at {s_sourceIndex}.");
            }
        }

        return releases;
    }

    /// <summary>
    /// Downloads and extracts a distribution atomically into a new directory, removing incomplete work on failure.
    /// </summary>
    internal async Task DownloadAsync(PostgresVersion version, string destination, bool windowsBinaries,
        CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        ValidateMajor(version.Major);
        string target = Path.GetFullPath(destination);
        if (Path.Exists(target))
        {
            throw new IOException($"The PostgreSQL download destination already exists: {target}");
        }

        string scratch = target + "." + Guid.NewGuid().ToString("N") + ".download";
        Directory.CreateDirectory(scratch);
        try
        {
            Uri uri = GetArchiveUri(version, windowsBinaries);
            string archive = Path.Join(scratch, "archive");
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadArchiveAsync(uri, archive, version, windowsBinaries, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (Exception error) when (attempt < DownloadAttempts && IsTransient(error, cancellationToken))
                {
                    TimeSpan wait = TimeSpan.FromSeconds(1 << (attempt - 1));
                    progress?.Report(string.Create(CultureInfo.InvariantCulture,
                        $"Warning: PostgreSQL {version} download attempt {attempt}/{DownloadAttempts} failed; retrying in {wait.TotalSeconds}s: {Summarize(error)}"));
                    File.Delete(archive);
                    await (delay ?? Task.Delay)(wait, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!windowsBinaries)
            {
                await VerifyChecksumAsync(uri, archive, cancellationToken).ConfigureAwait(false);
            }

            string extracted = Path.Join(scratch, "extracted");
            Directory.CreateDirectory(extracted);
            try
            {
                await ExtractAsync(archive, extracted, windowsBinaries ? "pgsql" : $"postgresql-{version}",
                    windowsBinaries, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException error)
            {
                throw new InvalidDataException("The PostgreSQL archive is truncated.", error);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(extracted, target);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// Returns the first line of a download failure, so a retry warning stays on one line.
    /// </summary>
    /// <param name="error">The failure.</param>
    /// <returns>The first line of its message.</returns>
    internal static string Summarize(Exception error)
    {
        string message = error.Message;
        int end = message.IndexOfAny(['\r', '\n']);
        string first = end < 0 ? message : message[..end];
        return first.Length == 0 ? "unknown download error" : first;
    }

    private async Task DownloadArchiveAsync(Uri uri, string archive, PostgresVersion version, bool windowsBinaries,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(uri,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (windowsBinaries && response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            throw new PostgresArchiveUnavailableException($"EDB has no available PostgreSQL {version} Windows x64 archive.");
        }

        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await CopyBoundedAsync(body, output, DownloadLimit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Selects failures another attempt can fix: a refused or failed request, an error status, an interrupted body
    /// or a request timeout, but not cancellation, an oversized archive or a missing Windows archive.
    /// </summary>
    private static bool IsTransient(Exception error, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested && error switch
        {
            HttpRequestException => true,
            TaskCanceledException => true,
            InvalidDataException => false,
            IOException => true,
            _ => false,
        };

    /// <summary>
    /// Constructs a URL from a validated version without trusting links supplied by a remote index.
    /// </summary>
    internal static Uri GetArchiveUri(PostgresVersion version, bool windowsBinaries)
    {
        ValidateMajor(version.Major);
        return windowsBinaries
            ? new Uri($"https://get.enterprisedb.com/postgresql/postgresql-{version}-1-windows-x64-binaries.zip")
            : new Uri(s_sourceIndex, $"v{version}/postgresql-{version}.tar.gz");
    }

    private async Task VerifyChecksumAsync(Uri uri, string archive, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(uri.AbsoluteUri + ".sha256"),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(4096, cancellationToken).ConfigureAwait(false);
        string checksum = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        string[] fields = checksum.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 || fields[0].Length != 64 || !fields[0].All(char.IsAsciiHexDigit) ||
            fields[1] != Path.GetFileName(uri.AbsolutePath))
        {
            throw new InvalidDataException($"Invalid PostgreSQL SHA-256 document from {uri}.sha256.");
        }

        await using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] actual = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(fields[0]), actual))
        {
            throw new InvalidDataException($"PostgreSQL archive SHA-256 verification failed for {uri}.");
        }
    }

    private static async Task ExtractAsync(string archive, string destination, string root, bool zip,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        long remaining = ExpandedLimit;
        int files = 0;
        if (zip)
        {
            using var reader = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            foreach (ZipArchiveEntry entry in reader.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (((entry.ExternalAttributes >> 16) & 0xF000) is not (0 or 0x8000 or 0x4000))
                {
                    throw new InvalidDataException($"Unsupported PostgreSQL archive entry: {entry.FullName}");
                }

                string path = GetEntryPath(destination, root, entry.FullName);
                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                remaining = RequireSpace(entry.Length, remaining, ref files);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using Stream body = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await CopyBoundedAsync(body, output, entry.Length, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new TarReader(gzip, leaveOpen: true);
            while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is TarEntry entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
                {
                    continue;
                }

                string path = GetEntryPath(destination, root, entry.Name);
                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    throw new InvalidDataException($"Unsupported PostgreSQL archive entry: {entry.Name}");
                }

                remaining = RequireSpace(entry.Length, remaining, ref files);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await entry.ExtractToFileAsync(path, overwrite: false, cancellationToken).ConfigureAwait(false);
            }
        }

        if (files == 0)
        {
            throw new InvalidDataException("The PostgreSQL archive contains no files.");
        }
    }

    private static long RequireSpace(long length, long remaining, ref int files)
    {
        if (length < 0 || length > remaining || ++files > 100_000)
        {
            throw new InvalidDataException("The PostgreSQL archive exceeds the extraction limit.");
        }

        return remaining - length;
    }

    private static string GetEntryPath(string destination, string root, string name)
    {
        string[] parts = name.TrimEnd('/').Split('/');
        if (parts[0] != root || parts.Any(static part => part.Length == 0 || part is "." or ".." ||
            part.IndexOfAny(['\\', ':', '\0']) >= 0 || part.EndsWith(' ') || part.EndsWith('.')))
        {
            throw new InvalidDataException($"Invalid PostgreSQL archive path: {name}");
        }

        return Path.Join([destination, .. parts.Skip(1)]);
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            limit -= count;
            if (limit < 0)
            {
                throw new InvalidDataException("The PostgreSQL download exceeds its size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    private static int Compare(PostgresVersion left, PostgresVersion right)
    {
        int stage = Rank(left.Stage).CompareTo(Rank(right.Stage));
        return stage != 0 ? stage : left.Stage == PostgresReleaseStage.Stable
            ? left.Minor.CompareTo(right.Minor) : left.StageNumber.CompareTo(right.StageNumber);
    }

    private static int Rank(PostgresReleaseStage stage) => stage switch
    {
        PostgresReleaseStage.Stable => 2,
        PostgresReleaseStage.ReleaseCandidate => 1,
        _ => 0,
    };

    private static void ValidateMajor(int major)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(major, 13);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(major, 19);
    }

    [GeneratedRegex("href=\"v([1-9][0-9]?(?:\\.[0-9]{1,4}|(?:beta|rc)[1-9][0-9]?))/\"", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseLink();
}
