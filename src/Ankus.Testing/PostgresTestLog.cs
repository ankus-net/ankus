using System;
using System.IO;
using Ankus.PgConfig;

namespace Ankus.Testing;

/// <summary>
/// Supplies a distinct server identity to the shared PostgreSQL diagnostic collector.
/// </summary>
internal sealed class PostgresTestLog
{
    /// <summary>
    /// Owns this invocation's diagnostics through the configuration package's deliberate public boundary.
    /// </summary>
    private readonly PostgresServerLog _log;

    /// <summary>
    /// Gets the account that owns a native log outside the retained log, or null when the server writes the retained log.
    /// </summary>
    private readonly PostgresServerAccount? _account;

    /// <summary>
    /// Initializes diagnostics for a server that writes the retained log itself.
    /// </summary>
    /// <param name="filePath">The retained log outside this invocation's data directory.</param>
    internal PostgresTestLog(string filePath)
    {
        _log = new(filePath, Guid.NewGuid());
        NativeFilePath = _log.NativeFilePath;
    }

    /// <summary>
    /// Initializes diagnostics for a server running as another account, which writes a native log it owns.
    /// </summary>
    /// <param name="filePath">The retained log, refreshed from the native log on each read.</param>
    /// <param name="account">The account that owns the native log.</param>
    /// <param name="nativeFilePath">The log the server writes.</param>
    internal PostgresTestLog(string filePath, PostgresServerAccount account, string nativeFilePath)
    {
        _log = new(filePath, Guid.NewGuid());
        _account = account;
        NativeFilePath = nativeFilePath;
    }

    /// <summary>
    /// Gets the stderr target held open by PostgreSQL.
    /// </summary>
    internal string NativeFilePath { get; }

    /// <summary>
    /// Gets this invocation's isolated event provider.
    /// </summary>
    internal string EventSource => _log.EventSource;

    /// <summary>
    /// Reads and retains this invocation's diagnostic snapshot.
    /// </summary>
    /// <returns>The available log text.</returns>
    internal string Read()
    {
        if (_account is null)
        {
            return _log.Read();
        }

        string text = _account.ReadFile(NativeFilePath);
        File.WriteAllText(_log.NativeFilePath, text);
        return text;
    }
}
