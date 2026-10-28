using System;

namespace Ankus.PgConfig;

/// <summary>
/// Reports that EDB publishes no Windows x64 binary archive for a PostgreSQL release, as for some betas and release
/// candidates.
/// </summary>
/// <param name="message">The description naming the release.</param>
internal sealed class PostgresArchiveUnavailableException(string message) : InvalidOperationException(message);
