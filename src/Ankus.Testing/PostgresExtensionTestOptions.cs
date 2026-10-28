using System.Collections.Generic;
using Ankus.PgConfig;

namespace Ankus.Testing;

/// <summary>
/// Selects the extension publication and isolated PostgreSQL environment owned by a test fixture.
/// </summary>
public sealed class PostgresExtensionTestOptions
{
    /// <summary>
    /// Gets the extension project to publish.
    /// </summary>
    public required string ProjectPath
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the selected PostgreSQL installation, or null for ordinary fixture discovery.
    /// </summary>
    public PostgresInstallation? Installation
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the MSBuild configuration used to publish the extension. The default is the ankus test
    /// command's configuration when invoked through that command, and Release otherwise.
    /// </summary>
    public string Configuration
    {
        get;
        init;
    } = TestCommandContext.Configuration;

    /// <summary>
    /// Gets literal MSBuild properties used for project selection and native publication.
    /// The default contains the effective selection and properties forwarded through ankus test,
    /// or an empty collection for direct fixture invocation.
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildProperties
    {
        get;
        init;
    } = TestCommandContext.BuildProperties;

    /// <summary>
    /// Gets whether the publication includes PgTest native entry points and SQL. The default is false.
    /// </summary>
    public bool IncludeTests
    {
        get;
        init;
    }

    /// <summary>
    /// Gets whether to reuse the last successful schema for this build while recompiling native code.
    /// The default follows ankus test --no-schema, or false for ordinary fixture invocation.
    /// Reuse requires unchanged native declarations and a previous successful publication for the same target and test mode.
    /// </summary>
    public bool ReuseSchema
    {
        get;
        init;
    } = TestCommandContext.ReuseSchema;

    /// <summary>
    /// Gets whether to load the extension during shared preload before any test backends start.
    /// </summary>
    public bool SharedPreload
    {
        get;
        init;
    }

    /// <summary>
    /// Gets whether the test server runs under Valgrind's Memcheck tool, as pgrx's <c>USE_VALGRIND</c> does.
    /// </summary>
    /// <remarks>
    /// <c>ankus test --valgrind</c> enables it for every fixture in the run. See <see cref="PostgresTestClusterOptions.UseValgrind"/>.
    /// </remarks>
    public bool UseValgrind
    {
        get;
        init;
    }

    /// <summary>
    /// Gets an exact requested TCP port, or null for automatic reservation.
    /// </summary>
    public int? Port
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the parent for isolated cluster data, or null for the system temporary directory.
    /// Only the fixture's unique child directory is removed. The ankus test command controls this location with --pgdata.
    /// </summary>
    public string? DataDirectoryBase
    {
        get;
        init;
    }

    /// <summary>
    /// Gets additional postgresql.conf lines for the isolated cluster.
    /// </summary>
    public IReadOnlyList<string> PostgreSqlConfiguration
    {
        get;
        init;
    } = [];
}
