using System;
using System.Collections.Generic;

namespace Ankus.PgConfig;

/// <summary>
/// Configures a local development build of PostgreSQL.
/// </summary>
public sealed class PostgresProvisionOptions
{
    /// <summary>
    /// Gets the maximum parallel make jobs, defaulting to the available processor count.
    /// </summary>
    public int Jobs
    {
        get;
        init;
    } = Environment.ProcessorCount;

    /// <summary>
    /// Gets additional arguments passed separately to PostgreSQL's configure script on Unix.
    /// Installation-directory overrides are not allowed.
    /// </summary>
    public IReadOnlyList<string> ConfigureFlags
    {
        get;
        init;
    } = [];

    /// <summary>
    /// Gets whether Unix builds include PostgreSQL's Valgrind memory-context instrumentation.
    /// </summary>
    public bool EnableValgrind
    {
        get;
        init;
    }

    /// <summary>
    /// Gets whether Unix builds leave out the assertions and allocated-memory randomization that development builds use,
    /// which catch mistakes early but make the server much slower, so that benchmarks measure the extension. Debug symbols
    /// are kept either way.
    /// </summary>
    public bool Release
    {
        get;
        init;
    }
}
