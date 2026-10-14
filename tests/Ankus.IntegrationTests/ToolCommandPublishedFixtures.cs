using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

/// <summary>
/// Owns immutable packaged probes reused by cases that vary only their backend operations.
/// </summary>
public sealed partial class ToolCommandTests
{
    private static CancellationTokenSource? s_publicationLifetime;
    private static Lazy<Task<string>>? s_workerCancellationPublication;
    private static Lazy<Task<string>>? s_lwLockInterruptPublication;
    private static Lazy<Task<SharedOutput>>? s_extensionSearchPathPublication;
    private static ConcurrentDictionary<string, Lazy<Task<SharedOutput>>>? s_contentOutputs;
    private static ConcurrentDictionary<string, ReusableProject>? s_reusableProjects;

    /// <summary>
    /// Retains one binary per probe while every consuming case owns a separate PostgreSQL server.
    /// </summary>
    private static void InitializeSharedPublications()
    {
        s_publicationLifetime = new CancellationTokenSource();
        CancellationToken token = s_publicationLifetime.Token;
        s_workerCancellationPublication = new(() => PublishPackageConsumerInDirectoryAsync(CreateSharedDirectory(),
            "WorkerCancellation", "ankus_worker_cancellation", WorkerCancellationSource, token));
        s_lwLockInterruptPublication = new(() => PublishPackageConsumerInDirectoryAsync(CreateSharedDirectory(),
            "LwLockInterrupts", "ankus_lwlock_interrupts", LwLockInterruptSource, token));
        s_extensionSearchPathPublication = new(() => PublishExtensionSearchPathAsync(CreateSharedDirectory(), token));
        s_contentOutputs = new(StringComparer.Ordinal);
        s_reusableProjects = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Cancels and joins fixture-owned compilation before class cleanup removes its output.
    /// </summary>
    private static async Task CleanupSharedPublicationsAsync()
    {
        if (s_publicationLifetime is null)
        {
            return;
        }

        await s_publicationLifetime.CancelAsync();
        List<Task> producers = [];
        Lazy<Task<string>>?[] publications = [s_workerCancellationPublication, s_lwLockInterruptPublication];
        producers.AddRange(publications.OfType<Lazy<Task<string>>>().Where(static publication => publication.IsValueCreated)
            .Select(static publication => publication.Value));

        if (s_extensionSearchPathPublication is { IsValueCreated: true })
        {
            producers.Add(s_extensionSearchPathPublication.Value);
        }

        if (s_contentOutputs is not null)
        {
            producers.AddRange(s_contentOutputs.Values.Where(static output => output.IsValueCreated).Select(static output => output.Value));
        }

        foreach (Task producer in producers)
        {
            // Consumers observe publication failures. Cleanup must still join the
            // producer when a test cancellation stopped waiting for its result.
            await producer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        s_publicationLifetime.Dispose();
        s_publicationLifetime = null;
        s_workerCancellationPublication = null;
        s_lwLockInterruptPublication = null;
        s_extensionSearchPathPublication = null;
        s_contentOutputs = null;
        s_reusableProjects = null;
    }

    /// <summary>
    /// Lets one case cancel its wait without cancelling another case's shared compilation.
    /// </summary>
    /// <typeparam name="T">The fixture's immutable result.</typeparam>
    /// <param name="publication">The class-owned producer.</param>
    /// <param name="token">Cancels only this case's wait.</param>
    /// <returns>The shared result.</returns>
    private static Task<T> GetSharedPublicationAsync<T>(Lazy<Task<T>>? publication, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (publication is null)
        {
            throw new InvalidOperationException("The packaged probe fixture is not initialized.");
        }

        return publication.Value.WaitAsync(token);
    }

    /// <summary>
    /// Runs one fixture-owned producer for each content key and shares its immutable output with every matching case.
    /// </summary>
    /// <param name="key">The exact input identity from <see cref="ContentKey"/>.</param>
    /// <param name="produce">
    /// Creates the output under the class lifetime. It must snapshot the caller's inputs before its first await.
    /// </param>
    /// <param name="token">Cancels only this case's wait.</param>
    /// <returns>The shared output.</returns>
    private static Task<SharedOutput> GetSharedOutputAsync(string key, Func<CancellationToken, Task<SharedOutput>> produce,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (s_contentOutputs is null || s_publicationLifetime is null)
        {
            throw new InvalidOperationException("The shared output fixture is not initialized.");
        }

        CancellationToken lifetime = s_publicationLifetime.Token;
        Lazy<Task<SharedOutput>> output = s_contentOutputs.GetOrAdd(key, _ => new(() => produce(lifetime)));
        return output.Value.WaitAsync(token);
    }

    /// <summary>
    /// Identifies an operation together with every relative path and byte beneath a directory and that directory's name.
    /// </summary>
    /// <param name="directory">The complete input tree.</param>
    /// <param name="operation">The command and options that will consume the tree.</param>
    /// <returns>A hexadecimal SHA-256 identity.</returns>
    private static string ContentKey(string directory, params string[] operation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string part in operation)
        {
            AppendText(part);
        }

        AppendText(Path.GetFileName(directory));
        string[] files = [.. Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];
        foreach (string file in files)
        {
            AppendText(file);
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(Path.Join(directory, file))));
        }

        return Convert.ToHexString(hash.GetHashAndReset());

        void AppendText(string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }
    }

    /// <summary>
    /// Copies a case's input tree into class-owned storage with the same directory name, so later cleanup cannot remove it.
    /// </summary>
    /// <param name="source">The case-owned input tree.</param>
    /// <returns>The class-owned copy.</returns>
    private static string CopySharedSnapshot(string source)
    {
        string destination = Path.Join(CreateSharedDirectory(), Path.GetFileName(source));
        CopyFiles(source, destination);
        return destination;
    }

    /// <summary>
    /// Copies every file beneath a directory, preserving relative paths and refusing to replace existing files.
    /// </summary>
    /// <param name="source">The directory to copy.</param>
    /// <param name="destination">The destination root.</param>
    private static void CopyFiles(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Join(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>
    /// Reserves one class-owned project per variant, so cases that differ only at run time build it once.
    /// </summary>
    /// <param name="variant">Names the project content; every case using it must create identical files.</param>
    /// <param name="create">Creates the project beneath a class-owned directory and returns its root.</param>
    /// <param name="evidence">
    /// Relative files and directories that one case's run writes and asserts. They are removed before reuse,
    /// so the next case must produce its own.
    /// </param>
    /// <param name="token">Cancels the wait, creation and cleanup.</param>
    /// <returns>An exclusive lease; later cases rebuild the project incrementally.</returns>
    private static async Task<ReusableProjectLease> AcquireReusableProjectAsync(string variant,
        Func<string, CancellationToken, Task<string>> create, string[] evidence, CancellationToken token)
    {
        ConcurrentDictionary<string, ReusableProject> projects = s_reusableProjects
            ?? throw new InvalidOperationException("The reusable project fixture is not initialized.");
        ReusableProject project = projects.GetOrAdd(variant, static _ => new ReusableProject());
        await project.Gate.WaitAsync(token);
        try
        {
            if (project.Directory is null)
            {
                project.Directory = await create(CreateSharedDirectory(), token);
            }
            else
            {
                foreach (string path in evidence.Select(relative => Path.Join(project.Directory, relative)))
                {
                    DeleteCaseDirectory(path);
                    File.Delete(path);
                }
            }

            return new ReusableProjectLease(project);
        }
        catch
        {
            project.Gate.Release();
            throw;
        }
    }

    /// <summary>
    /// A class-owned project and the gate that gives one case at a time exclusive use of it.
    /// </summary>
    private sealed class ReusableProject
    {
        /// <summary>
        /// Gets the gate held by the case using the project.
        /// </summary>
        internal SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>
        /// Gets or sets the created project, or null until a case creates it successfully.
        /// </summary>
        internal string? Directory
        {
            get;
            set;
        }
    }

    /// <summary>
    /// Exclusive use of a reusable project until the case's processes and assertions finish.
    /// </summary>
    /// <param name="project">The reserved project.</param>
    private sealed class ReusableProjectLease(ReusableProject project) : IDisposable
    {
        /// <summary>
        /// Gets the reserved project's root.
        /// </summary>
        internal string Directory => project.Directory!;

        /// <summary>
        /// Releases the project to the next case.
        /// </summary>
        public void Dispose() => project.Gate.Release();
    }

    /// <summary>
    /// A class-owned output directory and the process result that produced it.
    /// </summary>
    /// <param name="Directory">The immutable output; consumers copy it before changing anything.</param>
    /// <param name="Result">The producing command's exit code and streams.</param>
    /// <param name="Listing">A test listing run before the command, when one was requested.</param>
    /// <param name="PublishedDuringListing">Whether the listing published an extension.</param>
    private sealed record SharedOutput(string Directory, ProcessResult Result, ProcessResult? Listing = null,
        bool PublishedDuringListing = false);
}
