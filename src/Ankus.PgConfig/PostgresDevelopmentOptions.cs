using System.Collections.Generic;

namespace Ankus.PgConfig;

/// <summary>
/// Configures a local development server started by Ankus.
/// </summary>
public sealed class PostgresDevelopmentOptions
{
    /// <summary>
    /// Gets whether to run PostgreSQL under Valgrind Memcheck from PATH, writing diagnostics to the server log.
    /// Requires a supported Unix platform and an installed Valgrind. An already running server keeps its current mode.
    /// </summary>
    public bool UseValgrind
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the TCP port, or null to use the Ankus home's configured base plus the PostgreSQL major version.
    /// An unconfigured home uses a base of 28800.
    /// </summary>
    public int? Port
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the maximum number of seconds to wait for server startup, from 1 through 600.
    /// </summary>
    public int TimeoutSeconds
    {
        get;
        init;
    } = 60;

    /// <summary>
    /// Gets PostgreSQL settings applied on this start. Values are literal configuration values, without surrounding quotes.
    /// Data, authentication, log routing, and connection locations are managed by Ankus.
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings
    {
        get;
        init;
    } = new Dictionary<string, string>();
}
