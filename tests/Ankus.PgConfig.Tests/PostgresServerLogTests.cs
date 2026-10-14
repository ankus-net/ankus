using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies diagnostic identities, live native file reads and cancellation before filesystem changes.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
public sealed class PostgresServerLogTests(TestContext context)
{
    /// <summary>
    /// Contains only files created by this test instance.
    /// </summary>
    private readonly string _root = Directory.CreateTempSubdirectory("ankus-server-log-").FullName;

    /// <summary>
    /// Removes only the test's owned files.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Describing a log resolves its native target and provider without creating files or directories.
    /// </summary>
    [TestMethod]
    public void ConstructionPreservesIdentityWithoutChanges()
    {
        string parent = Path.Join(_root, "missing");
        string path = Path.Join(parent, "native.log");
        Guid identity = Guid.NewGuid();
        var log = new PostgresServerLog(path, identity);
        Assert.AreEqual("Ankus-" + identity.ToString("N"), log.EventSource);
        Assert.AreEqual(OperatingSystem.IsWindows() ? path + ".stderr.log" : path, log.NativeFilePath);
        Assert.IsFalse(Directory.Exists(parent));
    }

    /// <summary>
    /// Relative log targets resolve once against the caller's current directory.
    /// </summary>
    [TestMethod]
    public void ConstructionNormalizesRelativeTarget()
    {
        string absolute = Path.Join(_root, "native.log");
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, absolute);
        var log = new PostgresServerLog(relative, Guid.NewGuid());
        Assert.AreEqual(OperatingSystem.IsWindows() ? absolute + ".stderr.log" : absolute, log.NativeFilePath);
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// Invalid log paths are rejected before creating output.
    /// </summary>
    /// <param name="path">The invalid native output path.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("bad\0path")]
    public void ConstructionRejectsInvalidPaths(string path)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PostgresServerLog(path, Guid.NewGuid()));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// A null log path reports the missing argument without creating output.
    /// </summary>
    [TestMethod]
    public void ConstructionRejectsNullPath()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new PostgresServerLog(null!, Guid.NewGuid()));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// An empty identity cannot attach to a shared provider or create output.
    /// </summary>
    [TestMethod]
    public void ConstructionRejectsEmptyIdentity()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() =>
            new PostgresServerLog(Path.Join(_root, "native.log"), Guid.Empty));
        Assert.AreEqual("identity", error.ParamName);
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// An absent native file remains absent after a read.
    /// </summary>
    [TestMethod]
    public void MissingNativeFileRemainsAbsent()
    {
        string path = Path.Join(_root, "missing", "native.log");
        Assert.AreEqual(string.Empty, PostgresServerLog.ReadFile(path));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// A missing or blank native filename is rejected before filesystem access.
    /// </summary>
    /// <param name="path">The invalid read target.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public void NativeReadsRejectBlankTargets(string path)
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => PostgresServerLog.ReadFile(path));
        Assert.AreEqual("path", error.ParamName);
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// A null native filename identifies the missing argument.
    /// </summary>
    [TestMethod]
    public void NativeReadsRejectNullTarget()
    {
        ArgumentNullException error = Assert.ThrowsExactly<ArgumentNullException>(() => PostgresServerLog.ReadFile(null!));
        Assert.AreEqual("path", error.ParamName);
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// Unicode diagnostics can be read while a native writer retains its handle and appends more text.
    /// </summary>
    [TestMethod]
    public void NativeReadsPreserveLiveWriterAndExactText()
    {
        string path = Path.Join(_root, "native.log");
        using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        const string First = "WARNING: café 🐘\nDETAIL: first\n";
        const string Second = "HINT: later\n";
        writer.Write(Encoding.UTF8.GetBytes(First));
        writer.Flush();
        Assert.AreEqual(First, PostgresServerLog.ReadFile(path));
        writer.Write(Encoding.UTF8.GetBytes(Second));
        writer.Flush();
        Assert.AreEqual(First + Second, PostgresServerLog.ReadFile(path));
        Assert.AreSequenceEqual([path], Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// A canceled collection never creates the missing output parent or cursor files.
    /// </summary>
    [TestMethod]
    public void CanceledCollectionMakesNoChanges()
    {
        string path = Path.Join(_root, "missing", "native.log");
        var log = new PostgresServerLog(path, Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OperationCanceledException error = Assert.ThrowsExactly<OperationCanceledException>(() => log.Read(cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// Unix collection reads the original native file without adding event cursor or snapshot files.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public void UnixCollectionPreservesNativeFileAndEmptyState()
    {
        string path = Path.Join(_root, "native.log");
        var log = new PostgresServerLog(path, Guid.NewGuid());
        Assert.AreEqual(string.Empty, log.Read(context.CancellationToken));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
        const string Message = "WARNING: café 🐘\n";
        File.WriteAllText(path, Message);
        Assert.AreEqual(Message, log.Read(context.CancellationToken));
        Assert.AreEqual(Message, log.Read(context.CancellationToken));
        Assert.AreEqual(Message, File.ReadAllText(path));
        Assert.AreSequenceEqual([path], Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// A recreated Windows collector retains exact native text and sees a later append without duplicating it.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void WindowsNativeCollectionSurvivesCollectorRecreation()
    {
        string path = Path.Join(_root, "native.log");
        Guid identity = Guid.NewGuid();
        var first = new PostgresServerLog(path, identity);
        Assert.AreEqual(string.Empty, first.Read(context.CancellationToken));
        Assert.IsFalse(File.Exists(path));
        const string Initial = "WARNING: café 🐘\nDETAIL: original\n";
        const string Appended = "HINT: later\n";
        File.WriteAllText(first.NativeFilePath, Initial);
        Assert.AreEqual(Initial, first.Read(context.CancellationToken));
        var next = new PostgresServerLog(path, identity);
        Assert.AreEqual(Initial, next.Read(context.CancellationToken));
        File.AppendAllText(first.NativeFilePath, Appended);
        Assert.AreEqual(Initial + Appended, next.Read(context.CancellationToken));
        Assert.AreEqual(Initial + Appended, first.Read(context.CancellationToken));
        Assert.AreEqual(Initial + Appended, File.ReadAllText(path));
        Assert.AreEqual(Initial + Appended, File.ReadAllText(first.NativeFilePath));
    }

    /// <summary>
    /// Corrupted ownership metadata cannot overwrite a cursor or retained snapshot belonging to another server.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void WindowsCollectionRejectsForeignCursorWithoutOverwritingFiles()
    {
        string path = Path.Join(_root, "native.log");
        Guid identity = Guid.NewGuid();
        string statePath = path + ".events." + identity.ToString("N") + ".json";
        string state = "{\"source\":\"Ankus-" + Guid.NewGuid().ToString("N") + "\",\"cursor\":0,\"created\":0,\"events\":\"other server\"}";
        const string Retained = "previous retained diagnostics\n";
        File.WriteAllText(statePath, state);
        File.WriteAllText(path, Retained);
        var log = new PostgresServerLog(path, identity);
        Assert.ThrowsExactly<InvalidDataException>(() => log.Read(context.CancellationToken));
        Assert.AreEqual(state, File.ReadAllText(statePath));
        Assert.AreEqual(Retained, File.ReadAllText(path));
        Assert.IsFalse(File.Exists(log.NativeFilePath));
    }

    /// <summary>
    /// Cancellation interrupts a read waiting for another process's cursor lock without replacing a snapshot.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task WindowsCollectionCancelsWhileCursorIsLocked()
    {
        string path = Path.Join(_root, "native.log");
        const string Retained = "previous retained diagnostics\n";
        File.WriteAllText(path, Retained);
        using var held = new FileStream(path + ".events.lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var log = new PostgresServerLog(path, Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> read = Task.Run(() =>
        {
            started.SetResult();
            return log.Read(cancellation.Token);
        }, context.CancellationToken);
        await started.Task.WaitAsync(context.CancellationToken);
        await cancellation.CancelAsync();
        OperationCanceledException error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => read);
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.AreEqual(Retained, File.ReadAllText(path));
        Assert.IsFalse(File.Exists(log.NativeFilePath));
    }

    /// <summary>
    /// Invalid owned metadata is rejected without replacing its bytes or the retained diagnostic snapshot.
    /// </summary>
    /// <param name="state">The invalid metadata with a placeholder for this collector's valid source.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("{\"source\":null}")]
    [DataRow("{\"source\":42}")]
    [DataRow("{\"source\":\"$source\"}")]
    [DataRow("{\"source\":\"$source\",\"cursor\":\"0\",\"created\":0,\"events\":\"kept\"}")]
    [DataRow("{\"source\":\"$source\",\"cursor\":9223372036854775808,\"created\":0,\"events\":\"kept\"}")]
    [DataRow("{\"source\":\"$source\",\"cursor\":-1,\"created\":0,\"events\":\"kept\"}")]
    [DataRow("{\"source\":\"$source\",\"cursor\":0,\"events\":\"kept\"}")]
    [DataRow("{\"source\":\"$source\",\"cursor\":0,\"created\":\"0\",\"events\":\"kept\"}")]
    [DataRow("{\"source\":\"$source\",\"cursor\":0,\"created\":9223372036854775808,\"events\":\"kept\"}")]
    [DataRow("{\"source\":\"$source\",\"cursor\":0,\"created\":-1,\"events\":\"kept\"}")]
    [DataRow("{\"source\":\"$source\",\"cursor\":0,\"created\":0}")]
    [DataRow("{\"source\":\"$source\",\"cursor\":0,\"created\":0,\"events\":null}")]
    [DataRow("{\"source\":\"$source\",\"cursor\":0,\"created\":0,\"events\":42}")]
    public void WindowsCollectionRejectsInvalidCursorStructure(string state)
    {
        string path = Path.Join(_root, "native.log");
        Guid identity = Guid.NewGuid();
        var log = new PostgresServerLog(path, identity);
        string statePath = path + ".events." + identity.ToString("N") + ".json";
        state = state.Replace("$source", log.EventSource, StringComparison.Ordinal);
        File.WriteAllText(statePath, state);
        const string Retained = "previous retained diagnostics\n";
        File.WriteAllText(path, Retained);
        Assert.ThrowsExactly<InvalidDataException>(() => log.Read(context.CancellationToken));
        Assert.AreEqual(state, File.ReadAllText(statePath));
        Assert.AreEqual(Retained, File.ReadAllText(path));
        Assert.IsFalse(File.Exists(log.NativeFilePath));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp"));
    }

    /// <summary>
    /// Incomplete JSON remains intact rather than silently discarding the retained event cursor.
    /// </summary>
    /// <param name="state">The incomplete JSON document.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow("")]
    [DataRow("{")]
    public void WindowsCollectionRejectsMalformedCursorJson(string state)
    {
        string path = Path.Join(_root, "native.log");
        Guid identity = Guid.NewGuid();
        string statePath = path + ".events." + identity.ToString("N") + ".json";
        File.WriteAllText(statePath, state);
        const string Retained = "previous retained diagnostics\n";
        File.WriteAllText(path, Retained);
        var log = new PostgresServerLog(path, identity);
        Assert.Throws<JsonException>(() => log.Read(context.CancellationToken));
        Assert.AreEqual(state, File.ReadAllText(statePath));
        Assert.AreEqual(Retained, File.ReadAllText(path));
        Assert.IsFalse(File.Exists(log.NativeFilePath));
    }

    /// <summary>
    /// A collector times out on an occupied sharing lock without modifying retained output.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void WindowsCollectionTimesOutWhileCursorIsLocked()
    {
        string path = Path.Join(_root, "native.log");
        const string Retained = "previous retained diagnostics\n";
        File.WriteAllText(path, Retained);
        using var held = new FileStream(path + ".events.lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var log = new PostgresServerLog(path, Guid.NewGuid());
        var elapsed = Stopwatch.StartNew();
        IOException error = Assert.ThrowsExactly<IOException>(() => log.Read(context.CancellationToken));
        Assert.Contains(error.HResult & 0xffff, [32, 33]);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(2), elapsed.Elapsed);
        Assert.AreEqual(Retained, File.ReadAllText(path));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.json"));
    }

    /// <summary>
    /// A filesystem failure unrelated to sharing is reported without overwriting the snapshot.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void WindowsCollectionRejectsInvalidLockTarget()
    {
        string path = Path.Join(_root, "native.log");
        const string Retained = "previous retained diagnostics\n";
        File.WriteAllText(path, Retained);
        Directory.CreateDirectory(path + ".events.lock");
        var log = new PostgresServerLog(path, Guid.NewGuid());
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => log.Read(context.CancellationToken));
        Assert.AreEqual(Retained, File.ReadAllText(path));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.json"));
    }

    /// <summary>
    /// Failed snapshot replacement removes its staging file and a later read recovers the original native text.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void WindowsCollectionReclaimsFailedSnapshotStaging()
    {
        string path = Path.Join(_root, "native.log");
        Guid identity = Guid.NewGuid();
        var log = new PostgresServerLog(path, identity);
        const string Native = "WARNING: retained café 🐘\n";
        File.WriteAllText(log.NativeFilePath, Native);
        Directory.CreateDirectory(path);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => log.Read(context.CancellationToken));
        Assert.IsTrue(Directory.Exists(path));
        Assert.AreEqual(Native, File.ReadAllText(log.NativeFilePath));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp"));
        Assert.HasCount(1, Directory.GetFiles(_root, "*.json"));
        Directory.Delete(path);
        Assert.AreEqual(Native, new PostgresServerLog(path, identity).Read(context.CancellationToken));
        Assert.AreEqual(Native, File.ReadAllText(path));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp"));
    }

    /// <summary>
    /// A missing retained Application record resets the seek cursor while preserving original event text exactly once.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void WindowsCollectionPreservesTextWhenSavedRecordNoLongerExists()
    {
        string path = Path.Join(_root, "native.log");
        Guid identity = Guid.NewGuid();
        var log = new PostgresServerLog(path, identity);
        string statePath = path + ".events." + identity.ToString("N") + ".json";
        const string Retained = "WARNING: retained event café 🐘\n";
        File.WriteAllText(statePath, "{\"source\":\"" + log.EventSource + "\",\"cursor\":9223372036854775807,\"created\":0,\"events\":\"WARNING: retained event café 🐘\\n\"}");
        Assert.AreEqual(Retained, log.Read(context.CancellationToken));
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(statePath));
        Assert.AreEqual(0L, saved.RootElement.GetProperty("cursor").GetInt64());
        Assert.AreEqual(Retained, saved.RootElement.GetProperty("events").GetString());
        Assert.AreEqual(Retained, new PostgresServerLog(path, identity).Read(context.CancellationToken));
        Assert.AreEqual(Retained, File.ReadAllText(path));
    }
}
