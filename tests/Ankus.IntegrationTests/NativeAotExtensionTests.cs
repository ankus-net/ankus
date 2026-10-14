using System.Globalization;
using Ankus.PgConfig;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies the published native extension by calling it from real PostgreSQL backend processes.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed class NativeAotExtensionTests(TestContext context)
{
    /// <summary>
    /// Initializes the shared local PostgreSQL fixture before any integration test runs.
    /// </summary>
    /// <param name="context">The assembly context.</param>
    [AssemblyInitialize]
    public static Task InitializeAssemblyAsync(TestContext context)
    {
        // As PostgreSQL's own harness does on slow machines, give pg_ctl waits without an explicit limit the suite's
        // startup budget, unless the caller chose one.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PGCTLTIMEOUT")))
        {
            Environment.SetEnvironmentVariable("PGCTLTIMEOUT",
                ((int)IntegrationEnvironment.StartupTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        // The suite starts hundreds of dotnet builds. With CLI telemetry on, the SDK can deadlock: its telemetry setup
        // loads an assembly while MSBuild, holding its logging lock, hands that telemetry an event that needs the same
        // load, and the build never returns. Without telemetry neither path runs.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT")))
        {
            Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        }

        IntegrationEnvironment.PruneTestLogs();
        return PostgresFixture.InitializeAsync(context);
    }

    /// <summary>
    /// Stops the shared PostgreSQL cluster after all integration tests finish.
    /// </summary>
    [AssemblyCleanup]
    public static Task CleanupAssemblyAsync() => PostgresFixture.CleanupAsync();

    /// <summary>
    /// Publication and SQL use the selected backend's suffix, allowing ordinary unqualified library loading.
    /// </summary>
    [TestMethod]
    public async Task PublishedLibraryMatchesPostgresLoaderSuffix()
    {
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(context.CancellationToken);
        PublishedExtension manifest = PublishedExtension.Read(IntegrationEnvironment.NativeOutputDirectory);
        string suffix = OperatingSystem.IsWindows() ? ".dll"
            : OperatingSystem.IsMacOS() && installation.Version.Major >= 16 ? ".dylib" : ".so";
        Assert.AreEqual("Ankus.Examples.Hello" + suffix, manifest.Library);
        Assert.IsTrue(File.Exists(Path.Join(IntegrationEnvironment.NativeOutputDirectory, manifest.Library)));

        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("LOAD 'Ankus.Examples.Hello'; SELECT public.add(20, 22)", connection);
        Assert.AreEqual(42, await command.ExecuteScalarAsync(context.CancellationToken));
        command.CommandText = "SELECT probin FROM pg_proc WHERE oid = 'public.add(integer,integer)'::regprocedure";
        string library = Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(context.CancellationToken));
        Assert.AreEqual(manifest.Library, Path.GetFileName(library));
    }

    /// <summary>
    /// Native fixture SQL names the actual compiled files without depending on the backend's implicit library suffix.
    /// </summary>
    [TestMethod]
    public async Task NativeFixtureFunctionsNameExistingArtifacts()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT array_agg(probin ORDER BY probin)
            FROM pg_proc
            WHERE oid IN ('tests.cstring_argument(regprocedure,boolean)'::regprocedure,
                'tests.allocator_registry_fault(integer)'::regprocedure,
                'tests.raw_call_holdoffs()'::regprocedure)
            """, connection);
        string[] artifacts = Assert.IsInstanceOfType<string[]>(await command.ExecuteScalarAsync(context.CancellationToken));

        Assert.AreSequenceEqual(["Ankus.AllocatorFaultFixture", "Ankus.AllocatorFixture", "Ankus.RawCallFixture"],
            artifacts.Select(Path.GetFileNameWithoutExtension));
        foreach (string artifact in artifacts)
        {
            Assert.AreEqual(Path.GetFileName(artifact), artifact);
            Assert.IsTrue(File.Exists(Path.Join(IntegrationEnvironment.NativeOutputDirectory, artifact)),
                $"PostgreSQL must name the existing native fixture file '{artifact}' exactly.");
        }
    }

    /// <summary>
    /// Verifies signed datums and generated argument conversion through the sample's native entry point.
    /// </summary>
    /// <param name="left">The first operand.</param>
    /// <param name="right">The second operand.</param>
    /// <param name="expected">The expected SQL integer result.</param>
    [TestMethod]
    [DataRow(40, 2, 42)]
    [DataRow(-20, 5, -15)]
    [DataRow(0, 0, 0)]
    public Task AddReturnsExpectedInteger(int left, int right, int expected)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(AddReturnsExpectedInteger), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT public.add($1, $2)", connection, transaction);
            command.Parameters.AddWithValue(left);
            command.Parameters.AddWithValue(right);

            object? result = await command.ExecuteScalarAsync(token);

            Assert.AreEqual(expected, Assert.IsInstanceOfType<int>(result));
        }, context.CancellationToken);

    /// <summary>
    /// Verifies checked PostgreSQL memory-context allocation, reset invalidation, and current-context restoration.
    /// </summary>
    [TestMethod]
    public Task MemoryContextRoundTripPreservesOwnershipAndInvalidatesResetData()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(MemoryContextRoundTripPreservesOwnershipAndInvalidatesResetData), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT datatype.memory_context_round_trip(41)", connection, transaction);

            object? result = await command.ExecuteScalarAsync(token);

            Assert.AreEqual("Ankus memory test|41|True|42|True", Assert.IsInstanceOfType<string>(result));
        }, context.CancellationToken);

    /// <summary>
    /// A context borrowed by its native address and the owner of an allocation are the same context as the original
    /// handle, and an address that is not a context is rejected without losing the backend.
    /// </summary>
    [TestMethod]
    public Task MemoryContextsResolveFromNativeAddresses()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(MemoryContextsResolveFromNativeAddresses), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT datatype.memory_context_native_address()", connection, transaction);
            Assert.AreEqual("True|Ankus native address|ArgumentException|True", Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token)));
        }, context.CancellationToken);

    /// <summary>
    /// Verifies PostgreSQL's STRICT contract for either or both null operands.
    /// </summary>
    /// <param name="left">The nullable first operand.</param>
    /// <param name="right">The nullable second operand.</param>
    [TestMethod]
    [DataRow(null, 1)]
    [DataRow(1, null)]
    [DataRow(null, null)]
    public Task StrictFunctionReturnsSqlNull(int? left, int? right)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(StrictFunctionReturnsSqlNull), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT public.add($1, $2)", connection, transaction);
            command.Parameters.Add(new NpgsqlParameter<int?> { TypedValue = left });
            command.Parameters.Add(new NpgsqlParameter<int?> { TypedValue = right });

            object? result = await command.ExecuteScalarAsync(token);

            Assert.AreSame(DBNull.Value, result);
        }, context.CancellationToken);

    /// <summary>
    /// Verifies managed exceptions become SQL errors and the same backend remains usable after transaction rollback.
    /// </summary>
    /// <param name="left">The first operand.</param>
    /// <param name="right">The overflowing second operand.</param>
    [TestMethod]
    [DataRow(int.MaxValue, 1)]
    [DataRow(int.MinValue, -1)]
    public async Task ManagedExceptionBecomesSqlErrorWithoutLosingBackend(int left, int right)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int backend = connection.ProcessID;
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(context.CancellationToken))
        {
            await using var command = new NpgsqlCommand("SELECT public.add($1, $2)", connection, transaction);
            command.Parameters.AddWithValue(left);
            command.Parameters.AddWithValue(right);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(
                () => command.ExecuteScalarAsync(context.CancellationToken));

            Assert.AreEqual(PostgresErrorCodes.ExternalRoutineException, error.SqlState);
            Assert.AreEqual(new OverflowException().Message, error.MessageText);
        }

        await using var succeeding = new NpgsqlCommand("SELECT public.add(40, 2)", connection);
        Assert.AreEqual(42, Assert.IsInstanceOfType<int>(await succeeding.ExecuteScalarAsync(context.CancellationToken)));
        Assert.AreEqual(backend, connection.ProcessID);
    }
}
