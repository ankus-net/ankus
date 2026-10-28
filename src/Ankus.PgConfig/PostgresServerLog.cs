using System;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Ankus.PgConfig;

/// <summary>
/// Retains a server's native stderr and isolated Windows event diagnostics across collection and process restarts.
/// </summary>
/// <param name="filePath">The retained diagnostic file, outside the server's data directory.</param>
/// <param name="identity">The unique server identity supplied to PostgreSQL through the event_source setting.</param>
public sealed class PostgresServerLog(string filePath, Guid identity)
{
    /// <summary>
    /// Identifies the combined snapshot and its owned cursor files.
    /// </summary>
    private readonly string _filePath = ValidatePath(filePath);

    /// <summary>
    /// Keeps each server's event cursor separate when an output filename is reused by a later invocation.
    /// </summary>
    private readonly string _statePath = ValidatePath(filePath) + ".events." + ValidateIdentity(identity).ToString("N") + ".json";

    /// <summary>
    /// Serializes threads using this collector before taking its cross-process cursor lock.
    /// </summary>
    private readonly Lock _readLock = new();

    /// <summary>
    /// Gets the native stderr target, separately from the retained Windows snapshot.
    /// </summary>
    public string NativeFilePath { get; } = OperatingSystem.IsWindows() ? ValidatePath(filePath) + ".stderr.log" : ValidatePath(filePath);

    /// <summary>
    /// Gets this server's distinct provider name without registering a source or exposing machine identifiers.
    /// </summary>
    public string EventSource { get; } = "Ankus-" + ValidateIdentity(identity).ToString("N");

    /// <summary>
    /// Reads current native diagnostics and updates the retained snapshot without duplicating earlier events.
    /// </summary>
    /// <param name="cancellationToken">Cancels collection or waiting for another collector's cursor lock.</param>
    /// <returns>The native stderr and all retained events belonging to this identity.</returns>
    public string Read(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ReadFile(NativeFilePath);
        }

        lock (_readLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            using FileStream cursorLock = AcquireCursorLock(cancellationToken);
            (long cursor, long created, string retained) = ReadState();
            long previousCursor = cursor;
            long previousCreated = created;
            var events = new StringBuilder(retained);
            if (cursor != 0 && !CursorStillExists(cursor, created))
            {
                // Application retention or clearing must not erase our retained text.
                // A missing cursor cannot overlap still-available older records.
                cursor = 0;
            }

            ReadEvents(events, ref cursor, ref created, cancellationToken);
            string text = ReadFile(NativeFilePath) + events;
            if (cursor != previousCursor || created != previousCreated || !File.Exists(_statePath))
            {
                WriteState(cursor, created, events.ToString());
            }

            bool logged = text.Length != 0 || File.Exists(NativeFilePath);
            if (logged && (ReadFile(_filePath) != text || !File.Exists(_filePath)))
            {
                ReplaceFile(_filePath, text);
            }

            return text;
        }
    }

    /// <summary>
    /// Reads a live native log while preserving PostgreSQL's writer and cleanup handles.
    /// </summary>
    /// <param name="path">The native file or retained snapshot to inspect.</param>
    /// <returns>The current text, or empty text for an absent file.</returns>
    public static string ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Waits only for an existing collector's sharing lock; unrelated filesystem failures remain visible.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for an existing reader.</param>
    /// <returns>The exclusive cursor lock held until this collection finishes.</returns>
    private FileStream AcquireCursorLock(CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(_filePath + ".events.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
            {
                cancellationToken.WaitHandle.WaitOne(10);
            }
        }
    }

    /// <summary>
    /// Loads owned event text and its cursor together, without reflection-based serialization.
    /// </summary>
    /// <returns>The last retained record, timestamp and original event text.</returns>
    private (long Cursor, long Created, string Events) ReadState()
    {
        if (!File.Exists(_statePath))
        {
            return (0, 0, string.Empty);
        }

        using JsonDocument document = JsonDocument.Parse(ReadFile(_statePath));
        JsonElement state = document.RootElement;
        if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("source", out JsonElement source) ||
            source.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("The retained PostgreSQL diagnostic cursor has no valid server identity.");
        }

        if (source.GetString() != EventSource)
        {
            throw new InvalidDataException("The retained PostgreSQL diagnostic cursor belongs to a different server identity.");
        }

        if (!state.TryGetProperty("cursor", out JsonElement savedCursor) || savedCursor.ValueKind != JsonValueKind.Number ||
            !savedCursor.TryGetInt64(out long cursor) || !state.TryGetProperty("created", out JsonElement savedCreated) ||
            savedCreated.ValueKind != JsonValueKind.Number || !savedCreated.TryGetInt64(out long created))
        {
            throw new InvalidDataException("A PostgreSQL diagnostic cursor must contain integer record and timestamp identities.");
        }

        if (cursor < 0 || created < 0)
        {
            throw new InvalidDataException("A PostgreSQL diagnostic cursor cannot be negative.");
        }

        if (!state.TryGetProperty("events", out JsonElement events) || events.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("A PostgreSQL diagnostic snapshot has no event text.");
        }

        return (cursor, created, events.GetString()!);
    }

    /// <summary>
    /// Atomically retains the cursor and original insertion strings before replacing the readable snapshot.
    /// </summary>
    /// <param name="cursor">The last retained record identity.</param>
    /// <param name="created">Its UTC creation ticks, which detect a cleared event log.</param>
    /// <param name="events">The retained original insertion strings.</param>
    private void WriteState(long cursor, long created, string events)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("source", EventSource);
            writer.WriteNumber("cursor", cursor);
            writer.WriteNumber("created", created);
            writer.WriteString("events", events);
            writer.WriteEndObject();
        }

        ReplaceFile(_statePath, Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)));
    }

    /// <summary>
    /// Checks the retained event's identity and timestamp before seeking past it after retention or log clearing.
    /// </summary>
    /// <param name="cursor">The retained event record identity.</param>
    /// <param name="created">The retained UTC creation ticks.</param>
    /// <returns>Whether this exact event still exists in the Application log.</returns>
    [SupportedOSPlatform("windows")]
    private bool CursorStillExists(long cursor, long created)
    {
        string query = string.Create(CultureInfo.InvariantCulture,
            $"*[System[Provider[@Name='{EventSource}'] and EventRecordID = {cursor}]]");
        using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query));
        using EventRecord? entry = reader.ReadEvent();
        return entry is not null && (entry.TimeCreated?.ToUniversalTime().Ticks ?? 0) == created;
    }

    /// <summary>
    /// Reads only this server's provider and new Application records, retaining original insertion strings.
    /// </summary>
    /// <param name="events">The retained diagnostic text to extend.</param>
    /// <param name="cursor">Receives the final record identity.</param>
    /// <param name="created">Receives the final record's UTC creation ticks.</param>
    /// <param name="cancellationToken">Cancels collection between records.</param>
    [SupportedOSPlatform("windows")]
    private void ReadEvents(StringBuilder events, ref long cursor, ref long created, CancellationToken cancellationToken)
    {
        string query = string.Create(CultureInfo.InvariantCulture,
            $"*[System[Provider[@Name='{EventSource}'] and EventRecordID > {cursor}]]");
        using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query));
        for (EventRecord? entry = reader.ReadEvent(); entry is not null; entry = reader.ReadEvent())
        {
            using (entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (EventProperty property in entry.Properties)
                {
                    if (property.Value is not string message)
                    {
                        continue;
                    }

                    events.Append(message);
                }

                cursor = entry.RecordId ?? throw new InvalidOperationException("A PostgreSQL event has no record identity.");
                created = entry.TimeCreated?.ToUniversalTime().Ticks ?? 0;
            }
        }
    }

    /// <summary>
    /// Replaces only this collector's output and removes its own staging file on failure.
    /// </summary>
    /// <param name="path">The owned output file.</param>
    /// <param name="text">The complete replacement snapshot.</param>
    private static void ReplaceFile(string path, string text)
    {
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(staging, text);
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    /// <summary>
    /// Validates a native log target without creating any directories or opening files.
    /// </summary>
    /// <param name="filePath">The supplied output path.</param>
    /// <returns>The absolute output path.</returns>
    private static string ValidatePath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Path.GetFullPath(filePath);
    }

    /// <summary>
    /// Rejects an empty identity, which would share one common provider among servers.
    /// </summary>
    /// <param name="identity">The cluster's supplied provider identity.</param>
    /// <returns>The validated identity.</returns>
    private static Guid ValidateIdentity(Guid identity)
    {
        if (identity == Guid.Empty)
        {
            throw new ArgumentException("A PostgreSQL diagnostic identity must be unique and nonempty.", nameof(identity));
        }

        return identity;
    }
}
