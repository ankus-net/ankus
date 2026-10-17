using System.Globalization;
using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// An extension that registers shared memory runs under <c>ankus bench</c> without being named in
    /// <c>shared_preload_libraries</c>: bench sees the load fail with Ankus's preload error, restarts PostgreSQL with the
    /// library preloaded and says so. It used to fail until the setting was passed with <c>--postgresql-conf</c>.
    /// </summary>
    [TestMethod]
    public async Task BenchPreloadsExtensionsThatNeedIt()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        _ = await PublishPackageConsumerInDirectoryAsync(directory, "BenchPreload", "ankus_bench_preload", BenchPreloadSource, token);
        string home = CreateDirectory();
        (await InvokeAsync(["init", "--home", home, s_postgresOption, s_installation.PgConfigPath], token)).EnsureSuccess(s_tool, ["init"]);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        try
        {
            ProcessResult run = await InvokeAsync(["bench", "--home", home, "--project", Path.Join(directory, "BenchPreload.csproj"),
                "--configuration", "Release", "--port", port.ToString(CultureInfo.InvariantCulture), "--group-name", "preload"], token);
            Assert.AreEqual(0, run.ExitCode, run.StandardOutput + run.StandardError);
            Assert.Contains("Ankus: BenchPreload must be loaded through shared_preload_libraries; restarted PostgreSQL with it.", run.StandardError);
            Assert.MatchesRegex(@"Result (\d+) total, \1 ok, 0 failed", run.StandardOutput);
        }
        finally
        {
            await new PostgresDevelopmentCluster(s_installation, home).StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Registers shared memory while PostgreSQL preloads the library, which Ankus rejects at any other load, and
    /// benchmarks one access to it.
    /// </summary>
    private const string BenchPreloadSource = """
        using Ankus;

        public static class Preloaded
        {
            private static readonly PgAtomic<long> Counter = new("ankus_bench_preload.counter");

            [PgModuleLoad]
            public static void Load() => PgSharedMemory.Initialize(Counter);

            [PgBenchmark]
            public static void Exchange(PgBencher bencher) => bencher.Iterate(() => Counter.Exchange(1));
        }
        """;
}
