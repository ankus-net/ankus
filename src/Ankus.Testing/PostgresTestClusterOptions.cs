using Ankus.PgConfig;

namespace Ankus.Testing;

/// <summary>
/// Configures one isolated PostgreSQL test-cluster invocation.
/// </summary>
public sealed class PostgresTestClusterOptions
{
    /// <summary>
    /// Gets or sets the configured PostgreSQL installation used by the cluster.
    /// </summary>
    public required PostgresInstallation Installation
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the exact TCP port, from 1 through 65535, or null to reserve an automatic port.
    /// A requested port never changes silently when it is occupied.
    /// </summary>
    public int? Port
    {
        get;
        init;
    }

    /// <summary>
    /// Gets or sets the base directory under which per-invocation PGDATA directories
    /// are created. The ankus test command uses its own invocation directory beneath --pgdata when supplied,
    /// or temporary storage otherwise, so it can clean up aborted hosts.
    /// </summary>
    public string DataDirectoryBase
    {
        get;
        init;
    }
        = Path.Join(Directory.GetCurrentDirectory(), "artifacts", "test-pgdata");

    /// <summary>
    /// Gets or sets the architecture-independent PostgreSQL data directory supplied
    /// to <c>initdb -L</c>.
    /// </summary>
    public string? SharedDirectory
    {
        get;
        init;
    }

    /// <summary>
    /// Gets or sets the database created for extension tests.
    /// </summary>
    public string DatabaseName
    {
        get;
        init;
    } = "ankus_tests";

    /// <summary>
    /// Gets the bootstrap database superuser name, independent of the operating system account name.
    /// </summary>
    public string UserName
    {
        get;
        init;
    } = "ankus";

    /// <summary>
    /// Gets the Unix account that owns the cluster's data and runs its server through <c>sudo -u</c>, or null to use the
    /// account running the tests.
    /// </summary>
    /// <remarks>
    /// This corresponds to <c>cargo pgrx test --runas</c>; <c>ankus test --runas</c> selects it for every cluster in the
    /// run. The account creates the data and socket directories and runs <c>initdb</c> and <c>pg_ctl</c>, so it must be
    /// able to create directories in <see cref="DataDirectoryBase"/> and read the installation and extension files.
    /// The server log is read back through <c>sudo</c> into <see cref="LogDirectory"/>. Windows does not support another
    /// account.
    /// </remarks>
    public string? RunAs
    {
        get;
        init;
    }

    /// <summary>
    /// Gets whether the server runs under Valgrind's Memcheck tool.
    /// </summary>
    /// <remarks>
    /// This corresponds to pgrx's <c>USE_VALGRIND</c> and <c>ankus test --valgrind</c>. Valgrind must be on <c>PATH</c>, and
    /// Windows is not supported. Memory errors appear in the server log between <c>VALGRINDERROR-BEGIN</c> and
    /// <c>VALGRINDERROR-END</c> markers. Startup gets an extra minute beyond <see cref="StartupTimeout"/>.
    /// </remarks>
    public bool UseValgrind
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the directory where server logs remain available after cluster shutdown.
    /// </summary>
    public string LogDirectory
    {
        get;
        init;
    }
        = Path.Join(Directory.GetCurrentDirectory(), "artifacts", "test-logs");

    /// <summary>
    /// Gets additional settings appended to <c>postgresql.auto.conf</c> after the
    /// harness defaults, allowing extension settings to override them.
    /// </summary>
    /// <remarks>
    /// The harness defaults include <c>fsync = off</c>, as PostgreSQL's own test clusters use; supply
    /// <c>fsync = on</c> to test behavior that depends on it.
    /// </remarks>
    public IReadOnlyList<string> PostgreSqlConfiguration
    {
        get;
        init;
    } = [];

    /// <summary>
    /// Gets environment variables added to every PostgreSQL process invocation.
    /// </summary>
    public IReadOnlyDictionary<string, string?> ProcessEnvironment
    {
        get;
        init;
    }
        = new Dictionary<string, string?>();

    /// <summary>
    /// Gets the maximum time allowed for initialization, server readiness and creation of the test database.
    /// </summary>
    /// <remarks>
    /// The default is 180 seconds, the default timeout of PostgreSQL's own TAP test framework
    /// (<c>PG_TEST_TIMEOUT_DEFAULT</c>), because these steps share one budget on hosts whose disks may be busy.
    /// </remarks>
    public TimeSpan StartupTimeout
    {
        get;
        init;
    } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Gets the independent timeout used for shutdown, even when a test's cancellation token has been canceled.
    /// </summary>
    /// <remarks>
    /// It bounds the server's stop. Checking whether the server is still running has its own allowance.
    /// </remarks>
    public TimeSpan ShutdownTimeout
    {
        get;
        init;
    } = TimeSpan.FromSeconds(30);
}
